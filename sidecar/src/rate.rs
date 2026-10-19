//! Windowed event rate for per-camera frame counters.

use std::sync::Mutex;
use std::time::{Duration, Instant};

/// Event rate over roughly the last two seconds, cheap enough to record per
/// frame: one uncontended lock and no allocation.
pub struct RateWindow {
    inner: Mutex<Buckets>,
}

#[derive(Default)]
struct Buckets {
    start: Option<Instant>,
    current: u64,
    previous: u64,
    has_previous: bool,
}

impl Default for RateWindow {
    fn default() -> Self {
        Self::new()
    }
}

impl RateWindow {
    pub fn new() -> Self {
        Self {
            inner: Mutex::new(Buckets::default()),
        }
    }

    pub fn record(&self, n: u64) {
        self.record_at(Instant::now(), n);
    }

    pub fn rate(&self) -> f32 {
        self.rate_at(Instant::now())
    }

    pub fn record_at(&self, now: Instant, n: u64) {
        let mut b = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        if b.start.is_none() {
            b.start = Some(now);
        }
        b.roll(now);
        b.current += n;
    }

    pub fn rate_at(&self, now: Instant) -> f32 {
        let mut b = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        b.roll(now);
        let Some(start) = b.start else {
            return 0.0;
        };
        let into_bucket = now.saturating_duration_since(start);
        /* Previous full bucket plus the partial current one: a steady rate reads
        exactly, and two idle buckets read zero. Before the first bucket closes the
        span is floored so a lone frame cannot read as hundreds of fps. */
        let (count, span) = if b.has_previous {
            (b.previous + b.current, BUCKET + into_bucket)
        } else {
            (b.current, into_bucket.max(MIN_SPAN))
        };
        count as f32 / span.as_secs_f32()
    }
}

const BUCKET: Duration = Duration::from_secs(1);
const MIN_SPAN: Duration = Duration::from_millis(500);

impl Buckets {
    fn roll(&mut self, now: Instant) {
        let Some(start) = self.start else {
            return;
        };
        let elapsed = now.saturating_duration_since(start);
        if elapsed < BUCKET {
            return;
        }
        let whole = (elapsed.as_nanos() / BUCKET.as_nanos()) as u32;
        self.previous = if whole == 1 { self.current } else { 0 };
        self.current = 0;
        self.has_previous = true;
        self.start = Some(start + BUCKET * whole);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn feed(w: &RateWindow, t0: Instant, fps: u32, secs: u32) -> Instant {
        let step = Duration::from_secs(1) / fps;
        let mut t = t0;
        for _ in 0..fps * secs {
            w.record_at(t, 1);
            t += step;
        }
        t
    }

    #[test]
    fn steady_rate_is_reported() {
        let w = RateWindow::new();
        let t0 = Instant::now();
        let end = feed(&w, t0, 30, 5);
        let r = w.rate_at(end);
        assert!((r - 30.0).abs() < 1.0, "got {r}");
    }

    #[test]
    fn background_rate_is_reported() {
        let w = RateWindow::new();
        let t0 = Instant::now();
        let end = feed(&w, t0, 2, 6);
        let r = w.rate_at(end);
        assert!((r - 2.0).abs() < 0.6, "got {r}");
    }

    #[test]
    fn batched_counts_add_up() {
        let w = RateWindow::new();
        let t0 = Instant::now();
        for i in 0..20u32 {
            w.record_at(t0 + Duration::from_millis(250) * i, 5);
        }
        let r = w.rate_at(t0 + Duration::from_secs(5));
        assert!((r - 20.0).abs() < 3.0, "got {r}");
    }

    #[test]
    fn idle_decays_to_zero() {
        let w = RateWindow::new();
        let t0 = Instant::now();
        let end = feed(&w, t0, 30, 3);
        assert!(w.rate_at(end + Duration::from_millis(1500)) < 30.0);
        assert_eq!(w.rate_at(end + Duration::from_secs(3)), 0.0);
    }

    #[test]
    fn never_recorded_is_zero() {
        assert_eq!(RateWindow::new().rate_at(Instant::now()), 0.0);
    }

    #[test]
    fn first_burst_does_not_spike() {
        let w = RateWindow::new();
        let t0 = Instant::now();
        w.record_at(t0, 1);
        let r = w.rate_at(t0 + Duration::from_millis(1));
        assert!(
            r <= 4.0,
            "a single frame must not read as hundreds of fps, got {r}"
        );
    }
}
