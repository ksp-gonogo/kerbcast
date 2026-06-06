//! kerbcam sidecar binary. Reads RGBA frames from the KSP plugin's
//! shared-memory ring, encodes them via the selected EncoderBackend,
//! fans the resulting NAL units out to every connected WebRTC peer.
//! Browsers connect via the HTTP signalling endpoint at `--http-bind`.
//!
//! Subscription-driven: when no peer is connected, the encoder idles and
//! we don't poll the ring beyond the heartbeat interval. The plugin keeps
//! writing frames into the ring; the sidecar just doesn't consume them.

use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::Result;
use clap::Parser;
use tokio::net::TcpListener;
use tokio::signal;
use tokio::sync::RwLock;
use tokio::time::sleep;
use tracing::{info, warn};
use webrtc::media::Sample;

use kerbcam_sidecar::cameras::{CameraRegistry, CameraState};
use kerbcam_sidecar::encoder::{select_backend, EncodeConfig, EncoderChoice, RawFrame, Software};
use kerbcam_sidecar::protocol::{
    AdaptiveShedPayload, CameraLifecycle, CameraState as ProtocolCameraState,
    CameraStateChangedPayload, ServerMessage,
};
use kerbcam_sidecar::shared_mem::MmapRingConfig;
use kerbcam_sidecar::signalling::{router, AppState};
use kerbcam_sidecar::webrtc::KerbcamPeer;
use kerbcam_sidecar::VERSION;

#[derive(Parser, Debug)]
#[command(name = "kerbcam-sidecar", version = VERSION, about)]
struct Cli {
    #[arg(long, value_enum, default_value_t = EncoderChoice::Auto)]
    encoder: EncoderChoice,

    /// Directory the KSP plugin drops per-camera ring files into.
    /// Matches the plugin's `KerbcamCore.ResolveRingDir` default —
    /// `$XDG_RUNTIME_DIR/kerbcam/` if that dir exists, else
    /// `/tmp/kerbcam/`. Override with `--shm-dir` when running
    /// against a relocated install or a recorded fixture. Each file
    /// inside is named `<flight_id>.ring`.
    #[arg(long, default_value_os_t = default_shm_dir())]
    shm_dir: PathBuf,

    #[arg(long, default_value_t = 1024)]
    max_width: u32,

    #[arg(long, default_value_t = 576)]
    max_height: u32,

    #[arg(long, default_value_t = 4)]
    slot_count: u32,

    #[arg(long, default_value_t = 30)]
    fps: u32,

    #[arg(long, default_value_t = 1_500_000)]
    bitrate_bps: u32,

    /// Polling interval (ms) for the consumer loop while at least one
    /// peer is subscribed. When no peer is subscribed the loop sleeps for
    /// `idle_interval_ms` instead.
    #[arg(long, default_value_t = 16)]
    poll_interval_ms: u64,

    /// Sleep interval (ms) when no peer is subscribed. Keeps CPU near
    /// zero while still checking the subscriber set often enough to wake
    /// up promptly when a browser connects.
    #[arg(long, default_value_t = 250)]
    idle_interval_ms: u64,

    /// HTTP bind address for the signalling endpoint. The bundled browser
    /// test page is served at `GET /`; POST `/offer` accepts SDP.
    #[arg(long, default_value = "127.0.0.1:8088")]
    http_bind: SocketAddr,
}

fn default_shm_dir() -> PathBuf {
    // Mirror the C# plugin's path-picking so a vanilla `cargo run` lines
    // up with a vanilla KSP launch on the same machine.
    if let Some(xdg) = std::env::var_os("XDG_RUNTIME_DIR") {
        let p = PathBuf::from(xdg);
        if p.is_dir() {
            return p.join("kerbcam");
        }
    }
    PathBuf::from("/tmp/kerbcam")
}

#[tokio::main(flavor = "multi_thread")]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
        )
        .with_target(false)
        .init();

    let cli = Cli::parse();
    info!(
        version = VERSION,
        shm_dir = %cli.shm_dir.display(),
        encoder = ?cli.encoder,
        http_bind = %cli.http_bind,
        "kerbcam sidecar starting",
    );

    let ring_cfg = MmapRingConfig {
        slot_count: cli.slot_count,
        max_width: cli.max_width,
        max_height: cli.max_height,
    };

    let registry = Arc::new(CameraRegistry::new(cli.shm_dir.clone(), ring_cfg));
    info!(
        dir = %cli.shm_dir.display(),
        slot_count = ring_cfg.slot_count,
        max_dims = format!("{}x{}", ring_cfg.max_width, ring_cfg.max_height),
        "camera registry initialised — scanning for rings",
    );

    let peers: Arc<RwLock<Vec<Arc<KerbcamPeer>>>> = Arc::new(RwLock::new(Vec::new()));
    let state = AppState {
        registry: registry.clone(),
        peers: peers.clone(),
        encoder_choice: cli.encoder,
        fps: cli.fps,
        bitrate_bps: cli.bitrate_bps,
    };

    let http_listener = TcpListener::bind(cli.http_bind).await.map_err(|e| {
        anyhow::anyhow!("binding HTTP signalling endpoint at {}: {e}", cli.http_bind)
    })?;
    let http_addr = http_listener.local_addr().unwrap_or(cli.http_bind);
    info!(addr = %http_addr, "HTTP signalling endpoint listening");
    let http_app = router(state);
    let http_server = tokio::spawn(async move {
        if let Err(e) = axum::serve(http_listener, http_app).await {
            warn!(error = %e, "HTTP server exited with error");
        }
    });

    let ping_peers = peers.clone();
    tokio::spawn(async move {
        let interval = Duration::from_secs(5);
        loop {
            sleep(interval).await;
            let snapshot: Vec<Arc<KerbcamPeer>> = ping_peers.read().await.clone();
            for peer in &snapshot {
                if peer.is_alive() {
                    peer.push_message(&ServerMessage::Ping).await;
                }
            }
        }
    });

    let consume = consume_loop(
        registry,
        peers,
        cli.encoder,
        Duration::from_millis(cli.poll_interval_ms),
        Duration::from_millis(cli.idle_interval_ms),
        Duration::from_secs(1), // rescan cadence
        cli.fps,
        cli.bitrate_bps,
    );

    tokio::select! {
        result = consume => result,
        _ = signal::ctrl_c() => {
            info!("ctrl-c received, shutting down");
            http_server.abort();
            Ok(())
        }
    }
}

/// Per-tick: rescan the rings dir periodically; for every camera with
/// at least one subscribed track, read its latest frame, lazy-init its
/// own encoder, and fan the encoded sample out to its alive tracks.
/// Idle-sleeps when no camera has subscribers.
#[allow(clippy::too_many_arguments)]
async fn consume_loop(
    registry: Arc<CameraRegistry>,
    peers: Arc<RwLock<Vec<Arc<KerbcamPeer>>>>,
    encoder_choice: EncoderChoice,
    poll_interval: Duration,
    idle_interval: Duration,
    rescan_interval: Duration,
    fps: u32,
    bitrate_bps: u32,
) -> Result<()> {
    let mut last_rescan = Instant::now() - rescan_interval; // rescan immediately
    let frame_duration = Duration::from_secs(1) / fps;

    loop {
        if last_rescan.elapsed() >= rescan_interval {
            let newly_destroyed = registry.rescan().await;
            // Status poll piggybacks on the rescan cadence — both are
            // ~1Hz and the plugin's status writer matches that rate.
            let delta = registry.poll_status().await;
            if delta.adaptive_shed.is_some() || !delta.changed_cameras.is_empty() {
                broadcast_status_delta(&peers, delta).await;
            }
            // Broadcast camera-state-changed for newly-destroyed cameras and
            // acknowledge (delete tombstone). "Clean close" is via the
            // data-channel notification rather than RTCP/track removal because
            // KerbcamPeer doesn't store per-camera RTCRtpSender refs; the
            // published client renders SIGNAL LOST on receipt and keeps the
            // last decoded frame visible through the HTML video element.
            if !newly_destroyed.is_empty() {
                broadcast_destroyed_cameras(&registry, &peers, &newly_destroyed).await;
                for flight_id in newly_destroyed {
                    registry.acknowledge_destruction(flight_id).await;
                }
            }
            last_rescan = Instant::now();
        }

        // Prune dead peers — dropped Arcs propagate to the Weak refs
        // in each CameraState.tracks list, which we GC inline below.
        // Before forgetting the peer, wipe its SetDegrade entries from
        // every camera it was subscribed to so the noisiest-consumer
        // max relaxes when a degrade-requesting peer leaves.
        let dropped_peers: Vec<Arc<KerbcamPeer>> = {
            let mut guard = peers.write().await;
            let mut dropped = Vec::new();
            guard.retain(|p| {
                if p.is_alive() {
                    true
                } else {
                    dropped.push(p.clone());
                    false
                }
            });
            dropped
        };
        for peer in dropped_peers {
            // Close the RTCPeerConnection so its senders release the slot
            // tracks. Dropping the Arc<KerbcamPeer> alone is NOT enough —
            // webrtc-rs keeps the sender tasks (and their track Arcs) alive
            // until close(), so without this the camera's Weak refs never die,
            // the subscriber count stays > 0, and the plugin keeps rendering
            // for a viewer that has already disconnected. (See
            // peer::tests::close_releases_track_arc.)
            if let Err(e) = peer.close().await {
                warn!(peer_id = peer.peer_id, error = %e, "peer close on reap failed");
            }
            for flight_id in &peer.subscribed {
                if let Some(cam) = registry.get(*flight_id).await {
                    cam.forget_degrade(peer.peer_id).await;
                }
            }
            // Deadman: a browser that dropped mid-hold must not leave a
            // camera drifting to its travel limit. Zero the persistent
            // pan/zoom rates on every camera this peer was driving — using
            // the live slot bindings (dynamic Subscribe keeps them current),
            // not the stale construction-time `subscribed` list.
            for flight_id in peer.bound_flight_ids().await {
                registry.zero_rates(flight_id).await;
            }
        }

        let cameras = registry.snapshot().await;
        let mut any_active = false;

        for cam in &cameras {
            // Destroyed cameras never encode — the part is gone.
            if cam.destroyed.load(Ordering::Acquire) {
                continue;
            }
            if cam.subscribers.load(Ordering::Acquire) == 0 {
                continue;
            }
            any_active = true;
            encode_and_fan_out(cam, encoder_choice, fps, bitrate_bps, frame_duration).await;
        }

        // After encode + GC of dead weak refs, any cam that just lost its
        // last subscriber needs `subscribed=false` flushed so the plugin sleeps
        // it — unless the force-render profiling override is on, in which case
        // every live camera is kept subscribed so it renders without a peer.
        registry.refresh_idle_subscriptions(&cameras).await;

        if any_active {
            sleep(poll_interval).await;
        } else {
            sleep(idle_interval).await;
        }
    }
}

/// Push a status delta (adaptive-shed level changes + per-camera state
/// changes) to every connected peer's data channel. Best-effort: peers
/// whose data channels haven't opened yet (or have closed) silently
/// drop the message — there's always another snapshot a tick later.
async fn broadcast_status_delta(
    peers: &Arc<RwLock<Vec<Arc<KerbcamPeer>>>>,
    delta: kerbcam_sidecar::cameras::StatusDelta,
) {
    let snapshot: Vec<Arc<KerbcamPeer>> = peers.read().await.clone();
    if snapshot.is_empty() {
        return;
    }

    if let Some((level, ksp_fps)) = delta.adaptive_shed {
        let reason = if level == 0 {
            "ksp-fps-recovered".to_string()
        } else {
            "ksp-fps-low".to_string()
        };
        let msg = ServerMessage::AdaptiveShed(AdaptiveShedPayload {
            level,
            ksp_fps,
            reason,
        });
        for peer in &snapshot {
            peer.push_message(&msg).await;
        }
    }

    for state in delta.changed_cameras {
        let msg = ServerMessage::CameraStateChanged(CameraStateChangedPayload { state });
        for peer in &snapshot {
            peer.push_message(&msg).await;
        }
    }
}

/// Broadcast `camera-state-changed` with `lifecycle: "destroyed"` for
/// each camera whose part was destroyed this tick. Sends to every
/// connected peer so their UI can render a "SIGNAL LOST" overlay.
async fn broadcast_destroyed_cameras(
    registry: &Arc<CameraRegistry>,
    peers: &Arc<RwLock<Vec<Arc<KerbcamPeer>>>>,
    flight_ids: &[u32],
) {
    let snapshot: Vec<Arc<KerbcamPeer>> = peers.read().await.clone();
    if snapshot.is_empty() {
        return;
    }
    for &flight_id in flight_ids {
        let Some(cam) = registry.get(flight_id).await else {
            continue;
        };
        let state = ProtocolCameraState {
            flight_id,
            lifecycle: CameraLifecycle::Destroyed,
            part_name: cam.part_name.clone(),
            part_title: cam.part_title.clone(),
            camera_name: cam.camera_name.clone(),
            vessel_name: cam.vessel_name.clone(),
            layers: Vec::new(),
            operator_layers: Vec::new(),
            render_width: 0,
            render_height: 0,
            operator_width: 0,
            operator_height: 0,
            supports_zoom: cam.supports_zoom,
            fov: 0.0,
            fov_min: cam.fov_min,
            fov_max: cam.fov_max,
            supports_pan: cam.supports_pan,
            pan_yaw: 0.0,
            pan_pitch: 0.0,
            pan_yaw_min: cam.pan_yaw_min,
            pan_yaw_max: cam.pan_yaw_max,
            pan_pitch_min: cam.pan_pitch_min,
            pan_pitch_max: cam.pan_pitch_max,
            encoder_bitrate_bps: 0,
            target_bitrate_bps: 0,
            degrade_level: 0.0,
        };
        let msg = ServerMessage::CameraStateChanged(CameraStateChangedPayload { state });
        for peer in &snapshot {
            peer.push_message(&msg).await;
        }
    }
}

async fn encode_and_fan_out(
    cam: &Arc<CameraState>,
    encoder_choice: EncoderChoice,
    fps: u32,
    bitrate_bps: u32,
    frame_duration: Duration,
) {
    // Throttle against configured fps. The plugin writes into the ring at
    // LateUpdate's cadence (40–60 Hz on the Deck), but the encoder is
    // configured for `fps` and each emitted sample carries duration=1/fps —
    // tagging samples wall-clock-too-fast makes the receiver drop about
    // half of them (`framesReceived` ≫ `framesDecoded`). Skipping encodes
    // until enough wall time has elapsed since the last one keeps the
    // encoder running at its configured rate and the receiver's playback
    // clock honest.
    // Wall-clock interval since the previous encode for this camera.
    // Drives both (a) the throttle (skip if it's too soon) and (b) the
    // sample duration we tag on the wire. The browser playback clock
    // advances by `sample.duration` per frame; if we always tag the
    // configured 1/fps but real encodes arrive every ~140ms (CPU-bound
    // openh264 across 6 cams), playback time falls behind arrival
    // monotonically — jitterBufferDelay grows from 0 to hundreds of
    // ms over a session, which presents visually as "framerate slowly
    // drops as time goes by". Tagging with the actual interval keeps
    // playback honest.
    let actual_duration: Duration;
    {
        let mut last_at = cam.last_encoded_at.lock().await;
        let now = Instant::now();
        actual_duration = match *last_at {
            Some(prev) => {
                let elapsed = now.duration_since(prev);
                if elapsed < frame_duration {
                    return;
                }
                elapsed
            }
            None => frame_duration, // first encode — no prior reference
        };
        *last_at = Some(now);
    }

    let frame = match cam.ring.latest() {
        Ok(Some(f)) => f,
        Ok(None) => return,
        Err(e) => {
            warn!(flight_id = cam.flight_id, error = %e, "ring read failed");
            return;
        }
    };

    let last = cam.last_sequence.load(Ordering::Acquire);
    if frame.sequence <= last {
        return;
    }
    cam.last_sequence.store(frame.sequence, Ordering::Release);

    // Apply SetDegrade. Two cheap levers:
    //
    //   * Frame skipping — at level>0 we deterministically drop a
    //     fraction of frames. Receiver sees timestamp gaps → P-frame
    //     recovery → stuttering. Saves encoder CPU on every skipped
    //     frame (the whole encode/packetise path is bypassed).
    //
    //   * Bitrate floor — feeds into the encoder reinit threshold
    //     below. The encoder runs at `(1 - 0.7*level) * effective`
    //     so level=1.0 lands ≈ 30% of the operator target. Heavy
    //     bitrate squeeze creates the blocky/macro look real
    //     signal loss has.
    let degrade = cam.current_degrade();
    if degrade > 0.001 {
        let skip_every = 1u64 + (degrade * 3.0).round() as u64; // 1..=4
        if !frame.sequence.is_multiple_of(skip_every) {
            return;
        }
    }

    // Lazy encoder init — first encoded frame per camera. Done under
    // the camera's encoder lock so concurrent ticks can't double-init.
    //
    // Close + reinit ONLY on frame dimension changes (plugin's adaptive
    // downscale path) — that genuinely requires a new encoder session
    // because openh264 picks up dims from the first YUV buffer it sees.
    //
    // We used to also reinit when REMB-driven target bitrate diverged
    // >30% from the encoder's current bitrate. That turned out to be a
    // catastrophic death spiral: every reinit emits a fresh IDR + new
    // SPS/PPS, decoder loses its reference chain, fires PLI, encoder
    // produces another IDR, REMB sees bursts and drops bitrate further,
    // which trips another reinit. Receivers ended up at ~0.2 fps
    // visible with `pliCount > 1000` and `bytesReceived` collapsed to
    // a few kbps out of a 1.5Mbps budget.
    //
    // Until we plumb a live-update path through the EncoderBackend
    // trait (openh264 0.9 exposes SBitrateInfo via raw_api.set_option
    // but no safe wrapper; libva supports live bitrate natively), we
    // simply ignore REMB feedback on the encode side and let the
    // initial bitrate stand. The wire bitrate stays predictable, the
    // decoder stays in sync, and the LAN can comfortably carry
    // 6 × 1.5Mbps anyway.
    let mut encoder_guard = cam.encoder.lock().await;
    let cur_w = cam.encoder_width.load(Ordering::Acquire);
    let cur_h = cam.encoder_height.load(Ordering::Acquire);
    // Always init at the CLI bitrate default — DO NOT consult REMB's
    // target_bitrate_bps here. REMB starts with a very low probe
    // estimate (often <100kbps) and ramps up, so consulting it at
    // cold start means the encoder boots at probe-speed and stays
    // there forever (we don't re-init on bitrate change any more).
    // REMB feedback is still recorded for diagnostics but ignored
    // by the encode path until a real live-update lands.
    let effective_bps_pre_degrade = bitrate_bps;
    // Apply degrade as a multiplicative bitrate squeeze. degrade=1.0
    // lands the encoder around 30% of the otherwise-target — enough
    // to produce visible macroblocking. Floor at 64kbps so we never
    // try to init the encoder at an absurdly low rate. Only takes
    // effect at first init (no live-update yet).
    let effective_bps = if degrade > 0.001 {
        let factor = 1.0 - 0.7 * degrade.clamp(0.0, 1.0);
        ((effective_bps_pre_degrade as f32) * factor).max(64_000.0) as u32
    } else {
        effective_bps_pre_degrade
    };
    let dims_changed = encoder_guard.is_some() && (cur_w != frame.width || cur_h != frame.height);
    if dims_changed {
        if let Some(mut backend) = encoder_guard.take() {
            backend.close();
        }
        info!(
            flight_id = cam.flight_id,
            from = format!("{cur_w}x{cur_h}"),
            to = format!("{}x{}", frame.width, frame.height),
            "resolution change, encoder reinit",
        );
    }
    if encoder_guard.is_none() {
        let cfg = EncodeConfig {
            width: frame.width,
            height: frame.height,
            fps,
            bitrate_bps: effective_bps,
        };
        let mut backend = select_backend(encoder_choice);
        if let Err(e) = backend.init(cfg.clone()) {
            // Hardware backend failed (e.g. VAAPI probe passed but
            // avcodec_open2 rejected the resolution or profile). Fall back
            // to software immediately rather than retrying the same failing
            // backend every frame.
            warn!(flight_id = cam.flight_id, error = %e, "encoder init failed; falling back to software");
            backend = Box::new(Software::new());
            if let Err(e2) = backend.init(cfg) {
                warn!(flight_id = cam.flight_id, error = %e2, "software fallback init failed");
                return;
            }
        }
        info!(
            flight_id = cam.flight_id,
            width = frame.width,
            height = frame.height,
            bitrate_bps = effective_bps,
            backend = backend.name(),
            "per-camera encoder initialised",
        );
        cam.encoder_width.store(frame.width, Ordering::Release);
        cam.encoder_height.store(frame.height, Ordering::Release);
        cam.encoder_bitrate.store(effective_bps, Ordering::Release);
        *encoder_guard = Some(backend);
    }
    let encoder = encoder_guard.as_mut().unwrap();

    let nals = match encoder.encode(&RawFrame {
        width: frame.width,
        height: frame.height,
        data: &frame.pixels,
        capture_ts_ms: frame.capture_ts_ms,
    }) {
        Ok(n) => n,
        Err(e) => {
            warn!(flight_id = cam.flight_id, error = %e, "encode failed");
            return;
        }
    };
    drop(encoder_guard);

    if nals.is_empty() {
        return;
    }

    // Concatenate NALs into the Annex-B bytestream webrtc-rs expects.
    let total_len: usize = nals.iter().map(|n| n.0.len()).sum();
    let mut combined = Vec::with_capacity(total_len);
    for nal in &nals {
        combined.extend_from_slice(&nal.0);
    }
    let sample = Sample {
        data: combined.into(),
        duration: actual_duration,
        ..Default::default()
    };

    // Fan-out + GC dead weak refs in one pass. Pruned weaks mean a peer
    // dropped without telling us — decrement the camera's subscriber
    // count so the encoder can idle once the last viewer leaves.
    let mut tracks = cam.tracks.write().await;
    let prev = std::mem::take(&mut *tracks);
    let mut alive = Vec::with_capacity(prev.len());
    let mut pruned = 0usize;
    for weak in prev {
        if let Some(track) = weak.upgrade() {
            if let Err(e) = track.write_sample(&sample).await {
                warn!(flight_id = cam.flight_id, error = %e, "write_sample failed");
            }
            alive.push(weak);
        } else {
            pruned += 1;
        }
    }
    *tracks = alive;
    drop(tracks);
    if pruned > 0 {
        cam.release(pruned);
    }
}
