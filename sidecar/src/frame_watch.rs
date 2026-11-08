//! How the consumer loop waits between its passes over the cameras.

use std::time::Duration;

use tokio::time::{sleep, Instant};

/**
 * How often the consumer loop looks for a new frame between its passes. A
 * pass encodes every camera in turn, so sleeping the whole poll interval after
 * it held a frame that landed early in the sleep for up to that interval plus
 * the pass: the irregular send spacing the feeds showed on top of the game's
 * own. Looking costs one atomic read per watched ring.
 */
pub const FRAME_WATCH_STEP: Duration = Duration::from_millis(2);

/// Sleep for up to `max`, waking within `step` of `ready` turning true.
pub async fn sleep_until_ready(max: Duration, step: Duration, mut ready: impl FnMut() -> bool) {
    let deadline = Instant::now() + max;
    loop {
        if ready() {
            return;
        }
        let now = Instant::now();
        if now >= deadline {
            return;
        }
        sleep(step.min(deadline - now)).await;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::shared_mem::{MmapFrameRing, MmapRingConfig};

    const POLL: Duration = Duration::from_millis(16);

    #[tokio::test(start_paused = true)]
    async fn the_wait_ends_at_once_when_a_frame_is_already_waiting() {
        let start = Instant::now();
        sleep_until_ready(POLL, FRAME_WATCH_STEP, || true).await;
        assert_eq!(start.elapsed(), Duration::ZERO);
    }

    #[tokio::test(start_paused = true)]
    async fn with_no_new_frame_the_wait_is_the_poll_interval() {
        let start = Instant::now();
        sleep_until_ready(POLL, FRAME_WATCH_STEP, || false).await;
        assert_eq!(start.elapsed(), POLL);
    }

    /// The writer claims a frame before copying it in. The claim must not end
    /// the wait, since there is nothing to read yet; the publish must, within a step.
    #[tokio::test(start_paused = true)]
    async fn a_frame_wakes_the_wait_when_published_not_when_claimed() {
        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 8,
            max_height: 8,
        };
        let mut ring = MmapFrameRing::create(&dir.path().join("1.ring"), cfg).unwrap();
        ring.produce(8, 8, 1.0, &[0u8; 8 * 8 * 4]).unwrap();
        let seen = ring.published_sequence();
        assert_eq!(seen, 1);

        let start = Instant::now();
        let claim_at = start + Duration::from_millis(1);
        let publish_at = start + Duration::from_millis(5);
        let mut claimed = None;
        let mut published = false;
        sleep_until_ready(POLL, FRAME_WATCH_STEP, || {
            let now = Instant::now();
            if claimed.is_none() && now >= claim_at {
                claimed = Some(ring.claim_for_test());
            }
            if let (Some(seq), false) = (claimed, published) {
                if now >= publish_at {
                    ring.publish_for_test(seq);
                    published = true;
                }
            }
            ring.published_sequence() > seen
        })
        .await;
        assert_eq!(
            ring.latest_sequence(),
            2,
            "the claim moved the claimed sequence"
        );
        assert_eq!(
            start.elapsed(),
            Duration::from_millis(6),
            "woke at the first look after the publish"
        );
    }
}
