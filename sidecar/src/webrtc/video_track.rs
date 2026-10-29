//! One slot's outbound H.264 track, packetised here rather than inside
//! webrtc-rs so the sidecar knows the RTP timestamp every frame goes out
//! with. `TrackLocalStaticSample` starts its timestamp at a private random
//! value, which makes the timestamp a browser reports for a presented frame
//! (`requestVideoFrameCallback` metadata `rtpTimestamp`) impossible to match
//! back to the frame. Owning the packetizer lets each frame's mission-time
//! stamp be published under the exact RTP timestamp it carries.

use std::sync::Arc;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use tokio::sync::Mutex;
use webrtc::rtp::codecs::h264::H264Payloader;
use webrtc::rtp::packetizer::{new_packetizer, Packetizer};
use webrtc::rtp::sequence::new_random_sequencer;
use webrtc::rtp_transceiver::rtp_codec::RTCRtpCodecCapability;
use webrtc::track::track_local::track_local_static_rtp::TrackLocalStaticRTP;

use super::outbox::{Outbox, SentFrame};
use crate::shared_mem::CaptureStamp;

/// RTP clock for video, fixed by RFC 6184 for H.264.
pub const H264_CLOCK_RATE: u32 = 90_000;

/// Same outbound MTU webrtc-rs uses for its own sample tracks.
const RTP_OUTBOUND_MTU: usize = 1200;

pub struct VideoTrack {
    rtp: Arc<TrackLocalStaticRTP>,
    packetizer: Mutex<Paced>,
    /// Where frame stamps for this track go. `None` sends none (tests).
    stamps: Option<Outbox>,
}

impl VideoTrack {
    pub fn new(
        codec: RTCRtpCodecCapability,
        id: String,
        stream_id: String,
        stamps: Option<Outbox>,
    ) -> Self {
        let packetizer = new_packetizer(
            RTP_OUTBOUND_MTU,
            0, // payload type and SSRC are set per binding at write time
            0,
            Box::new(H264Payloader::default()),
            Box::new(new_random_sequencer()),
            H264_CLOCK_RATE,
        );
        Self {
            rtp: Arc::new(TrackLocalStaticRTP::new(codec, id, stream_id)),
            packetizer: Mutex::new(Paced {
                packetizer: Box::new(packetizer),
                clock: MediaClock::default(),
            }),
            stamps,
        }
    }

    /// The webrtc-rs track to `add_track` onto the peer connection.
    pub fn rtp_track(&self) -> Arc<TrackLocalStaticRTP> {
        self.rtp.clone()
    }

    /**
     * Packetise and send one encoded frame (Annex-B NALs) captured at `time`,
     * `keyframe` when it is an IDR.
     * When `capture` carries a UT, its stamp is queued for the control channel
     * before the frame's packets are written. Never waits on the data channel.
     * Returns the frame's RTP timestamp, or `None` when nothing was sent.
     */
    pub async fn write_frame(
        &self,
        data: &[u8],
        time: CaptureTime,
        keyframe: bool,
        flight_id: u32,
        capture: &CaptureStamp,
    ) -> webrtc::error::Result<Option<u32>> {
        /* Unbound or paused everywhere (`all` is true with no bindings): send
        nothing and leave the sequence untouched, matching
        TrackLocalStaticSample. */
        if self.rtp.all_binding_paused().await {
            return Ok(None);
        }
        let packets = self.packetize(data, time, capture).await?;
        let Some(first) = packets.first() else {
            return Ok(None);
        };
        let rtp_timestamp = first.header.timestamp;

        if let Some(stamps) = &self.stamps {
            let sent = SentFrame {
                wall_ms: unix_ms_now(),
                bytes: data.len() as u32,
                keyframe,
            };
            stamps.offer_stamp(flight_id, rtp_timestamp, capture, sent);
        }

        for p in &packets {
            self.rtp.write_rtp_with_extensions(p, &[]).await?;
        }
        Ok(Some(rtp_timestamp))
    }

    /// Every packet of one frame shares the timestamp, which is the last
    /// frame's plus the media time between their captures.
    async fn packetize(
        &self,
        data: &[u8],
        time: CaptureTime,
        capture: &CaptureStamp,
    ) -> webrtc::error::Result<Vec<webrtc::rtp::packet::Packet>> {
        let mut paced = self.packetizer.lock().await;
        let step = paced.clock.step(time, capture.ut, capture.epoch);
        paced.packetizer.skip_samples(step);
        Ok(paced.packetizer.packetize(&data.to_vec().into(), 0)?)
    }
}

struct Paced {
    packetizer: Box<dyn Packetizer + Send + Sync>,
    clock: MediaClock,
}

/// When a frame was captured, on the two clocks that can tell.
#[derive(Debug, Clone, Copy)]
pub struct CaptureTime {
    /// The game's real-time clock at the render the frame came from, in ms
    /// (the ring's `capture_ts_ms`). Not Unix time; only its differences count.
    pub game_clock_ms: f64,
    /// When the sidecar took the frame from the ring: the fallback for a
    /// writer whose clock did not advance.
    pub picked_up: Instant,
}

/**
 * Spaces one track's RTP timestamps by when each frame was captured, so the
 * receiver's jitter buffer and renderer play them at the rhythm they were
 * captured at rather than the rhythm they happened to be encoded and sent at.
 *
 * Each step is the UT between the two frames, since that is how far the
 * picture moved (physics ticks fall unevenly across render frames, so equal
 * real time between captures can hold one tick or three), divided by the
 * game's pace in UT per real second over the last couple of seconds. A game
 * running at 0.9x then plays at real time with every step in proportion to its
 * UT, rather than at 0.9x with a correction added to each step. What the pace
 * misses (it moves, and rounds to whole RTP ticks) is repaid by a small share
 * of how far media time has fallen behind real time, and media time is never
 * let more than `MAX_BEHIND_MS` either side of it.
 *
 * Whether a UT step is the picture's motion is judged on the game's pace over
 * the last fraction of a second, not on the one step: at 1x a loaded game
 * runs two physics ticks in one short render frame and one in a long one, so
 * a single step can be twice or half the real time between its captures, and
 * judging each step alone sent about one in twelve of them to real time. A UT
 * step is followed while that pace is near 1x and the scaled step is within
 * the debt cap of the real one. Anything else is the real time (plus a share
 * of the debt): physics or on-rails warp, a pause, a new epoch, a camera
 * rebound to this track, or a hitch where the game stood still for longer
 * than the cap, whose frame arrives that late however it is stamped. The debt
 * is booked in the whole RTP ticks actually sent, so rounding cannot drift.
 */
#[derive(Debug)]
struct MediaClock {
    last: Option<LastFrame>,
    /// Real time minus media time since the first frame, ms.
    behind_ms: f64,
    /// The game's recent pace in UT per real second, smoothed over about
    /// `PACE_SMOOTHING_MS` of the steps that could be motion. Decides whether
    /// a step is followed at all.
    pace: f64,
    /// UT and real time of the followed steps, each smoothed over about
    /// `SCALE_SMOOTHING_MS`. Their ratio is the pace a followed step is divided
    /// by: steadier than `pace`, so steps of one size stay one size.
    scale_ut_ms: f64,
    scale_real_ms: f64,
}

impl Default for MediaClock {
    fn default() -> Self {
        Self {
            last: None,
            behind_ms: 0.0,
            pace: 1.0,
            scale_ut_ms: SCALE_PRIOR_MS,
            scale_real_ms: SCALE_PRIOR_MS,
        }
    }
}

#[derive(Debug, Clone, Copy)]
struct LastFrame {
    time: CaptureTime,
    ut: Option<f64>,
    epoch: u32,
}

/// Share of the gap to real time each real-time step repays: about a second
/// to settle.
const MEDIA_CLOCK_GAIN: f64 = 0.1;

/// Share of the gap a followed step repays. The scale already keeps those
/// steps on real time, so this only mops up what it misses, and is kept small
/// so it barely moves one step against the next.
const FOLLOW_GAIN: f64 = 0.05;

/// How far media time may run from real time: about two frames at 25 fps.
const MAX_BEHIND_MS: f64 = 80.0;

/**
 * Longest real-time step between two frames. RTP timestamps wrap, so a
 * receiver reads a step past half their range (about 6.6 h at 90 kHz) as a
 * step backwards; a feed idle longer than this resumes one hour on.
 */
const MAX_STEP_MS: f64 = 3_600_000.0;

/**
 * Real time the pace is smoothed over. Long enough that a frame's uneven
 * ticks do not move it far, short enough that entering 2x warp is caught
 * within a few frames, and the cap bounds what those frames can owe.
 */
const PACE_SMOOTHING_MS: f64 = 300.0;

/// The pace band UT steps are followed in: below it the game is paused or
/// crawling, above it warping (physics warp starts at 2x).
const PACE_FOLLOWED: std::ops::Range<f64> = 0.5..1.5;

/**
 * Real time the scale is smoothed over: long enough that a run of three-tick
 * steps does not shrink the next one-tick step, short enough to follow the
 * game as its load changes over a flight.
 */
const SCALE_SMOOTHING_MS: f64 = 2000.0;

/// The scale starts as if it had seen 1x steps of a 30 fps frame, so the
/// first steps are not divided by whatever pace one uneven step suggests.
const SCALE_PRIOR_MS: f64 = 1000.0 / 30.0;

impl MediaClock {
    /// RTP ticks from the last frame on this track to this one.
    fn step(&mut self, time: CaptureTime, ut: Option<f64>, epoch: u32) -> u32 {
        let Some(last) = self.last.replace(LastFrame { time, ut, epoch }) else {
            return 0;
        };
        let game_clock = time.game_clock_ms - last.time.game_clock_ms;
        let real = if game_clock.is_finite() && game_clock > 0.0 {
            game_clock
        } else {
            time.picked_up
                .saturating_duration_since(last.time.picked_up)
                .as_secs_f64()
                * 1000.0
        }
        .min(MAX_STEP_MS);
        let game = match (ut, last.ut) {
            (Some(now), Some(then)) if epoch == last.epoch => (now - then) * 1000.0,
            _ => f64::NAN,
        };
        let within_cap = (game / self.scale() - real).abs() <= MAX_BEHIND_MS;
        if within_cap && real > 0.0 {
            let weight = real / (real + PACE_SMOOTHING_MS);
            self.pace += weight * (game / real - self.pace);
        }
        /* Out of the band is warp or a pause, and the steps that led into it
        (a pace takes a few frames to cross the band's edge) say nothing about
        the game's 1x pace, so the scale starts again from 1x when the game
        is back. A hitch leaves the band alone and keeps the scale. */
        if !PACE_FOLLOWED.contains(&self.pace) {
            self.scale_ut_ms = SCALE_PRIOR_MS;
            self.scale_real_ms = SCALE_PRIOR_MS;
        }
        let step_ms = if within_cap && game > 0.0 && PACE_FOLLOWED.contains(&self.pace) {
            let weight = real / (real + SCALE_SMOOTHING_MS);
            self.scale_ut_ms += weight * (game - self.scale_ut_ms);
            self.scale_real_ms += weight * (real - self.scale_real_ms);
            (game / self.scale() + FOLLOW_GAIN * self.behind_ms)
                .clamp(
                    real + self.behind_ms - MAX_BEHIND_MS,
                    real + self.behind_ms + MAX_BEHIND_MS,
                )
                .max(0.0)
        } else {
            (real + MEDIA_CLOCK_GAIN * self.behind_ms).max(0.0)
        };
        let ticks = rtp_samples(step_ms);
        self.behind_ms += real - ticks as f64 * 1000.0 / H264_CLOCK_RATE as f64;
        ticks
    }

    /// UT per real ms over the recent followed steps.
    fn scale(&self) -> f64 {
        self.scale_ut_ms / self.scale_real_ms
    }
}

/// RTP clock ticks spanning `ms`.
fn rtp_samples(ms: f64) -> u32 {
    (ms * H264_CLOCK_RATE as f64 / 1000.0) as u32
}

fn unix_ms_now() -> f64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs_f64() * 1000.0)
        .unwrap_or(0.0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::ServerMessage;
    use crate::webrtc::outbox::ControlChannel;
    use std::time::Duration;
    use webrtc::api::media_engine::MIME_TYPE_H264;

    fn track() -> VideoTrack {
        VideoTrack::new(
            RTCRtpCodecCapability {
                mime_type: MIME_TYPE_H264.to_owned(),
                ..Default::default()
            },
            "video".to_owned(),
            "stream".to_owned(),
            None,
        )
    }

    /// One IDR-ish NAL: start code + a type-5 header byte + payload.
    fn nal(len: usize) -> Vec<u8> {
        let mut v = vec![0, 0, 0, 1, 0x65];
        v.resize(len, 0xAB);
        v
    }

    /// A frame captured `ms` into the game clock at `ut` seconds.
    fn at(ms: f64, ut: Option<f64>) -> (CaptureTime, CaptureStamp) {
        (
            CaptureTime {
                game_clock_ms: ms,
                picked_up: Instant::now(),
            },
            CaptureStamp::at(ut, 0),
        )
    }

    async fn frame_timestamps(t: &VideoTrack, data: &[u8], ms: f64, ut: Option<f64>) -> Vec<u32> {
        let (time, stamp) = at(ms, ut);
        t.packetize(data, time, &stamp)
            .await
            .unwrap()
            .iter()
            .map(|p| p.header.timestamp)
            .collect()
    }

    /// The step between two frames is in front of the second frame, the one
    /// it belongs to, and follows the UT between them.
    #[tokio::test]
    async fn timestamps_follow_the_ut_between_captures() {
        let t = track();
        let a = frame_timestamps(&t, &nal(64), 0.0, Some(100.0)).await;
        let b = frame_timestamps(&t, &nal(64), 40.0, Some(100.06)).await;
        let c = frame_timestamps(&t, &nal(64), 80.0, Some(100.08)).await;
        let ms = |ts: u32| ts as f64 * 1000.0 / H264_CLOCK_RATE as f64;
        let ab = ms(b[0].wrapping_sub(a[0]));
        let bc = ms(c[0].wrapping_sub(b[0]));
        assert!(
            ab > 50.0 && bc < 25.0,
            "three ticks then one in equal real time step 3:1, not 1:1: {ab}, {bc}"
        );
        assert!(
            (ab / bc - 3.0).abs() < 0.2,
            "in proportion to their UT: {ab} / {bc}"
        );
    }

    impl MediaClock {
        /// The step in ms, as the RTP ticks it sends.
        fn step_ms(&mut self, time: CaptureTime, ut: Option<f64>, epoch: u32) -> f64 {
            self.step(time, ut, epoch) as f64 * 1000.0 / H264_CLOCK_RATE as f64
        }
    }

    /// With no UT, or a UT step that is not motion (warp, a pause), the step
    /// is the game's real time between the captures.
    #[test]
    fn steps_fall_back_to_real_time() {
        let mut c = MediaClock::default();
        let (t0, _) = at(1000.0, None);
        assert_eq!(c.step_ms(t0, None, 0), 0.0, "the first frame has no step");
        let (t1, _) = at(1033.0, None);
        assert!((c.step_ms(t1, None, 0) - 33.0).abs() < 1e-6);
        let (t2, _) = at(1066.0, None);
        c.step_ms(t2, Some(5.0), 0);
        let (t3, _) = at(1100.0, None);
        assert!(
            (c.step_ms(t3, Some(15.0), 0) - 34.0).abs() < 1e-6,
            "ten seconds of UT in 34 ms is warp"
        );
        let (t4, _) = at(1133.0, None);
        assert!(
            (c.step_ms(t4, Some(15.0), 0) - 33.0).abs() < 1e-6,
            "a paused UT"
        );
        let (t5, _) = at(1166.0, None);
        assert!(
            (c.step_ms(t5, Some(15.033), 1) - 33.0).abs() < 1e-6,
            "a new epoch"
        );
        assert!(c.behind_ms.abs() < 1e-6, "real-time steps owe nothing");
    }

    /// A feed idle for hours resumes with a step under half the RTP range,
    /// and owes nothing for the time it skipped.
    #[test]
    fn a_long_idle_step_is_clamped() {
        let mut c = MediaClock::default();
        let (t0, _) = at(0.0, None);
        c.step(t0, None, 0);
        let (t1, _) = at(14.0 * 3_600_000.0, None);
        let ticks = c.step(t1, None, 0);
        assert!(ticks < 1 << 31, "under half the range: {ticks}");
        assert!((ticks as f64 * 1000.0 / H264_CLOCK_RATE as f64 - MAX_STEP_MS).abs() < 1.0);
        assert!(c.behind_ms.abs() < 1.0, "owes nothing: {}", c.behind_ms);
    }

    /**
     * A game below real time, captured unevenly: each step follows its UT, and
     * over many frames the media time stays a fixed distance from real time
     * instead of drifting.
     */
    #[test]
    fn media_time_holds_to_real_time_over_a_slow_game() {
        let mut c = MediaClock::default();
        let ticks = [1u32, 2, 3, 2, 1, 4, 2, 2, 3, 1];
        let (mut real, mut ut, mut media) = (0.0f64, 0.0f64, 0.0f64);
        let (t, _) = at(real, None);
        c.step_ms(t, Some(ut), 0);
        for i in 0..2000 {
            let n = ticks[i % ticks.len()] as f64;
            ut += n * 0.02;
            real += n * 20.0 / 0.9;
            let (t, _) = at(real, None);
            media += c.step_ms(t, Some(ut), 0);
        }
        let gap = real - media;
        assert!(
            gap > 0.0 && gap <= MAX_BEHIND_MS,
            "media time {gap} ms behind real time"
        );
    }

    /**
     * The Deck at load: a 0.88x game whose captures hold one to four physics
     * ticks, with render jitter on the real time between them and a hitch
     * every second or so where the game stands still for 200 ms. Once the
     * scale has settled every ordinary step is its UT divided by the pace, so
     * a one-tick step is a quarter of a four-tick one; hitches step on real
     * time; media time stays within the cap of real time throughout.
     */
    #[test]
    fn steps_scale_with_ut_in_a_slow_game() {
        const PACE: f64 = 0.88;
        let mut c = MediaClock::default();
        let ticks = [1u32, 2, 3, 2, 1, 4, 2, 2, 3, 1, 3, 2];
        let jitter = [6.0, -4.0, 0.0, -7.0, 3.0, 8.0, -2.0, -5.0];
        let (mut real, mut ut, mut media) = (0.0f64, 0.0f64, 0.0f64);
        let (t, _) = at(real, None);
        c.step_ms(t, Some(ut), 0);
        let mut prev_jitter = 0.0;
        for i in 0..1500 {
            let hitch = i % 45 == 44;
            let n = if hitch { 2 } else { ticks[i % ticks.len()] };
            let game = n as f64 * 20.0;
            let r = if hitch {
                200.0
            } else {
                let j = jitter[i % jitter.len()];
                let r = game / PACE + j - prev_jitter;
                prev_jitter = j;
                r
            };
            real += r;
            ut += game / 1000.0;
            let (t, _) = at(real, None);
            let step = c.step_ms(t, Some(ut), 0);
            media += step;
            assert!(
                (real - media).abs() <= MAX_BEHIND_MS + 0.1,
                "media time {} ms from real time at frame {i}",
                real - media
            );
            if i < 150 {
                continue;
            }
            if hitch {
                assert!(
                    (step - r).abs() <= MEDIA_CLOCK_GAIN * MAX_BEHIND_MS + 0.1,
                    "a hitch steps on real time: {step} for {r} ms real"
                );
            } else {
                let want = game / PACE;
                assert!(
                    (step - want).abs() <= 0.05 * want + 1.0,
                    "frame {i}: {n} ticks step {step} ms, not {want}"
                );
            }
        }
    }

    /**
     * Into 2x physics warp and back: the warp's UT steps are not the picture's
     * pace, so media time follows real time through it, and never strays more
     * than the cap from it either side.
     */
    #[test]
    fn media_time_stays_near_real_time_through_physics_warp() {
        let mut c = MediaClock::default();
        let (mut real, mut ut, mut media) = (0.0f64, 0.0f64, 0.0f64);
        let (t, _) = at(real, None);
        c.step_ms(t, Some(ut), 0);
        for warp in std::iter::repeat_n(1.0, 30)
            .chain(std::iter::repeat_n(2.0, 60))
            .chain(std::iter::repeat_n(1.0, 30))
        {
            real += 40.0;
            ut += 0.04 * warp;
            let (t, _) = at(real, None);
            media += c.step_ms(t, Some(ut), 0);
            assert!(
                (real - media).abs() <= MAX_BEHIND_MS + 0.1,
                "media time {} ms from real time at warp {warp}",
                real - media
            );
        }
        assert!(
            (real - media).abs() < 1.0,
            "and none of it is owed after: {}",
            real - media
        );
    }

    /**
     * A loaded game at 1x, as the Deck runs one: physics ticks land two in a
     * short render frame and one in a long one, and every second or so the
     * game stalls for a whole frame of 150 ms while UT moves on by the 40 ms
     * the physics limit allows. Each ordinary step follows its UT however far
     * it sits from the real time between the captures; the stall follows real
     * time, since its frame arrives that late regardless.
     */
    #[test]
    fn steps_follow_ut_through_uneven_ticks_at_1x() {
        let mut c = MediaClock::default();
        /* (real ms, UT ticks) per capture: 2:1 and 1:2 tick-to-frame ratios. */
        let steps = [
            (20.0, 2u32),
            (40.0, 1),
            (20.0, 1),
            (40.0, 2),
            (27.0, 2),
            (33.0, 1),
            (40.0, 2),
            (21.0, 1),
        ];
        let (mut real, mut ut, mut media) = (0.0f64, 0.0f64, 0.0f64);
        let (t, _) = at(real, None);
        c.step_ms(t, Some(ut), 0);
        let mut followed = 0;
        let mut ordinary = 0;
        for i in 0..600 {
            let stall = i % 40 == 39;
            let (r, n) = if stall {
                (150.0, 2)
            } else {
                steps[i % steps.len()]
            };
            real += r;
            ut += n as f64 * 0.02;
            let (t, _) = at(real, None);
            let step = c.step_ms(t, Some(ut), 0);
            media += step;
            let game = n as f64 * 20.0;
            if stall {
                assert!(
                    (step - r).abs() <= MEDIA_CLOCK_GAIN * MAX_BEHIND_MS + 0.1,
                    "a stall steps on real time: {step} for {r} ms real, {game} ms UT"
                );
            } else {
                ordinary += 1;
                if (step - game).abs() <= MEDIA_CLOCK_GAIN * MAX_BEHIND_MS + 0.1 {
                    followed += 1;
                }
            }
            assert!(
                (real - media).abs() <= MAX_BEHIND_MS + 0.1,
                "media time {} ms from real time",
                real - media
            );
        }
        assert_eq!(followed, ordinary, "every ordinary step follows its UT");
    }

    /**
     * 2x physics warp whose ticks fall unevenly, so some single steps look
     * like 1x: the pace, not the step, decides, and once it has risen the
     * steps stay on real time for the rest of the warp.
     */
    #[test]
    fn uneven_physics_warp_still_steps_on_real_time() {
        let mut c = MediaClock::default();
        let (mut real, mut ut, mut media) = (0.0f64, 0.0f64, 0.0f64);
        let (t, _) = at(real, None);
        c.step_ms(t, Some(ut), 0);
        let warp_ticks = [4u32, 2, 6, 3, 5, 4];
        for i in 0..150 {
            let n = if (30..120).contains(&i) {
                warp_ticks[i % warp_ticks.len()]
            } else {
                2
            };
            real += 40.0;
            ut += n as f64 * 0.02;
            let (t, _) = at(real, None);
            let step = c.step_ms(t, Some(ut), 0);
            media += step;
            assert!(
                (real - media).abs() <= MAX_BEHIND_MS + 0.1,
                "media time {} ms from real time at frame {i}",
                real - media
            );
            if (37..120).contains(&i) {
                assert!(
                    (step - 40.0).abs() <= MEDIA_CLOCK_GAIN * MAX_BEHIND_MS + 0.1,
                    "frame {i} of the warp steps on real time, not {step}"
                );
            }
        }
        assert!(
            (real - media).abs() < 1.0,
            "and none of it is owed after: {}",
            real - media
        );
    }

    /// Rounding each step down to whole RTP ticks is booked, so an hour of
    /// steps that do not divide the tick lands on real time.
    #[test]
    fn rounding_to_rtp_ticks_does_not_drift() {
        let mut c = MediaClock::default();
        let (mut real, mut ut, mut ticks) = (0.0f64, 0.0f64, 0u64);
        let (t, _) = at(real, None);
        c.step(t, Some(ut), 0);
        for _ in 0..(3600 * 30) {
            real += 33.337;
            ut += 0.033337;
            let (t, _) = at(real, None);
            ticks += c.step(t, Some(ut), 0) as u64;
        }
        let media = ticks as f64 * 1000.0 / H264_CLOCK_RATE as f64;
        assert!(
            (real - media).abs() < 1.0,
            "{} ms off after an hour",
            real - media
        );
    }

    #[tokio::test]
    async fn fragmented_frame_shares_one_timestamp_across_packets() {
        let t = track();
        let ts = frame_timestamps(&t, &nal(5000), 33.0, None).await;
        assert!(ts.len() > 1, "a 5 kB frame spans several FU-A packets");
        assert!(ts.iter().all(|&x| x == ts[0]));
    }

    #[tokio::test]
    async fn unbound_track_sends_nothing() {
        let t = track();
        let (time, stamp) = at(33.0, Some(1.0));
        let got = t
            .write_frame(&nal(64), time, false, 1, &stamp)
            .await
            .unwrap();
        assert_eq!(got, None);
    }

    /**
     * End to end over a real loopback connection: the stamp a viewer receives
     * on the control channel names the RTP timestamp the frame's packets
     * actually arrive with.
     */
    #[tokio::test]
    async fn stamp_names_the_rtp_timestamp_the_viewer_receives() {
        let (sent, stamps, rtp_seen, extra) = loopback_stamps(true).await;
        let (anchors, fov, width, live, subject, send) = extra.expect("a stamp arrived");
        let (bytes, keyframe, sent_wall_ms) = send;
        assert_eq!(bytes, Some(64), "the frame's encoded size rides its stamp");
        assert_eq!(keyframe, Some(true), "and whether it is a keyframe");
        assert!(
            sent_wall_ms.is_some_and(|ms| ms > 0.0),
            "and when it was sent"
        );
        assert_eq!(fov, Some(35.0), "the frame's fov rides its stamp");
        assert_eq!(width, Some(64), "and its size");
        assert_eq!(live, Some(2.5), "and its live bound");
        assert_eq!(
            subject,
            Some(42),
            "and the command state the viewer asked for"
        );
        assert_eq!(
            anchors,
            vec![
                ("mine".to_owned(), None),
                ("subject".to_owned(), Some("kerbcast".to_owned()))
            ],
            "the viewer gets its own anchors and the game's, never another browser's"
        );
        let matched: Vec<_> = stamps
            .iter()
            .filter(|(ts, _)| rtp_seen.contains(ts))
            .collect();
        assert!(
            !matched.is_empty(),
            "a stamped frame arrived: stamps={stamps:?} rtp={rtp_seen:?}"
        );
        for (ts, ut) in &matched {
            assert!(
                sent.contains(&(*ts, *ut)),
                "stamp ({ts}, {ut}) pairs the UT with the timestamp that frame was sent under"
            );
        }
    }

    /// A viewer that never asks for stamps (any SDK before 1.11) gets its
    /// frames and no stamps.
    #[tokio::test]
    async fn a_viewer_that_never_asks_gets_no_stamps() {
        let (_, stamps, rtp_seen, _) = loopback_stamps(false).await;
        assert!(!rtp_seen.is_empty(), "frames still arrive");
        assert!(stamps.is_empty(), "no stamps sent: {stamps:?}");
    }

    /**
     * Send stamped frames over a real loopback connection whose viewer has
     * (or has not) asked for stamps. Returns what was sent as (RTP timestamp,
     * UT), the stamps the viewer received, and the RTP timestamps it saw.
     */
    #[allow(clippy::type_complexity)]
    async fn loopback_stamps(
        wanted: bool,
    ) -> (
        Vec<(u32, f64)>,
        Vec<(u32, f64)>,
        Vec<u32>,
        Option<StampExtra>,
    ) {
        use tokio::sync::mpsc;
        use webrtc::api::media_engine::MediaEngine;
        use webrtc::api::APIBuilder;
        use webrtc::data_channel::data_channel_message::DataChannelMessage;
        use webrtc::peer_connection::configuration::RTCConfiguration;
        use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
        use webrtc::rtp_transceiver::rtp_codec::RTPCodecType;
        use webrtc::track::track_local::TrackLocal;

        async fn pc() -> Arc<webrtc::peer_connection::RTCPeerConnection> {
            let mut me = MediaEngine::default();
            me.register_default_codecs().unwrap();
            let api = APIBuilder::new().with_media_engine(me).build();
            Arc::new(
                api.new_peer_connection(RTCConfiguration::default())
                    .await
                    .unwrap(),
            )
        }

        #[derive(Debug)]
        enum Seen {
            Stamp(u32, f64, StampExtra),
            Rtp(u32),
        }
        let (tx, mut rx) = mpsc::unbounded_channel::<Seen>();

        /* Viewer: recv-only video plus the control channel, like the SDK. */
        let viewer = pc().await;
        viewer
            .add_transceiver_from_kind(RTPCodecType::Video, None)
            .await
            .unwrap();
        let viewer_dc = viewer
            .create_data_channel("kerbcast-control", None)
            .await
            .unwrap();
        let tx_dc = tx.clone();
        viewer_dc.on_message(Box::new(move |m: DataChannelMessage| {
            let tx = tx_dc.clone();
            Box::pin(async move {
                let msg: ServerMessage = serde_json::from_slice(&m.data).unwrap();
                if let ServerMessage::FrameStamp(p) = msg {
                    let anchors = p
                        .anchors
                        .unwrap_or_default()
                        .into_iter()
                        .map(|a| (a.id, a.owner))
                        .collect();
                    let _ = tx.send(Seen::Stamp(
                        p.rtp_timestamp,
                        p.capture_ut,
                        (
                            anchors,
                            p.fov,
                            p.width,
                            p.live_light_time_s,
                            p.command.map(|c| c.subject_persistent_id),
                            (p.encoded_bytes, p.keyframe, p.sent_wall_ms),
                        ),
                    ));
                }
            })
        }));
        let tx_rtp = tx.clone();
        viewer.on_track(Box::new(move |track, _, _| {
            let tx = tx_rtp.clone();
            Box::pin(async move {
                tokio::spawn(async move {
                    while let Ok((pkt, _)) = track.read_rtp().await {
                        let _ = tx.send(Seen::Rtp(pkt.header.timestamp));
                    }
                });
            })
        }));

        /* Sidecar side: one VideoTrack whose stamps go to the opened channel. */
        let sidecar = pc().await;
        let control: ControlChannel = Arc::new(Mutex::new(None));
        let vt = Arc::new(VideoTrack::new(
            RTCRtpCodecCapability {
                mime_type: webrtc::api::media_engine::MIME_TYPE_H264.to_owned(),
                sdp_fmtp_line:
                    "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"
                        .to_owned(),
                ..Default::default()
            },
            "video".to_owned(),
            "stream".to_owned(),
            Some({
                let outbox = Outbox::spawn(control.clone(), "peer:0:1".to_owned());
                outbox.set_stamps_wanted(wanted, wanted, wanted);
                outbox
            }),
        ));
        sidecar
            .add_track(vt.rtp_track() as Arc<dyn TrackLocal + Send + Sync>)
            .await
            .unwrap();
        let control_for_dc = control.clone();
        sidecar.on_data_channel(Box::new(move |dc| {
            let control = control_for_dc.clone();
            Box::pin(async move {
                let ready = dc.clone();
                dc.on_open(Box::new(move || {
                    Box::pin(async move {
                        *control.lock().await = Some(ready);
                    })
                }));
            })
        }));
        let (conn_tx, mut conn_rx) = mpsc::unbounded_channel();
        sidecar.on_peer_connection_state_change(Box::new(move |s| {
            if s == RTCPeerConnectionState::Connected {
                let _ = conn_tx.send(());
            }
            Box::pin(async {})
        }));

        let offer = viewer.create_offer(None).await.unwrap();
        let mut gather = viewer.gathering_complete_promise().await;
        viewer.set_local_description(offer).await.unwrap();
        let _ = gather.recv().await;
        sidecar
            .set_remote_description(viewer.local_description().await.unwrap())
            .await
            .unwrap();
        let answer = sidecar.create_answer(None).await.unwrap();
        let mut gather = sidecar.gathering_complete_promise().await;
        sidecar.set_local_description(answer).await.unwrap();
        let _ = gather.recv().await;
        viewer
            .set_remote_description(sidecar.local_description().await.unwrap())
            .await
            .unwrap();

        tokio::time::timeout(Duration::from_secs(10), conn_rx.recv())
            .await
            .expect("loopback connects");
        for _ in 0..100 {
            if control.lock().await.is_some() {
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        assert!(control.lock().await.is_some(), "control channel opened");

        /* Keep sending until a stamp and its frame have both been seen. */
        let mut sent = Vec::new();
        let mut stamps = Vec::new();
        let mut rtp_seen = Vec::new();
        let mut extra = None;
        let deadline = tokio::time::Instant::now() + Duration::from_secs(10);
        let mut n = 0u32;
        while tokio::time::Instant::now() < deadline {
            let ut = 1000.0 + n as f64;
            let stamp = CaptureStamp {
                fov: Some(35.0),
                size: Some((64, 36)),
                anchors: Some(std::sync::Arc::new(vec![
                    frame_anchor("mine", Some("peer:0:1")),
                    frame_anchor("another-browser", Some("peer:0:2")),
                    frame_anchor("subject", Some("kerbcast")),
                ])),
                live_light_time_s: Some(2.5),
                command: Some(std::sync::Arc::new(
                    serde_json::from_str(
                        r#"{"hasFix":true,"frameHeightPx":36,"framingFits":true,"occluded":false,"occluder":"","sunlit":true,"subjectPersistentId":42}"#,
                    )
                    .unwrap(),
                )),
                ..CaptureStamp::at(Some(ut), 2)
            };
            if let Some(ts) = vt
                .write_frame(&nal(64), at(n as f64 * 33.0, None).0, true, 9, &stamp)
                .await
                .unwrap()
            {
                sent.push((ts, ut));
            }
            n += 1;
            tokio::time::sleep(Duration::from_millis(33)).await;
            while let Ok(seen) = rx.try_recv() {
                match seen {
                    Seen::Stamp(ts, ut, e) => {
                        stamps.push((ts, ut));
                        extra = Some(e);
                    }
                    Seen::Rtp(ts) => rtp_seen.push(ts),
                }
            }
            let done = if wanted {
                stamps.iter().any(|(ts, _)| rtp_seen.contains(ts))
            } else {
                /* Long enough for a stamp to have overtaken its frame. */
                rtp_seen.len() >= 10
            };
            if done {
                break;
            }
        }

        viewer.close().await.ok();
        sidecar.close().await.ok();
        (sent, stamps, rtp_seen, extra)
    }

    /// Anchor ids with owners, fov and width from one received stamp.
    type StampExtra = (
        Vec<(String, Option<String>)>,
        Option<f64>,
        Option<u32>,
        Option<f64>,
        Option<u32>,
        (Option<u32>, Option<bool>, Option<f64>),
    );

    fn frame_anchor(id: &str, owner: Option<&str>) -> crate::protocol::FrameAnchor {
        crate::protocol::FrameAnchor {
            id: id.into(),
            owner: owner.map(str::to_owned),
            observed: false,
            x: Some(10.0),
            y: Some(5.0),
            in_frame: true,
            behind: Some(false),
            distance_m: Some(100.0),
            apparent_radius_px: Some(3.0),
            angle_off_axis: Some(0.5),
            forward: None,
            up: None,
            occluded: None,
        }
    }

    mod churn {
        use super::*;
        use std::net::IpAddr;
        use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
        use webrtc::api::media_engine::MediaEngine;
        use webrtc::api::setting_engine::SettingEngine;
        use webrtc::api::APIBuilder;
        use webrtc::data_channel::data_channel_message::DataChannelMessage;
        use webrtc::data_channel::data_channel_state::RTCDataChannelState;
        use webrtc::data_channel::RTCDataChannel;
        use webrtc::peer_connection::configuration::RTCConfiguration;
        use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
        use webrtc::peer_connection::RTCPeerConnection;
        use webrtc::rtp_transceiver::rtp_codec::RTPCodecType;
        use webrtc::track::track_local::TrackLocal;
        use webrtc::util::vnet::chunk::Chunk;
        use webrtc::util::vnet::net::{Net, NetConfig};
        use webrtc::util::vnet::router::{Router, RouterConfig};

        const TRACKS_PER_VIEWER: usize = 3;
        /// 30 s of 30 fps video.
        const FRAMES_PER_PHASE: u32 = 900;

        /// What one viewer has received so far.
        #[derive(Default)]
        struct Seen {
            rtp_packets: AtomicUsize,
            stamps: AtomicUsize,
            rtp_timestamps: Mutex<Vec<u32>>,
            stamp_pairs: Mutex<Vec<(u32, f64)>>,
            /// Sequence numbers of the stand-in status messages, in arrival order.
            status: Mutex<Vec<u64>>,
        }

        struct Viewer {
            pc: Arc<RTCPeerConnection>,
            dc: Arc<RTCDataChannel>,
            seen: Arc<Seen>,
        }

        /// The sidecar's half of one browser connection.
        struct SidecarPeer {
            pc: Arc<RTCPeerConnection>,
            tracks: Vec<Arc<VideoTrack>>,
            outbox: Outbox,
        }

        impl SidecarPeer {
            /// What the reaper does to a dead peer.
            async fn reap(&self) {
                self.outbox.shutdown();
                self.pc.close().await.ok();
            }
        }

        /// Padding that brings a status message near a real camera-state diff.
        const STATUS_PAD: usize = 400;

        /// A stand-in for a state broadcast, numbered so loss or reordering shows.
        fn status_message(seq: u64) -> ServerMessage {
            ServerMessage::Error(crate::protocol::ErrorPayload {
                message: format!("status {seq} {}", "x".repeat(STATUS_PAD)),
                ..Default::default()
            })
        }

        fn status_seq(msg: &ServerMessage) -> Option<u64> {
            let ServerMessage::Error(e) = msg else {
                return None;
            };
            e.message
                .strip_prefix("status ")?
                .split(' ')
                .next()?
                .parse()
                .ok()
        }

        async fn net_on(router: &Arc<Mutex<Router>>, ip: &str) -> Arc<Net> {
            let net = Arc::new(Net::new(Some(NetConfig {
                static_ips: vec![ip.to_owned()],
                ..Default::default()
            })));
            let nic = net.get_nic().unwrap();
            router.lock().await.add_net(nic.clone()).await.unwrap();
            nic.lock().await.set_router(router.clone()).await.unwrap();
            net
        }

        async fn pc_on(net: Arc<Net>) -> Arc<RTCPeerConnection> {
            let mut me = MediaEngine::default();
            me.register_default_codecs().unwrap();
            let mut se = SettingEngine::default();
            se.set_vnet(Some(net));
            Arc::new(
                APIBuilder::new()
                    .with_media_engine(me)
                    .with_setting_engine(se)
                    .build()
                    .new_peer_connection(RTCConfiguration::default())
                    .await
                    .unwrap(),
            )
        }

        /**
         * One viewer (recv-only video transceivers plus the control channel,
         * like the SDK) connected to a fresh sidecar peer whose tracks stamp
         * onto that channel, as `KerbcastPeer::new` wires them.
         */
        async fn connect(viewer_net: Arc<Net>, sidecar_net: Arc<Net>) -> (Viewer, SidecarPeer) {
            let seen = Arc::new(Seen::default());
            let viewer = pc_on(viewer_net).await;
            for _ in 0..TRACKS_PER_VIEWER {
                viewer
                    .add_transceiver_from_kind(RTPCodecType::Video, None)
                    .await
                    .unwrap();
            }
            let dc = viewer
                .create_data_channel("kerbcast-control", None)
                .await
                .unwrap();
            let seen_dc = seen.clone();
            dc.on_message(Box::new(move |m: DataChannelMessage| {
                let seen = seen_dc.clone();
                Box::pin(async move {
                    match serde_json::from_slice::<ServerMessage>(&m.data) {
                        Ok(ServerMessage::FrameStamp(p)) => {
                            seen.stamps.fetch_add(1, Ordering::SeqCst);
                            seen.stamp_pairs
                                .lock()
                                .await
                                .push((p.rtp_timestamp, p.capture_ut));
                        }
                        Ok(msg) => {
                            if let Some(seq) = status_seq(&msg) {
                                seen.status.lock().await.push(seq);
                            }
                        }
                        Err(_) => {}
                    }
                })
            }));
            let seen_rtp = seen.clone();
            viewer.on_track(Box::new(move |track, _, _| {
                let seen = seen_rtp.clone();
                Box::pin(async move {
                    tokio::spawn(async move {
                        while let Ok((pkt, _)) = track.read_rtp().await {
                            seen.rtp_packets.fetch_add(1, Ordering::SeqCst);
                            seen.rtp_timestamps.lock().await.push(pkt.header.timestamp);
                        }
                    });
                })
            }));

            let sidecar = pc_on(sidecar_net).await;
            let control: ControlChannel = Arc::new(Mutex::new(None));
            let outbox = Outbox::spawn(control.clone(), "peer:0:1".to_owned());
            outbox.set_stamps_wanted(true, false, false);
            let mut tracks = Vec::new();
            for i in 0..TRACKS_PER_VIEWER {
                let vt = Arc::new(VideoTrack::new(
                    RTCRtpCodecCapability {
                        mime_type: webrtc::api::media_engine::MIME_TYPE_H264.to_owned(),
                        sdp_fmtp_line:
                            "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"
                                .to_owned(),
                        ..Default::default()
                    },
                    format!("video-slot{i}"),
                    format!("kerbcast-slot{i}"),
                    Some(outbox.clone()),
                ));
                sidecar
                    .add_track(vt.rtp_track() as Arc<dyn TrackLocal + Send + Sync>)
                    .await
                    .unwrap();
                tracks.push(vt);
            }
            let control_for_dc = control.clone();
            sidecar.on_data_channel(Box::new(move |dc| {
                let control = control_for_dc.clone();
                Box::pin(async move {
                    let ready = dc.clone();
                    dc.on_open(Box::new(move || {
                        Box::pin(async move {
                            *control.lock().await = Some(ready);
                        })
                    }));
                })
            }));
            let (conn_tx, mut conn_rx) = tokio::sync::mpsc::unbounded_channel();
            sidecar.on_peer_connection_state_change(Box::new(move |s| {
                if s == RTCPeerConnectionState::Connected {
                    let _ = conn_tx.send(());
                }
                Box::pin(async {})
            }));

            let offer = viewer.create_offer(None).await.unwrap();
            let mut gather = viewer.gathering_complete_promise().await;
            viewer.set_local_description(offer).await.unwrap();
            let _ = gather.recv().await;
            sidecar
                .set_remote_description(viewer.local_description().await.unwrap())
                .await
                .unwrap();
            let answer = sidecar.create_answer(None).await.unwrap();
            let mut gather = sidecar.gathering_complete_promise().await;
            sidecar.set_local_description(answer).await.unwrap();
            let _ = gather.recv().await;
            viewer
                .set_remote_description(sidecar.local_description().await.unwrap())
                .await
                .unwrap();

            tokio::time::timeout(Duration::from_secs(10), conn_rx.recv())
                .await
                .expect("viewer connects");
            for _ in 0..250 {
                if control.lock().await.is_some() {
                    break;
                }
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
            assert!(control.lock().await.is_some(), "control channel opened");

            (
                Viewer {
                    pc: viewer,
                    dc,
                    seen,
                },
                SidecarPeer {
                    pc: sidecar,
                    tracks,
                    outbox,
                },
            )
        }

        /**
         * Drive `frames` frames through every live track the way the consume
         * loop's fan-out does: one task, each track in turn, plus
         * `status_per_tick` state broadcasts to every peer numbered from
         * `*status_seq`. Anything that waits on a viewer stalls every viewer,
         * so each tick is bounded.
         */
        async fn fan_out(
            peers: &[&SidecarPeer],
            frames: u32,
            ut_base: f64,
            status_per_tick: usize,
            status_seq: &mut u64,
        ) {
            for n in 0..frames {
                let stamp = CaptureStamp::at(Some(ut_base + n as f64), 1);
                let first_status = *status_seq;
                *status_seq += status_per_tick as u64;
                let tick = async {
                    for p in peers {
                        for seq in first_status..first_status + status_per_tick as u64 {
                            p.outbox.send(&status_message(seq));
                        }
                    }
                    for t in peers.iter().flat_map(|p| p.tracks.iter()) {
                        let time = at(n as f64 * 33.0, None).0;
                        let _ = t.write_frame(&nal(64), time, false, 9, &stamp).await;
                    }
                };
                tokio::time::timeout(Duration::from_secs(2), tick)
                    .await
                    .unwrap_or_else(|_| panic!("fan-out stalled at frame {n} of {frames}"));
                tokio::time::sleep(Duration::from_millis(2)).await;
            }
        }

        /// Wait for in-flight packets and stamps to land, then assert a viewer
        /// got video plus correctly paired stamps since `before`.
        async fn assert_receiving(name: &str, v: &Viewer, before: (usize, usize)) {
            tokio::time::sleep(Duration::from_millis(500)).await;
            let rtp = v.seen.rtp_packets.load(Ordering::SeqCst) - before.0;
            let stamps = v.seen.stamps.load(Ordering::SeqCst) - before.1;
            let want = (FRAMES_PER_PHASE as usize * TRACKS_PER_VIEWER) * 9 / 10;
            assert!(rtp >= want, "{name}: {rtp} RTP packets, want >= {want}");
            assert!(stamps >= want, "{name}: {stamps} stamps, want >= {want}");
            assert_eq!(
                v.dc.ready_state(),
                RTCDataChannelState::Open,
                "{name}: control channel still open"
            );
            let rtp_ts = v.seen.rtp_timestamps.lock().await;
            let pairs = v.seen.stamp_pairs.lock().await;
            let matched = pairs.iter().filter(|(ts, _)| rtp_ts.contains(ts)).count();
            assert!(
                matched >= want,
                "{name}: {matched} stamps name a received RTP timestamp"
            );
        }

        /// Every status message sent in `range` arrived, once and in order.
        async fn assert_status_intact(name: &str, v: &Viewer, range: std::ops::Range<u64>) {
            tokio::time::sleep(Duration::from_millis(500)).await;
            let got: Vec<u64> = v
                .seen
                .status
                .lock()
                .await
                .iter()
                .copied()
                .filter(|s| range.contains(s))
                .collect();
            let want: Vec<u64> = range.collect();
            assert!(
                got == want,
                "{name}: status messages lost or reordered: got {} of {}, first gap after {:?}",
                got.len(),
                want.len(),
                got.iter().zip(&want).find(|(g, w)| g != w).map(|(g, _)| g),
            );
        }

        fn counts(v: &Viewer) -> (usize, usize) {
            (
                v.seen.rtp_packets.load(Ordering::SeqCst),
                v.seen.stamps.load(Ordering::SeqCst),
            )
        }

        /// Three viewers on one virtual LAN with the sidecar; the first can be
        /// cut off without closing.
        struct Lan {
            router: Arc<Mutex<Router>>,
            sidecar: Arc<Net>,
            viewers: [Arc<Net>; 3],
            blackhole: Arc<AtomicBool>,
        }

        async fn lan() -> Lan {
            let router = Arc::new(Mutex::new(
                Router::new(RouterConfig {
                    cidr: "10.0.0.0/24".to_owned(),
                    ..Default::default()
                })
                .unwrap(),
            ));
            let sidecar = net_on(&router, "10.0.0.1").await;
            let viewers = [
                net_on(&router, "10.0.0.11").await,
                net_on(&router, "10.0.0.12").await,
                net_on(&router, "10.0.0.13").await,
            ];
            let vanished: IpAddr = "10.0.0.11".parse().unwrap();
            let blackhole = Arc::new(AtomicBool::new(false));
            let bh = blackhole.clone();
            router
                .lock()
                .await
                .add_chunk_filter(Box::new(move |c: &(dyn Chunk + Send + Sync)| {
                    !(bh.load(Ordering::SeqCst) && c.get_source_ip() == vanished)
                }))
                .await;
            router.lock().await.start().await.unwrap();
            Lan {
                router,
                sidecar,
                viewers,
                blackhole,
            }
        }

        /**
         * A viewer that vanishes without closing (a reloaded page, a dropped
         * Wi-Fi link) leaves its sidecar peer looking alive until ICE gives
         * up. Frames and stamps keep flowing to it meanwhile, and none of
         * that may hold up the viewers that replace it: every later viewer
         * keeps receiving video and stamps through 30 s of frames.
         */
        #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
        async fn later_viewers_keep_receiving_after_a_viewer_vanishes() {
            let lan = lan().await;
            let mut seq = 0;

            let (a, a_side) = connect(lan.viewers[0].clone(), lan.sidecar.clone()).await;
            let before = counts(&a);
            fan_out(&[&a_side], FRAMES_PER_PHASE, 0.0, 0, &mut seq).await;
            assert_receiving("first viewer", &a, before).await;

            /* A vanishes. Its sidecar peer is not reaped yet. */
            lan.blackhole.store(true, Ordering::SeqCst);
            let (b, b_side) = connect(lan.viewers[1].clone(), lan.sidecar.clone()).await;
            let before_b = counts(&b);
            fan_out(&[&a_side, &b_side], FRAMES_PER_PHASE, 10_000.0, 0, &mut seq).await;
            assert_receiving("second viewer", &b, before_b).await;

            /* The reaper finally closes A; a third viewer joins B. */
            a_side.reap().await;
            let (c, c_side) = connect(lan.viewers[2].clone(), lan.sidecar.clone()).await;
            let (before_b, before_c) = (counts(&b), counts(&c));
            fan_out(&[&b_side, &c_side], FRAMES_PER_PHASE, 20_000.0, 0, &mut seq).await;
            assert_receiving("second viewer, later", &b, before_b).await;
            assert_receiving("third viewer", &c, before_c).await;

            for pc in [&a.pc, &b.pc, &c.pc, &b_side.pc, &c_side.pc] {
                pc.close().await.ok();
            }
            lan.router.lock().await.stop().await.ok();
        }

        /**
         * The same vanished viewer while every tick also broadcasts state to
         * every peer. The fan-out never waits; the vanished peer's backlog
         * marks it dead for the reaper instead of silently dropping state;
         * live viewers get every broadcast once and in order, with video and
         * stamps still flowing.
         */
        #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
        async fn a_vanished_viewer_under_heavy_status_traffic_is_reaped_not_waited_on() {
            const STATUS_PER_TICK: usize = 2;
            let lan = lan().await;
            let mut seq = 0;

            let (a, a_side) = connect(lan.viewers[0].clone(), lan.sidecar.clone()).await;
            let (b, b_side) = connect(lan.viewers[1].clone(), lan.sidecar.clone()).await;
            let start = seq;
            let (before_a, before_b) = (counts(&a), counts(&b));
            fan_out(
                &[&a_side, &b_side],
                FRAMES_PER_PHASE,
                0.0,
                STATUS_PER_TICK,
                &mut seq,
            )
            .await;
            assert_receiving("viewer A, before it vanishes", &a, before_a).await;
            assert_receiving("viewer B", &b, before_b).await;
            assert_status_intact("viewer A", &a, start..seq).await;
            assert_status_intact("viewer B", &b, start..seq).await;
            assert!(!a_side.outbox.is_dead() && !b_side.outbox.is_dead());

            lan.blackhole.store(true, Ordering::SeqCst);
            let start = seq;
            let before_b = counts(&b);
            fan_out(
                &[&a_side, &b_side],
                FRAMES_PER_PHASE,
                10_000.0,
                STATUS_PER_TICK,
                &mut seq,
            )
            .await;
            assert!(
                a_side.outbox.is_dead(),
                "a peer that stopped taking state broadcasts is marked dead"
            );
            assert!(!b_side.outbox.is_dead(), "a live peer is never marked dead");
            assert_receiving("viewer B, alongside the vanished one", &b, before_b).await;
            assert_status_intact("viewer B, alongside the vanished one", &b, start..seq).await;

            a_side.reap().await;
            let (c, c_side) = connect(lan.viewers[2].clone(), lan.sidecar.clone()).await;
            let start = seq;
            let (before_b, before_c) = (counts(&b), counts(&c));
            fan_out(
                &[&b_side, &c_side],
                FRAMES_PER_PHASE,
                20_000.0,
                STATUS_PER_TICK,
                &mut seq,
            )
            .await;
            assert_receiving("viewer B, after the reap", &b, before_b).await;
            assert_receiving("viewer C", &c, before_c).await;
            assert_status_intact("viewer B, after the reap", &b, start..seq).await;
            assert_status_intact("viewer C", &c, start..seq).await;
            assert!(!b_side.outbox.is_dead() && !c_side.outbox.is_dead());

            for pc in [&a.pc, &b.pc, &c.pc, &b_side.pc, &c_side.pc] {
                pc.close().await.ok();
            }
            lan.router.lock().await.stop().await.ok();
        }
    }
}
