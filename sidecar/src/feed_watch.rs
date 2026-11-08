//! Notices a camera with viewers whose ring has stopped moving.

use std::time::{Duration, Instant};

/// The least time a watched ring may stand still before it counts as silent.
pub const SILENT_AFTER: Duration = Duration::from_secs(5);

/// Longest wait between attempts on a feed that stays silent.
const MAX_RETRY_GAP: Duration = Duration::from_secs(60);

/// Frame intervals a slow camera may miss before it counts as silent, so a
/// background capture at a frame every few seconds is not mistaken for one.
const SILENT_INTERVALS: f32 = 4.0;

/// What one look at a camera found.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum FeedVerdict {
    /// Nothing to do.
    Quiet,
    /// Silent, and this attempt at recovering it is due.
    Silent { silent_for: Duration, attempt: u32 },
    /// Frames are moving again after `attempts` attempts.
    Resumed { silent_for: Duration, attempts: u32 },
}

/// One camera's silence clock, looked at about once a second.
#[derive(Debug, Default)]
pub struct FeedWatch {
    sequence: u64,
    since: Option<Instant>,
    next_attempt: Option<Instant>,
    attempts: u32,
}

impl FeedWatch {
    /**
     * Records the ring's published `sequence` at `now`. A camera not `watched`,
     * or one the plugin is not meant to capture (`target_fps` of 0), has no
     * reason to be moving and starts its clock again.
     */
    pub fn observe(
        &mut self,
        now: Instant,
        watched: bool,
        sequence: u64,
        target_fps: f32,
    ) -> FeedVerdict {
        if !watched || target_fps <= 0.0 {
            *self = Self::default();
            return FeedVerdict::Quiet;
        }
        let Some(since) = self.since.filter(|_| sequence == self.sequence) else {
            let resumed = (self.attempts > 0).then(|| FeedVerdict::Resumed {
                silent_for: now.saturating_duration_since(self.since.unwrap_or(now)),
                attempts: self.attempts,
            });
            *self = Self {
                sequence,
                since: Some(now),
                next_attempt: None,
                attempts: 0,
            };
            return resumed.unwrap_or(FeedVerdict::Quiet);
        };
        let first =
            since + SILENT_AFTER.max(Duration::from_secs_f32(SILENT_INTERVALS / target_fps));
        if now < self.next_attempt.unwrap_or(first) {
            return FeedVerdict::Quiet;
        }
        self.attempts += 1;
        let gap = SILENT_AFTER.saturating_mul(1 << self.attempts.min(8));
        self.next_attempt = Some(now + gap.min(MAX_RETRY_GAP));
        FeedVerdict::Silent {
            silent_for: now.saturating_duration_since(since),
            attempt: self.attempts,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const FPS: f32 = 30.0;

    fn secs(n: u64) -> Duration {
        Duration::from_secs(n)
    }

    #[test]
    fn a_moving_ring_is_never_silent() {
        let (mut w, t) = (FeedWatch::default(), Instant::now());
        for n in 0..60 {
            assert_eq!(w.observe(t + secs(n), true, n, FPS), FeedVerdict::Quiet);
        }
    }

    #[test]
    fn a_stopped_ring_is_tried_again_less_and_less_often() {
        let (mut w, t) = (FeedWatch::default(), Instant::now());
        let attempts: Vec<u64> = (0..300)
            .filter(|&n| {
                matches!(
                    w.observe(t + secs(n), true, 7, FPS),
                    FeedVerdict::Silent { .. }
                )
            })
            .collect();
        assert_eq!(attempts[..6], [5, 15, 35, 75, 135, 195]);
    }

    #[test]
    fn frames_returning_report_how_long_it_was_silent() {
        let (mut w, t) = (FeedWatch::default(), Instant::now());
        w.observe(t, true, 7, FPS);
        w.observe(t + secs(6), true, 7, FPS);
        assert_eq!(
            w.observe(t + secs(9), true, 8, FPS),
            FeedVerdict::Resumed {
                silent_for: secs(9),
                attempts: 1
            }
        );
        assert_eq!(w.observe(t + secs(10), true, 9, FPS), FeedVerdict::Quiet);
    }

    #[test]
    fn a_camera_nobody_watches_or_nothing_captures_is_left_alone() {
        let (mut w, t) = (FeedWatch::default(), Instant::now());
        for n in 0..30 {
            assert_eq!(w.observe(t + secs(n), false, 7, FPS), FeedVerdict::Quiet);
        }
        for n in 30..60 {
            assert_eq!(w.observe(t + secs(n), true, 7, 0.0), FeedVerdict::Quiet);
        }
        assert_eq!(w.observe(t + secs(60), true, 7, FPS), FeedVerdict::Quiet);
        assert_eq!(
            w.observe(t + secs(64), true, 7, FPS),
            FeedVerdict::Quiet,
            "a viewer who just arrived starts the clock"
        );
    }

    #[test]
    fn a_slow_background_capture_gets_its_own_margin() {
        let (mut w, t) = (FeedWatch::default(), Instant::now());
        w.observe(t, true, 7, 0.25);
        assert_eq!(w.observe(t + secs(15), true, 7, 0.25), FeedVerdict::Quiet);
        assert!(matches!(
            w.observe(t + secs(16), true, 7, 0.25),
            FeedVerdict::Silent { .. }
        ));
    }
}
