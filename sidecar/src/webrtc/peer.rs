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
use tracing::{info, warn};

use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_H264};
use webrtc::api::setting_engine::SettingEngine;
use webrtc::api::APIBuilder;
use webrtc::data_channel::data_channel_message::DataChannelMessage;
use webrtc::data_channel::RTCDataChannel;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::peer_connection::RTCPeerConnection;
use webrtc::rtp_transceiver::rtp_codec::RTCRtpCodecCapability;
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;

use crate::cameras::CameraState as InternalCameraState;

use crate::cameras::CameraRegistry;
use crate::encoder::selected_backend_name;
use crate::protocol::{
    CameraLifecycle, CameraSnapshotPayload, CameraState, CameraStateChangedPayload, ClientMessage,
    ErrorPayload, ErrorSource, FlightIdPayload, HelloPayload, Layer, ServerMessage,
    SetDegradePayload, SetFovPayload, SetLayersPayload, SetPanPayload, SetPanRatePayload,
    SetRenderSizePayload, SetZoomRatePayload, SlotMapPayload,
};

const CONTROL_CHANNEL_LABEL: &str = "kerbcam-control";

/// One pre-negotiated video slot. The track stays on the peer connection
/// for the connection's lifetime; `bound` records which camera currently
/// feeds it (`None` = idle/silent) and `mid` is the transceiver mid the
/// browser keys its `SlotMap` routing on (filled once the answer is set).
struct Slot {
    track: Arc<TrackLocalStaticSample>,
    mid: Option<String>,
    bound: Option<u32>,
}

pub struct KerbcamPeer {
    pc: Arc<RTCPeerConnection>,
    /// Stable identifier for this peer for the lifetime of the
    /// connection. Used as the per-camera degrade map key (see
    /// `CameraState.degrade_levels`).
    pub peer_id: u32,
    /// The pre-negotiated slot pool. `Subscribe` binds a camera to a free
    /// slot; `Unsubscribe` frees one — no renegotiation. Dropping the peer
    /// drops the slot-track Arcs; the matching Weak refs in each camera's
    /// `tracks` list go stale and are pruned on the next encode tick.
    slots: Arc<Mutex<Vec<Slot>>>,
    /// flight_ids bound at connect time — echoed (in slot order) in the
    /// `/offer` answer so the browser maps its initial cameras to slots.
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
    /// Build a peer with a pool of `slot_count` pre-negotiated H.264 video
    /// slots + a "kerbcam-control" data channel. The cameras in `initial`
    /// are bound to the first slots immediately (skipping unknown/destroyed
    /// ones); the rest stay idle until the browser `Subscribe`s a camera
    /// into them. `slot_count` must equal the number of recv-only video
    /// transceivers in the browser's offer or SDP negotiation won't match.
    pub async fn new(
        registry: Arc<CameraRegistry>,
        initial: &[u32],
        slot_count: usize,
    ) -> Result<Self> {
        let peer_id = NEXT_PEER_ID.fetch_add(1, Ordering::Relaxed);
        let mut media_engine = MediaEngine::default();
        media_engine.register_default_codecs()?;
        // Disable mDNS candidate obfuscation: the default QueryAndGather mode
        // generates .local hostnames that desktop Chrome resolves but Android
        // Chrome cannot, preventing streams from loading on mobile.
        let mut setting_engine = SettingEngine::default();
        setting_engine.set_ice_multicast_dns_mode(ice::mdns::MulticastDnsMode::Disabled);
        let api = APIBuilder::new()
            .with_media_engine(media_engine)
            .with_setting_engine(setting_engine)
            .build();

        // No external STUN server: this is a LAN-only tool and host candidates
        // are sufficient. The Google STUN URL was unreachable over IPv6 from
        // the Deck (Network is unreachable) and its timeout caused 1-2 minute
        // delays before mobile ICE proceeded with what it had.
        let config = RTCConfiguration::default();

        let pc = Arc::new(api.new_peer_connection(config).await?);

        // Build the slot pool: one send-track per slot, added to the PC so
        // the answer carries `slot_count` video m-lines matching the offer's
        // recv-only transceivers. Each sender's RTCP must be drained or
        // webrtc-rs's NACK/PLI pipelines stall (see `drain_rtcp_sink`).
        let mut slots: Vec<Slot> = Vec::with_capacity(slot_count);
        for i in 0..slot_count {
            let track = Arc::new(TrackLocalStaticSample::new(
                RTCRtpCodecCapability {
                    mime_type: MIME_TYPE_H264.to_owned(),
                    ..Default::default()
                },
                format!("video-slot{i}"),
                format!("kerbcam-slot{i}"),
            ));
            let rtp_sender = pc
                .add_track(track.clone() as Arc<dyn TrackLocal + Send + Sync>)
                .await?;
            tokio::spawn(async move {
                drain_rtcp_sink(rtp_sender).await;
            });
            slots.push(Slot {
                track,
                mid: None,
                bound: None,
            });
        }

        // Bind the initial subscription onto the first free slots. Camera ->
        // slot binding is just `cam.add_track`; the consume loop then feeds
        // the slot from that camera's encoder. set_subscribed(true) wakes the
        // plugin's per-camera render on its next ~1Hz control poll.
        let mut subscribed = Vec::new();
        for &flight_id in initial {
            let cam = match registry.get(flight_id).await {
                Some(c) => c,
                None => {
                    warn!(flight_id, "initial camera not found, skipping");
                    continue;
                }
            };
            if cam.destroyed.load(Ordering::Acquire) {
                warn!(flight_id, "skipping destroyed initial camera");
                continue;
            }
            let Some(slot) = slots.iter_mut().find(|s| s.bound.is_none()) else {
                warn!(flight_id, "no free slot for initial subscription");
                break;
            };
            cam.add_track(slot.track.clone()).await;
            registry.set_subscribed(flight_id, true).await;
            slot.bound = Some(flight_id);
            subscribed.push(flight_id);
        }

        let slots = Arc::new(Mutex::new(slots));

        let control_channel: Arc<Mutex<Option<Arc<RTCDataChannel>>>> = Arc::new(Mutex::new(None));

        let registry_for_dc = registry.clone();
        let control_channel_for_handler = control_channel.clone();
        let slots_for_handler = slots.clone();
        pc.on_data_channel(Box::new(move |dc: Arc<RTCDataChannel>| {
            let registry = registry_for_dc.clone();
            let control_channel = control_channel_for_handler.clone();
            let slots = slots_for_handler.clone();
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
                    let slots = slots.clone();
                    let dc = dc_for_msg.clone();
                    Box::pin(async move {
                        handle_client_message(registry, peer_id, slots, dc, msg).await;
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
            slots,
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

        // Record each slot's negotiated transceiver mid so SlotMap addresses
        // slots by the same stable mid the browser sees. Slot-tracks were
        // add_track'd in order, so get_transceivers() is index-aligned with
        // the pool (Unified Plan preserves m-line order).
        let transceivers = self.pc.get_transceivers().await;
        {
            let mut slots = self.slots.lock().await;
            if slots.len() != transceivers.len() {
                warn!(
                    slots = slots.len(),
                    transceivers = transceivers.len(),
                    "slot/transceiver count mismatch — slot mids may misalign",
                );
            }
            for (slot, tr) in slots.iter_mut().zip(transceivers.iter()) {
                slot.mid = tr.mid().map(|m| m.to_string());
            }
        }

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

    /// Flight IDs currently bound to this peer's slots. Unlike the
    /// construction-time `subscribed` list, this reflects dynamic
    /// `Subscribe`/`Unsubscribe` (which mutate the slots, not that list),
    /// so the dead-peer cleanup can zero rates on exactly the cameras this
    /// peer was driving when it dropped.
    pub async fn bound_flight_ids(&self) -> Vec<u32> {
        self.slots
            .lock()
            .await
            .iter()
            .filter_map(|s| s.bound)
            .collect()
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

    /// Tear down the peer cleanly. Idempotent. Releases the RTCRtpSenders'
    /// strong refs to the slot tracks (see `close_releases_track_arc`), so each
    /// camera's Weak goes dead and the consume loop prunes it — dropping the
    /// subscriber count to zero and flushing `set_subscribed(false)`. Must be
    /// called when reaping a dropped peer, or cameras keep rendering for a
    /// viewer that's already gone.
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
    slots: Arc<Mutex<Vec<Slot>>>,
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
                    source: ErrorSource::Sidecar,
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
                    encoder_backend: selected_backend_name().to_owned(),
                }),
            )
            .await;
            send_camera_snapshot(&registry, &dc).await;
            // Announce the initial slot bindings so the client maps its
            // initial cameras to slots by mid — uniform with dynamic
            // Subscribe, no reliance on the answer's camera order.
            send_initial_slot_maps(&slots, &dc).await;
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
        ClientMessage::SetPanRate(SetPanRatePayload {
            flight_id,
            yaw_rate,
            pitch_rate,
        }) => {
            apply_pan_rate_change(&registry, &dc, flight_id, yaw_rate, pitch_rate).await;
        }
        ClientMessage::SetZoomRate(SetZoomRatePayload { flight_id, rate }) => {
            apply_zoom_rate_change(&registry, &dc, flight_id, rate).await;
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
        ClientMessage::Subscribe(FlightIdPayload { flight_id }) => {
            handle_subscribe(&registry, &slots, &dc, flight_id).await;
        }
        ClientMessage::Unsubscribe(FlightIdPayload { flight_id }) => {
            handle_unsubscribe(&registry, &slots, &dc, flight_id).await;
        }
        ClientMessage::Pong => {
            // No-op — the peer is alive by virtue of having sent this.
        }
    }
}

/// Force the camera's encoder to emit an IDR on its next encode. No-op when no
/// encoder exists yet (a cold start already produces an IDR). Shared by the
/// explicit keyframe request and the slot-subscribe path.
async fn request_keyframe_for(cam: &Arc<InternalCameraState>) {
    let mut guard = cam.encoder.lock().await;
    if let Some(backend) = guard.as_mut() {
        backend.request_keyframe();
    }
}

/// Bind a camera to a free slot (or re-announce if already bound). On a fresh
/// bind the camera's subscriber count goes 0 -> 1, waking the plugin render;
/// the consume loop then feeds the slot's track from the camera's encoder.
async fn handle_subscribe(
    registry: &Arc<CameraRegistry>,
    slots: &Arc<Mutex<Vec<Slot>>>,
    dc: &Arc<RTCDataChannel>,
    flight_id: u32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) if !c.destroyed.load(Ordering::Acquire) => c,
        _ => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no live camera with flight_id={flight_id}"),
                    source: ErrorSource::Sidecar,
                }),
            )
            .await;
            return;
        }
    };

    // Choose a slot under the lock, then release it before the awaiting camera
    // ops. `fresh` distinguishes a new bind (add_track once) from a re-subscribe
    // of an already-bound camera (re-announce only — never double add_track).
    let (track, mid, fresh) = {
        let mut guard = slots.lock().await;
        if let Some(slot) = guard.iter().find(|s| s.bound == Some(flight_id)) {
            (slot.track.clone(), slot.mid.clone(), false)
        } else if let Some(slot) = guard.iter_mut().find(|s| s.bound.is_none()) {
            slot.bound = Some(flight_id);
            (slot.track.clone(), slot.mid.clone(), true)
        } else {
            drop(guard);
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: "no free slot — negotiate more video transceivers".into(),
                    source: ErrorSource::Sidecar,
                }),
            )
            .await;
            return;
        }
    };

    if fresh {
        // Keyframe BEFORE add_track so the next encode emits an IDR (with
        // SPS/PPS) to every track of this camera, including the freshly-added
        // slot — otherwise the slot's first frame is a mid-GOP P-frame that
        // decodes as garbage until the next periodic IDR.
        request_keyframe_for(&cam).await;
        cam.add_track(track).await;
        registry.set_subscribed(flight_id, true).await;
        info!(flight_id, "subscribed camera to slot");
    }

    match mid {
        Some(mid) => {
            send_server_message(
                dc,
                &ServerMessage::SlotMap(SlotMapPayload {
                    mid,
                    flight_id: Some(flight_id),
                }),
            )
            .await;
        }
        None => warn!(
            flight_id,
            "slot has no negotiated mid yet — SlotMap skipped"
        ),
    }
}

/// Release a camera's slot. The slot-track stays alive (reused for a later
/// subscribe); the camera sleeps once its last viewer leaves.
async fn handle_unsubscribe(
    registry: &Arc<CameraRegistry>,
    slots: &Arc<Mutex<Vec<Slot>>>,
    dc: &Arc<RTCDataChannel>,
    flight_id: u32,
) {
    let (track, mid) = {
        let mut guard = slots.lock().await;
        match guard.iter_mut().find(|s| s.bound == Some(flight_id)) {
            Some(slot) => {
                slot.bound = None;
                (slot.track.clone(), slot.mid.clone())
            }
            None => return, // not bound to any slot — nothing to do
        }
    };

    if let Some(cam) = registry.get(flight_id).await {
        let remaining = cam.remove_track(&track).await;
        if remaining == 0 {
            // Last viewer — sleep the camera promptly (the consume loop's
            // maybe_sleep_idle_cameras would also catch this next tick).
            registry.set_subscribed(flight_id, false).await;
        }
        info!(flight_id, remaining, "unsubscribed camera from slot");
    }

    if let Some(mid) = mid {
        send_server_message(
            dc,
            &ServerMessage::SlotMap(SlotMapPayload {
                mid,
                flight_id: None,
            }),
        )
        .await;
    }
}

/// Announce the currently-bound slots to a freshly-opened control channel so
/// the client maps its initial cameras to slots by mid (the same mechanism
/// dynamic Subscribe uses). Idle slots and slots without a negotiated mid are
/// skipped.
async fn send_initial_slot_maps(slots: &Arc<Mutex<Vec<Slot>>>, dc: &Arc<RTCDataChannel>) {
    let bindings: Vec<(String, u32)> = {
        let guard = slots.lock().await;
        guard
            .iter()
            .filter_map(|s| match (&s.mid, s.bound) {
                (Some(mid), Some(flight_id)) => Some((mid.clone(), flight_id)),
                _ => None,
            })
            .collect()
    };
    for (mid, flight_id) in bindings {
        send_server_message(
            dc,
            &ServerMessage::SlotMap(SlotMapPayload {
                mid,
                flight_id: Some(flight_id),
            }),
        )
        .await;
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
                    source: ErrorSource::Sidecar,
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
                source: ErrorSource::Sidecar,
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
                    source: ErrorSource::Sidecar,
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
                source: ErrorSource::Sidecar,
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
                    source: ErrorSource::Sidecar,
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
                source: ErrorSource::Sidecar,
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
        // Bump so the plugin applies this absolute even mid zoom-rate hold
        // (and distinguishes it from the same value re-serialised on an
        // unrelated write). Wrapping is fine — the plugin compares for
        // change, not magnitude.
        ctrl.fov_seq = ctrl.fov_seq.wrapping_add(1);
        ctrl.clone()
    };

    if let Err(e) = registry.flush_control(flight_id, &snapshot).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
                source: ErrorSource::Sidecar,
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
                    source: ErrorSource::Sidecar,
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
                source: ErrorSource::Sidecar,
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
        // Bump so the plugin applies this absolute even mid pan-rate hold
        // (see `ControlState::pan_seq`). Covers both yaw and pitch.
        ctrl.pan_seq = ctrl.pan_seq.wrapping_add(1);
        ctrl.clone()
    };

    if let Err(e) = registry.flush_control(flight_id, &snapshot).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
                source: ErrorSource::Sidecar,
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

/// Persistent pan velocity. Mirrors `apply_pan_change` but stores a
/// normalised rate the plugin integrates per frame rather than an absolute
/// target. A rate holds until superseded (including by zero), so the on-disk
/// `pan_yaw_rate`/`pan_pitch_rate` carry the last command.
async fn apply_pan_rate_change(
    registry: &Arc<CameraRegistry>,
    dc: &Arc<RTCDataChannel>,
    flight_id: u32,
    yaw_rate: f32,
    pitch_rate: f32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                    source: ErrorSource::Sidecar,
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
                source: ErrorSource::Sidecar,
            }),
        )
        .await;
        return;
    }
    let yaw_clamped = yaw_rate.clamp(-1.0, 1.0);
    let pitch_clamped = pitch_rate.clamp(-1.0, 1.0);

    let snapshot = {
        let mut ctrl = cam.control.lock().await;
        ctrl.pan_yaw_rate = Some(yaw_clamped);
        ctrl.pan_pitch_rate = Some(pitch_clamped);
        ctrl.clone()
    };

    if let Err(e) = registry.flush_control(flight_id, &snapshot).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
                source: ErrorSource::Sidecar,
            }),
        )
        .await;
        return;
    }

    info!(
        flight_id,
        yaw_rate = yaw_clamped,
        pitch_rate = pitch_clamped,
        "data-channel set-pan-rate applied"
    );
    push_camera_state(registry, dc, flight_id).await;
}

/// Persistent zoom velocity. Mirrors `apply_fov_change` but stores a
/// normalised rate (+1 = zoom in) the plugin integrates into the FoV target
/// per frame, rather than an absolute FoV. Holds until superseded.
async fn apply_zoom_rate_change(
    registry: &Arc<CameraRegistry>,
    dc: &Arc<RTCDataChannel>,
    flight_id: u32,
    rate: f32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                    source: ErrorSource::Sidecar,
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
                source: ErrorSource::Sidecar,
            }),
        )
        .await;
        return;
    }
    let clamped = rate.clamp(-1.0, 1.0);

    let snapshot = {
        let mut ctrl = cam.control.lock().await;
        ctrl.zoom_rate = Some(clamped);
        ctrl.clone()
    };

    if let Err(e) = registry.flush_control(flight_id, &snapshot).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
                source: ErrorSource::Sidecar,
            }),
        )
        .await;
        return;
    }

    info!(
        flight_id,
        rate = clamped,
        "data-channel set-zoom-rate applied"
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
                    source: ErrorSource::Sidecar,
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
            lifecycle: c.lifecycle,
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
        lifecycle: if cam.destroyed.load(Ordering::Acquire) {
            CameraLifecycle::Destroyed
        } else {
            CameraLifecycle::Active
        },
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

/// Per-slot RTCP drain. webrtc-rs's NACK/PLI pipelines stall silently if a
/// sender's RTCP isn't read, so every slot-track's sender must be drained for
/// the connection's lifetime regardless of which camera (if any) currently
/// feeds the slot. Exits when the sender closes (peer torn down).
///
/// REMB is deliberately NOT consumed here. In the slot model a slot's camera
/// changes on Subscribe/Unsubscribe, so a REMB keyed by the slot's (stable)
/// SSRC can't be pinned to one camera. That's acceptable *only* because
/// REMB-driven bitrate is currently disabled (it caused a reinit death spiral
/// — see `main.rs`). The frame-skip congestion follow-up must re-introduce
/// per-slot bandwidth attribution before it can act on REMB again.
async fn drain_rtcp_sink(sender: Arc<webrtc::rtp_transceiver::rtp_sender::RTCRtpSender>) {
    while sender.read_rtcp().await.is_ok() {
        // Packets (including REMB) intentionally discarded — see fn doc.
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Weak;
    use std::time::Duration;

    // Build a minimal RTCPeerConnection the same way KerbcamPeer::new does, so
    // the sender/track lifecycle matches production.
    async fn build_pc() -> Arc<RTCPeerConnection> {
        let mut media_engine = MediaEngine::default();
        media_engine
            .register_default_codecs()
            .expect("register default codecs");
        let api = APIBuilder::new().with_media_engine(media_engine).build();
        Arc::new(
            api.new_peer_connection(RTCConfiguration::default())
                .await
                .expect("create peer connection"),
        )
    }

    // F4 premise (deterministic, no real connection needed): `pc.close()` must
    // release the RTCRtpSender's strong ref to the track, so the camera's Weak
    // goes dead and the consume loop prunes it — dropping the subscriber count
    // to zero, which flushes `set_subscribed(false)` and lets the plugin stop
    // rendering. WITHOUT close() the sender holds the track Arc for the life of
    // the (already-dropped) peer, so the Weak never dies: that is the leak that
    // left cameras rendering after a clean disconnect (the reaper dropped the
    // Arc<KerbcamPeer> but never called close()).
    #[tokio::test]
    async fn close_releases_track_arc() {
        let pc = build_pc().await;
        let track = Arc::new(TrackLocalStaticSample::new(
            RTCRtpCodecCapability {
                mime_type: MIME_TYPE_H264.to_owned(),
                ..Default::default()
            },
            "video".to_owned(),
            "stream".to_owned(),
        ));
        pc.add_track(track.clone() as Arc<dyn TrackLocal + Send + Sync>)
            .await
            .expect("add_track");

        let weak: Weak<TrackLocalStaticSample> = Arc::downgrade(&track);
        drop(track); // release our ref; the sender inside pc still holds one

        assert!(
            weak.upgrade().is_some(),
            "while the peer is open the sender keeps the track alive — this is \
             exactly the leak when close() is never called on disconnect"
        );

        pc.close().await.expect("close");

        // close() winds sender tasks down asynchronously; poll briefly.
        let mut freed = false;
        for _ in 0..40 {
            if weak.upgrade().is_none() {
                freed = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
        assert!(
            freed,
            "pc.close() must release the sender's track Arc within ~2s — \
             this is the mechanism F4 relies on to free camera subscriptions"
        );
    }
}
