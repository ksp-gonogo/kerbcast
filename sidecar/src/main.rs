//! kerbcam sidecar binary entry. Parses CLI args, initialises logging, opens
//! the shared-memory ring written by the KSP plugin, encodes each frame
//! through the selected EncoderBackend, and logs throughput stats.
//!
//! v0.1 scaffold: encoding terminates at logging the NAL byte rate. WebRTC
//! transport + control-channel handling come online in subsequent spikes.

use std::path::PathBuf;
use std::time::{Duration, Instant};

use anyhow::{Context, Result};
use clap::{Parser, ValueEnum};
use tokio::signal;
use tokio::time::sleep;
use tracing::{info, warn};

use kerbcam_sidecar::encoder::{self, EncodeConfig, EncoderBackend, RawFrame};
use kerbcam_sidecar::shared_mem::{MmapFrameRing, MmapRingConfig};
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
    /// Encoder backend to use. `auto` enumerates capabilities at startup.
    #[arg(long, value_enum, default_value_t = EncoderChoice::Auto)]
    encoder: EncoderChoice,

    /// Path to the shared-memory ring file the KSP plugin writes to.
    /// Plugin and sidecar must agree on this path.
    #[arg(long, default_value = "/tmp/kerbcam-frames")]
    shm_path: PathBuf,

    /// Max width per slot in the ring. Must match what the plugin writes.
    #[arg(long, default_value_t = 1280)]
    max_width: u32,

    /// Max height per slot in the ring.
    #[arg(long, default_value_t = 720)]
    max_height: u32,

    /// Number of frame slots in the ring.
    #[arg(long, default_value_t = 4)]
    slot_count: u32,

    /// Target framerate the encoder is configured for. Used for bitrate
    /// budgeting; the actual rate is determined by how fast the plugin
    /// produces frames.
    #[arg(long, default_value_t = 30)]
    fps: u32,

    /// Target encoder bitrate, bits per second.
    #[arg(long, default_value_t = 1_500_000)]
    bitrate_bps: u32,

    /// Polling interval (ms) for the consumer loop. The ring is read at
    /// most this often; if frames arrive faster they're dropped on the
    /// reader side (we always read .latest, not a queue).
    #[arg(long, default_value_t = 16)]
    poll_interval_ms: u64,
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
        "kerbcam sidecar starting",
    );

    let backend = select_backend(cli.encoder);
    info!(backend = backend.name(), "encoder backend selected");

    // Wait for the plugin to create the ring file before we open it. The
    // plugin runs as part of KSP, which may take longer to start than the
    // sidecar. Don't fail outright — the user-facing experience is "kerbcam
    // is patient and connects when KSP is ready".
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

    let consume = consume_loop(
        ring,
        backend,
        Duration::from_millis(cli.poll_interval_ms),
        cli.fps,
        cli.bitrate_bps,
    );

    tokio::select! {
        result = consume => result,
        _ = signal::ctrl_c() => {
            info!("ctrl-c received, shutting down");
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
    poll_interval: Duration,
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

    loop {
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
            // Same frame we already encoded — wait for a new one.
            sleep(poll_interval).await;
            continue;
        }

        // Detect dropped frames so we report them in stats. With a 4-slot
        // ring and a slow consumer we can lap easily; the operator should
        // see this surfaced.
        if last_sequence > 0 {
            let expected = last_sequence + 1;
            if frame.sequence > expected {
                frames_dropped += frame.sequence - expected;
            }
        }
        last_sequence = frame.sequence;

        // Lazy init the encoder on first frame so it sees real dimensions.
        // The CLI doesn't get them up front — the plugin's RT shape decides.
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

        match backend.encode(&RawFrame {
            width: frame.width,
            height: frame.height,
            data: &frame.pixels,
            capture_ts_ms: frame.capture_ts_ms,
        }) {
            Ok(nals) => {
                frames_seen += 1;
                nals_emitted += nals.len() as u64;
                for nal in &nals {
                    nal_bytes += nal.0.len() as u64;
                }
            }
            Err(e) => {
                warn!(error = %e, "encode failed on frame {}", frame.sequence);
            }
        }

        if last_stats_at.elapsed() >= stats_interval {
            let secs = last_stats_at.elapsed().as_secs_f64();
            let fps_in = frames_seen as f64 / secs;
            let bps_out = (nal_bytes as f64 * 8.0) / secs;
            info!(
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
