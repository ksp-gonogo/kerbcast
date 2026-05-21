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

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use anyhow::{anyhow, Result};
use tokio::sync::{Mutex, Notify};
use tracing::{debug, info, warn};

use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_H264};
use webrtc::api::APIBuilder;
use webrtc::data_channel::data_channel_message::DataChannelMessage;
use webrtc::data_channel::RTCDataChannel;
use webrtc::ice_transport::ice_server::RTCIceServer;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::peer_connection::RTCPeerConnection;
use webrtc::rtp_transceiver::rtp_codec::RTCRtpCodecCapability;
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;

use crate::cameras::CameraRegistry;
use crate::protocol::{
    CameraSnapshotPayload, CameraState, CameraStateChangedPayload, ClientMessage, ErrorPayload,
    FlightIdPayload, HelloPayload, Layer, ServerMessage, SetLayersPayload, SetRenderSizePayload,
};

const CONTROL_CHANNEL_LABEL: &str = "kerbcam-control";

pub struct KerbcamPeer {
    pc: Arc<RTCPeerConnection>,
    /// Arcs held for the lifetime of the peer connection. Dropping the
    /// peer drops these Arcs; the matching Weak refs in each camera's
    /// `tracks` list become stale and are pruned on the next encode tick.
    _tracks: Vec<Arc<TrackLocalStaticSample>>,
    /// flight_ids the peer subscribed to — surfaced for logging.
    pub subscribed: Vec<u32>,
    /// Set once the browser opens the control channel. Held for the
    /// peer's lifetime so server-initiated pushes (AdaptiveShed, vessel
    /// changes once a plugin status file lands) can address this peer
    /// directly without re-discovering the channel each time.
    #[allow(dead_code)]
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
        let mut media_engine = MediaEngine::default();
        media_engine.register_default_codecs()?;
        let api = APIBuilder::new().with_media_engine(media_engine).build();

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

            // webrtc-rs requires us to drain the RTCP feedback stream from
            // each sender, otherwise NACK / PLI / REMB break silently.
            // Spawned per track; loop exits when the sender closes.
            tokio::spawn(async move {
                let mut rtcp_buf = vec![0u8; 1500];
                while rtp_sender.read(&mut rtcp_buf).await.is_ok() {}
                debug!("RTCP drain loop exited");
            });

            cam.add_track(track.clone()).await;
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
                        handle_client_message(registry, dc, msg).await;
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

async fn send_camera_snapshot(registry: &Arc<CameraRegistry>, dc: &Arc<RTCDataChannel>) {
    let cams = registry.list().await;
    let cameras: Vec<CameraState> = cams
        .into_iter()
        .map(|c| CameraState {
            flight_id: c.flight_id,
            part_name: c.part_name,
            part_title: c.part_title,
            camera_name: c.camera_name,
            vessel_name: c.vessel_name,
            // We don't yet have plugin-side status reporting, so the
            // sidecar mirrors operator state for both effective and
            // operator views. Once the plugin writes back a status file,
            // these split.
            layers: vec![Layer::Near, Layer::Scaled, Layer::Galaxy],
            operator_layers: vec![Layer::Near, Layer::Scaled, Layer::Galaxy],
            render_width: c.max_width,
            render_height: c.max_height,
            operator_width: c.max_width,
            operator_height: c.max_height,
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
