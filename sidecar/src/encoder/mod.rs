//! Encoder backend trait + factory + stub implementations per OS tier.
//!
//! See the gonogo repo's `local_docs/ocisly_state_and_rebuild.md` §2.3.
//! Tier-1 is libva (Steam Deck Linux). Tier-2 is VideoToolbox (macOS) and
//! NVENC (Windows/NVIDIA). Software fallback (OpenH264 / x264) ships always
//! to guarantee any host can encode at reduced quality.

use clap::ValueEnum;
use thiserror::Error;

mod libva;
mod nvenc;
mod software;
mod videotoolbox;

pub use libva::Libva;
pub use nvenc::Nvenc;
pub use software::Software;
pub use videotoolbox::VideoToolbox;

/// CLI/registry-friendly enum of backends. `Auto` picks the best
/// available at construction time.
#[derive(Copy, Clone, Debug, ValueEnum)]
pub enum EncoderChoice {
    Auto,
    Libva,
    Videotoolbox,
    Nvenc,
    Software,
}

/// Construct a fresh backend for an `EncoderChoice`. One call per
/// per-camera encoder session — each camera gets its own backend
/// instance with its own SPS/PPS + keyframe state.
pub fn select_backend(choice: EncoderChoice) -> Box<dyn EncoderBackend> {
    match choice {
        EncoderChoice::Auto => auto_select(),
        EncoderChoice::Libva => Box::new(Libva::new()),
        EncoderChoice::Videotoolbox => Box::new(VideoToolbox::new()),
        EncoderChoice::Nvenc => Box::new(Nvenc::new()),
        EncoderChoice::Software => Box::new(Software::new()),
    }
}

/// A single encoded NAL unit. The exact slicing is encoder-specific; consumers
/// (the WebRTC packetiser) should treat each Nal as one bytestring to feed
/// into the H.264 depacketiser without further parsing.
#[derive(Debug, Clone)]
pub struct Nal(pub Vec<u8>);

/// One frame's worth of raw input pixels. RGBA8, top-down, tightly packed.
/// `width * height * 4 == data.len()` is a documented invariant; encoders
/// must validate on init.
#[derive(Debug)]
pub struct RawFrame<'a> {
    pub width: u32,
    pub height: u32,
    pub data: &'a [u8],
    /// Capture timestamp from KSP's `Time.unscaledTime * 1000` (ms). Carried
    /// end-to-end so receivers can compute glass-to-glass latency.
    pub capture_ts_ms: f64,
}

/// Configuration for one encode session, applied at `init` and immutable for
/// the session's lifetime. Resolution changes mean `close + init` to a new
/// session at a keyframe boundary.
#[derive(Debug, Clone)]
pub struct EncodeConfig {
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub bitrate_bps: u32,
}

#[derive(Debug, Error)]
pub enum EncodeError {
    #[error("encoder backend not available on this platform")]
    Unavailable,
    #[error("invalid input: {0}")]
    Invalid(String),
    #[error("encoder runtime error: {0}")]
    Runtime(String),
}

/// Common surface every backend implements. Backends own the lifecycle of
/// their underlying encoder session (e.g. libva context, VTCompressionSession,
/// NvencEncoder handle).
pub trait EncoderBackend: Send {
    /// Human-readable identifier for logs and `/metrics`.
    fn name(&self) -> &'static str;

    /// True if the underlying encoder can be initialised on this host. Used
    /// at `--encoder=auto` enumeration; backends with `false` are skipped.
    fn is_available(&self) -> bool;

    /// Allocate the encoder session for `cfg`. Must be called before `encode`.
    fn init(&mut self, cfg: EncodeConfig) -> Result<(), EncodeError>;

    /// Push one raw frame and return any NAL units produced. May return zero
    /// NALs (e.g. encoder is buffering) — that's fine and not an error.
    fn encode(&mut self, frame: &RawFrame<'_>) -> Result<Vec<Nal>, EncodeError>;

    /// Hint to the encoder that the next emitted frame should be an IDR.
    /// Useful when a new subscriber attaches; called by the WebRTC peer.
    fn request_keyframe(&mut self);

    /// Release encoder resources. Must be idempotent.
    fn close(&mut self);
}

/// Pick the best available backend for the current platform. Tier-1 first
/// (libva on Linux), tier-2 next (VideoToolbox / NVENC), software last.
pub fn auto_select() -> Box<dyn EncoderBackend> {
    let candidates: [Box<dyn EncoderBackend>; 4] = [
        Box::new(Libva::new()),
        Box::new(VideoToolbox::new()),
        Box::new(Nvenc::new()),
        Box::new(Software::new()),
    ];
    for b in candidates {
        if b.is_available() {
            return b;
        }
    }
    // Software backend always reports available so this is unreachable in
    // practice, but the type checker doesn't know that.
    Box::new(Software::new())
}

/// Return the name of the backend that `auto_select` would choose, without
/// allocating a full encoder. Probe results are cached via OnceLock so this
/// is cheap after the first call.
pub fn selected_backend_name() -> &'static str {
    let candidates: [Box<dyn EncoderBackend>; 4] = [
        Box::new(Libva::new()),
        Box::new(VideoToolbox::new()),
        Box::new(Nvenc::new()),
        Box::new(Software::new()),
    ];
    for b in candidates {
        if b.is_available() {
            return b.name();
        }
    }
    Software::new().name()
}
