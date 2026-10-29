//! kerbcast sidecar binary. Reads RGBA frames from the KSP plugin's
//! shared-memory ring, encodes them via the selected EncoderBackend,
//! fans the resulting NAL units out to every connected WebRTC peer.
//! Browsers connect via the HTTP signalling endpoint at `--http-bind`.
//!
//! Subscription-driven: when no peer is connected, the encoder idles and
//! we don't poll the ring beyond the heartbeat interval. The plugin keeps
//! writing frames into the ring; the sidecar just doesn't consume them.

use std::collections::HashSet;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime};

use anyhow::Result;
use clap::Parser;
use tokio::net::TcpListener;
use tokio::signal;
use tokio::sync::RwLock;
use tokio::time::sleep;
use tracing::{debug, info, warn};

use kerbcast_sidecar::cameras::{CameraRegistry, CameraState};
use kerbcast_sidecar::encoder::{
    record_init_failure, resolve_bitrate_bps, select_backend, EncodeConfig, EncoderBackend,
    EncoderChoice, RawFrame, SessionVerdict, Software, SILENT_SESSION_FRAME_LIMIT,
};
use kerbcast_sidecar::frame_watch::{sleep_until_ready, FRAME_WATCH_STEP};
use kerbcast_sidecar::heartbeat::{HeartbeatWatch, HEARTBEAT_FILE};
use kerbcast_sidecar::protocol::{
    AdaptiveShedPayload, CameraLifecycle, CameraState as ProtocolCameraState,
    CameraStateChangedPayload, CaptureState, SceneStateChangedPayload, ServerMessage,
};
use kerbcast_sidecar::shared_mem::MmapRingConfig;
use kerbcast_sidecar::signalling::{router, AppState};
use kerbcast_sidecar::webrtc::video_track::CaptureTime;
use kerbcast_sidecar::webrtc::{resync_after_camera_churn, KerbcastPeer};
use kerbcast_sidecar::VERSION;

/// Consecutive encode() failures before the consume loop drops a
/// camera's encoder to force re-initialisation. ~1s of frames at 30fps:
/// transient hiccups don't churn the session, a wedged one recovers
/// quickly.
const ENCODE_FAILURE_REINIT_THRESHOLD: u32 = 30;

/// How often `--telemetry` logs each camera's send counts, matching the
/// plugin's "capture over" lines.
const SEND_REPORT_INTERVAL: Duration = Duration::from_secs(5);

#[derive(Parser, Debug)]
#[command(name = "kerbcast-sidecar", version = VERSION, about)]
struct Cli {
    #[arg(long, value_enum, default_value_t = EncoderChoice::Auto)]
    encoder: EncoderChoice,

    /// Directory the KSP plugin drops per-camera ring files into.
    /// Matches the plugin's `KerbcastCore.ResolveRingDir` default —
    /// `$XDG_RUNTIME_DIR/kerbcast/` if that dir exists, else
    /// `/tmp/kerbcast/`. Override with `--shm-dir` when running
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

    /// Target encode bitrate in bits per second. When omitted, the default
    /// derives from the selected encoder backend: hardware backends
    /// (libva/videotoolbox/nvenc) get 4 Mbps, the software fallback stays
    /// at 1.5 Mbps.
    #[arg(long)]
    bitrate_bps: Option<u32>,

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

    /// Orphan protection: the KSP plugin touches `global.heartbeat` in the
    /// shm dir ~1Hz for the whole game session. Once that file has been
    /// seen at least once, the sidecar exits after its mtime stays stale
    /// for longer than this many seconds on two consecutive checks
    /// (covering a Deck suspend/resume race). Dev workflows without the
    /// plugin never write the file, so the watch never arms. 0 disables.
    /// Generous default: a long scene load blocks Unity's Update (and so
    /// the heartbeat) for its whole duration.
    #[arg(long, default_value_t = 90)]
    heartbeat_timeout_secs: u64,
}

/**
 * Whether to log, per camera every few seconds, how many frames the ring
 * advanced and why any of them were not sent. The plugin sets
 * `KERBCAST_TELEMETRY=1` when settings.cfg sets EnableTelemetry. An
 * environment variable rather than a flag, because an older sidecar exits on
 * a flag it does not know and a newer plugin would then stream nothing.
 */
fn telemetry_enabled() -> bool {
    std::env::var_os("KERBCAST_TELEMETRY").is_some_and(|v| v == "1")
}

fn default_shm_dir() -> PathBuf {
    // Mirror the C# plugin's path-picking so a vanilla `cargo run` lines
    // up with a vanilla KSP launch on the same machine.
    if let Some(xdg) = std::env::var_os("XDG_RUNTIME_DIR") {
        let p = PathBuf::from(xdg);
        if p.is_dir() {
            return p.join("kerbcast");
        }
    }
    PathBuf::from("/tmp/kerbcast")
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
        "kerbcast sidecar starting",
    );

    // Resolve the effective bitrate once, here: AppState and the consume
    // loop both take the resolved value, so nothing downstream needs to
    // know whether the operator passed --bitrate-bps or not.
    let backend_for_default = select_backend(cli.encoder);
    let bitrate_bps = resolve_bitrate_bps(cli.bitrate_bps, backend_for_default.as_ref());
    if cli.bitrate_bps.is_some() {
        info!(bitrate_bps, "bitrate set explicitly via --bitrate-bps");
    } else {
        info!(
            bitrate_bps,
            backend = backend_for_default.name(),
            hardware = backend_for_default.is_hardware(),
            "no --bitrate-bps flag; default chosen from the encoder backend",
        );
    }
    drop(backend_for_default);

    let ring_cfg = MmapRingConfig {
        slot_count: cli.slot_count,
        max_width: cli.max_width,
        max_height: cli.max_height,
    };

    let registry = Arc::new(CameraRegistry::new(cli.shm_dir.clone()));
    info!(
        dir = %cli.shm_dir.display(),
        slot_count = ring_cfg.slot_count,
        max_dims = format!("{}x{}", ring_cfg.max_width, ring_cfg.max_height),
        "camera registry initialised — scanning for rings (per-ring geometry)",
    );
    registry.reset_anchors().await;

    let peers: Arc<RwLock<Vec<Arc<KerbcastPeer>>>> = Arc::new(RwLock::new(Vec::new()));
    let state = AppState {
        registry: registry.clone(),
        peers: peers.clone(),
        encoder_choice: cli.encoder,
        fps: cli.fps,
        bitrate_bps,
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
    let ping_task = tokio::spawn(async move {
        let interval = Duration::from_secs(5);
        loop {
            sleep(interval).await;
            let snapshot: Vec<Arc<KerbcastPeer>> = ping_peers.read().await.clone();
            for peer in &snapshot {
                if peer.is_alive() {
                    peer.push_message(&ServerMessage::Ping);
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
        bitrate_bps,
        telemetry_enabled(),
    );

    // Orphan watch: the plugin owns this process for the KSP session and
    // kills it on a clean game exit; if KSP dies hard nothing does, so we
    // self-exit once the plugin's heartbeat file goes stale. Pure decision
    // logic (and the arming/streak rules) live in heartbeat::HeartbeatWatch.
    let heartbeat_path = cli.shm_dir.join(HEARTBEAT_FILE);
    let heartbeat_timeout = cli.heartbeat_timeout_secs;
    let orphan_watch = async move {
        if heartbeat_timeout == 0 {
            std::future::pending::<()>().await;
        }
        let mut watch = HeartbeatWatch::new(Duration::from_secs(heartbeat_timeout));
        loop {
            sleep(Duration::from_secs(5)).await;
            let mtime = tokio::fs::metadata(&heartbeat_path)
                .await
                .ok()
                .and_then(|m| m.modified().ok());
            if watch.observe(mtime, SystemTime::now()) {
                info!(
                    timeout_secs = heartbeat_timeout,
                    path = %heartbeat_path.display(),
                    "plugin heartbeat stale; assuming KSP exited without stopping us, shutting down",
                );
                break;
            }
        }
    };

    // SIGTERM is what the plugin's process kill, systemd and container
    // runtimes send first — without a handler the runtime never unwinds
    // and the supervisor escalates to SIGKILL after its timeout. No-op
    // pending future on non-unix (Windows gets ctrl_c only).
    #[cfg(unix)]
    let terminate = async {
        match signal::unix::signal(signal::unix::SignalKind::terminate()) {
            Ok(mut sig) => {
                sig.recv().await;
            }
            Err(e) => {
                warn!(error = %e, "SIGTERM handler unavailable");
                std::future::pending::<()>().await;
            }
        }
    };
    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();

    let result = tokio::select! {
        result = consume => result,
        _ = signal::ctrl_c() => {
            info!("ctrl-c received, shutting down");
            Ok(())
        }
        _ = terminate => {
            info!("SIGTERM received, shutting down");
            Ok(())
        }
        _ = orphan_watch => {
            Ok(())
        }
    };
    // Detached loops would otherwise keep the runtime alive past the
    // shutdown signal.
    http_server.abort();
    ping_task.abort();
    result
}

/// Per-tick: rescan the rings dir periodically; for every camera with
/// at least one subscribed track, read its latest frame, lazy-init its
/// own encoder, and fan the encoded sample out to its alive tracks.
/// Idle-sleeps when no camera has subscribers.
#[allow(clippy::too_many_arguments)]
async fn consume_loop(
    registry: Arc<CameraRegistry>,
    peers: Arc<RwLock<Vec<Arc<KerbcastPeer>>>>,
    encoder_choice: EncoderChoice,
    poll_interval: Duration,
    idle_interval: Duration,
    rescan_interval: Duration,
    fps: u32,
    bitrate_bps: u32,
    telemetry: bool,
) -> Result<()> {
    let mut last_rescan = Instant::now() - rescan_interval; // rescan immediately
    let mut last_send_report = Instant::now();
    let frame_duration = Duration::from_secs(1) / fps;
    // Cameras currently skipped for lack of subscribers, so the skip is
    // logged on the transition rather than on every tick.
    let mut idle_cameras: HashSet<u32> = HashSet::new();

    loop {
        if last_rescan.elapsed() >= rescan_interval {
            let churn = registry.rescan().await;
            // The sidecar persists across KSP scene changes, so ring churn
            // (every camera removed on flight exit, re-attached on the next
            // flight) happens under live peers: rebind surviving slot
            // subscriptions to the re-attached cameras and push a fresh
            // camera snapshot so browser lists drain and repopulate.
            if !churn.attached.is_empty() || !churn.removed.is_empty() {
                let snapshot: Vec<Arc<KerbcastPeer>> = peers.read().await.clone();
                resync_after_camera_churn(&registry, &snapshot, &churn.attached).await;
            }
            let newly_destroyed = churn.newly_destroyed;
            // Status poll piggybacks on the rescan cadence — both are
            // ~1Hz and the plugin's status writer matches that rate.
            let delta = registry.poll_status().await;
            /* Background capture ceiling moved (operator change, or the plugin shedding /
            restoring the background rung), so every camera's requested
            capture rate has to be recomputed and pushed back down. Done
            before the broadcast so a peer's next state read is consistent
            with what the plugin was just told. */
            if delta.background_ceiling_changed {
                registry.reflush_all_capture_rates().await;
            }
            if delta.adaptive_shed.is_some()
                || !delta.changed_cameras.is_empty()
                || delta.settings.is_some()
            {
                broadcast_status_delta(&peers, delta).await;
            }
            // Scene state lives in a separate host-written file (outlives the
            // flight-only status writer). Broadcast only when the flag flips.
            let mut left_flight = false;
            if let Some(in_flight) = registry.poll_in_flight().await {
                left_flight = !in_flight;
                let snapshot: Vec<Arc<KerbcastPeer>> = peers.read().await.clone();
                /* Leaving flight ends every tombstone's SIGNAL LOST window, even
                when the departing craft had no live rings left to churn. */
                if !in_flight && !registry.prune_tombstones().await.is_empty() {
                    resync_after_camera_churn(&registry, &snapshot, &[]).await;
                }
                let msg = ServerMessage::SceneStateChanged(SceneStateChangedPayload { in_flight });
                broadcast(&snapshot, &msg).await;
            }
            // Broadcast camera-state-changed for newly-destroyed cameras and
            // acknowledge (delete tombstone). "Clean close" is via the
            // data-channel notification rather than RTCP/track removal because
            // KerbcastPeer doesn't store per-camera RTCRtpSender refs; the
            // published client renders SIGNAL LOST on receipt and keeps the
            // last decoded frame visible through the HTML video element.
            if !newly_destroyed.is_empty() {
                broadcast_destroyed_cameras(&registry, &peers, &newly_destroyed).await;
                for flight_id in newly_destroyed {
                    registry.acknowledge_destruction(flight_id).await;
                }
                /* A tombstone first seen in the tick that left flight was kept by
                the prune above so its notification could go out; it is
                acknowledged now, so drop it too. */
                if left_flight && !registry.prune_tombstones().await.is_empty() {
                    let snapshot: Vec<Arc<KerbcastPeer>> = peers.read().await.clone();
                    resync_after_camera_churn(&registry, &snapshot, &[]).await;
                }
            }
            last_rescan = Instant::now();
        }

        // Sidecar-side state changes (viewer quality requests) marked dirty
        // by the data-channel handlers. Broadcast the authoritative state to
        // EVERY peer each tick so all UIs converge on the last write;
        // unlike the 1Hz status diff above, this also fires when the
        // effective render dims didn't move (e.g. a preset set while the
        // adaptive controller already holds the camera lower).
        let dirty = registry.take_dirty_cameras().await;
        if !dirty.is_empty() {
            let snapshot: Vec<Arc<KerbcastPeer>> = peers.read().await.clone();
            for flight_id in dirty {
                let Some(state) = registry.protocol_state(flight_id).await else {
                    continue;
                };
                let msg = ServerMessage::CameraStateChanged(CameraStateChangedPayload { state });
                broadcast(&snapshot, &msg).await;
            }
        }

        // Prune dead peers — dropped Arcs propagate to the Weak refs
        // in each CameraState.tracks list, which we GC inline below.
        // Each dropped peer also gets its per-consumer state cleared so no
        // stale entry pins a camera high (pan/zoom rates zeroed; reported
        // display size forgotten peer-wide).
        let dropped_peers: Vec<Arc<KerbcastPeer>> = {
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
            // tracks. Dropping the Arc<KerbcastPeer> alone is NOT enough —
            // webrtc-rs keeps the sender tasks (and their track Arcs) alive
            // until close(), so without this the camera's Weak refs never die,
            // the subscriber count stays > 0, and the plugin keeps rendering
            // for a viewer that has already disconnected. (See
            // peer::tests::close_releases_track_arc.)
            if let Err(e) = peer.close().await {
                warn!(peer_id = peer.peer_id, error = %e, "peer close on reap failed");
            }
            // Release this peer's cameras IMMEDIATELY (decrement subscribers +
            // flush set_subscribed(false) at zero), the same way the graceful
            // Disconnect message does. Don't wait for the consume loop's lazy
            // dead-Weak prune — that only runs on a tick where the camera has a
            // NEW frame to encode, which capture staggering can starve, leaving
            // a disconnected viewer's cameras rendering indefinitely (fps never
            // recovers until a KSP restart).
            // Snapshot what this peer was driving BEFORE release_all: it
            // take()s every slot.bound, so reading bound_flight_ids() after
            // it would see nothing and the deadman below would silently no-op.
            let driven_flight_ids = peer.bound_flight_ids().await;
            peer.release_all(&registry).await;
            // Deadman: a browser that dropped mid-hold must not leave a
            // camera drifting to its travel limit. Zero the persistent
            // pan/zoom rates on every camera this peer was driving.
            for flight_id in driven_flight_ids {
                registry.zero_rates(flight_id).await;
            }
            // Peer-scoped display-size clear: forget this peer's reported size
            // across EVERY camera, not just the ones it bound — a report can
            // exist for a camera it never bound (report + slot-exhausted
            // Subscribe), which a binding-scoped clear would leak forever
            // (the v1.6.3 pin-high class).
            registry.forget_display_size_all(peer.peer_id).await;
            // Same peer-scoped sweep for any force and size hold this peer
            // held, so a feed never stays pinned after a dead-peer reap. A
            // client that may reconnect keeps them for a short grace instead.
            registry.depart_peer(peer.peer_id, Instant::now()).await;
            // Same for background capture request: a dead peer must not leave background
            // capture pinned on (the v1.6.3 pin-high class).
            if registry.forget_background_request(peer.peer_id).await {
                registry.reflush_all_capture_rates().await;
            }
            // And its anchors, on every camera it set them on.
            registry.forget_anchors_all(peer.peer_id).await;
        }
        registry.expire_departed(Instant::now()).await;

        let cameras = registry.snapshot().await;
        let mut any_active = false;
        let mut watched: Vec<(Arc<CameraState>, u64)> = Vec::new();

        for cam in &cameras {
            // Destroyed cameras never encode — the part is gone.
            if cam.destroyed.load(Ordering::Acquire) {
                continue;
            }
            let idle = cam.subscribers.load(Ordering::Acquire) == 0;
            if idle_changed(&mut idle_cameras, cam.flight_id, idle) {
                if idle {
                    debug!(flight_id = cam.flight_id, "no subscribers, not encoding");
                } else {
                    debug!(flight_id = cam.flight_id, "subscriber present");
                    cam.rebase_after_idle();
                }
            }
            if idle {
                continue;
            }
            any_active = true;
            let capped =
                encode_and_fan_out(cam, encoder_choice, fps, bitrate_bps, frame_duration).await;
            /* Capped, the camera left frames unread, so it is due again at once. */
            let seen = if capped {
                cam.last_sequence.load(Ordering::Acquire)
            } else {
                cam.ring.published_sequence()
            };
            watched.push((cam.clone(), seen));
        }

        if telemetry && last_send_report.elapsed() >= SEND_REPORT_INTERVAL {
            let over = last_send_report.elapsed();
            for cam in &cameras {
                let counts = cam.send_tally.take();
                if counts.advanced > 0 {
                    info!("{}", counts.report(cam.flight_id, over));
                }
            }
            last_send_report = Instant::now();
        }

        idle_cameras.retain(|id| {
            cameras
                .iter()
                .any(|c| c.flight_id == *id && !c.destroyed.load(Ordering::Acquire))
        });

        // After encode + GC of dead weak refs, any cam that just lost its
        // last subscriber needs `subscribed=false` flushed so the plugin sleeps
        // it — unless the force-render profiling override is on, in which case
        // every live camera is kept subscribed so it renders without a peer.
        registry.refresh_idle_subscriptions(&cameras).await;

        if any_active {
            /* Against the frame each ring had published just after its own
            camera's turn, so a frame written while later cameras encoded wakes
            the loop at once, and a ring stuck on a frame it cannot send never
            does. Published, not claimed: the plugin claims a frame before it
            copies it in, and a wake at the claim finds nothing to read. */
            sleep_until_ready(poll_interval, FRAME_WATCH_STEP, || {
                watched
                    .iter()
                    .any(|(cam, seen)| cam.ring.published_sequence() > *seen)
            })
            .await;
        } else {
            sleep(idle_interval).await;
        }
    }
}

/// Record a camera's idle state and report whether it differs from the last
/// tick, so a steady state is logged once instead of every tick.
fn idle_changed(idle_cameras: &mut HashSet<u32>, flight_id: u32, idle: bool) -> bool {
    if idle {
        idle_cameras.insert(flight_id)
    } else {
        idle_cameras.remove(&flight_id)
    }
}

/// Fan one message out to an already-snapshotted set of peers. Best-effort:
/// peers whose data channels aren't open silently drop it. Empty set is a
/// no-op. Callers snapshot `peers` once and may call this several times.
async fn broadcast(peers: &[Arc<KerbcastPeer>], msg: &ServerMessage) {
    if peers.is_empty() {
        return;
    }
    for peer in peers {
        peer.push_message(msg);
    }
}

/// Push a status delta (adaptive-shed level changes + per-camera state
/// changes) to every connected peer's data channel. Best-effort: peers
/// whose data channels haven't opened yet (or have closed) silently
/// drop the message — there's always another snapshot a tick later.
async fn broadcast_status_delta(
    peers: &Arc<RwLock<Vec<Arc<KerbcastPeer>>>>,
    delta: kerbcast_sidecar::cameras::StatusDelta,
) {
    let snapshot: Vec<Arc<KerbcastPeer>> = peers.read().await.clone();
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
        broadcast(&snapshot, &msg).await;
    }

    for state in delta.changed_cameras {
        let msg = ServerMessage::CameraStateChanged(CameraStateChangedPayload { state });
        broadcast(&snapshot, &msg).await;
    }

    if let Some(settings) = delta.settings {
        let msg = ServerMessage::SettingsState(settings);
        broadcast(&snapshot, &msg).await;
    }
}

/// Broadcast `camera-state-changed` with `lifecycle: "destroyed"` for
/// each camera whose part was destroyed this tick. Sends to every
/// connected peer so their UI can render a "SIGNAL LOST" overlay.
async fn broadcast_destroyed_cameras(
    registry: &Arc<CameraRegistry>,
    peers: &Arc<RwLock<Vec<Arc<KerbcastPeer>>>>,
    flight_ids: &[u32],
) {
    let snapshot: Vec<Arc<KerbcastPeer>> = peers.read().await.clone();
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
            // Destroyed: nothing is being captured, whatever subscribers linger.
            capture_state: CaptureState::Off,
            kind: cam.kind,
            kerbal_persistent_id: cam.kerbal_persistent_id,
            command_centre: cam.command_centre.clone(),
            crew_location: cam.crew_location(),
            track_mode: cam.track_mode(),
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
            max_width: Some(cam.max_width),
            max_height: Some(cam.max_height),
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
            viewer_quality: None,
            quality_limited_by: None,
            // Nothing is captured or sent for a destroyed camera.
            ..Default::default()
        };
        let msg = ServerMessage::CameraStateChanged(CameraStateChangedPayload { state });
        broadcast(&snapshot, &msg).await;
    }
}

/// Close and forget a camera's encoder session so the next frame
/// re-runs init, and reset the per-session counters that describe it.
fn drop_encoder_session(
    cam: &Arc<CameraState>,
    encoder_guard: &mut tokio::sync::MutexGuard<'_, Option<Box<dyn EncoderBackend>>>,
) {
    if let Some(mut backend) = encoder_guard.take() {
        backend.close();
    }
    cam.encoder_width.store(0, Ordering::Release);
    cam.encoder_height.store(0, Ordering::Release);
    cam.encoder_bitrate.store(0, Ordering::Release);
    cam.encode_failure_streak.store(0, Ordering::Release);
}

/// The one operator-facing line when a camera gives up on hardware
/// encode. Fired exactly once per camera, gated by the
/// EscalateToSoftware verdict.
fn warn_escalated(cam: &Arc<CameraState>, backend_name: &str, what_happened: &str) {
    warn!(
        flight_id = cam.flight_id,
        camera = %cam.camera_name,
        part = %cam.part_title,
        backend = backend_name,
        "{what_happened}; this camera now encodes in software \
         (likely concurrent hardware encode session limit). Other cameras keep hardware."
    );
}

/// Whether a frame read from the ring is sent.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum FrameTiming {
    Skip,
    Send,
}

/* Decides a frame from when it was captured against the last frame actually
sent. The plugin already paces capture (MaxCaptureFps), so this is only a rate
guard: it skips a frame captured less than half a frame interval after the
last one, which still lets an irregular capture (a 40 ms median with 0 to 200
ms spread was measured on the Deck) through while stopping a writer that
captures faster than `fps` from feeding the encoder at more than twice its
configured rate. Capture time, not send time, because frames the sidecar
catches up on leave the ring a moment apart however far apart they were
captured. A writer whose clock did not advance falls back to when the frames
were picked up. */
fn frame_timing(
    last_sent: Option<CaptureTime>,
    this: CaptureTime,
    frame_duration: Duration,
) -> FrameTiming {
    let Some(prev) = last_sent else {
        return FrameTiming::Send;
    };
    let game_clock_ms = this.game_clock_ms - prev.game_clock_ms;
    let elapsed = if game_clock_ms.is_finite() && game_clock_ms > 0.0 {
        Duration::from_secs_f64(game_clock_ms / 1000.0)
    } else {
        this.picked_up.saturating_duration_since(prev.picked_up)
    };
    if elapsed < frame_duration / 2 {
        FrameTiming::Skip
    } else {
        FrameTiming::Send
    }
}

/**
 * Unread frames a camera may owe before the sidecar skips ahead. The plugin
 * keeps at most two readbacks in flight per camera and drains every one that
 * has landed in the same game frame, so two frames can reach the ring a
 * microsecond apart. Reading only the newest lost the first of each such pair:
 * on the Deck a camera read just after the loop woke sent 18 of 28 captured
 * frames a second while the camera read 7 ms later sent 27. A backlog past
 * two is the encoder falling behind, where the newest frames keep the feed live.
 */
const MAX_FRAMES_BEHIND: u64 = 2;

/// Send every frame the camera has not sent yet, oldest first. Returns
/// whether it stopped at `MAX_FRAMES_BEHIND` rather than running out of frames.
async fn encode_and_fan_out(
    cam: &Arc<CameraState>,
    encoder_choice: EncoderChoice,
    fps: u32,
    bitrate_bps: u32,
    frame_duration: Duration,
) -> bool {
    let mut taken = 0;
    while taken < MAX_FRAMES_BEHIND
        && encode_next_frame(cam, encoder_choice, fps, bitrate_bps, frame_duration).await
    {
        taken += 1;
    }
    if taken > 1 {
        cam.send_tally.note_caught_up();
    }
    taken == MAX_FRAMES_BEHIND
}

/// Take the camera's next unread frame from the ring and send it. Returns
/// whether a frame was taken, so the caller can look for another.
async fn encode_next_frame(
    cam: &Arc<CameraState>,
    encoder_choice: EncoderChoice,
    fps: u32,
    bitrate_bps: u32,
    frame_duration: Duration,
) -> bool {
    /* Frames dropped below leave `last_sent` alone, so the rate guard measures
    from the last frame actually sent. Each track spaces its RTP timestamps by
    the frames' capture times. */
    let now = Instant::now();
    let mut last = cam.last_sequence.load(Ordering::Acquire);
    let claimed = cam.ring.latest_sequence();
    if claimed == last {
        return false;
    }
    /* Defensive: the plugin replaces a ring with a new file, which attaches
    as a new entry, but a ring ever rewritten in place would restart its
    sequence from one under this entry. Nothing it holds has been sent, so it
    is read as if just attached. */
    if claimed < last {
        info!(
            flight_id = cam.flight_id,
            ring_sequence = claimed,
            last_sequence = last,
            "ring restarted its sequence, reading it afresh"
        );
        last = 0;
        cam.last_sequence.store(0, Ordering::Release);
    }
    /* The first frame since attach takes the newest frame; anything else
    takes the oldest unread one. */
    let after = if last == 0 {
        None
    } else {
        Some(last.max(claimed.saturating_sub(MAX_FRAMES_BEHIND)))
    };
    let read = match after {
        Some(after) => cam.ring.next_after(after),
        None => cam.ring.latest(),
    };
    let frame = match read {
        Ok(Some(f)) => f,
        Ok(None) => {
            debug!(flight_id = cam.flight_id, "ring had no frame this tick");
            return false;
        }
        Err(e) => {
            warn!(flight_id = cam.flight_id, error = %e, "ring read failed");
            return false;
        }
    };

    if frame.sequence <= last {
        debug!(
            flight_id = cam.flight_id,
            sequence = frame.sequence,
            "no new frame this tick (sequence unchanged)"
        );
        return false;
    }
    debug!(
        flight_id = cam.flight_id,
        sequence = frame.sequence,
        advanced_by = frame.sequence - last,
        "new frame accepted for encode"
    );
    cam.last_sequence.store(frame.sequence, Ordering::Release);
    cam.note_ring_advance(last, frame.sequence);
    if let Some(after) = after {
        cam.send_tally
            .note_read(after - last, frame.sequence - after - 1);
    }

    let capture_time = CaptureTime {
        game_clock_ms: frame.capture_ts_ms,
        picked_up: now,
    };
    if frame_timing(*cam.last_sent.lock().await, capture_time, frame_duration) == FrameTiming::Skip
    {
        cam.send_tally.note_rate_guard();
        return true;
    }

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
            cam.send_tally.note_degrade();
            return true;
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
        // The session-health machine picks the backend: the configured
        // choice normally, the software fallback once this camera has
        // been escalated (consecutive silent/failed hardware sessions).
        let mut backend = cam.session_health.lock().unwrap().select(encoder_choice);
        if let Err(e) = backend.init(cfg.clone()) {
            // Hardware backend failed (e.g. VAAPI probe passed but
            // avcodec_open2 rejected the resolution or profile). Fall back
            // to software immediately rather than retrying the same failing
            // backend every frame. Also record the failure against this
            // camera's session health: some drivers (e.g. Media Foundation
            // on certain D3D devices) fail hardware init on every single
            // resolution reinit, and without this the camera would retry
            // the doomed hardware path on every adaptive-quality resize
            // for the rest of the session instead of pinning to software
            // after a couple of strikes.
            let was_hardware = backend.is_hardware();
            let failed_backend_name = backend.name();
            warn!(flight_id = cam.flight_id, error = %e, backend = failed_backend_name, "encoder init failed; falling back to software");
            let verdict =
                record_init_failure(&mut cam.session_health.lock().unwrap(), was_hardware);
            if verdict == Some(SessionVerdict::EscalateToSoftware) {
                warn_escalated(
                    cam,
                    failed_backend_name,
                    "hardware init failed on consecutive reinits",
                );
            }
            backend = Box::new(Software::new());
            if let Err(e2) = backend.init(cfg) {
                warn!(flight_id = cam.flight_id, error = %e2, "software fallback init failed");
                return true;
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
    let backend_name = encoder.name();
    let backend_is_hardware = encoder.is_hardware();

    /* Consume any pending promotion keyframe request before encoding, so the
    IDR rides the very next frame rather than waiting out the periodic GOP. */
    if cam.take_keyframe_request() {
        encoder.request_keyframe();
    }

    let encode_started = Instant::now();
    let encoded = encoder.encode(&RawFrame {
        width: frame.width,
        height: frame.height,
        data: &frame.pixels,
        capture_ts_ms: frame.capture_ts_ms,
    });
    cam.send_tally.note_encode(encode_started.elapsed());
    let nals = match encoded {
        Ok(n) => {
            cam.encode_failure_streak.store(0, Ordering::Release);
            if n.iter().any(|nal| nal.is_keyframe()) {
                cam.note_keyframe_sent();
            }
            /* A promotion is complete once a keyframe actually goes out — that
            is the moment the feed becomes renderable, so it's what flips the
            published state from Ramping to Full. Keyed on a real IDR in the
            output rather than on time elapsed, so a slow ramp reports
            honestly instead of claiming Full while still undecodable. */
            if cam.ramping.load(Ordering::Acquire) && n.iter().any(|nal| nal.is_keyframe()) {
                cam.finish_ramp();
            }
            n
        }
        Err(e) => {
            // A persistently-failing encoder session (wedged VAAPI
            // context, driver reset) never heals by retrying encode();
            // peers would stall forever on an open track. Drop it after
            // a sustained streak so the next frame re-runs init. The
            // session-health strike means a backend whose fresh sessions
            // keep failing too escalates to software instead of cycling
            // the same broken hardware forever.
            let streak = cam.encode_failure_streak.fetch_add(1, Ordering::AcqRel) + 1;
            warn!(flight_id = cam.flight_id, error = %e, streak, "encode failed");
            if streak >= ENCODE_FAILURE_REINIT_THRESHOLD {
                drop_encoder_session(cam, &mut encoder_guard);
                warn!(
                    flight_id = cam.flight_id,
                    backend = backend_name,
                    "encoder dropped after {streak} consecutive failures; will reinit on next frame"
                );
                let verdict = cam.session_health.lock().unwrap().note_session_error();
                if verdict == SessionVerdict::EscalateToSoftware {
                    warn_escalated(cam, backend_name, "consecutive sessions kept failing");
                }
            }
            cam.send_tally.note_encode_failed();
            return true;
        }
    };

    if nals.is_empty() {
        // Zero NALs from one encode() call is legal (encoder still
        // buffering), but a hardware session that stays silent for
        // SILENT_SESSION_FRAME_LIMIT straight calls is the GPU
        // concurrent-session-limit signature: it inits cleanly, accepts
        // every frame, errors never, emits nothing. Field case: AMD RX
        // 9070 XT via Media Foundation, 13 cameras, the last-added few
        // streamed black with zero log evidence. Strike the session;
        // enough strikes pin this camera to the software encoder.
        if backend_is_hardware {
            let verdict = cam.session_health.lock().unwrap().note_silent_frame();
            if verdict != SessionVerdict::Continue {
                drop_encoder_session(cam, &mut encoder_guard);
                warn!(
                    flight_id = cam.flight_id,
                    camera = %cam.camera_name,
                    backend = backend_name,
                    frames = SILENT_SESSION_FRAME_LIMIT,
                    "hardware encode session accepted frames but never produced output; dropping it"
                );
                if verdict == SessionVerdict::EscalateToSoftware {
                    warn_escalated(cam, backend_name, "sessions init fine but stay silent");
                }
            }
        }
        cam.send_tally.note_no_output();
        return true;
    }
    // Only a hardware session producing real output clears strikes. A
    // software encode succeeding here (the fallback path after a hardware
    // init failure) says nothing about hardware health, so it must not
    // wipe out the strike record_init_failure just logged above — otherwise
    // a camera whose hardware init fails on every resolution reinit would
    // strike, immediately get cleared by the successful fallback encode,
    // and never reach SESSION_STRIKE_LIMIT.
    if backend_is_hardware {
        cam.session_health.lock().unwrap().note_output();
    }
    drop(encoder_guard);

    // Concatenate NALs into the Annex-B bytestream webrtc-rs expects.
    let keyframe = nals.iter().any(|nal| nal.is_keyframe());
    let total_len: usize = nals.iter().map(|n| n.0.len()).sum();
    let mut combined = Vec::with_capacity(total_len);
    for nal in &nals {
        combined.extend_from_slice(&nal.0);
    }

    // Fan-out + GC dead weak refs in one pass. Pruned weaks mean a peer
    // dropped without telling us — decrement the camera's subscriber
    // count so the encoder can idle once the last viewer leaves.
    *cam.last_sent.lock().await = Some(capture_time);
    let mut tracks = cam.tracks.write().await;
    let prev = std::mem::take(&mut *tracks);
    let mut alive = Vec::with_capacity(prev.len());
    let mut pruned = 0usize;
    let mut sent = 0usize;
    for weak in prev {
        if let Some(track) = weak.upgrade() {
            if let Err(e) = track
                .write_frame(
                    &combined,
                    capture_time,
                    keyframe,
                    cam.flight_id,
                    &frame.capture,
                )
                .await
            {
                warn!(flight_id = cam.flight_id, error = %e, "write_frame failed");
            } else {
                sent += 1;
            }
            alive.push(weak);
        } else {
            pruned += 1;
        }
    }
    /* Subscribers>0 but zero live tracks means the subscriber count and the
    weak-ref list have diverged: we believe someone is watching, so we keep
    encoding, but there is nobody to send to. Silent black feed. */
    if sent == 0 {
        warn!(
            flight_id = cam.flight_id,
            pruned,
            subscribers = cam.subscribers.load(Ordering::Acquire),
            "encoded a frame but sent it to NOBODY (no live tracks)"
        );
    } else {
        cam.note_frame_sent();
        debug!(
            flight_id = cam.flight_id,
            sent,
            pruned,
            bytes = combined.len(),
            "sample fanned out"
        );
    }
    *tracks = alive;
    drop(tracks);
    if pruned > 0 {
        cam.release(pruned);
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use kerbcast_sidecar::encoder::{Nvenc, Software};

    /// Only a frame captured less than half an interval after the last sent
    /// one is skipped, however soon after it the sidecar picks it up.
    #[test]
    fn frame_timing_guards_on_capture_time() {
        let fd = Duration::from_secs(1) / 30;
        let start = Instant::now();
        let at = |capture_ms: f64, picked_up_ms: u64| CaptureTime {
            game_clock_ms: capture_ms,
            picked_up: start + Duration::from_millis(picked_up_ms),
        };
        assert_eq!(frame_timing(None, at(0.0, 0), fd), FrameTiming::Send);
        assert_eq!(
            frame_timing(Some(at(0.0, 0)), at(10.0, 40), fd),
            FrameTiming::Skip,
            "captured 10 ms after the last send"
        );
        assert_eq!(
            frame_timing(Some(at(0.0, 40)), at(25.0, 40), fd),
            FrameTiming::Send,
            "caught up in the same tick but captured a game frame later"
        );
        assert_eq!(
            frame_timing(Some(at(5.0, 0)), at(5.0, 10), fd),
            FrameTiming::Skip,
            "a clock that did not advance falls back to pickup time"
        );
        assert_eq!(
            frame_timing(Some(at(5.0, 0)), at(5.0, 40), fd),
            FrameTiming::Send
        );
        assert_eq!(
            frame_timing(Some(at(500.0, 0)), at(100.0, 10), fd),
            FrameTiming::Skip,
            "a clock that went backwards falls back to pickup time too"
        );
        assert_eq!(
            frame_timing(Some(at(500.0, 0)), at(100.0, 40), fd),
            FrameTiming::Send
        );
    }

    async fn camera_with_viewer(
        dir: &std::path::Path,
    ) -> (
        kerbcast_sidecar::shared_mem::MmapFrameRing,
        Arc<CameraState>,
        Arc<kerbcast_sidecar::webrtc::video_track::VideoTrack>,
    ) {
        use kerbcast_sidecar::shared_mem::MmapFrameRing;
        use kerbcast_sidecar::webrtc::video_track::VideoTrack;
        use webrtc::api::media_engine::MIME_TYPE_H264;
        use webrtc::rtp_transceiver::rtp_codec::RTCRtpCodecCapability;

        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        let ring = MmapFrameRing::create(&dir.join("1.ring"), cfg).expect("create ring");
        let registry = CameraRegistry::new(dir.to_path_buf());
        registry.rescan().await;
        let cam = registry.get(1).await.expect("camera attached from ring");
        let track = Arc::new(VideoTrack::new(
            RTCRtpCodecCapability {
                mime_type: MIME_TYPE_H264.to_owned(),
                ..Default::default()
            },
            "video".to_owned(),
            "stream".to_owned(),
            None,
        ));
        cam.add_track(track.clone()).await;
        (ring, cam, track)
    }

    async fn tick(cam: &Arc<CameraState>) {
        encode_and_fan_out(
            cam,
            EncoderChoice::Software,
            30,
            500_000,
            Duration::from_secs(1) / 30,
        )
        .await;
    }

    /// Two readbacks landing in one game frame reach the ring a moment apart.
    /// The next tick sends both, oldest first, rather than only the newest.
    #[tokio::test]
    async fn a_pair_written_between_ticks_is_sent_whole() {
        let dir = tempfile::tempdir().expect("tempdir");
        let (mut ring, cam, _track) = camera_with_viewer(dir.path()).await;
        let pixels = vec![0x80u8; 64 * 64 * 4];

        ring.produce(64, 64, 0.0, &pixels).unwrap();
        tick(&cam).await;
        assert_eq!(cam.send_tally.take().sent, 1, "the first frame");

        ring.produce(64, 64, 33.0, &pixels).unwrap();
        ring.produce(64, 64, 66.0, &pixels).unwrap();
        tick(&cam).await;
        let counts = cam.send_tally.take();
        assert_eq!(counts.advanced, 2);
        assert_eq!(counts.sent, 2, "both frames of the pair");
        assert_eq!((counts.overwritten, counts.behind), (0, 0));
        assert_eq!(counts.caught_up, 1);
        assert_eq!(cam.last_sequence.load(Ordering::Acquire), 3);

        tick(&cam).await;
        assert_eq!(cam.send_tally.take().sent, 0, "nothing new");
    }

    /// A camera nobody watched was not read. When a viewer returns it starts
    /// from the first frame captured after, sending none of what the ring
    /// held from before the gap and counting none of it as owed.
    #[tokio::test]
    async fn resubscribing_starts_from_the_next_captured_frame() {
        let dir = tempfile::tempdir().expect("tempdir");
        let (mut ring, cam, _track) = camera_with_viewer(dir.path()).await;
        let pixels = vec![0x80u8; 64 * 64 * 4];

        ring.produce(64, 64, 0.0, &pixels).unwrap();
        tick(&cam).await;
        cam.send_tally.take();

        for n in 1..=6 {
            ring.produce(64, 64, 33.0 * n as f64, &pixels).unwrap();
        }
        cam.rebase_after_idle();
        tick(&cam).await;
        let counts = cam.send_tally.take();
        assert_eq!(
            (counts.sent, counts.advanced),
            (0, 0),
            "nothing from before the gap"
        );

        ring.produce(64, 64, 9_000.0, &pixels).unwrap();
        tick(&cam).await;
        let counts = cam.send_tally.take();
        assert_eq!((counts.sent, counts.behind, counts.overwritten), (1, 0, 0));
        assert_eq!(cam.last_sequence.load(Ordering::Acquire), 8);
        assert_eq!(
            cam.last_sent.lock().await.map(|t| t.game_clock_ms),
            Some(9_000.0),
            "the first frame sent is the fresh one"
        );
    }

    /// A ring recreated in place keeps its file, so the camera keeps its entry,
    /// but its sequence starts again from one. Its frames still go out.
    /// Unix only: Windows refuses to truncate a file another handle has mapped.
    #[cfg(unix)]
    #[tokio::test]
    async fn a_ring_recreated_in_place_keeps_sending() {
        use kerbcast_sidecar::shared_mem::MmapFrameRing;

        let dir = tempfile::tempdir().expect("tempdir");
        let (mut ring, cam, _track) = camera_with_viewer(dir.path()).await;
        let pixels = vec![0x80u8; 64 * 64 * 4];
        for n in 0..5 {
            ring.produce(64, 64, 33.0 * n as f64, &pixels).unwrap();
            tick(&cam).await;
        }
        cam.send_tally.take();
        assert_eq!(cam.last_sequence.load(Ordering::Acquire), 5);

        drop(ring);
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        let mut ring = MmapFrameRing::create(&dir.path().join("1.ring"), cfg).unwrap();
        ring.produce(64, 64, 60_000.0, &pixels).unwrap();
        tick(&cam).await;
        assert_eq!(cam.send_tally.take().sent, 1, "the restarted ring's frame");
        assert_eq!(cam.last_sequence.load(Ordering::Acquire), 1);

        ring.produce(64, 64, 60_033.0, &pixels).unwrap();
        tick(&cam).await;
        assert_eq!(cam.send_tally.take().sent, 1, "and the one after");
        assert_eq!(cam.last_sequence.load(Ordering::Acquire), 2);
    }

    /// Past the catch-up allowance the oldest frames are passed over, so a
    /// camera whose encoder fell behind stays live, and a frame captured too
    /// soon after the last one sent is still skipped.
    #[tokio::test]
    async fn a_long_backlog_skips_to_the_newest_two() {
        let dir = tempfile::tempdir().expect("tempdir");
        let (mut ring, cam, _track) = camera_with_viewer(dir.path()).await;
        let pixels = vec![0x80u8; 64 * 64 * 4];

        ring.produce(64, 64, 0.0, &pixels).unwrap();
        tick(&cam).await;
        cam.send_tally.take();

        for n in 1..=4 {
            ring.produce(64, 64, 33.0 * n as f64, &pixels).unwrap();
        }
        tick(&cam).await;
        let counts = cam.send_tally.take();
        assert_eq!((counts.advanced, counts.sent, counts.behind), (4, 2, 2));

        ring.produce(64, 64, 33.0 * 4.0 + 5.0, &pixels).unwrap();
        tick(&cam).await;
        let counts = cam.send_tally.take();
        assert_eq!((counts.sent, counts.rate_guard), (0, 1));
    }

    #[test]
    fn idle_state_is_reported_only_on_change() {
        let mut idle = HashSet::new();
        assert!(idle_changed(&mut idle, 7, true), "first idle tick logs");
        assert!(!idle_changed(&mut idle, 7, true), "steady idle stays quiet");
        assert!(!idle_changed(&mut idle, 7, true));
        assert!(idle_changed(&mut idle, 7, false), "resuming logs once");
        assert!(
            !idle_changed(&mut idle, 7, false),
            "steady active stays quiet"
        );
        assert!(idle_changed(&mut idle, 7, true), "going idle again logs");
        assert!(
            idle_changed(&mut idle, 8, true),
            "cameras are tracked apart"
        );
    }

    #[test]
    fn bitrate_flag_absent_is_distinguishable_from_explicit() {
        // No clap default: an omitted flag must parse as None so the
        // backend-aware default can kick in downstream.
        let absent = Cli::try_parse_from(["kerbcast-sidecar"]).unwrap();
        assert_eq!(absent.bitrate_bps, None);

        let explicit =
            Cli::try_parse_from(["kerbcast-sidecar", "--bitrate-bps", "1500000"]).unwrap();
        assert_eq!(explicit.bitrate_bps, Some(1_500_000));
    }

    #[test]
    fn explicit_flag_wins_regardless_of_backend_class() {
        let cli = Cli::try_parse_from(["kerbcast-sidecar", "--bitrate-bps", "2000000"]).unwrap();
        assert_eq!(
            resolve_bitrate_bps(cli.bitrate_bps, &Nvenc::new()),
            2_000_000
        );
        assert_eq!(
            resolve_bitrate_bps(cli.bitrate_bps, &Software::new()),
            2_000_000
        );
    }
}
