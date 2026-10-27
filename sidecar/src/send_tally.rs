//! Why each frame the ring advanced past was or was not sent, for the
//! `--telemetry` report.

use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

/// Per-camera counts since the last report, cheap enough to bump per frame.
#[derive(Default)]
pub struct SendTally {
    advanced: AtomicU64,
    sent: AtomicU64,
    overwritten: AtomicU64,
    behind: AtomicU64,
    rate_guard: AtomicU64,
    degrade: AtomicU64,
    no_output: AtomicU64,
    encode_failed: AtomicU64,
    caught_up: AtomicU64,
    encodes: AtomicU64,
    encode_us: AtomicU64,
    encode_us_max: AtomicU64,
}

/// One report's worth of counts, taken and reset together.
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct SendCounts {
    /// Sequences the ring moved past, sent or not.
    pub advanced: u64,
    pub sent: u64,
    /// Frames the writer replaced before the sidecar read them.
    pub overwritten: u64,
    /// Frames passed over because the camera was more than the catch-up
    /// allowance behind.
    pub behind: u64,
    pub rate_guard: u64,
    pub degrade: u64,
    /// Encodes that returned no packets.
    pub no_output: u64,
    pub encode_failed: u64,
    /// Ticks that sent more than one frame for this camera.
    pub caught_up: u64,
    pub encodes: u64,
    pub encode_us: u64,
    pub encode_us_max: u64,
}

impl SendTally {
    pub fn note_advance(&self, n: u64) {
        self.advanced.fetch_add(n, Ordering::Relaxed);
    }

    pub fn note_sent(&self) {
        self.sent.fetch_add(1, Ordering::Relaxed);
    }

    /// A frame read from the ring, after passing over `behind` frames on
    /// purpose and finding `overwritten` already gone.
    pub fn note_read(&self, behind: u64, overwritten: u64) {
        self.behind.fetch_add(behind, Ordering::Relaxed);
        self.overwritten.fetch_add(overwritten, Ordering::Relaxed);
    }

    pub fn note_rate_guard(&self) {
        self.rate_guard.fetch_add(1, Ordering::Relaxed);
    }

    pub fn note_degrade(&self) {
        self.degrade.fetch_add(1, Ordering::Relaxed);
    }

    pub fn note_no_output(&self) {
        self.no_output.fetch_add(1, Ordering::Relaxed);
    }

    pub fn note_encode_failed(&self) {
        self.encode_failed.fetch_add(1, Ordering::Relaxed);
    }

    pub fn note_caught_up(&self) {
        self.caught_up.fetch_add(1, Ordering::Relaxed);
    }

    pub fn note_encode(&self, took: Duration) {
        let us = took.as_micros().min(u64::MAX as u128) as u64;
        self.encodes.fetch_add(1, Ordering::Relaxed);
        self.encode_us.fetch_add(us, Ordering::Relaxed);
        self.encode_us_max.fetch_max(us, Ordering::Relaxed);
    }

    /// The counts since the last call, resetting them.
    pub fn take(&self) -> SendCounts {
        let t = |a: &AtomicU64| a.swap(0, Ordering::Relaxed);
        SendCounts {
            advanced: t(&self.advanced),
            sent: t(&self.sent),
            overwritten: t(&self.overwritten),
            behind: t(&self.behind),
            rate_guard: t(&self.rate_guard),
            degrade: t(&self.degrade),
            no_output: t(&self.no_output),
            encode_failed: t(&self.encode_failed),
            caught_up: t(&self.caught_up),
            encodes: t(&self.encodes),
            encode_us: t(&self.encode_us),
            encode_us_max: t(&self.encode_us_max),
        }
    }
}

impl SendCounts {
    /// One log line, shaped like the plugin's per-camera "capture over" line
    /// so the two read side by side.
    pub fn report(&self, flight_id: u32, over: Duration) -> String {
        let secs = over.as_secs_f64().max(0.001);
        let per_s = |n: u64| n as f64 / secs;
        let encode_mean_ms = if self.encodes > 0 {
            format!(
                "{:.1}",
                self.encode_us as f64 / self.encodes as f64 / 1000.0
            )
        } else {
            "n/a".to_string()
        };
        format!(
            "cam={flight_id} send over {secs:.1} s: ring advanced {:.1}/s, sent {:.1}/s; \
             not sent: overwritten before read {:.1}/s, too far behind {:.1}/s, \
             rate guard {:.1}/s, degrade {:.1}/s, no encoder output {:.1}/s, \
             encode failed {:.1}/s; caught up two in a tick {:.1}/s; \
             encode mean {encode_mean_ms} ms, max {:.1} ms",
            per_s(self.advanced),
            per_s(self.sent),
            per_s(self.overwritten),
            per_s(self.behind),
            per_s(self.rate_guard),
            per_s(self.degrade),
            per_s(self.no_output),
            per_s(self.encode_failed),
            per_s(self.caught_up),
            self.encode_us_max as f64 / 1000.0,
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn take_reports_and_resets() {
        let t = SendTally::default();
        t.note_advance(10);
        for _ in 0..7 {
            t.note_sent();
        }
        t.note_read(1, 2);
        t.note_encode(Duration::from_millis(4));
        t.note_encode(Duration::from_millis(8));
        let c = t.take();
        assert_eq!((c.advanced, c.sent, c.behind, c.overwritten), (10, 7, 1, 2));
        assert_eq!(
            (c.encodes, c.encode_us, c.encode_us_max),
            (2, 12_000, 8_000)
        );
        assert_eq!(t.take(), SendCounts::default(), "taking resets");
        let line = c.report(42, Duration::from_secs(5));
        assert!(
            line.starts_with("cam=42 send over 5.0 s: ring advanced 2.0/s, sent 1.4/s;"),
            "{line}"
        );
        assert!(line.contains("overwritten before read 0.4/s"), "{line}");
        assert!(line.ends_with("encode mean 6.0 ms, max 8.0 ms"), "{line}");
    }
}
