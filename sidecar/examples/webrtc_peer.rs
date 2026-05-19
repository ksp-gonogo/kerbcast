//! Dev-only WebRTC peer test. Encodes a moving gradient through the
//! Software backend and pushes the NAL units to a single browser via a
//! manually-pasted SDP exchange.
//!
//! Usage:
//!
//!   1. cargo run --example webrtc_peer --release
//!   2. The binary prints "PASTE THIS OFFER IN THE BROWSER:" followed by
//!      a base64-encoded SDP blob.
//!   3. In the browser test page, paste the offer, click "Generate Answer",
//!      copy the answer back into the terminal.
//!   4. The peer connects, and a moving green-channel-counter video starts
//!      streaming to the browser's <video> element.
//!
//! Companion HTML test page: see kerbcam/sidecar/examples/webrtc_peer.html
//! (generated alongside).

use std::io::{self, BufRead, Write};
use std::time::Duration;

use anyhow::{Context, Result};
use base64::{engine::general_purpose, Engine as _};
use tokio::time::{interval, sleep};
use tracing::{info, warn};

use kerbcam_sidecar::encoder::{EncodeConfig, EncoderBackend, RawFrame, Software};
use kerbcam_sidecar::webrtc::KerbcamPeer;

const WIDTH: u32 = 320;
const HEIGHT: u32 = 240;
const FPS: u32 = 30;

#[tokio::main(flavor = "multi_thread")]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
        )
        .with_target(false)
        .init();

    info!(
        width = WIDTH,
        height = HEIGHT,
        fps = FPS,
        "webrtc_peer dev binary starting"
    );

    let peer = KerbcamPeer::new().await.context("peer setup")?;
    let mut encoder = Software::new();
    encoder
        .init(EncodeConfig {
            width: WIDTH,
            height: HEIGHT,
            fps: FPS,
            bitrate_bps: 1_000_000,
        })
        .context("encoder init")?;

    let offer_sdp = peer.create_offer().await.context("create offer")?;
    let offer_b64 = general_purpose::STANDARD.encode(offer_sdp.as_bytes());

    println!();
    println!("================ PASTE THIS OFFER IN THE BROWSER ================");
    println!("{offer_b64}");
    println!("=================================================================");
    println!();
    println!("Then paste the browser's answer (base64) on one line and press enter:");
    io::stdout().flush().ok();

    let stdin = io::stdin();
    let answer_b64 = stdin
        .lock()
        .lines()
        .next()
        .ok_or_else(|| anyhow::anyhow!("stdin closed before answer"))??;
    let answer_sdp = String::from_utf8(
        general_purpose::STANDARD
            .decode(answer_b64.trim())
            .context("decode base64 answer")?,
    )
    .context("answer is not utf-8")?;
    peer.set_remote_answer(answer_sdp)
        .await
        .context("set remote answer")?;

    info!("answer applied, waiting for peer to reach Connected");
    peer.wait_connected().await;
    info!("peer connected — starting frame loop");

    let mut frame_n: u32 = 0;
    let frame_duration = Duration::from_secs(1) / FPS;
    let mut ticker = interval(frame_duration);
    loop {
        ticker.tick().await;
        let pixels = synthetic_rgba(WIDTH, HEIGHT, frame_n);
        let nals = match encoder.encode(&RawFrame {
            width: WIDTH,
            height: HEIGHT,
            data: &pixels,
            capture_ts_ms: (frame_n as f64) * (1000.0 / FPS as f64),
        }) {
            Ok(n) => n,
            Err(e) => {
                warn!(error = %e, "encode failed, skipping frame");
                continue;
            }
        };
        if !nals.is_empty() {
            if let Err(e) = peer.send_h264_nals(&nals, frame_duration).await {
                warn!(error = %e, "send_h264_nals failed");
            }
        }
        frame_n = frame_n.wrapping_add(1);
        if frame_n.is_multiple_of(FPS * 5) {
            info!(frame_n, "still streaming");
        }
        // Tiny pause helps when terminal is busy with the SDP exchange print
        if frame_n == 1 {
            sleep(Duration::from_millis(100)).await;
        }
    }
}

fn synthetic_rgba(width: u32, height: u32, frame_n: u32) -> Vec<u8> {
    let mut data = Vec::with_capacity((width * height * 4) as usize);
    let seed = (frame_n & 0xFF) as u8;
    for y in 0..height {
        for x in 0..width {
            data.push(((x * 255 / width) & 0xFF) as u8);
            data.push(((y * 255 / height) & 0xFF) as u8);
            data.push(seed);
            data.push(0xFF);
        }
    }
    data
}
