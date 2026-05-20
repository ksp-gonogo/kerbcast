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
use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::{Context, Result};
use clap::{Parser, ValueEnum};
use tokio::net::TcpListener;
use tokio::signal;
use tokio::sync::RwLock;
use tokio::time::sleep;
use tracing::{info, warn};

use kerbcam_sidecar::encoder::{self, EncodeConfig, EncoderBackend, RawFrame};
use kerbcam_sidecar::shared_mem::{MmapFrameRing, MmapRingConfig};
use kerbcam_sidecar::signalling::{router, AppState};
use kerbcam_sidecar::webrtc::KerbcamPeer;
use kerbcam_sidecar::VERSION;

#[derive(Copy, Clone, Debug, ValueEnum)]
enum EncoderChoice {
    Auto,
    Libva,
    Videotoolbox,
    Nvenc,
    Software,
}

#[derive(Parser, Debug)]
#[command(name = "kerbcam-sidecar", version = VERSION, about)]
struct Cli {
    #[arg(long, value_enum, default_value_t = EncoderChoice::Auto)]
    encoder: EncoderChoice,

    /// Path to the shared-memory ring file the KSP plugin writes to.
    /// Matches the plugin's `KerbcamCore.ResolveRingPath` default —
    /// `$XDG_RUNTIME_DIR/kerbcam.ring` if that dir exists, else
    /// `/tmp/kerbcam.ring`. Override with `--shm-path` when running
    /// against a relocated install or a recorded fixture.
    #[arg(long, default_value_os_t = default_shm_path())]
    shm_path: PathBuf,

    #[arg(long, default_value_t = 768)]
    max_width: u32,

    #[arg(long, default_value_t = 768)]
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

fn default_shm_path() -> PathBuf {
    // Mirror the C# plugin's path-picking so a vanilla `cargo run` lines
    // up with a vanilla KSP launch on the same machine.
    if let Some(xdg) = std::env::var_os("XDG_RUNTIME_DIR") {
        let p = PathBuf::from(xdg);
        if p.is_dir() {
            return p.join("kerbcam.ring");
        }
    }
    PathBuf::from("/tmp/kerbcam.ring")
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
        shm_path = %cli.shm_path.display(),
        encoder = ?cli.encoder,
        http_bind = %cli.http_bind,
        "kerbcam sidecar starting",
    );

    let backend = select_backend(cli.encoder);
    info!(backend = backend.name(), "encoder backend selected");

    let ring_cfg = MmapRingConfig {
        slot_count: cli.slot_count,
        max_width: cli.max_width,
        max_height: cli.max_height,
    };
    let ring = wait_for_ring(&cli.shm_path, ring_cfg).await?;
    info!(
        path = %cli.shm_path.display(),
        slot_count = ring_cfg.slot_count,
        max_dims = format!("{}x{}", ring_cfg.max_width, ring_cfg.max_height),
        "ring attached",
    );

    let state = AppState {
        peers: Arc::new(RwLock::new(Vec::new())),
    };
    let peers = state.peers.clone();

    let http_listener = TcpListener::bind(cli.http_bind)
        .await
        .with_context(|| format!("binding HTTP signalling endpoint at {}", cli.http_bind))?;
    let http_addr = http_listener.local_addr().unwrap_or(cli.http_bind);
    info!(addr = %http_addr, "HTTP signalling endpoint listening");
    let http_app = router(state);
    let http_server = tokio::spawn(async move {
        if let Err(e) = axum::serve(http_listener, http_app).await {
            warn!(error = %e, "HTTP server exited with error");
        }
    });

    let consume = consume_loop(
        ring,
        backend,
        peers,
        Duration::from_millis(cli.poll_interval_ms),
        Duration::from_millis(cli.idle_interval_ms),
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

fn select_backend(choice: EncoderChoice) -> Box<dyn EncoderBackend> {
    match choice {
        EncoderChoice::Auto => encoder::auto_select(),
        EncoderChoice::Libva => Box::new(encoder::Libva::new()),
        EncoderChoice::Videotoolbox => Box::new(encoder::VideoToolbox::new()),
        EncoderChoice::Nvenc => Box::new(encoder::Nvenc::new()),
        EncoderChoice::Software => Box::new(encoder::Software::new()),
    }
}

async fn wait_for_ring(path: &std::path::Path, cfg: MmapRingConfig) -> Result<MmapFrameRing> {
    let mut last_log = Instant::now();
    loop {
        if path.exists() {
            match MmapFrameRing::open(path, cfg) {
                Ok(ring) => return Ok(ring),
                Err(e) => {
                    warn!(error = %e, "ring file exists but open failed, retrying");
                }
            }
        }
        if last_log.elapsed() > Duration::from_secs(10) {
            info!(path = %path.display(), "waiting for plugin to create ring file");
            last_log = Instant::now();
        }
        sleep(Duration::from_millis(500)).await;
    }
}

async fn consume_loop(
    ring: MmapFrameRing,
    mut backend: Box<dyn EncoderBackend>,
    peers: Arc<RwLock<Vec<Arc<KerbcamPeer>>>>,
    poll_interval: Duration,
    idle_interval: Duration,
    fps: u32,
    bitrate_bps: u32,
) -> Result<()> {
    let mut encoder_initialised = false;
    let mut last_sequence: u64 = 0;
    let mut frames_seen: u64 = 0;
    let mut frames_dropped: u64 = 0;
    let mut nals_emitted: u64 = 0;
    let mut nal_bytes: u64 = 0;
    let mut last_stats_at = Instant::now();
    let stats_interval = Duration::from_secs(5);
    let frame_duration = Duration::from_secs(1) / fps;

    loop {
        // Subscription-driven: prune dead peers, idle if nobody's watching.
        let active_peers = {
            let mut guard = peers.write().await;
            guard.retain(|p| p.is_alive());
            guard.clone()
        };
        if active_peers.is_empty() {
            sleep(idle_interval).await;
            continue;
        }

        let frame = match ring.latest() {
            Ok(Some(frame)) => frame,
            Ok(None) => {
                sleep(poll_interval).await;
                continue;
            }
            Err(e) => {
                warn!(error = %e, "ring read failed, retrying");
                sleep(poll_interval).await;
                continue;
            }
        };

        if frame.sequence == last_sequence {
            sleep(poll_interval).await;
            continue;
        }

        if last_sequence > 0 {
            let expected = last_sequence + 1;
            if frame.sequence > expected {
                frames_dropped += frame.sequence - expected;
            }
        }
        last_sequence = frame.sequence;

        if !encoder_initialised {
            backend
                .init(EncodeConfig {
                    width: frame.width,
                    height: frame.height,
                    fps,
                    bitrate_bps,
                })
                .context("encoder init")?;
            info!(
                width = frame.width,
                height = frame.height,
                fps,
                bitrate_bps,
                "encoder initialised on first frame"
            );
            encoder_initialised = true;
        }

        let nals = match backend.encode(&RawFrame {
            width: frame.width,
            height: frame.height,
            data: &frame.pixels,
            capture_ts_ms: frame.capture_ts_ms,
        }) {
            Ok(n) => n,
            Err(e) => {
                warn!(error = %e, "encode failed on frame {}", frame.sequence);
                continue;
            }
        };

        frames_seen += 1;
        nals_emitted += nals.len() as u64;
        for nal in &nals {
            nal_bytes += nal.0.len() as u64;
        }

        // Fan-out: every alive peer gets the same NAL set. write_sample is
        // backpressure-aware inside webrtc-rs (it'll skip if the SCTP buffer
        // is wedged), so we don't need to worry about a slow consumer
        // blocking the encoder loop.
        if !nals.is_empty() {
            for peer in &active_peers {
                if let Err(e) = peer.send_h264_nals(&nals, frame_duration).await {
                    warn!(error = %e, "send_h264_nals failed; peer will be pruned next tick");
                }
            }
        }

        if last_stats_at.elapsed() >= stats_interval {
            let secs = last_stats_at.elapsed().as_secs_f64();
            let fps_in = frames_seen as f64 / secs;
            let bps_out = (nal_bytes as f64 * 8.0) / secs;
            info!(
                peer_count = active_peers.len(),
                frames_seen,
                fps_in = format!("{fps_in:.1}"),
                frames_dropped,
                nals_emitted,
                bps_out = format!("{:.2} Mbps", bps_out / 1_000_000.0),
                "stats"
            );
            frames_seen = 0;
            frames_dropped = 0;
            nals_emitted = 0;
            nal_bytes = 0;
            last_stats_at = Instant::now();
        }
    }
}
