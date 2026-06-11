//! Orphan protection for the once-per-KSP-session sidecar.
//!
//! The plugin's KerbcamSidecarHost touches `global.heartbeat` in the shm
//! dir about once a second for the whole game session and kills the
//! sidecar on a clean game exit. If KSP dies hard (crash, SIGKILL, power
//! loss to the window manager) nothing kills the child process, so the
//! sidecar watches the heartbeat's mtime and self-exits once it has gone
//! stale.
//!
//! The watch only ARMS after the file has been seen at least once:
//! dev workflows that run the sidecar standalone (fake_camera harness,
//! `cargo run` against a fixture dir) never write a heartbeat and are
//! never affected.
//!
//! Staleness must be observed on two CONSECUTIVE checks before exiting.
//! A suspend/resume (Deck sleep) freezes both processes; on wake the
//! sidecar's check can fire before Unity's first Update rewrites the
//! file, so a single stale observation is not proof of an orphan.

use std::time::{Duration, SystemTime};

/// File the plugin touches ~1Hz, relative to the shm dir. Lives beside
/// `global.status.json` / `global.control.json`; the ring rescan only
/// globs `*.ring` so it never collides.
pub const HEARTBEAT_FILE: &str = "global.heartbeat";

/// Consecutive stale observations required before the watcher exits.
const REQUIRED_STALE_STREAK: u32 = 2;

/// Pure staleness decision, factored out of the IO loop for testing.
/// Feed it the heartbeat file's mtime (None = missing) and the current
/// time on every check; it returns true once the sidecar should exit.
#[derive(Debug)]
pub struct HeartbeatWatch {
    timeout: Duration,
    armed: bool,
    stale_streak: u32,
}

impl HeartbeatWatch {
    pub fn new(timeout: Duration) -> Self {
        Self {
            timeout,
            armed: false,
            stale_streak: 0,
        }
    }

    /// Record one observation. `mtime` is the heartbeat file's modified
    /// time, or `None` when the file doesn't exist.
    pub fn observe(&mut self, mtime: Option<SystemTime>, now: SystemTime) -> bool {
        match mtime {
            None => {
                /* Missing-before-armed: no plugin in play (dev workflow),
                never exit. Missing-after-armed: the runtime dir was
                cleaned (logout) or the plugin deleted it on quit after
                our kill somehow missed; counts as stale. */
                if !self.armed {
                    return false;
                }
                self.stale_streak += 1;
            }
            Some(t) => {
                self.armed = true;
                /* duration_since errors when mtime is in the future (clock
                step); treat that as fresh, not stale. */
                let stale = now
                    .duration_since(t)
                    .map(|d| d > self.timeout)
                    .unwrap_or(false);
                if stale {
                    self.stale_streak += 1;
                } else {
                    self.stale_streak = 0;
                }
            }
        }
        self.stale_streak >= REQUIRED_STALE_STREAK
    }

    pub fn armed(&self) -> bool {
        self.armed
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const TIMEOUT: Duration = Duration::from_secs(90);

    fn t0() -> SystemTime {
        SystemTime::UNIX_EPOCH + Duration::from_secs(1_700_000_000)
    }

    /// Standalone dev workflow: no plugin, no heartbeat file, the sidecar
    /// must run forever no matter how many checks pass.
    #[test]
    fn never_exits_when_file_never_appears() {
        let mut w = HeartbeatWatch::new(TIMEOUT);
        for i in 0..10_000u64 {
            let now = t0() + Duration::from_secs(5 * i);
            assert!(!w.observe(None, now), "check {i} must not exit");
        }
        assert!(!w.armed());
    }

    /// The KSP-crash case the watch exists for: heartbeat seen, then the
    /// plugin stops writing. Two consecutive stale checks -> exit.
    #[test]
    fn stale_after_arming_exits_on_second_consecutive_check() {
        let mut w = HeartbeatWatch::new(TIMEOUT);
        let written = t0();
        assert!(!w.observe(Some(written), t0()), "fresh sighting arms only");
        assert!(w.armed());

        let late = t0() + TIMEOUT + Duration::from_secs(10);
        assert!(!w.observe(Some(written), late), "first stale check holds");
        assert!(
            w.observe(Some(written), late + Duration::from_secs(5)),
            "second consecutive stale check exits"
        );
    }

    /// Suspend/resume: one stale observation followed by a fresh write
    /// (Unity resumed and touched the file) must reset the streak.
    #[test]
    fn fresh_write_resets_the_stale_streak() {
        let mut w = HeartbeatWatch::new(TIMEOUT);
        let written = t0();
        w.observe(Some(written), t0());

        let after_resume = t0() + TIMEOUT + Duration::from_secs(30);
        assert!(!w.observe(Some(written), after_resume), "first stale: hold");
        /* Host rewrote the file within the next check interval. */
        let rewritten = after_resume + Duration::from_secs(1);
        assert!(!w.observe(Some(rewritten), after_resume + Duration::from_secs(5)));

        /* Streak restarted: a later single stale observation still holds. */
        let stale_again = rewritten + TIMEOUT + Duration::from_secs(10);
        assert!(!w.observe(Some(rewritten), stale_again), "streak was reset");
    }

    /// Heartbeat file deleted after arming (runtime-dir cleanup at logout,
    /// or plugin quit cleanup that outlived a missed kill): counts as
    /// stale, exits after the consecutive-check rule.
    #[test]
    fn missing_after_armed_counts_as_stale() {
        let mut w = HeartbeatWatch::new(TIMEOUT);
        w.observe(Some(t0()), t0());
        let now = t0() + Duration::from_secs(5);
        assert!(!w.observe(None, now), "first missing check holds");
        assert!(
            w.observe(None, now + Duration::from_secs(5)),
            "second missing check exits"
        );
    }

    /// A heartbeat mtime in the future (clock step between the writer and
    /// the watcher) is fresh, not stale.
    #[test]
    fn future_mtime_is_fresh() {
        let mut w = HeartbeatWatch::new(TIMEOUT);
        let future = t0() + Duration::from_secs(3600);
        assert!(!w.observe(Some(future), t0()));
        assert!(!w.observe(Some(future), t0() + Duration::from_secs(5)));
    }

    /// A fresh write within the timeout never accumulates a streak, even
    /// across many checks (the steady-state of a healthy session).
    #[test]
    fn healthy_session_never_exits() {
        let mut w = HeartbeatWatch::new(TIMEOUT);
        for i in 0..10_000u64 {
            let now = t0() + Duration::from_secs(5 * i);
            let written = now - Duration::from_secs(1); /* host wrote 1s ago */
            assert!(!w.observe(Some(written), now), "check {i} must not exit");
        }
    }
}
