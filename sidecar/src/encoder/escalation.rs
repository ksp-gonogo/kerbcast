//! Reactive per-camera escalation from hardware to software encode.
//!
//! Consumer GPUs cap concurrent hardware encode sessions, and behaviour
//! at the cap varies by driver. The mode observed in the field (AMD RX
//! 9070 XT, 13 cameras, Media Foundation): sessions past the cap init
//! cleanly, grant input credits and accept frames forever, but never
//! emit a single NAL. No error ever surfaces, so the consume loop's
//! error-streak reinit never fires and the WebRTC track stays black.
//!
//! `SessionHealth` is the per-camera state machine that catches this:
//! a session that stays silent for `SILENT_SESSION_FRAME_LIMIT` encode
//! calls (or gets dropped by the error-streak path) counts as one
//! strike; at `SESSION_STRIKE_LIMIT` consecutive strikes the camera is
//! pinned to the software fallback. Escalation is per camera (the
//! cameras that won hardware sessions keep them) and purely reactive:
//! no upfront vendor session-cap detection.

use super::{select_backend, EncoderBackend, EncoderChoice, Software};

/// Encode calls a hardware session may answer with zero NALs before it
/// is declared silent and dropped. A healthy hardware encoder emits
/// within a frame or two of the first input; ~3 s at 30 fps is patient
/// enough for any real pipelining depth.
pub const SILENT_SESSION_FRAME_LIMIT: u32 = 90;

/// Consecutive dropped sessions (silent or persistently erroring)
/// before the camera's encode is pinned to the software fallback.
pub const SESSION_STRIKE_LIMIT: u32 = 2;

/// Smallest frame side every encoder backend accepts. The Steam Deck's VAAPI
/// refuses anything under 128x128, and OpenH264 fails on frames that small.
pub const MIN_ENCODE_DIMENSION: u32 = 128;

/// Largest frame side the hardware encoders accept.
pub const MAX_ENCODE_DIMENSION: u32 = 4096;

/// Whether a session failure at this frame size says anything about the
/// backend. Outside the range any backend may refuse the size itself, so the
/// failure is the size's fault and must not push the camera to software.
pub fn size_is_encodable(width: u32, height: u32) -> bool {
    let range = MIN_ENCODE_DIMENSION..=MAX_ENCODE_DIMENSION;
    range.contains(&width) && range.contains(&height)
}

/// What the consume loop must do after recording a session-health event.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionVerdict {
    /// Session still within patience; keep feeding it.
    Continue,
    /// Drop the session; the next frame reinitialises on whatever
    /// backend `select` picks.
    DropSession,
    /// Drop the session; this camera is software-only from now on.
    /// Returned exactly once per camera; gates the operator warn.
    EscalateToSoftware,
}

/// Per-camera encode-session health. Mutated only under the camera's
/// encoder lock, so a plain std Mutex around it never contends.
#[derive(Debug, Default)]
pub struct SessionHealth {
    silent_frames: u32,
    session_strikes: u32,
    forced_software: bool,
}

impl SessionHealth {
    pub fn new() -> Self {
        Self::default()
    }

    /// The current session produced output: real NALs prove the backend
    /// works at this load, so clear the silent counter and all strikes.
    pub fn note_output(&mut self) {
        self.silent_frames = 0;
        self.session_strikes = 0;
    }

    /// The current session accepted a frame but emitted nothing. Only
    /// meaningful for hardware sessions; the caller gates on
    /// `is_hardware()` so a buffering software encoder is never struck.
    pub fn note_silent_frame(&mut self) -> SessionVerdict {
        self.silent_frames += 1;
        if self.silent_frames < SILENT_SESSION_FRAME_LIMIT {
            return SessionVerdict::Continue;
        }
        self.silent_frames = 0;
        self.strike()
    }

    /// The current session was dropped for persistent encode errors, or
    /// failed to initialise at all.
    pub fn note_session_error(&mut self) -> SessionVerdict {
        self.silent_frames = 0;
        self.strike()
    }

    /// `note_session_error` for a session encoding `width` x `height`: one
    /// at a size no encoder supports is dropped without a strike.
    pub fn note_session_error_at(&mut self, width: u32, height: u32) -> SessionVerdict {
        if size_is_encodable(width, height) {
            self.note_session_error()
        } else {
            self.silent_frames = 0;
            SessionVerdict::DropSession
        }
    }

    fn strike(&mut self) -> SessionVerdict {
        if self.forced_software {
            // Already escalated; software sessions don't strike further.
            return SessionVerdict::DropSession;
        }
        self.session_strikes += 1;
        if self.session_strikes >= SESSION_STRIKE_LIMIT {
            self.forced_software = true;
            SessionVerdict::EscalateToSoftware
        } else {
            SessionVerdict::DropSession
        }
    }

    pub fn forced_software(&self) -> bool {
        self.forced_software
    }

    /// Backend for the camera's next encoder session: the configured
    /// choice until escalation, the software fallback after.
    pub fn select(&self, choice: EncoderChoice) -> Box<dyn EncoderBackend> {
        if self.forced_software {
            Box::new(Software::new())
        } else {
            select_backend(choice)
        }
    }
}

/// Record a failed encoder init against a camera's session health, wiring
/// the reactive escalation machine to init failures (some drivers, e.g.
/// Media Foundation on certain D3D devices, fail hardware init on every
/// resolution reinit rather than just going silent post-init). Only
/// hardware backends strike — software's own init failures have nowhere
/// softer to fall back to, and neither does a failure at a size outside
/// `size_is_encodable`. Returns the verdict, or `None` when nothing struck.
pub fn record_init_failure(
    health: &mut SessionHealth,
    was_hardware: bool,
    width: u32,
    height: u32,
) -> Option<SessionVerdict> {
    (was_hardware && size_is_encodable(width, height)).then(|| health.note_session_error())
}

#[cfg(test)]
mod tests {
    use super::super::{EncodeConfig, EncodeError, Nal, RawFrame};
    use super::*;

    /// Hardware-flavoured backend that initialises fine, accepts every
    /// frame, and never emits output: the session-cap failure mode.
    struct SilentBackend {
        inits: u32,
        encodes: u32,
    }

    impl SilentBackend {
        fn new() -> Self {
            Self {
                inits: 0,
                encodes: 0,
            }
        }
    }

    impl EncoderBackend for SilentBackend {
        fn name(&self) -> &'static str {
            "silent fake hw"
        }
        fn is_available(&self) -> bool {
            true
        }
        fn is_hardware(&self) -> bool {
            true
        }
        fn init(&mut self, _cfg: EncodeConfig) -> Result<(), EncodeError> {
            self.inits += 1;
            Ok(())
        }
        fn encode(&mut self, _frame: &RawFrame<'_>) -> Result<Vec<Nal>, EncodeError> {
            self.encodes += 1;
            Ok(Vec::new())
        }
        fn request_keyframe(&mut self) {}
        fn close(&mut self) {}
    }

    fn frame_pixels() -> Vec<u8> {
        vec![0u8; 2 * 2 * 4]
    }

    /// Drive one session the way the consume loop does: init, feed
    /// frames, record silence per zero-NAL encode, stop when the health
    /// machine says drop. Returns the verdict and frames fed.
    fn run_silent_session(
        health: &mut SessionHealth,
        backend: &mut SilentBackend,
    ) -> (SessionVerdict, u32) {
        backend
            .init(EncodeConfig {
                width: 2,
                height: 2,
                fps: 30,
                bitrate_bps: 500_000,
            })
            .expect("fake init never fails");
        let pixels = frame_pixels();
        let mut fed = 0u32;
        loop {
            let nals = backend
                .encode(&RawFrame {
                    width: 2,
                    height: 2,
                    data: &pixels,
                    capture_ts_ms: 0.0,
                })
                .expect("fake encode never errors");
            fed += 1;
            assert!(nals.is_empty(), "silent fake must never emit");
            let verdict = if backend.is_hardware() {
                health.note_silent_frame()
            } else {
                SessionVerdict::Continue
            };
            if verdict != SessionVerdict::Continue {
                backend.close();
                return (verdict, fed);
            }
        }
    }

    #[test]
    fn escalates_after_strike_limit_silent_sessions() {
        let mut health = SessionHealth::new();
        let mut backend = SilentBackend::new();

        // First silent session: dropped for reinit, not yet escalated.
        let (verdict, fed) = run_silent_session(&mut health, &mut backend);
        assert_eq!(verdict, SessionVerdict::DropSession);
        assert_eq!(
            fed, SILENT_SESSION_FRAME_LIMIT,
            "bounded patience per session"
        );
        assert!(!health.forced_software());

        // Second silent session: escalate, exactly at the strike limit.
        let (verdict, fed) = run_silent_session(&mut health, &mut backend);
        assert_eq!(verdict, SessionVerdict::EscalateToSoftware);
        assert_eq!(fed, SILENT_SESSION_FRAME_LIMIT);
        assert!(health.forced_software());
        assert_eq!(backend.inits, SESSION_STRIKE_LIMIT);

        // Software fallback engages for this camera's next session.
        assert!(!health.select(EncoderChoice::Mediafoundation).is_hardware());
    }

    #[test]
    fn escalation_verdict_fires_exactly_once() {
        let mut health = SessionHealth::new();
        let mut backend = SilentBackend::new();
        let mut escalations = 0;
        for _ in 0..5 {
            let (verdict, _) = run_silent_session(&mut health, &mut backend);
            if verdict == SessionVerdict::EscalateToSoftware {
                escalations += 1;
            }
        }
        // The warn is gated on the EscalateToSoftware verdict, so this
        // is also "warn fires once".
        assert_eq!(escalations, 1);
    }

    #[test]
    fn other_cameras_keep_their_configured_backend() {
        let mut starved = SessionHealth::new();
        let healthy = SessionHealth::new();
        let mut backend = SilentBackend::new();
        for _ in 0..SESSION_STRIKE_LIMIT {
            run_silent_session(&mut starved, &mut backend);
        }
        assert!(starved.forced_software());
        assert!(!healthy.forced_software());
        // Escalation is per camera: the healthy one still selects the
        // configured (hardware) backend, the starved one software.
        assert!(healthy.select(EncoderChoice::Mediafoundation).is_hardware());
        assert!(!starved.select(EncoderChoice::Mediafoundation).is_hardware());
    }

    #[test]
    fn real_output_clears_strikes() {
        let mut health = SessionHealth::new();
        let mut backend = SilentBackend::new();
        // One silent session = one strike.
        let (verdict, _) = run_silent_session(&mut health, &mut backend);
        assert_eq!(verdict, SessionVerdict::DropSession);
        // Next session produces output: strikes reset.
        health.note_output();
        // A later silent session starts the count over: drop, not escalate.
        let (verdict, _) = run_silent_session(&mut health, &mut backend);
        assert_eq!(verdict, SessionVerdict::DropSession);
        assert!(!health.forced_software());
    }

    #[test]
    fn error_streak_sessions_count_toward_escalation() {
        let mut health = SessionHealth::new();
        assert_eq!(health.note_session_error(), SessionVerdict::DropSession);
        assert_eq!(
            health.note_session_error(),
            SessionVerdict::EscalateToSoftware
        );
        assert!(health.forced_software());
        // Further failures on the software path just drop the session.
        assert_eq!(health.note_session_error(), SessionVerdict::DropSession);
    }

    #[test]
    fn init_failure_on_hardware_backend_strikes_and_escalates() {
        let mut health = SessionHealth::new();
        assert_eq!(
            record_init_failure(&mut health, true, 640, 360),
            Some(SessionVerdict::DropSession)
        );
        assert!(!health.forced_software());
        assert_eq!(
            record_init_failure(&mut health, true, 640, 360),
            Some(SessionVerdict::EscalateToSoftware)
        );
        assert!(health.forced_software());
    }

    #[test]
    fn init_failure_on_software_backend_does_not_strike() {
        let mut health = SessionHealth::new();
        assert_eq!(record_init_failure(&mut health, false, 640, 360), None);
        assert!(!health.forced_software());
    }

    /// Reproduces the exact real call-site sequence in `main.rs`'s
    /// `encode_and_fan_out`: a hardware init failure strikes, then the
    /// software fallback it drops into succeeds and produces output. The
    /// call site must gate its `note_output()` call on `backend_is_hardware`
    /// (it does not call it here, mirroring that gate) — a software
    /// session succeeding says nothing about hardware health and must not
    /// erase the strike `record_init_failure` just recorded. Before that
    /// gate existed, an unconditional `note_output()` after every
    /// successful encode (hardware or not) reset `session_strikes` on
    /// every resolution reinit, so a camera whose hardware init failed on
    /// every single reinit never reached `SESSION_STRIKE_LIMIT` and kept
    /// retrying the doomed hardware backend for the rest of the session.
    #[test]
    fn hardware_init_failures_survive_an_intervening_software_fallback_success() {
        let mut health = SessionHealth::new();

        // Reinit #1: hardware init fails (strike 1), software fallback
        // succeeds — deliberately NOT calling note_output() here, since
        // the fallback backend is software, not hardware.
        assert_eq!(
            record_init_failure(&mut health, true, 640, 360),
            Some(SessionVerdict::DropSession)
        );
        assert!(!health.forced_software());

        // Reinit #2: hardware init fails again. If the strike from #1
        // had been wiped by an intervening note_output(), this would only
        // be strike 1 again (DropSession) instead of strike 2 (escalate).
        assert_eq!(
            record_init_failure(&mut health, true, 640, 360),
            Some(SessionVerdict::EscalateToSoftware)
        );
        assert!(health.forced_software());
    }

    #[test]
    fn intra_session_silence_below_limit_is_tolerated() {
        let mut health = SessionHealth::new();
        for _ in 0..(SILENT_SESSION_FRAME_LIMIT - 1) {
            assert_eq!(health.note_silent_frame(), SessionVerdict::Continue);
        }
        // Output arrives just in time: nothing was struck.
        health.note_output();
        assert!(!health.forced_software());
        assert_eq!(health.note_silent_frame(), SessionVerdict::Continue);
    }

    /// A hardware open refused because the frame is smaller than the encoder
    /// supports, then a software session failing at the same size, must not
    /// pin the camera to software once the size grows back.
    #[test]
    fn failures_at_an_unsupported_size_do_not_strike() {
        let mut health = SessionHealth::new();
        assert_eq!(record_init_failure(&mut health, true, 18, 10), None);
        assert_eq!(
            health.note_session_error_at(18, 10),
            SessionVerdict::DropSession
        );
        assert_eq!(record_init_failure(&mut health, true, 640, 8192), None);
        assert!(!health.forced_software());
        assert!(health.select(EncoderChoice::Mediafoundation).is_hardware());

        /* Strikes at a supported size still count, so the size exemption
        cannot hide a backend that really is failing. */
        assert_eq!(
            record_init_failure(&mut health, true, 640, 360),
            Some(SessionVerdict::DropSession)
        );
        assert_eq!(
            health.note_session_error_at(640, 360),
            SessionVerdict::EscalateToSoftware
        );
    }

    #[test]
    fn encodable_range_is_inclusive_on_both_axes() {
        assert!(size_is_encodable(
            MIN_ENCODE_DIMENSION,
            MIN_ENCODE_DIMENSION
        ));
        assert!(size_is_encodable(
            MAX_ENCODE_DIMENSION,
            MAX_ENCODE_DIMENSION
        ));
        assert!(!size_is_encodable(MIN_ENCODE_DIMENSION - 1, 720));
        assert!(!size_is_encodable(1280, MIN_ENCODE_DIMENSION - 1));
        assert!(!size_is_encodable(MAX_ENCODE_DIMENSION + 1, 720));
    }
}
