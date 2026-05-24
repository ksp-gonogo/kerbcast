//! WebRTC peer for the kerbcam sidecar. One `KerbcamPeer` per browser
//! connection; each carries one H.264 video track per camera the browser
//! subscribed to AND a "kerbcam-control" data channel for bidirectional
//! protocol messages (see `crate::protocol`). webrtc-rs handles
//! ICE/DTLS/SRTP/RTP packetisation internally.
//!
//! Track lifecycle: the peer owns Arcs to its tracks for the duration of
//! its RTCPeerConnection. Each camera's registry entry holds a Weak ref
//! to the same track + a subscriber count. When the peer is dropped,
//! the Arcs go with it; the camera-side consume loop notices the dead
//! Weaks on its next tick and decrements its subscriber count.
//!
//! Data channel: created by the browser side (the offer SDP includes a
//! datachannel m-section). When it opens, the handler dispatches
//! incoming `ClientMessage` JSON; the server replies / pushes
//! `ServerMessage` JSON back. The plugin reads the mutated control
//! state via its periodic `*.control.json` poll, so layer/render-size
//! changes propagate within ~1s of the data-channel write.

use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::Arc;

/// Monotonic counter for per-peer identifiers. Used as the key on
/// each camera's `degrade_levels` map (SetDegrade arrives via the
/// data channel rather than RTCP, so we don't have an SSRC to lean
/// on — peer_id fills the same slot).
static NEXT_PEER_ID: AtomicU32 = AtomicU32::new(1);

use anyhow::{anyhow, Result};
use tokio::sync::{Mutex, Notify};
use tracing::{debug, info, warn};

use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_H264};
use webrtc::api::setting_engine::SettingEngine;
use webrtc::api::APIBuilder;
use webrtc::data_channel::data_channel_message::DataChannelMessage;
use webrtc::data_channel::RTCDataChannel;
use webrtc::ice_transport::ice_server::RTCIceServer;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::peer_connection::RTCPeerConnection;
use webrtc::rtcp::payload_feedbacks::receiver_estimated_maximum_bitrate::ReceiverEstimatedMaximumBitrate;
use webrtc::rtp_transceiver::rtp_codec::RTCRtpCodecCapability;
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;

use crate::cameras::CameraState as InternalCameraState;

use crate::cameras::CameraRegistry;
use crate::protocol::{
    CameraSnapshotPayload, CameraState, CameraStateChangedPayload, ClientMessage, ErrorPayload,
    FlightIdPayload, HelloPayload, Layer, ServerMessage, SetDegradePayload, SetFovPayload,
    SetLayersPayload, SetPanPayload, SetRenderSizePayload,
};

const CONTROL_CHANNEL_LABEL: &str = "kerbcam-control";

pub struct KerbcamPeer {
    pc: Arc<RTCPeerConnection>,
    /// Stable identifier for this peer for the lifetime of the
    /// connection. Used as the per-camera degrade map key (see
    /// `CameraState.degrade_levels`).
    pub peer_id: u32,
    /// Arcs held for the lifetime of the peer connection. Dropping the
    /// peer drops these Arcs; the matching Weak refs in each camera's
    /// `tracks` list become stale and are pruned on the next encode tick.
    _tracks: Vec<Arc<TrackLocalStaticSample>>,
    /// flight_ids the peer subscribed to — surfaced for logging.
    pub subscribed: Vec<u32>,
    /// Set once the browser opens the control channel. Held for the
    /// peer's lifetime so server-initiated pushes (AdaptiveShed,
    /// vessel-change-driven camera-state-changed) can address this
    /// peer directly without re-discovering the channel each time.
    control_channel: Arc<Mutex<Option<Arc<RTCDataChannel>>>>,
    connected: Arc<Notify>,
    /// Flipped to `false` when the underlying RTCPeerConnection reaches a
    /// terminal state. The daemon polls `is_alive()` each consume tick.
    alive: Arc<AtomicBool>,
}

impl KerbcamPeer {
    /// Build a peer with one H.264 track per requested camera that exists
    /// in the registry. Unknown camera IDs are dropped with a warning —
    /// the caller can still return a useful answer to the browser with
    /// the surviving tracks. Also registers an `on_data_channel` handler
    /// that wires up the "kerbcam-control" protocol channel when the
    /// browser opens one.
    pub async fn new(registry: Arc<CameraRegistry>, requested: &[u32]) -> Result<Self> {
        let peer_id = NEXT_PEER_ID.fetch_add(1, Ordering::Relaxed);
        let mut media_engine = MediaEngine::default();
        media_engine.register_default_codecs()?;
        // Disable mDNS candidate obfuscation: the default QueryAndGather mode
        // generates .local hostnames that desktop Chrome resolves but Android
        // Chrome cannot, preventing streams from loading on mobile.
        let mut setting_engine = SettingEngine::default();
        setting_engine
            .set_ice_multicast_dns_mode(ice::mdns::MulticastDnsMode::Disabled);
        let api = APIBuilder::new()
            .with_media_engine(media_engine)
            .with_setting_engine(setting_engine)
            .build();

        let config = RTCConfiguration {
            ice_servers: vec![RTCIceServer {
                urls: vec!["stun:stun.l.google.com:19302".to_owned()],
                ..Default::default()
            }],
            ..Default::default()
        };

        let pc = Arc::new(api.new_peer_connection(config).await?);

        let mut owned_tracks = Vec::with_capacity(requested.len());
        let mut subscribed = Vec::with_capacity(requested.len());

        for &flight_id in requested {
            let cam = match registry.get(flight_id).await {
                Some(c) => c,
                None => {
                    warn!(flight_id, "requested camera not found, skipping track");
                    continue;
                }
            };

            let track = Arc::new(TrackLocalStaticSample::new(
                RTCRtpCodecCapability {
                    mime_type: MIME_TYPE_H264.to_owned(),
                    ..Default::default()
                },
                format!("video-{flight_id}"),
                format!("kerbcam-{flight_id}"),
            ));

            let rtp_sender = pc
                .add_track(track.clone() as Arc<dyn TrackLocal + Send + Sync>)
                .await?;

            // RTCP feedback stream from the receiver. webrtc-rs requires
            // us to drain it (NACK / PLI break silently otherwise) — we
            // additionally parse REMB packets to drive per-camera bitrate
            // adaptation. The browser sends REMB ~1Hz with its current
            // bandwidth estimate; the consume loop uses the min across
            // active subscribers to retarget the encoder.
            //
            // SSRC discovery: the sender's get_parameters() returns the
            // encoding's SSRC, which the REMB packet's `ssrcs` field
            // matches. We capture it once on attach and use it to wipe
            // our estimate from the camera's map when the loop exits
            // (peer dropped or sender closed).
            let cam_for_drain = cam.clone();
            let track_ssrc = rtp_sender
                .get_parameters()
                .await
                .encodings
                .first()
                .map(|e| e.ssrc)
                .unwrap_or(0);
            tokio::spawn(async move {
                drain_rtcp(rtp_sender, cam_for_drain, track_ssrc).await;
            });

            cam.add_track(track.clone()).await;
            // Plugin's subscriber-aware capture skip uses the
            // ControlState's `subscribed` flag — flip it true now so
            // the plugin wakes the camera on its next 1Hz control
            // poll. (No-op if already true from a prior peer.)
            registry.set_subscribed(flight_id, true).await;
            owned_tracks.push(track);
            subscribed.push(flight_id);
        }

        let control_channel: Arc<Mutex<Option<Arc<RTCDataChannel>>>> = Arc::new(Mutex::new(None));

        let registry_for_dc = registry.clone();
        let control_channel_for_handler = control_channel.clone();
        pc.on_data_channel(Box::new(move |dc: Arc<RTCDataChannel>| {
            let registry = registry_for_dc.clone();
            let control_channel = control_channel_for_handler.clone();
            Box::pin(async move {
                if dc.label() != CONTROL_CHANNEL_LABEL {
                    info!(label = %dc.label(), "ignoring unexpected data channel");
                    return;
                }
                info!(label = %dc.label(), "control channel opened");
                *control_channel.lock().await = Some(dc.clone());

                let dc_for_msg = dc.clone();
                dc.on_message(Box::new(move |msg: DataChannelMessage| {
                    let registry = registry.clone();
                    let dc = dc_for_msg.clone();
                    Box::pin(async move {
                        handle_client_message(registry, peer_id, dc, msg).await;
                    })
                }));
            })
        }));

        let connected = Arc::new(Notify::new());
        let alive = Arc::new(AtomicBool::new(true));
        let connected_for_handler = connected.clone();
        let alive_for_handler = alive.clone();
        pc.on_peer_connection_state_change(Box::new(move |state: RTCPeerConnectionState| {
            info!(?state, "peer connection state");
            if state == RTCPeerConnectionState::Connected {
                connected_for_handler.notify_waiters();
            }
            if matches!(
                state,
                RTCPeerConnectionState::Disconnected
                    | RTCPeerConnectionState::Failed
                    | RTCPeerConnectionState::Closed
            ) {
                alive_for_handler.store(false, Ordering::Release);
            }
            Box::pin(async {})
        }));

        Ok(Self {
            pc,
            peer_id,
            _tracks: owned_tracks,
            subscribed,
            control_channel,
            connected,
            alive,
        })
    }

    /// Browser-initiated SDP flow used by the HTTP signalling endpoint.
    /// Browser POSTs its offer, we set it as the remote description, then
    /// create + return our answer.
    pub async fn answer_to_offer(&self, offer_sdp: String) -> Result<String> {
        let offer = RTCSessionDescription::offer(offer_sdp)?;
        self.pc.set_remote_description(offer).await?;

        let answer = self.pc.create_answer(None).await?;
        self.pc.set_local_description(answer).await?;

        let mut gather_complete = self.pc.gathering_complete_promise().await;
        let _ = gather_complete.recv().await;

        let local = self
            .pc
            .local_description()
            .await
            .ok_or_else(|| anyhow!("local description missing after set_local_description"))?;
        Ok(local.sdp)
    }

    pub fn is_alive(&self) -> bool {
        self.alive.load(Ordering::Acquire)
    }

    /// Server-initiated push to the browser. No-op if the control
    /// channel hasn't been opened yet (we drop the message rather than
    /// queue — pushes from the consume loop are state snapshots, so a
    /// later snapshot supersedes a dropped one).
    pub async fn push_message(&self, msg: &ServerMessage) {
        let dc = match self.control_channel.lock().await.as_ref() {
            Some(d) => d.clone(),
            None => return,
        };
        send_server_message(&dc, msg).await;
    }

    /// Block until the peer reaches the Connected state. Used by tests.
    #[allow(dead_code)]
    pub async fn wait_connected(&self) {
        self.connected.notified().await;
    }

    /// Tear down the peer cleanly. Idempotent.
    #[allow(dead_code)]
    pub async fn close(&self) -> Result<()> {
        self.pc.close().await?;
        Ok(())
    }
}

/// Single-message dispatch. Pulled out of the closure so the error
/// paths and per-variant logic don't drown the construction code.
async fn handle_client_message(
    registry: Arc<CameraRegistry>,
    peer_id: u32,
    dc: Arc<RTCDataChannel>,
    msg: DataChannelMessage,
) {
    if msg.is_string {
        // expected branch
    }
    let text = match std::str::from_utf8(&msg.data) {
        Ok(s) => s,
        Err(e) => {
            warn!(error = %e, "control channel: non-utf8 payload, dropping");
            return;
        }
    };
    let parsed: ClientMessage = match serde_json::from_str(text) {
        Ok(m) => m,
        Err(e) => {
            warn!(error = %e, payload = %text, "control channel: parse failed");
            send_server_message(
                &dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("parse failed: {e}"),
                }),
            )
            .await;
            return;
        }
    };

    match parsed {
        ClientMessage::Hello => {
            info!("control channel: hello received");
            send_server_message(
                &dc,
                &ServerMessage::Hello(HelloPayload {
                    sidecar_version: crate::VERSION.to_owned(),
                    encoder_backend: "openh264".to_owned(),
                }),
            )
            .await;
            send_camera_snapshot(&registry, &dc).await;
        }
        ClientMessage::SetLayers(SetLayersPayload { flight_id, layers }) => {
            apply_layer_change(&registry, &dc, flight_id, layers).await;
        }
        ClientMessage::SetRenderSize(SetRenderSizePayload {
            flight_id,
            width,
            height,
        }) => {
            apply_render_size_change(&registry, &dc, flight_id, width, height).await;
        }
        ClientMessage::SetFov(SetFovPayload { flight_id, fov }) => {
            apply_fov_change(&registry, &dc, flight_id, fov).await;
        }
        ClientMessage::SetPan(SetPanPayload {
            flight_id,
            yaw,
            pitch,
        }) => {
            apply_pan_change(&registry, &dc, flight_id, yaw, pitch).await;
        }
        ClientMessage::SetDegrade(SetDegradePayload { flight_id, level }) => {
            apply_degrade_change(&registry, &dc, peer_id, flight_id, level).await;
        }
        ClientMessage::RequestKeyframe(FlightIdPayload { flight_id }) => {
            // The encoder backends expose `request_keyframe()`; we call
            // it through the per-camera encoder lock if it's currently
            // initialised. If not, the next encode produces an IDR
            // anyway (cold start), so the no-op is the right thing.
            if let Some(cam) = registry.get(flight_id).await {
                let mut guard = cam.encoder.lock().await;
                if let Some(backend) = guard.as_mut() {
                    backend.request_keyframe();
                    info!(flight_id, "keyframe requested");
                }
            }
        }
    }
}

async fn apply_layer_change(
    registry: &Arc<CameraRegistry>,
    dc: &Arc<RTCDataChannel>,
    flight_id: u32,
    layers: Vec<Layer>,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                }),
            )
            .await;
            return;
        }
    };

    let snapshot = {
        let mut ctrl = cam.control.lock().await;
        ctrl.layers = layers.clone();
        ctrl.clone()
    };

    if let Err(e) = registry.flush_control(flight_id, &snapshot).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
            }),
        )
        .await;
        return;
    }

    info!(flight_id, layers = ?layers, "data-channel set-layers applied");
    push_camera_state(registry, dc, flight_id).await;
}

async fn apply_render_size_change(
    registry: &Arc<CameraRegistry>,
    dc: &Arc<RTCDataChannel>,
    flight_id: u32,
    width: u32,
    height: u32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                }),
            )
            .await;
            return;
        }
    };

    // Cap at the ring's allocated max — anything bigger would be
    // rejected by the plugin's MmapFrameRing.Produce on the next emit.
    let w = make_even(width.min(cam.max_width));
    let h = make_even(height.min(cam.max_height));

    let snapshot = {
        let mut ctrl = cam.control.lock().await;
        ctrl.width = Some(w);
        ctrl.height = Some(h);
        ctrl.clone()
    };

    if let Err(e) = registry.flush_control(flight_id, &snapshot).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
            }),
        )
        .await;
        return;
    }

    info!(
        flight_id,
        width = w,
        height = h,
        "data-channel set-render-size applied"
    );
    push_camera_state(registry, dc, flight_id).await;
}

async fn apply_fov_change(
    registry: &Arc<CameraRegistry>,
    dc: &Arc<RTCDataChannel>,
    flight_id: u32,
    fov: f32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                }),
            )
            .await;
            return;
        }
    };
    if !cam.supports_zoom {
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("camera {flight_id} does not support zoom"),
            }),
        )
        .await;
        return;
    }
    // Clamp to the part's declared FoV range so the plugin doesn't get
    // a value the Hullcam module would reject.
    let clamped = fov.clamp(cam.fov_min, cam.fov_max);

    let snapshot = {
        let mut ctrl = cam.control.lock().await;
        ctrl.fov = Some(clamped);
        ctrl.clone()
    };

    if let Err(e) = registry.flush_control(flight_id, &snapshot).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
            }),
        )
        .await;
        return;
    }

    info!(flight_id, fov = clamped, "data-channel set-fov applied");
    push_camera_state(registry, dc, flight_id).await;
}

async fn apply_pan_change(
    registry: &Arc<CameraRegistry>,
    dc: &Arc<RTCDataChannel>,
    flight_id: u32,
    yaw: f32,
    pitch: f32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                }),
            )
            .await;
            return;
        }
    };
    if !cam.supports_pan {
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!(
                    "camera {flight_id} does not support pan/tilt (yet — \
                     planned mod extension)"
                ),
            }),
        )
        .await;
        return;
    }
    let yaw_clamped = yaw.clamp(cam.pan_yaw_min, cam.pan_yaw_max);
    let pitch_clamped = pitch.clamp(cam.pan_pitch_min, cam.pan_pitch_max);

    let snapshot = {
        let mut ctrl = cam.control.lock().await;
        ctrl.pan_yaw = Some(yaw_clamped);
        ctrl.pan_pitch = Some(pitch_clamped);
        ctrl.clone()
    };

    if let Err(e) = registry.flush_control(flight_id, &snapshot).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
            }),
        )
        .await;
        return;
    }

    info!(
        flight_id,
        yaw = yaw_clamped,
        pitch = pitch_clamped,
        "data-channel set-pan applied"
    );
    push_camera_state(registry, dc, flight_id).await;
}

async fn apply_degrade_change(
    registry: &Arc<CameraRegistry>,
    dc: &Arc<RTCDataChannel>,
    peer_id: u32,
    flight_id: u32,
    level: f32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                }),
            )
            .await;
            return;
        }
    };
    cam.record_degrade(peer_id, level).await;
    info!(
        flight_id,
        peer_id,
        level = level.clamp(0.0, 1.0),
        effective = cam.current_degrade(),
        "set-degrade applied",
    );
    push_camera_state(registry, dc, flight_id).await;
}

async fn send_camera_snapshot(registry: &Arc<CameraRegistry>, dc: &Arc<RTCDataChannel>) {
    let cams = registry.list().await;
    // Initial snapshot: optimistic defaults until the plugin's status
    // file pushes the real effective state. Operator == effective for
    // both layers and dims at this point; subsequent
    // `camera-state-changed` messages from the status poller correct
    // these once adaptive shedding kicks in.
    let cameras: Vec<CameraState> = cams
        .into_iter()
        .map(|c| CameraState {
            flight_id: c.flight_id,
            part_name: c.part_name,
            part_title: c.part_title,
            camera_name: c.camera_name,
            vessel_name: c.vessel_name,
            layers: vec![Layer::Near, Layer::Scaled, Layer::Galaxy],
            operator_layers: vec![Layer::Near, Layer::Scaled, Layer::Galaxy],
            render_width: c.max_width,
            render_height: c.max_height,
            operator_width: c.max_width,
            operator_height: c.max_height,
            supports_zoom: c.supports_zoom,
            fov: c.fov,
            fov_min: c.fov_min,
            fov_max: c.fov_max,
            supports_pan: c.supports_pan,
            pan_yaw: 0.0,
            pan_pitch: 0.0,
            pan_yaw_min: c.pan_yaw_min,
            pan_yaw_max: c.pan_yaw_max,
            pan_pitch_min: c.pan_pitch_min,
            pan_pitch_max: c.pan_pitch_max,
            encoder_bitrate_bps: c.encoder_bitrate_bps,
            target_bitrate_bps: c.target_bitrate_bps,
            degrade_level: c.degrade_level,
        })
        .collect();
    send_server_message(
        dc,
        &ServerMessage::CameraSnapshot(CameraSnapshotPayload { cameras }),
    )
    .await;
}

async fn push_camera_state(
    registry: &Arc<CameraRegistry>,
    dc: &Arc<RTCDataChannel>,
    flight_id: u32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => return,
    };
    let ctrl = cam.control.lock().await.clone();
    let layers = if ctrl.layers.is_empty() {
        vec![Layer::Near, Layer::Scaled, Layer::Galaxy]
    } else {
        ctrl.layers.clone()
    };
    let w = ctrl.width.unwrap_or(cam.max_width);
    let h = ctrl.height.unwrap_or(cam.max_height);
    let fov = ctrl.fov.unwrap_or(cam.fov_default);
    let pan_yaw = ctrl.pan_yaw.unwrap_or(0.0);
    let pan_pitch = ctrl.pan_pitch.unwrap_or(0.0);
    let state = CameraState {
        flight_id,
        part_name: cam.part_name.clone(),
        part_title: cam.part_title.clone(),
        camera_name: cam.camera_name.clone(),
        vessel_name: cam.vessel_name.clone(),
        layers: layers.clone(),
        operator_layers: layers,
        render_width: w,
        render_height: h,
        operator_width: w,
        operator_height: h,
        supports_zoom: cam.supports_zoom,
        fov,
        fov_min: cam.fov_min,
        fov_max: cam.fov_max,
        supports_pan: cam.supports_pan,
        pan_yaw,
        pan_pitch,
        pan_yaw_min: cam.pan_yaw_min,
        pan_yaw_max: cam.pan_yaw_max,
        pan_pitch_min: cam.pan_pitch_min,
        pan_pitch_max: cam.pan_pitch_max,
        encoder_bitrate_bps: cam.encoder_bitrate.load(Ordering::Acquire),
        target_bitrate_bps: cam.target_bitrate_bps.load(Ordering::Acquire),
        degrade_level: cam.current_degrade(),
    };
    send_server_message(
        dc,
        &ServerMessage::CameraStateChanged(CameraStateChangedPayload { state }),
    )
    .await;
}

async fn send_server_message(dc: &Arc<RTCDataChannel>, msg: &ServerMessage) {
    let body = match serde_json::to_string(msg) {
        Ok(s) => s,
        Err(e) => {
            warn!(error = %e, "control channel: serialise failed");
            return;
        }
    };
    if let Err(e) = dc.send_text(body).await {
        warn!(error = %e, "control channel: send failed");
    }
}

fn make_even(v: u32) -> u32 {
    if v.is_multiple_of(2) {
        v
    } else {
        v.saturating_sub(1)
    }
}

/// Per-track RTCP drain. Mostly serves to keep webrtc-rs's internal
/// pipelines flowing (NACK / PLI break silently if we don't drain) —
/// but also parses REMB packets and pushes the receiver's bandwidth
/// estimate into the camera's per-SSRC estimate map. The consume loop
/// reads the camera's `target_bitrate_bps` (min across active
/// subscribers) and retargets the encoder when it diverges
/// significantly from the encoder's current bitrate.
///
/// Loop exits on read error (sender closed → peer connection torn
/// down) at which point we wipe our estimate from the camera's map so
/// stale numbers don't pin the encoder at an obsolete target.
async fn drain_rtcp(
    sender: Arc<webrtc::rtp_transceiver::rtp_sender::RTCRtpSender>,
    cam: Arc<InternalCameraState>,
    track_ssrc: u32,
) {
    use std::any::Any;
    loop {
        match sender.read_rtcp().await {
            Ok((packets, _attrs)) => {
                for packet in packets {
                    let any: &dyn Any = packet.as_any();
                    if let Some(remb) = any.downcast_ref::<ReceiverEstimatedMaximumBitrate>() {
                        // REMB carries a max-bitrate estimate the
                        // receiver thinks the path can sustain. We log
                        // at debug — info would flood at the ~1Hz REMB
                        // cadence × N peers.
                        let bps = remb.bitrate as u32;
                        debug!(
                            flight_id = cam.flight_id,
                            ssrc = track_ssrc,
                            bps,
                            "REMB received",
                        );
                        cam.record_bandwidth_estimate(track_ssrc, bps).await;
                    }
                }
            }
            Err(_) => {
                debug!(
                    flight_id = cam.flight_id,
                    ssrc = track_ssrc,
                    "RTCP drain loop exited"
                );
                cam.forget_estimate(track_ssrc).await;
                break;
            }
        }
    }
}
