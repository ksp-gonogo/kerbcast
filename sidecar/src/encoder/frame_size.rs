/* Which frame sizes a stream may use. H.264 codes whole 16x16 macroblocks and
crops anything else from the padded picture, and some phone hardware decoders
show part of that padding as a coloured line at the edge. A size that is a
multiple of 16 on both sides has nothing to crop.

Rounding each side on its own would move the shape, up to 5% on a small stream,
and viewers fit the video into a box at the configured shape, so they would show
bars. These functions keep a frame within SHAPE_TOLERANCE of the shape instead,
which leaves a viewer at most half a percent of edge on each side. The plugin's
`QualityClamp` makes the same choices: change both together. */

use super::escalation::MIN_ENCODE_DIMENSION;

/// Every encoded frame's width and height are a multiple of this.
pub const FRAME_ALIGNMENT: u32 = 16;

/// How far a frame's aspect may sit from the configured one, as a fraction.
const SHAPE_TOLERANCE: f64 = 0.01;

/// A side this many pixels short still covers: the viewer scales it up by a
/// fraction of a percent, far cheaper than moving to the next larger size.
const COVER_SLACK_PX: f64 = 2.0;

/// Tries this many heights under the wanted one before accepting a shape that
/// is outside the tolerance, so a hard-to-fit shape never loses a large area.
const DOWN_STEPS: u32 = 3;

/// The largest multiple of 16 at or under `v`, never under 16.
pub fn align_down(v: u32) -> u32 {
    (v & !(FRAME_ALIGNMENT - 1)).max(FRAME_ALIGNMENT)
}

fn ceil_aligned(v: f64) -> u32 {
    let a = FRAME_ALIGNMENT as f64;
    ((v / a).ceil() * a).max(a) as u32
}

/**
 * The smallest aligned size at `aspect` that covers `want` and fits `limit`.
 * When nothing covers within the limit (a request at a ring max that is not
 * itself aligned, or at a manual cap) it falls back to `aligned_down`.
 */
pub fn aligned_cover(want: (f64, f64), limit: (u32, u32), aspect: f64) -> (u32, u32) {
    let (limit_w, limit_h) = (align_down(limit.0), align_down(limit.1));
    let min_w = want.0 - COVER_SLACK_PX;
    let mut h = ceil_aligned(want.1 - COVER_SLACK_PX);
    while h <= limit_h {
        let ideal = h as f64 * aspect;
        let w = ceil_aligned(min_w.max(ideal * (1.0 - SHAPE_TOLERANCE)));
        if w <= limit_w && w as f64 <= ideal * (1.0 + SHAPE_TOLERANCE) {
            return (w, h);
        }
        h += FRAME_ALIGNMENT;
    }
    aligned_down(want, limit, aspect)
}

/**
 * The largest aligned size at `aspect` that fits inside `want` and `limit`.
 * When no nearby height holds the shape, as on a ring too small to have much
 * choice, each side rounds down on its own.
 */
pub fn aligned_down(want: (f64, f64), limit: (u32, u32), aspect: f64) -> (u32, u32) {
    let top_w = align_down((want.0 as u32).min(limit.0));
    let top_h = align_down((want.1 as u32).min(limit.1));
    for step in 0..DOWN_STEPS {
        let Some(h) = top_h.checked_sub(step * FRAME_ALIGNMENT) else {
            break;
        };
        if h < MIN_ENCODE_DIMENSION {
            break;
        }
        let ideal = h as f64 * aspect;
        let w = top_w.min(align_down((ideal * (1.0 + SHAPE_TOLERANCE)) as u32));
        if w >= MIN_ENCODE_DIMENSION && w as f64 >= ideal * (1.0 - SHAPE_TOLERANCE) {
            return (w, h);
        }
    }
    (top_w, top_h)
}

#[cfg(test)]
mod tests {
    use super::*;

    const WIDE: f64 = 16.0 / 9.0;

    fn cover(w: f64, h: f64, limit: (u32, u32)) -> (u32, u32) {
        aligned_cover((w, h), limit, limit.0 as f64 / limit.1 as f64)
    }

    #[test]
    fn the_size_that_showed_a_green_line_now_needs_no_crop() {
        assert_eq!(cover(432.0, 244.0, (1024, 576)), (480, 272));
        assert_eq!(cover(434.0, 244.0, (1024, 576)), (480, 272));
    }

    #[test]
    fn an_aligned_size_at_the_shape_comes_back_unchanged() {
        for (w, h) in [
            (256, 144),
            (512, 288),
            (1024, 576),
            (1280, 720),
            (2048, 1152),
        ] {
            assert_eq!(cover(w as f64, h as f64, (2048, 1152)), (w, h));
        }
    }

    #[test]
    fn a_tiny_request_takes_the_smallest_size_that_holds_the_shape() {
        assert_eq!(cover(228.0, 128.0, (1280, 720)), (256, 144));
    }

    #[test]
    fn a_side_barely_short_does_not_jump_to_the_next_size() {
        assert_eq!(cover(400.0, 225.0, (1280, 720)), (400, 224));
    }

    /// A ring max that is not aligned rounds down inside it, staying in shape.
    #[test]
    fn a_request_at_an_unaligned_ring_max_rounds_down_within_it() {
        assert_eq!(cover(1920.0, 1080.0, (1920, 1080)), (1920, 1072));
        assert_eq!(cover(640.0, 360.0, (640, 360)), (624, 352));
        assert_eq!(cover(1000.0, 562.0, (1000, 562)), (992, 560));
    }

    #[test]
    fn a_shape_with_no_aligned_match_nearby_still_aligns_and_fits() {
        let aspect = 200.0 / 137.0;
        let (w, h) = aligned_cover((200.0, 137.0), (200, 137), aspect);
        assert_eq!((w % 16, h % 16), (0, 0));
        assert!(w <= 200 && h <= 137, "{w}x{h}");
    }

    #[test]
    fn a_ring_under_the_minimum_keeps_its_own_size() {
        assert_eq!(cover(64.0, 64.0, (64, 64)), (64, 64));
    }

    /// Every request at every common shape: aligned, inside the limit, covering
    /// when there is room, and close enough to the shape to leave no real bars.
    #[test]
    fn every_size_is_aligned_covering_and_in_shape() {
        for (mw, mh) in [(1024u32, 576u32), (1280, 720), (1920, 1088), (1024, 768)] {
            let aspect = mw as f64 / mh as f64;
            for want_h in (128..=mh / 2).step_by(3) {
                let want_w = want_h as f64 * aspect;
                let (w, h) = aligned_cover((want_w, want_h as f64), (mw, mh), aspect);
                assert_eq!((w % 16, h % 16), (0, 0), "{want_w}x{want_h} gave {w}x{h}");
                assert!(w <= mw && h <= mh, "{want_w}x{want_h} gave {w}x{h}");
                assert!(
                    w as f64 >= want_w - COVER_SLACK_PX && h as f64 >= want_h as f64 - 2.0,
                    "{want_w}x{want_h} gave {w}x{h}"
                );
                let drift = (w as f64 / h as f64 / aspect - 1.0).abs();
                assert!(drift <= SHAPE_TOLERANCE, "{w}x{h} is {drift} off the shape");
            }
        }
    }

    #[test]
    fn rounding_down_never_grows_a_side() {
        assert_eq!(aligned_down((640.0, 360.0), (640, 360), WIDE), (624, 352));
        assert_eq!(aligned_down((512.0, 288.0), (512, 288), WIDE), (512, 288));
        let (w, h) = aligned_down((321.0, 181.0), (321, 181), 642.0 / 362.0);
        assert!(w <= 321 && h <= 181, "{w}x{h}");
        assert_eq!((w % 16, h % 16), (0, 0));
    }

    #[test]
    fn align_down_floors_to_a_multiple_of_sixteen() {
        assert_eq!(align_down(1080), 1072);
        assert_eq!(align_down(1024), 1024);
        assert_eq!(align_down(3), 16);
    }
}
