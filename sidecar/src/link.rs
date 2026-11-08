//! Per-viewer connection quality, measured from what the browser already
//! sends back: receiver reports (loss, RTT), NACKs, PLIs and REMB.
//!
//! The viewer's link is the bottleneck, not any one camera, so everything here
//! is per peer: a peer's slots are summed into one window.

use std::collections::{HashMap, VecDeque};
use std::time::{Duration, Instant};

use serde::Serialize;

/// How often a peer's stats are sampled. Each sample closes one window.
pub const LINK_WINDOW: Duration = Duration::from_secs(2);

/// How far back the minimum RTT looks. A link's queueing shows as RTT above
/// this floor, so it must outlast a congested spell but follow a route change.
pub const MIN_RTT_HORIZON: Duration = Duration::from_secs(60);

/// A REMB older than this no longer describes the link.
pub const REMB_STALE: Duration = Duration::from_secs(5);

/// One slot's cumulative outbound counters, as webrtc-rs's stats report them.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SlotCounters {
    pub ssrc: u32,
    pub packets_sent: u64,
    pub bytes_sent: u64,
    pub nacks: u64,
    pub plis: u64,
    /// Cumulative packets the browser reports lost. Signed: duplicates can
    /// make RFC 3550's count go down.
    pub remote_lost: i64,
    /// Latest RTT from a receiver report, if one has been measured.
    pub rtt_ms: Option<f64>,
}

/// What one peer's link did over one window.
#[derive(Debug, Clone, Copy, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LinkWindow {
    pub span_ms: u64,
    /// Payload sent to this peer across all its slots.
    pub send_bps: u64,
    pub packets_sent: u64,
    /// Fraction of the window's packets the browser reported lost, 0 to 1.
    pub loss: f64,
    /// Worst latest RTT across the peer's slots.
    pub rtt_ms: Option<f64>,
    /// Lowest RTT seen over `MIN_RTT_HORIZON`, the link's unloaded floor.
    pub min_rtt_ms: Option<f64>,
    pub nacks_per_s: f64,
    pub plis_per_s: f64,
    /// The browser's latest bandwidth estimate, if it sends REMB at all.
    pub remb_bps: Option<u64>,
}

/// Turns successive cumulative samples of one peer's slots into windows.
#[derive(Debug, Default)]
pub struct LinkMonitor {
    prev: HashMap<u32, SlotCounters>,
    prev_at: Option<Instant>,
    rtts: VecDeque<(Instant, f64)>,
    remb: Option<(Instant, u64)>,
    latest: Option<LinkWindow>,
}

impl LinkMonitor {
    pub fn new() -> Self {
        Self::default()
    }

    /// Record a REMB. Returns true for the peer's first one, which is worth a
    /// log line: whether browsers send REMB here at all is an open question.
    pub fn note_remb(&mut self, now: Instant, bps: u64) -> bool {
        self.remb.replace((now, bps)).is_none()
    }

    pub fn latest(&self) -> Option<LinkWindow> {
        self.latest
    }

    /**
     * Fold one sample in, returning the window it closes. The first sample
     * only sets the baseline. A slot is counted only when it was in the
     * previous sample too and none of its counters went backwards, so a slot
     * that appears, disappears or restarts its stream never reads as a burst.
     */
    pub fn observe(&mut self, now: Instant, slots: &[SlotCounters]) -> Option<LinkWindow> {
        let prev_at = self.prev_at.replace(now);
        let prev = std::mem::replace(&mut self.prev, slots.iter().map(|s| (s.ssrc, *s)).collect());
        let span = now.saturating_duration_since(prev_at?);
        if span.is_zero() {
            return None;
        }

        let (mut packets, mut bytes, mut nacks, mut plis, mut lost) =
            (0u64, 0u64, 0u64, 0u64, 0i64);
        let mut rtt: Option<f64> = None;
        for cur in slots {
            let Some(old) = prev.get(&cur.ssrc) else {
                continue;
            };
            if cur.packets_sent < old.packets_sent
                || cur.bytes_sent < old.bytes_sent
                || cur.nacks < old.nacks
                || cur.plis < old.plis
            {
                continue;
            }
            packets += cur.packets_sent - old.packets_sent;
            bytes += cur.bytes_sent - old.bytes_sent;
            nacks += cur.nacks - old.nacks;
            plis += cur.plis - old.plis;
            lost += (cur.remote_lost - old.remote_lost).max(0);
            /* An idle slot keeps its last RTT forever; only a slot that sent
            this window says anything about the link now. */
            if cur.packets_sent > old.packets_sent {
                if let Some(r) = cur.rtt_ms {
                    rtt = Some(rtt.map_or(r, |w: f64| w.max(r)));
                }
            }
        }

        if let Some(r) = rtt {
            self.rtts.push_back((now, r));
        }
        while self
            .rtts
            .front()
            .is_some_and(|&(at, _)| now.saturating_duration_since(at) > MIN_RTT_HORIZON)
        {
            self.rtts.pop_front();
        }
        let min_rtt = self.rtts.iter().map(|&(_, r)| r).reduce(f64::min);

        let secs = span.as_secs_f64();
        /* Receiver reports trail the packets they cover, so a window's lost
        count can belong to the previous window's packets. Clamping keeps a
        quiet window after a lossy one from reading above 100%. */
        let loss = if packets == 0 {
            0.0
        } else {
            (lost as f64 / packets as f64).clamp(0.0, 1.0)
        };
        let remb = self
            .remb
            .filter(|&(at, _)| now.saturating_duration_since(at) <= REMB_STALE)
            .map(|(_, bps)| bps);

        let window = LinkWindow {
            span_ms: span.as_millis() as u64,
            send_bps: (bytes as f64 * 8.0 / secs) as u64,
            packets_sent: packets,
            loss,
            rtt_ms: rtt,
            min_rtt_ms: min_rtt,
            nacks_per_s: nacks as f64 / secs,
            plis_per_s: plis as f64 / secs,
            remb_bps: remb,
        };
        self.latest = Some(window);
        Some(window)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn slot(ssrc: u32, packets: u64, bytes: u64, lost: i64, rtt: Option<f64>) -> SlotCounters {
        SlotCounters {
            ssrc,
            packets_sent: packets,
            bytes_sent: bytes,
            nacks: 0,
            plis: 0,
            remote_lost: lost,
            rtt_ms: rtt,
        }
    }

    #[test]
    fn the_first_sample_only_sets_the_baseline() {
        let mut m = LinkMonitor::new();
        assert_eq!(
            m.observe(Instant::now(), &[slot(1, 10, 1000, 0, None)]),
            None
        );
        assert_eq!(m.latest(), None);
    }

    #[test]
    fn a_window_sums_every_slot_of_the_peer() {
        let mut m = LinkMonitor::new();
        let t0 = Instant::now();
        m.observe(t0, &[slot(1, 0, 0, 0, None), slot(2, 0, 0, 0, None)]);
        let w = m
            .observe(
                t0 + LINK_WINDOW,
                &[
                    slot(1, 100, 250_000, 5, Some(40.0)),
                    slot(2, 100, 125_000, 5, Some(90.0)),
                ],
            )
            .unwrap();
        assert_eq!(w.span_ms, 2000);
        assert_eq!(w.send_bps, 1_500_000);
        assert_eq!(w.packets_sent, 200);
        assert!((w.loss - 0.05).abs() < 1e-9);
        assert_eq!(w.rtt_ms, Some(90.0), "the worst slot speaks for the link");
        assert_eq!(w.min_rtt_ms, Some(90.0));
    }

    #[test]
    fn a_slot_new_this_window_or_restarted_is_not_counted() {
        let mut m = LinkMonitor::new();
        let t0 = Instant::now();
        m.observe(t0, &[slot(1, 500, 50_000, 0, None)]);
        let w = m
            .observe(
                t0 + LINK_WINDOW,
                &[slot(1, 10, 1000, 0, None), slot(2, 999, 99_000, 0, None)],
            )
            .unwrap();
        assert_eq!(w.packets_sent, 0);
        assert_eq!(w.send_bps, 0);
        assert_eq!(w.loss, 0.0);
    }

    #[test]
    fn an_idle_slot_does_not_report_a_stale_rtt() {
        let mut m = LinkMonitor::new();
        let t0 = Instant::now();
        m.observe(t0, &[slot(1, 10, 1000, 0, Some(900.0))]);
        let w = m
            .observe(t0 + LINK_WINDOW, &[slot(1, 10, 1000, 0, Some(900.0))])
            .unwrap();
        assert_eq!(w.rtt_ms, None);
    }

    #[test]
    fn loss_is_clamped_when_reports_trail_the_packets() {
        let mut m = LinkMonitor::new();
        let t0 = Instant::now();
        m.observe(t0, &[slot(1, 100, 1000, 0, None)]);
        let w = m
            .observe(t0 + LINK_WINDOW, &[slot(1, 110, 1100, 50, None)])
            .unwrap();
        assert_eq!(w.loss, 1.0);
        let w = m
            .observe(t0 + LINK_WINDOW * 2, &[slot(1, 120, 1200, 40, None)])
            .unwrap();
        assert_eq!(w.loss, 0.0, "a falling lost count is not negative loss");
    }

    #[test]
    fn min_rtt_forgets_samples_past_its_horizon() {
        let mut m = LinkMonitor::new();
        let t0 = Instant::now();
        let mut packets = 0;
        let mut at = t0;
        m.observe(at, &[slot(1, packets, 0, 0, None)]);
        let mut step = |m: &mut LinkMonitor, rtt: f64, after: Duration| {
            packets += 10;
            at += after;
            m.observe(at, &[slot(1, packets, 0, 0, Some(rtt))]).unwrap()
        };
        assert_eq!(step(&mut m, 30.0, LINK_WINDOW).min_rtt_ms, Some(30.0));
        assert_eq!(step(&mut m, 200.0, LINK_WINDOW).min_rtt_ms, Some(30.0));
        let w = step(&mut m, 120.0, MIN_RTT_HORIZON);
        assert_eq!(w.min_rtt_ms, Some(120.0), "the 30 ms floor has aged out");
    }

    #[test]
    fn remb_is_reported_until_it_goes_stale() {
        let mut m = LinkMonitor::new();
        let t0 = Instant::now();
        m.observe(t0, &[]);
        m.note_remb(t0, 800_000);
        assert_eq!(
            m.observe(t0 + LINK_WINDOW, &[]).unwrap().remb_bps,
            Some(800_000)
        );
        assert_eq!(
            m.observe(t0 + REMB_STALE + LINK_WINDOW, &[])
                .unwrap()
                .remb_bps,
            None
        );
    }

    #[test]
    fn nack_and_pli_rates_are_per_second() {
        let mut m = LinkMonitor::new();
        let t0 = Instant::now();
        let mut s = slot(1, 0, 0, 0, None);
        m.observe(t0, &[s]);
        s.packets_sent = 100;
        s.nacks = 8;
        s.plis = 1;
        let w = m.observe(t0 + LINK_WINDOW, &[s]).unwrap();
        assert_eq!(w.nacks_per_s, 4.0);
        assert_eq!(w.plis_per_s, 0.5);
    }
}
