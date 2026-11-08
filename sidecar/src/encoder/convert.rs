//! Pixel-format conversion shared by the hardware encoder backends.
//!
//! Hardware H.264 encoders (VAAPI on Linux, Media Foundation on Windows)
//! want NV12 input (planar Y followed by interleaved UV at half
//! resolution); `RawFrame` carries RGBA8. The conversion is pure CPU math
//! with no OS dependencies, so it lives here once and the unit tests run
//! on every platform.

use super::frame_size::aligned_down;

/// BT.601 limited-range RGBA to NV12 conversion into caller-provided
/// planes. Writes the Y plane into `y_plane` and the interleaved UV plane
/// into `uv_plane`, respecting each plane's stride. We do BT.601 (not
/// BT.709) for consistency with libavutil's default for SD/low-resolution
/// content; camera tiles in the 768x768 range live in that bucket and
/// matching the decoder's default avoids a colour-space mismatch in the
/// browser.
///
/// Invariants (callers validate before calling):
/// - `rgba.len() == width * height * 4`
/// - `width` and `height` are even (NV12 chroma subsampling)
/// - `y_plane.len() >= y_stride * height`
/// - `uv_plane.len() >= uv_stride * height / 2`
#[cfg_attr(not(any(target_os = "linux", target_os = "windows")), allow(dead_code))]
#[allow(clippy::too_many_arguments)]
pub(crate) fn rgba_to_nv12_planes(
    rgba: &[u8],
    width: u32,
    height: u32,
    y_plane: &mut [u8],
    y_stride: usize,
    uv_plane: &mut [u8],
    uv_stride: usize,
) {
    let w = width as usize;
    let h = height as usize;

    // Y plane: per-pixel.
    for y in 0..h {
        let row = y * y_stride;
        for x in 0..w {
            let src = (y * w + x) * 4;
            let r = rgba[src] as i32;
            let g = rgba[src + 1] as i32;
            let b = rgba[src + 2] as i32;
            // BT.601 limited-range Y' coefficients, fixed-point Q8.
            // Y' = 16 + (66R + 129G + 25B + 128) >> 8
            let yv = (66 * r + 129 * g + 25 * b + 128) >> 8;
            y_plane[row + x] = (yv + 16).clamp(0, 255) as u8;
        }
    }

    // UV plane: 2x2-averaged chroma, interleaved U then V per sample.
    let cw = w / 2;
    let ch = h / 2;
    for cy in 0..ch {
        for cx in 0..cw {
            // Average a 2x2 RGB block to reduce chroma aliasing, same
            // approach the standard NV12 conversion uses.
            let mut sr = 0i32;
            let mut sg = 0i32;
            let mut sb = 0i32;
            for dy in 0..2 {
                for dx in 0..2 {
                    let px = (cx * 2 + dx, cy * 2 + dy);
                    let src = (px.1 * w + px.0) * 4;
                    sr += rgba[src] as i32;
                    sg += rgba[src + 1] as i32;
                    sb += rgba[src + 2] as i32;
                }
            }
            let r = sr / 4;
            let g = sg / 4;
            let b = sb / 4;
            // BT.601 limited-range U/V coefficients, fixed-point Q8.
            let u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
            let v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
            let row = cy * uv_stride;
            uv_plane[row + cx * 2] = u.clamp(0, 255) as u8;
            uv_plane[row + cx * 2 + 1] = v.clamp(0, 255) as u8;
        }
    }
}

/**
 * The size a half-size copy of a `width` by `height` frame has: half on each
 * side, rounded down to an aligned size at the frame's shape, or `None` when
 * that would fall under `min` on either side (the frame is then sent at its own
 * size). Never more than half, which `downscale_half_rgba` relies on.
 */
pub fn half_size(width: u32, height: u32, min: u32) -> Option<(u32, u32)> {
    let half = (width / 2, height / 2);
    let (w, h) = aligned_down(
        (half.0 as f64, half.1 as f64),
        half,
        width as f64 / height as f64,
    );
    (w >= min && h >= min && w <= half.0 && h <= half.1).then_some((w, h))
}

/**
 * Box-filter a tightly packed RGBA frame down to `out_w` by `out_h`, at most half
 * the frame on each side, each output pixel the average of a 2x2 block. The
 * blocks spread evenly from edge to edge, so a copy a little under half still
 * holds the whole picture rather than losing its right and bottom edges.
 */
pub fn downscale_half_rgba(
    rgba: &[u8],
    width: u32,
    height: u32,
    out_w: u32,
    out_h: u32,
    out: &mut Vec<u8>,
) {
    let (w, h, ow, oh) = (
        width as usize,
        height as usize,
        out_w as usize,
        out_h as usize,
    );
    out.clear();
    out.resize(ow * oh * 4, 0);
    let (x_gaps, y_gaps) = ((ow - 1).max(1), (oh - 1).max(1));
    for y in 0..oh {
        let top = (y * (h - 2) / y_gaps) * w * 4;
        let bottom = top + w * 4;
        for x in 0..ow {
            let src_x = x * (w - 2) / x_gaps;
            let (a, b) = (top + src_x * 4, bottom + src_x * 4);
            let o = (y * ow + x) * 4;
            for c in 0..4 {
                let sum = rgba[a + c] as u16
                    + rgba[a + 4 + c] as u16
                    + rgba[b + c] as u16
                    + rgba[b + 4 + c] as u16;
                out[o + c] = ((sum + 2) / 4) as u8;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn half_size_is_aligned_and_respects_the_minimum() {
        assert_eq!(half_size(1024, 576, 128), Some((512, 288)));
        assert_eq!(half_size(1280, 720, 128), Some((624, 352)));
        assert_eq!(half_size(480, 272, 128), Some((224, 128)));
        assert_eq!(half_size(656, 368, 128), Some((288, 160)));
        assert_eq!(
            half_size(384, 216, 128),
            None,
            "108 rows is under the minimum"
        );
    }

    /// A copy a little under half keeps both far edges of the picture.
    #[test]
    fn downscale_reaches_every_edge_when_under_half() {
        let (w, h) = (8usize, 6usize);
        let mut rgba = vec![0u8; w * h * 4];
        for y in 0..h {
            for x in 0..w {
                if x == w - 1 || y == h - 1 {
                    rgba[(y * w + x) * 4] = 200;
                }
            }
        }
        let mut out = Vec::new();
        downscale_half_rgba(&rgba, w as u32, h as u32, 3, 2, &mut out);
        assert_eq!(out[0], 0, "top-left block is untouched");
        assert_eq!(out[2 * 4], 100, "right column reached");
        assert_eq!(out[3 * 4], 100, "bottom row reached");
    }

    #[test]
    fn downscale_averages_each_two_by_two_block() {
        /* 4x2 image: left block 0 and 100 alternating, right block all 200. */
        let px = |v: u8| [v, v, v, 0xFF];
        let mut rgba = Vec::new();
        for row in 0..2 {
            for col in 0..4 {
                let v = if col < 2 {
                    if (row + col) % 2 == 0 {
                        0
                    } else {
                        100
                    }
                } else {
                    200
                };
                rgba.extend_from_slice(&px(v));
            }
        }
        let mut out = Vec::new();
        downscale_half_rgba(&rgba, 4, 2, 2, 1, &mut out);
        assert_eq!(out, vec![50, 50, 50, 0xFF, 200, 200, 200, 0xFF]);
    }

    /// Convert a uniform RGBA image and return (y, u, v) of the first
    /// sample of each plane.
    fn convert_uniform(r: u8, g: u8, b: u8) -> (u8, u8, u8) {
        let (w, h) = (4u32, 4u32);
        let mut rgba = Vec::with_capacity((w * h * 4) as usize);
        for _ in 0..(w * h) {
            rgba.extend_from_slice(&[r, g, b, 0xFF]);
        }
        let mut y = vec![0u8; (w * h) as usize];
        let mut uv = vec![0u8; (w * h / 2) as usize];
        rgba_to_nv12_planes(&rgba, w, h, &mut y, w as usize, &mut uv, w as usize);
        (y[0], uv[0], uv[1])
    }

    #[test]
    fn black_maps_to_limited_range_floor() {
        // BT.601 limited range: black is Y=16, chroma neutral at 128.
        let (y, u, v) = convert_uniform(0, 0, 0);
        assert_eq!(y, 16);
        assert_eq!(u, 128);
        assert_eq!(v, 128);
    }

    #[test]
    fn white_maps_to_limited_range_ceiling() {
        // BT.601 limited range: white lands at Y=235 (plus or minus
        // a rounding step), chroma neutral.
        let (y, u, v) = convert_uniform(255, 255, 255);
        assert!((234..=236).contains(&y), "white Y = {y}");
        assert!((127..=129).contains(&u), "white U = {u}");
        assert!((127..=129).contains(&v), "white V = {v}");
    }

    #[test]
    fn pure_red_pushes_v_high_and_u_low() {
        let (_, u, v) = convert_uniform(255, 0, 0);
        assert!(v > 200, "red V should be high, got {v}");
        assert!(u < 110, "red U should be below neutral, got {u}");
    }

    #[test]
    fn respects_plane_strides() {
        // 2x2 white image into planes with stride 8: only the first
        // `width` bytes of each row may be written.
        let (w, h) = (2u32, 2u32);
        let rgba = vec![0xFFu8; (w * h * 4) as usize];
        let stride = 8usize;
        let mut y = vec![0u8; stride * h as usize];
        let mut uv = vec![0u8; stride * (h as usize / 2)];
        rgba_to_nv12_planes(&rgba, w, h, &mut y, stride, &mut uv, stride);
        assert_ne!(y[0], 0, "Y row 0 written");
        assert_ne!(y[stride], 0, "Y row 1 written at stride offset");
        assert_eq!(
            y[w as usize..stride],
            vec![0u8; stride - w as usize][..],
            "Y row padding untouched"
        );
        assert_eq!(
            uv[w as usize..stride],
            vec![0u8; stride - w as usize][..],
            "UV row padding untouched"
        );
    }
}
