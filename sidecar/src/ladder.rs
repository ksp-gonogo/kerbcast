//! Which delivery tier each of one viewer's feeds should be on, decided from
//! that viewer's link windows.
//!
//! The detector is a pure state machine over injected time. It only ever
//! moves this viewer's own slots, one rung per step, so a slow viewer cannot
//! change what anyone else receives, and no encoder is ever retuned.

use std::collections::HashMap;
use std::time::{Duration, Instant};

use serde::Serialize;

use crate::link::LinkWindow;

/// A rung of the delivery ladder.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Tier {
    /// The camera's own encoder at the ring's size and full rate.
    Full,
    /// Half width and height, half frame rate, about a quarter of the bits.
    Reduced,
}

/// How one window read.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Verdict {
    Clean,
    /// Neither clean nor congested: holds the current tiers.
    Fair,
    Congested,
    Severe,
    /// Overlaps the aftermath of a tier switch, whose keyframe burst would
    /// otherwise read as fresh congestion.
    Masked,
}

/// Thresholds and timings. The defaults are starting points to tune from
/// measured links.
#[derive(Debug, Clone, Copy)]
pub struct LadderConfig {
    pub congested_loss: f64,
    pub severe_loss: f64,
    pub clean_loss: f64,
    /// RTT above the link's floor by this much (and by `queue_factor`) means
    /// a queue is building.
    pub queue_rtt_ms: f64,
    pub queue_factor: f64,
    pub severe_rtt_ms: f64,
    /// A REMB under this share of what is being sent backs up loss or PLI
    /// evidence of congestion. It never demotes on its own: Chrome's estimate
    /// ramps up from a low start and dips now and then on a clean link.
    pub remb_short: f64,
    /// How long a new viewer's REMB is ignored while it ramps up.
    pub remb_warmup: Duration,
    /// Consecutive congested windows before a demote.
    pub congested_windows: u32,
    /// Least time between two demotes, so each step's effect lands first.
    pub demote_dwell: Duration,
    pub mask_after_switch: Duration,
    pub promote_wait: Duration,
    pub promote_wait_max: Duration,
    /// A demote this soon after a promote means the promote failed.
    pub promote_trial: Duration,
    /// Steady this long at one set of tiers resets the promote backoff.
    pub steady_reset: Duration,
    /// REMB at least this multiple of the promoted send rate lets a promote
    /// skip the rest of its wait (after `remb_promote_wait`).
    pub remb_headroom: f64,
    pub remb_promote_wait: Duration,
    /// What one feed costs on each tier, for the promoted send rate estimate.
    pub full_bps: u64,
    pub reduced_bps: u64,
}

impl Default for LadderConfig {
    fn default() -> Self {
        Self {
            congested_loss: 0.05,
            severe_loss: 0.20,
            clean_loss: 0.01,
            queue_rtt_ms: 150.0,
            queue_factor: 1.5,
            severe_rtt_ms: 1000.0,
            remb_short: 0.8,
            remb_warmup: Duration::from_secs(10),
            congested_windows: 2,
            demote_dwell: Duration::from_secs(3),
            mask_after_switch: Duration::from_secs(2),
            promote_wait: Duration::from_secs(20),
            promote_wait_max: Duration::from_secs(300),
            promote_trial: Duration::from_secs(10),
            steady_reset: Duration::from_secs(300),
            remb_headroom: 1.3,
            remb_promote_wait: Duration::from_secs(10),
            full_bps: 1_500_000,
            reduced_bps: 350_000,
        }
    }
}

/// One of the viewer's bound slots, as the detector needs to see it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SlotView {
    pub slot: usize,
    pub flight_id: u32,
    /// Reported display area in px; the smallest feed is demoted first.
    pub display_area: u64,
    /// The viewer forces this camera to full resolution (a recording): never
    /// demoted.
    pub pinned_full: bool,
}

/// One move the detector made.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TierChange {
    pub slot: usize,
    pub flight_id: u32,
    pub to: Tier,
    pub reason: ChangeReason,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ChangeReason {
    Congested,
    Severe,
    Recovered,
    Pinned,
}

/// What the detector holds for a viewer, as `/links` reports it.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LadderState {
    pub verdict: Option<Verdict>,
    pub tiers: Vec<SlotTier>,
    pub promote_wait_s: u64,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SlotTier {
    pub slot: usize,
    pub flight_id: u32,
    pub tier: Tier,
}

/// The per-viewer tier decision.
#[derive(Debug)]
pub struct Ladder {
    cfg: LadderConfig,
    /// Keyed by slot and camera, so a slot rebound to another camera starts
    /// over on Full.
    tiers: HashMap<(usize, u32), Tier>,
    congested_run: u32,
    clean_since: Option<Instant>,
    last_switch: Option<Instant>,
    last_demote: Option<Instant>,
    last_promote: Option<Instant>,
    promote_wait: Duration,
    verdict: Option<Verdict>,
    first_window: Option<Instant>,
}

impl Ladder {
    pub fn new(cfg: LadderConfig) -> Self {
        Self {
            promote_wait: cfg.promote_wait,
            cfg,
            tiers: HashMap::new(),
            congested_run: 0,
            clean_since: None,
            last_switch: None,
            last_demote: None,
            last_promote: None,
            verdict: None,
            first_window: None,
        }
    }

    pub fn tier(&self, slot: usize, flight_id: u32) -> Tier {
        self.tiers
            .get(&(slot, flight_id))
            .copied()
            .unwrap_or(Tier::Full)
    }

    pub fn state(&self) -> LadderState {
        let mut tiers: Vec<SlotTier> = self
            .tiers
            .iter()
            .map(|(&(slot, flight_id), &tier)| SlotTier {
                slot,
                flight_id,
                tier,
            })
            .collect();
        tiers.sort_by_key(|t| t.slot);
        LadderState {
            verdict: self.verdict,
            tiers,
            promote_wait_s: self.promote_wait.as_secs(),
        }
    }

    /// Read one window as a verdict, before masking.
    pub fn judge(&self, w: &LinkWindow) -> Verdict {
        let c = &self.cfg;
        let rtt = w.rtt_ms;
        let floor = w.min_rtt_ms;
        if w.loss > c.severe_loss || rtt.is_some_and(|r| r > c.severe_rtt_ms) {
            return Verdict::Severe;
        }
        let queued = match (rtt, floor) {
            (Some(r), Some(f)) => r > f + c.queue_rtt_ms && r > f * c.queue_factor,
            _ => false,
        };
        let plis_in_window = w.plis_per_s * w.span_ms as f64 / 1000.0;
        let remb_short = w
            .remb_bps
            .is_some_and(|remb| (remb as f64) < c.remb_short * w.send_bps as f64);
        let some_loss = w.loss >= c.clean_loss || plis_in_window >= 0.5;
        if w.loss > c.congested_loss || queued || plis_in_window > 1.0 || (remb_short && some_loss)
        {
            return Verdict::Congested;
        }
        /* RTT that rises and falls without building a queue is jitter (a Wi-Fi
        LAN swings between 10 and 95 ms on a clean link), so only the queue
        rule above counts RTT against a window. */
        if some_loss {
            Verdict::Fair
        } else {
            Verdict::Clean
        }
    }

    /**
     * Fold one window in and return the moves to make. `slots` is every slot
     * the viewer has bound right now; slots no longer in it are forgotten.
     * At most one feed moves per step, except a severe window, which moves
     * every unpinned Full feed to Reduced at once.
     */
    pub fn step(&mut self, now: Instant, w: &LinkWindow, slots: &[SlotView]) -> Vec<TierChange> {
        let first = *self.first_window.get_or_insert(now);
        let warming = now.saturating_duration_since(first) < self.cfg.remb_warmup;
        let w = &LinkWindow {
            remb_bps: if warming { None } else { w.remb_bps },
            ..*w
        };
        self.tiers
            .retain(|key, _| slots.iter().any(|s| (s.slot, s.flight_id) == *key));
        let mut changes = Vec::new();

        /* A pin is a viewer's explicit choice and applies at once, whatever
        the link is doing. */
        for s in slots.iter().filter(|s| s.pinned_full) {
            if self.tier(s.slot, s.flight_id) != Tier::Full {
                changes.push(self.set(s, Tier::Full, ChangeReason::Pinned));
            }
        }

        let span = Duration::from_millis(w.span_ms);
        let masked = self.last_switch.is_some_and(|at| {
            now.checked_sub(span)
                .is_none_or(|start| start < at + self.cfg.mask_after_switch)
        });
        let verdict = if masked {
            Verdict::Masked
        } else {
            self.judge(w)
        };
        self.verdict = Some(verdict);

        if self
            .last_switch
            .is_some_and(|at| now.saturating_duration_since(at) >= self.cfg.steady_reset)
        {
            self.promote_wait = self.cfg.promote_wait;
        }

        match verdict {
            Verdict::Masked => {}
            Verdict::Severe => {
                self.congested_run = 0;
                self.clean_since = None;
                let demotable: Vec<SlotView> = slots
                    .iter()
                    .filter(|s| !s.pinned_full && self.tier(s.slot, s.flight_id) == Tier::Full)
                    .copied()
                    .collect();
                if !demotable.is_empty() {
                    self.note_demote(now);
                    for s in &demotable {
                        changes.push(self.set(s, Tier::Reduced, ChangeReason::Severe));
                    }
                }
            }
            Verdict::Congested => {
                self.clean_since = None;
                self.congested_run += 1;
                let dwelt = self
                    .last_demote
                    .is_none_or(|at| now.saturating_duration_since(at) >= self.cfg.demote_dwell);
                if self.congested_run >= self.cfg.congested_windows && dwelt {
                    if let Some(s) = self.next_to_demote(slots) {
                        self.congested_run = 0;
                        self.note_demote(now);
                        changes.push(self.set(&s, Tier::Reduced, ChangeReason::Congested));
                    }
                }
            }
            Verdict::Fair => {
                self.congested_run = 0;
                self.clean_since = None;
            }
            Verdict::Clean => {
                self.congested_run = 0;
                let since = *self.clean_since.get_or_insert(now);
                let clean_for = now.saturating_duration_since(since);
                if let Some(s) = self.next_to_promote(slots) {
                    let promoted_bps =
                        w.send_bps + self.cfg.full_bps.saturating_sub(self.cfg.reduced_bps);
                    let headroom = w.remb_bps.is_some_and(|remb| {
                        remb as f64 >= self.cfg.remb_headroom * promoted_bps as f64
                    });
                    /* A browser estimate below what the promote would send says
                    the room is not there yet; the spell keeps running. */
                    let room = w.remb_bps.is_none_or(|remb| remb >= promoted_bps);
                    let wait = if headroom {
                        self.promote_wait.min(self.cfg.remb_promote_wait)
                    } else {
                        self.promote_wait
                    };
                    if room && clean_for >= wait {
                        self.last_promote = Some(now);
                        changes.push(self.set(&s, Tier::Full, ChangeReason::Recovered));
                    }
                }
            }
        }
        if !changes.is_empty() {
            self.last_switch = Some(now);
            self.clean_since = None;
        }
        changes
    }

    fn set(&mut self, s: &SlotView, to: Tier, reason: ChangeReason) -> TierChange {
        self.tiers.insert((s.slot, s.flight_id), to);
        TierChange {
            slot: s.slot,
            flight_id: s.flight_id,
            to,
            reason,
        }
    }

    fn note_demote(&mut self, now: Instant) {
        if self
            .last_promote
            .is_some_and(|at| now.saturating_duration_since(at) <= self.cfg.promote_trial)
        {
            self.promote_wait = (self.promote_wait * 2).min(self.cfg.promote_wait_max);
        }
        self.last_promote = None;
        self.last_demote = Some(now);
    }

    /// The smallest unpinned Full feed: it loses the least by shrinking.
    fn next_to_demote(&self, slots: &[SlotView]) -> Option<SlotView> {
        slots
            .iter()
            .filter(|s| !s.pinned_full && self.tier(s.slot, s.flight_id) == Tier::Full)
            .min_by_key(|s| (s.display_area, s.slot))
            .copied()
    }

    /// The largest Reduced feed, the reverse of the demote order.
    fn next_to_promote(&self, slots: &[SlotView]) -> Option<SlotView> {
        slots
            .iter()
            .filter(|s| self.tier(s.slot, s.flight_id) == Tier::Reduced)
            .max_by_key(|s| (s.display_area, std::cmp::Reverse(s.slot)))
            .copied()
    }
}

/// Two clean LAN viewers measured on a Deck (390x844 phone and 1280x720
/// desktop, three feeds each), one window every 5 s: send rate, REMB, RTT
/// and RTT floor. Chrome's REMB dips below the send rate twice per viewer;
/// none of it is congestion.
#[cfg(test)]
pub(crate) mod measured {
    pub(crate) const PHONE: &[(u64, u64, f64, f64)] = &[
        (1677313, 2149984, 53.8, 53.8),
        (1148456, 2151984, 79.2, 13.4),
        (1864714, 2366992, 12.3, 12.3),
        (2022355, 3237712, 43.9, 12.3),
        (2000957, 3524272, 87.6, 12.3),
        (2133183, 3811376, 16.1, 12.3),
        (2013338, 3811376, 54.2, 12.3),
        (1750374, 3811376, 17.6, 12.3),
        (1832340, 1565424, 14.0, 12.3),
        (1972617, 2245888, 14.3, 12.2),
        (2110700, 2886608, 48.2, 12.2),
        (1902016, 3246688, 13.2, 12.2),
        (2046075, 3565760, 35.8, 12.2),
        (1941240, 3565760, 13.5, 12.2),
        (1960202, 3678048, 44.0, 11.2),
        (1979626, 3736000, 18.1, 11.2),
        (1998285, 3854608, 13.5, 11.2),
        (1989255, 3854608, 13.3, 11.2),
        (1977360, 3854608, 14.5, 10.5),
        (2190671, 2049152, 30.1, 10.5),
        (1823988, 2672064, 16.8, 10.5),
        (1793800, 3378720, 77.1, 10.5),
        (2119657, 3593168, 67.7, 10.5),
        (1850114, 3756432, 33.6, 10.5),
        (1785988, 3875552, 68.6, 10.5),
    ];
    pub(crate) const DESKTOP: &[(u64, u64, f64, f64)] = &[
        (1641930, 1715560, 46.1, 46.1),
        (1167512, 1744528, 74.0, 46.1),
        (1895538, 2138320, 13.5, 13.5),
        (1973244, 2975344, 13.3, 13.3),
        (1999424, 3390848, 12.5, 12.5),
        (2099349, 3842336, 47.0, 12.5),
        (2060822, 3842336, 87.9, 12.5),
        (1845306, 3842336, 88.6, 12.5),
        (1993942, 1531336, 94.7, 12.5),
        (1958527, 2236720, 14.5, 12.5),
        (2064825, 2916848, 81.2, 12.5),
        (1955497, 3261920, 13.1, 12.5),
        (1982024, 3526304, 50.7, 12.5),
        (1962562, 3526304, 11.5, 11.4),
        (2042877, 3628560, 14.8, 11.4),
        (1901853, 3743984, 65.7, 11.1),
        (1988635, 3863488, 86.7, 11.1),
        (1939308, 3863488, 26.8, 11.1),
        (2055874, 3863488, 11.7, 11.1),
        (2191171, 2052864, 10.5, 9.8),
        (1877274, 2636416, 17.5, 9.8),
        (1811142, 3330688, 15.7, 9.8),
        (2154637, 3602032, 14.5, 9.8),
        (1845464, 3716576, 61.5, 9.8),
        (1757836, 3896240, 13.3, 9.8),
    ];
}

#[cfg(test)]
mod tests {
    use super::*;

    const W: Duration = Duration::from_secs(2);

    fn window(loss: f64, rtt: Option<f64>, min_rtt: Option<f64>) -> LinkWindow {
        LinkWindow {
            span_ms: 2000,
            send_bps: 3_000_000,
            packets_sent: 600,
            loss,
            rtt_ms: rtt,
            min_rtt_ms: min_rtt,
            nacks_per_s: 0.0,
            plis_per_s: 0.0,
            remb_bps: None,
        }
    }

    fn clean() -> LinkWindow {
        window(0.0, Some(40.0), Some(40.0))
    }

    fn lossy() -> LinkWindow {
        window(0.08, Some(60.0), Some(40.0))
    }

    fn severe() -> LinkWindow {
        window(0.3, Some(60.0), Some(40.0))
    }

    fn slots() -> Vec<SlotView> {
        vec![
            SlotView {
                slot: 0,
                flight_id: 10,
                display_area: 640 * 360,
                pinned_full: false,
            },
            SlotView {
                slot: 1,
                flight_id: 11,
                display_area: 200 * 112,
                pinned_full: false,
            },
            SlotView {
                slot: 2,
                flight_id: 12,
                display_area: 320 * 180,
                pinned_full: false,
            },
        ]
    }

    struct Run {
        ladder: Ladder,
        now: Instant,
        slots: Vec<SlotView>,
    }

    impl Run {
        fn new() -> Self {
            Self {
                ladder: Ladder::new(LadderConfig::default()),
                now: Instant::now() + Duration::from_secs(1000),
                slots: slots(),
            }
        }

        fn feed(&mut self, w: LinkWindow) -> Vec<TierChange> {
            self.now += W;
            self.ladder.step(self.now, &w, &self.slots)
        }

        fn feed_n(&mut self, w: LinkWindow, n: usize) -> Vec<TierChange> {
            (0..n).flat_map(|_| self.feed(w)).collect()
        }

        fn tiers(&self) -> Vec<Tier> {
            self.slots
                .iter()
                .map(|s| self.ladder.tier(s.slot, s.flight_id))
                .collect()
        }
    }

    #[test]
    fn verdicts_follow_the_thresholds() {
        let l = Ladder::new(LadderConfig::default());
        assert_eq!(l.judge(&clean()), Verdict::Clean);
        assert_eq!(l.judge(&lossy()), Verdict::Congested);
        assert_eq!(l.judge(&severe()), Verdict::Severe);
        assert_eq!(
            l.judge(&window(0.03, Some(40.0), Some(40.0))),
            Verdict::Fair
        );
        assert_eq!(
            l.judge(&window(0.0, Some(250.0), Some(40.0))),
            Verdict::Congested,
            "a queue shows as RTT well above the floor"
        );
        assert_eq!(
            l.judge(&window(0.0, Some(1200.0), Some(40.0))),
            Verdict::Severe
        );
        assert_eq!(
            l.judge(&window(0.0, Some(400.0), Some(300.0))),
            Verdict::Clean,
            "a long but steady RTT is not a queue"
        );
        assert_eq!(
            l.judge(&window(0.0, Some(95.0), Some(12.5))),
            Verdict::Clean,
            "RTT jitter on a LAN is not a queue"
        );
        let mut plis = clean();
        plis.plis_per_s = 1.0;
        assert_eq!(l.judge(&plis), Verdict::Congested);
        let mut remb = clean();
        remb.remb_bps = Some(1_000_000);
        assert_eq!(l.judge(&remb), Verdict::Clean, "REMB alone never congests");
        remb.loss = 0.02;
        assert_eq!(
            l.judge(&remb),
            Verdict::Congested,
            "REMB backs up some loss"
        );
    }

    #[test]
    fn one_congested_window_moves_nothing() {
        let mut r = Run::new();
        assert!(r.feed(lossy()).is_empty());
        assert!(r.feed(clean()).is_empty());
        assert!(r.feed(lossy()).is_empty());
    }

    #[test]
    fn sustained_congestion_demotes_the_smallest_feed_first() {
        let mut r = Run::new();
        assert!(r.feed(lossy()).is_empty());
        let c = r.feed(lossy());
        assert_eq!(c.len(), 1);
        assert_eq!((c[0].slot, c[0].to), (1, Tier::Reduced));
        assert_eq!(c[0].reason, ChangeReason::Congested);
    }

    #[test]
    fn the_windows_after_a_switch_are_masked_and_demotes_wait_out_the_dwell() {
        let mut r = Run::new();
        r.feed_n(lossy(), 2);
        assert!(r.feed(lossy()).is_empty());
        assert_eq!(r.ladder.state().verdict, Some(Verdict::Masked));
        /* Two counted windows after the mask, and past the 3 s dwell. */
        assert!(r.feed(lossy()).is_empty());
        let c = r.feed(lossy());
        assert_eq!(c.len(), 1);
        assert_eq!(c[0].slot, 2, "the next smallest feed");
        assert_eq!(r.tiers(), vec![Tier::Full, Tier::Reduced, Tier::Reduced]);
    }

    #[test]
    fn a_severe_window_moves_every_full_feed_at_once() {
        let mut r = Run::new();
        let c = r.feed(severe());
        assert_eq!(c.len(), 3);
        assert!(c.iter().all(|c| c.reason == ChangeReason::Severe));
        assert_eq!(r.tiers(), vec![Tier::Reduced; 3]);
        assert!(
            r.feed_n(severe(), 5).is_empty(),
            "no rung below Reduced yet"
        );
    }

    #[test]
    fn a_pinned_feed_is_never_demoted_and_returns_to_full_at_once() {
        let mut r = Run::new();
        r.feed(severe());
        r.slots[0].pinned_full = true;
        let c = r.feed(severe());
        assert_eq!(c.len(), 1);
        assert_eq!(
            (c[0].slot, c[0].to, c[0].reason),
            (0, Tier::Full, ChangeReason::Pinned)
        );
        r.feed_n(severe(), 5);
        assert_eq!(r.ladder.tier(0, 10), Tier::Full);
    }

    #[test]
    fn promotion_waits_for_a_clean_spell_and_takes_the_largest_feed_first() {
        let mut r = Run::new();
        r.feed(severe());
        /* The window after the switch is masked; the next opens a 20 s spell. */
        assert!(r.feed_n(clean(), 11).is_empty());
        let c = r.feed(clean());
        assert_eq!(c.len(), 1);
        assert_eq!((c[0].slot, c[0].to), (0, Tier::Full));
        assert_eq!(c[0].reason, ChangeReason::Recovered);
        /* The next one waits a whole spell again. */
        assert!(r.feed_n(clean(), 11).is_empty());
        assert_eq!(r.feed(clean())[0].slot, 2);
    }

    #[test]
    fn a_fair_window_restarts_the_clean_spell() {
        let mut r = Run::new();
        r.feed(severe());
        r.feed_n(clean(), 8);
        r.feed(window(0.03, Some(40.0), Some(40.0)));
        assert!(r.feed_n(clean(), 10).is_empty());
        assert_eq!(r.feed(clean()).len(), 1);
    }

    #[test]
    fn a_failed_promote_doubles_the_wait_and_a_steady_spell_resets_it() {
        let mut r = Run::new();
        r.feed(severe());
        assert_eq!(r.feed_n(clean(), 12).len(), 1);
        assert_eq!(r.ladder.state().promote_wait_s, 20);
        /* Congestion inside the trial: masked once, then two counted. */
        r.feed_n(lossy(), 3);
        assert_eq!(r.ladder.state().promote_wait_s, 40);
        assert_eq!(r.ladder.tier(0, 10), Tier::Reduced);
        assert!(r.feed_n(clean(), 21).is_empty());
        assert_eq!(r.feed(clean()).len(), 1, "promoted after 40 s");
        r.feed_n(lossy(), 3);
        assert_eq!(r.ladder.state().promote_wait_s, 80);
        /* Mild trouble holds the tiers for five minutes; then the wait resets. */
        r.feed_n(window(0.03, Some(40.0), Some(40.0)), 150);
        assert_eq!(r.ladder.state().promote_wait_s, 20);
    }

    #[test]
    fn remb_headroom_shortens_the_promote_wait() {
        let mut r = Run::new();
        r.feed(severe());
        let mut roomy = clean();
        roomy.send_bps = 1_050_000;
        roomy.remb_bps = Some(10_000_000);
        assert!(r.feed_n(roomy, 6).is_empty());
        assert_eq!(r.feed(roomy).len(), 1, "promoted after 10 s, not 20");
    }

    fn remb_window(send_bps: u64, remb_bps: u64, rtt: f64, min_rtt: f64) -> LinkWindow {
        LinkWindow {
            send_bps,
            remb_bps: Some(remb_bps),
            ..window(0.0, Some(rtt), Some(min_rtt))
        }
    }

    #[test]
    fn remb_below_the_send_rate_alone_never_demotes() {
        let mut r = Run::new();
        assert!(r
            .feed_n(remb_window(2_000_000, 1_500_000, 90.0, 12.0), 30)
            .is_empty());
    }

    #[test]
    fn remb_is_ignored_while_a_new_viewer_ramps_up() {
        let mut r = Run::new();
        let mut w = remb_window(2_000_000, 400_000, 14.0, 12.0);
        w.loss = 0.02;
        r.feed(w);
        assert_eq!(r.ladder.state().verdict, Some(Verdict::Fair));
        r.feed_n(w, 4);
        assert_eq!(r.ladder.state().verdict, Some(Verdict::Fair), "8 s in");
        r.feed(w);
        assert_eq!(
            r.ladder.state().verdict,
            Some(Verdict::Congested),
            "10 s in"
        );
    }

    #[test]
    fn a_short_remb_holds_back_a_promote_until_it_recovers() {
        let mut r = Run::new();
        r.feed(severe());
        let short = remb_window(1_050_000, 1_500_000, 14.0, 12.0);
        assert!(r.feed_n(short, 20).is_empty(), "1.5 Mbps cannot carry 2.2");
        let roomy = remb_window(1_050_000, 2_500_000, 14.0, 12.0);
        assert_eq!(r.feed(roomy).len(), 1);
    }

    /// A Wi-Fi LAN's RTT swings well above its floor on a clean link. That
    /// must not keep a demoted feed from coming back.
    #[test]
    fn rtt_jitter_does_not_hold_back_a_promote() {
        let mut r = Run::new();
        r.feed_n(lossy(), 2);
        assert_eq!(r.ladder.tier(1, 11), Tier::Reduced);
        let calm = window(0.0, Some(14.0), Some(12.5));
        let jitter = window(0.0, Some(88.0), Some(12.5));
        let mut changes = Vec::new();
        for n in 0..12 {
            changes.extend(r.feed(if n % 2 == 0 { jitter } else { calm }));
        }
        assert_eq!(
            changes.len(),
            1,
            "promoted within the 20 s dwell plus the mask"
        );
        assert_eq!(r.ladder.tier(1, 11), Tier::Full);
    }

    /// The measured Deck viewers, each reading fed twice so it lasts two
    /// windows.
    #[test]
    fn measured_clean_lan_viewers_are_never_demoted() {
        use super::measured::{DESKTOP, PHONE};
        for samples in [PHONE, DESKTOP] {
            let mut r = Run::new();
            for &(send, remb, rtt, min_rtt) in samples {
                let w = remb_window(send, remb, rtt, min_rtt);
                assert!(r.feed_n(w, 2).is_empty(), "demoted on {w:?}");
            }
        }
    }

    #[test]
    fn a_rebound_or_dropped_slot_starts_over_on_full() {
        let mut r = Run::new();
        r.feed(severe());
        r.slots[1].flight_id = 99;
        r.slots.remove(2);
        r.feed(window(0.03, Some(40.0), Some(40.0)));
        assert_eq!(r.ladder.tier(1, 99), Tier::Full);
        assert_eq!(r.ladder.state().tiers.len(), 1);
    }
}
