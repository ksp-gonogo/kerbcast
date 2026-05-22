//! Linux/Steam Deck VA-API H.264 backend — TIER-1.
//!
//! Real implementation on Linux via `ffmpeg-next` (h264_vaapi). On every
//! other platform this collapses to a compile-only stub that reports
//! `is_available() == false` so `auto_select` walks past us to the
//! software fallback. The Mac dev iteration loop must not pull in
//! libavcodec — the heavy dep is gated to `cfg(target_os = "linux")`
//! in `Cargo.toml`.
//!
//! ## Implementation notes (Linux path)
//!
//! `h264_vaapi` is a hardware encoder: it only accepts VAAPI surfaces as
//! input, not regular CPU buffers. The flow per frame is:
//!
//! 1. RGBA8 input → NV12 (Y + interleaved UV) software conversion into
//!    a CPU-side `AVFrame` (sw_format = NV12).
//! 2. `av_hwframe_transfer_data` uploads that CPU frame into a fresh
//!    GPU-side `AVFrame` allocated from the hwframe pool.
//! 3. `avcodec_send_frame` + drain `avcodec_receive_packet` loop.
//! 4. Each output `AVPacket` carries Annex-B bytestream — we split on
//!    start codes (`00 00 00 01` / `00 00 01`) into our `Nal` units.
//!
//! ffmpeg-next's high-level wrapper doesn't cover `hw_frames_ctx` /
//! `av_hwdevice_ctx_create`, so the init path drops into the re-exported
//! `ffmpeg::ffi::*` sys bindings. The unsafe is bounded to the ~50 lines
//! of context plumbing in `init()`; the per-frame hot path uses the safe
//! wrappers for everything except the hwframe transfer + raw codec ctx
//! pointer set.

#[cfg(target_os = "linux")]
mod imp {
    use ffmpeg_next as ffmpeg;
    use ffmpeg_next::format::Pixel;
    use ffmpeg_next::picture;
    use ffmpeg_next::util::frame::video::Video as VideoFrame;
    use std::path::Path;
    use std::ptr;
    use std::sync::OnceLock;
    use tracing::{debug, warn};

    use super::super::{EncodeConfig, EncodeError, EncoderBackend, Nal, RawFrame};

    pub struct Libva {
        cfg: Option<EncodeConfig>,
        /// Owned `AVBufferRef` for the VAAPI device context. Dropped in `close`.
        hw_device_ref: *mut ffmpeg::ffi::AVBufferRef,
        /// Owned `AVBufferRef` for the hardware frames pool (NV12-backed
        /// VAAPI surfaces). Dropped in `close`.
        hw_frames_ref: *mut ffmpeg::ffi::AVBufferRef,
        /// Opened H.264 VAAPI encoder. `open_as` returns
        /// `encoder::Video` (the video-specific wrapper around
        /// `Encoder`). Video derefs to Encoder so `as_mut_ptr()` works
        /// transparently.
        encoder: Option<ffmpeg::codec::encoder::Video>,
        /// Reusable CPU-side NV12 staging frame. Allocated once at init so
        /// the per-frame hot path doesn't churn AVFrames.
        sw_frame: Option<VideoFrame>,
        keyframe_requested: bool,
        frames_encoded: u64,
    }

    // The raw `*mut AVBufferRef` pointers are not Send/Sync by default. They
    // are reference-counted FFmpeg buffer refs that we own exclusively per
    // encoder instance and only touch from the consume loop thread that
    // owns the Libva instance. Mirroring webrtc-rs's pattern for the same.
    unsafe impl Send for Libva {}

    impl Libva {
        pub fn new() -> Self {
            Self {
                cfg: None,
                hw_device_ref: ptr::null_mut(),
                hw_frames_ref: ptr::null_mut(),
                encoder: None,
                sw_frame: None,
                keyframe_requested: false,
                frames_encoded: 0,
            }
        }

        /// Probe whether VAAPI H.264 encoding is actually usable on this host.
        /// Result is cached in a `OnceLock` — probing is expensive (opens the
        /// VAAPI device) and `auto_select` calls `is_available()` every frame
        /// on the first camera until an encoder succeeds.
        ///
        /// Gates: (1) a render node exists, (2) `av_hwdevice_ctx_create(VAAPI)`
        /// succeeds, (3) `avcodec_get_hw_config` confirms h264_vaapi has an
        /// HW_FRAMES_CTX config, (4) `av_hwframe_ctx_init` with that pixel
        /// format succeeds. Gate 4 is critical: on some setups (e.g. Ubuntu
        /// 22.04's ffmpeg 4.4 where `FF_API_VAAPI` flips the value of
        /// `AV_PIX_FMT_VAAPI` between the build-time Rust enum and the runtime
        /// libavcodec.so) the device opens fine but hwframe init fails with
        /// `vaapi_moco not supported`. Catching it here lets `auto_select` fall
        /// through to the software backend instead of retrying every frame.
        fn probe() -> bool {
            static CACHE: OnceLock<bool> = OnceLock::new();
            *CACHE.get_or_init(Self::probe_once)
        }

        fn probe_once() -> bool {
            if !render_node_exists() {
                return false;
            }
            unsafe {
                // ---- 1. Open VAAPI device ----
                let mut device_ref: *mut ffmpeg::ffi::AVBufferRef = ptr::null_mut();
                let rc = ffmpeg::ffi::av_hwdevice_ctx_create(
                    &mut device_ref,
                    ffmpeg::ffi::AVHWDeviceType::AV_HWDEVICE_TYPE_VAAPI,
                    ptr::null(),
                    ptr::null_mut(),
                    0,
                );
                if rc < 0 || device_ref.is_null() {
                    return false;
                }

                // ---- 2. Find h264_vaapi codec + get its runtime hw pixel format ----
                let name = std::ffi::CString::new("h264_vaapi").unwrap();
                let codec = ffmpeg::ffi::avcodec_find_encoder_by_name(name.as_ptr());
                if codec.is_null() {
                    ffmpeg::ffi::av_buffer_unref(&mut device_ref);
                    return false;
                }
                let hw_pix_fmt = hw_frames_pix_fmt_for_codec(codec);
                if hw_pix_fmt == ffmpeg::ffi::AVPixelFormat::AV_PIX_FMT_NONE {
                    ffmpeg::ffi::av_buffer_unref(&mut device_ref);
                    return false;
                }

                // ---- 3. Test hwframe init with the runtime pixel format ----
                // This catches pixel-format enum mismatches between the Rust
                // bindings and the installed libavcodec that only show up at
                // av_hwframe_ctx_init time (not at device open time).
                let frames_ref = ffmpeg::ffi::av_hwframe_ctx_alloc(device_ref);
                if frames_ref.is_null() {
                    ffmpeg::ffi::av_buffer_unref(&mut device_ref);
                    return false;
                }
                let fctx = (*frames_ref).data as *mut ffmpeg::ffi::AVHWFramesContext;
                (*fctx).format = hw_pix_fmt;
                (*fctx).sw_format = ffmpeg::ffi::AVPixelFormat::AV_PIX_FMT_NV12;
                (*fctx).width = 64;
                (*fctx).height = 64;
                (*fctx).initial_pool_size = 1;
                let hw_ok = ffmpeg::ffi::av_hwframe_ctx_init(frames_ref) >= 0;
                // av_buffer_unref handles the frames_ref; device_ref is kept
                // alive by the frames context until we unref both.
                let mut frames_mut = frames_ref;
                ffmpeg::ffi::av_buffer_unref(&mut frames_mut);
                ffmpeg::ffi::av_buffer_unref(&mut device_ref);
                hw_ok
            }
        }
    }

    impl Default for Libva {
        fn default() -> Self {
            Self::new()
        }
    }

    /// Walk the codec's hw configs and return the pixel format for the first
    /// entry that supports HW_FRAMES_CTX on VAAPI. Returns AV_PIX_FMT_NONE if
    /// no such config exists.
    ///
    /// Using avcodec_get_hw_config avoids hardcoding `AV_PIX_FMT_VAAPI` —
    /// that constant's numeric value varies depending on whether FF_API_VAAPI
    /// was defined at the time the Rust bindings were generated, causing a
    /// mismatch with the runtime libavcodec.
    ///
    /// # Safety
    /// `codec` must be a valid non-null `*const AVCodec`.
    unsafe fn hw_frames_pix_fmt_for_codec(
        codec: *const ffmpeg::ffi::AVCodec,
    ) -> ffmpeg::ffi::AVPixelFormat {
        // AV_CODEC_HW_CONFIG_METHOD_HW_FRAMES_CTX = 0x02 (stable since ffmpeg 4.0)
        const HW_FRAMES_CTX: i32 = 0x02;
        let mut i = 0;
        loop {
            let cfg = ffmpeg::ffi::avcodec_get_hw_config(codec, i);
            if cfg.is_null() {
                return ffmpeg::ffi::AVPixelFormat::AV_PIX_FMT_NONE;
            }
            if (*cfg).methods & HW_FRAMES_CTX != 0
                && (*cfg).device_type == ffmpeg::ffi::AVHWDeviceType::AV_HWDEVICE_TYPE_VAAPI
            {
                return (*cfg).pix_fmt;
            }
            i += 1;
        }
    }

    fn render_node_exists() -> bool {
        // /dev/dri/renderD128 is the canonical first render node. Walk the
        // dir for any renderD* so we catch hosts with non-default numbering
        // (multi-GPU rigs, container mounts).
        let dri = Path::new("/dev/dri");
        let Ok(entries) = std::fs::read_dir(dri) else {
            return false;
        };
        for entry in entries.flatten() {
            let name = entry.file_name();
            let Some(s) = name.to_str() else { continue };
            if s.starts_with("renderD") {
                return true;
            }
        }
        false
    }

    impl EncoderBackend for Libva {
        fn name(&self) -> &'static str {
            "h264_vaapi (libva)"
        }

        fn is_available(&self) -> bool {
            Self::probe()
        }

        fn init(&mut self, cfg: EncodeConfig) -> Result<(), EncodeError> {
            if cfg.width == 0 || cfg.height == 0 {
                return Err(EncodeError::Invalid("zero-sized dimensions".into()));
            }
            if cfg.fps == 0 {
                return Err(EncodeError::Invalid("fps == 0".into()));
            }
            // NV12 requires even dimensions for chroma subsampling. VAAPI
            // typically requires 2-pixel alignment; some drivers want 16
            // for tile alignment but H.264 itself only needs 2.
            if !cfg.width.is_multiple_of(2) || !cfg.height.is_multiple_of(2) {
                return Err(EncodeError::Invalid(format!(
                    "dimensions must be even (got {}x{})",
                    cfg.width, cfg.height
                )));
            }

            // Make sure any previous session is fully torn down before we
            // start (init-after-init must work).
            self.close();

            unsafe {
                // ---- 1. Create VAAPI hwdevice context ----
                let rc = ffmpeg::ffi::av_hwdevice_ctx_create(
                    &mut self.hw_device_ref,
                    ffmpeg::ffi::AVHWDeviceType::AV_HWDEVICE_TYPE_VAAPI,
                    ptr::null(),
                    ptr::null_mut(),
                    0,
                );
                if rc < 0 || self.hw_device_ref.is_null() {
                    return Err(EncodeError::Runtime(format!(
                        "av_hwdevice_ctx_create(VAAPI) failed ({rc})"
                    )));
                }

                // ---- 2. Find codec early so we can query the runtime hw pixel format ----
                // We look this up before allocating the hwframe pool because
                // the pool's `format` field must be the *runtime* value of the
                // VAAPI pixel format enum — not the compile-time Rust constant.
                // On Ubuntu 22.04 (ffmpeg 4.4 + FF_API_VAAPI=1) the runtime
                // value is 124 (AV_PIX_FMT_VAAPI_VLD), but ffmpeg-sys-next 8.x
                // generates bindings without FF_API_VAAPI, assigning
                // AV_PIX_FMT_VAAPI = 122 (vaapi_moco). Using the constant
                // directly writes the wrong value → av_hwframe_ctx_init fails.
                // avcodec_get_hw_config returns the value from the installed
                // libavcodec, which is always correct.
                let codec_name = std::ffi::CString::new("h264_vaapi").unwrap();
                let codec_ptr = ffmpeg::ffi::avcodec_find_encoder_by_name(codec_name.as_ptr());
                if codec_ptr.is_null() {
                    self.close();
                    return Err(EncodeError::Runtime(
                        "h264_vaapi encoder not registered in this libavcodec build".into(),
                    ));
                }
                let hw_pix_fmt = hw_frames_pix_fmt_for_codec(codec_ptr);
                if hw_pix_fmt == ffmpeg::ffi::AVPixelFormat::AV_PIX_FMT_NONE {
                    self.close();
                    return Err(EncodeError::Runtime(
                        "h264_vaapi: no HW_FRAMES_CTX config found for VAAPI".into(),
                    ));
                }

                // ---- 3. Allocate + init hwframe pool (NV12-backed VAAPI surfaces) ----
                self.hw_frames_ref = ffmpeg::ffi::av_hwframe_ctx_alloc(self.hw_device_ref);
                if self.hw_frames_ref.is_null() {
                    self.close();
                    return Err(EncodeError::Runtime(
                        "av_hwframe_ctx_alloc returned null".into(),
                    ));
                }
                let frames_ctx_data =
                    (*self.hw_frames_ref).data as *mut ffmpeg::ffi::AVHWFramesContext;
                (*frames_ctx_data).format = hw_pix_fmt;
                (*frames_ctx_data).sw_format = ffmpeg::ffi::AVPixelFormat::AV_PIX_FMT_NV12;
                (*frames_ctx_data).width = cfg.width as i32;
                (*frames_ctx_data).height = cfg.height as i32;
                // A pool large enough for the encoder's reordering buffer +
                // one in-flight upload. 20 mirrors FFmpeg's vaapi_encode.c
                // reference example.
                (*frames_ctx_data).initial_pool_size = 20;

                let rc = ffmpeg::ffi::av_hwframe_ctx_init(self.hw_frames_ref);
                if rc < 0 {
                    self.close();
                    return Err(EncodeError::Runtime(format!(
                        "av_hwframe_ctx_init failed ({rc})"
                    )));
                }

                // ---- 4. Build the encoder context from the codec we already found ----
                // `codec_ptr` is a raw *const AVCodec; wrap it in the safe
                // ffmpeg-next type via find_by_name (cheap — just a registry
                // lookup, same codec object).
                let codec = match ffmpeg::codec::encoder::find_by_name("h264_vaapi") {
                    Some(c) => c,
                    None => {
                        self.close();
                        return Err(EncodeError::Runtime(
                            "h264_vaapi encoder not registered in this libavcodec build".into(),
                        ));
                    }
                };

                // ffmpeg-next safe wrappers: Context::new_with_codec → encoder
                // → video → set params → open. We splice in the raw
                // hw_frames_ctx on the AVCodecContext just before open;
                // that's the bit the safe wrapper doesn't cover.
                let ctx = ffmpeg::codec::context::Context::new_with_codec(codec);
                let mut encoder = match ctx.encoder().video() {
                    Ok(v) => v,
                    Err(e) => {
                        self.close();
                        return Err(EncodeError::Runtime(format!(
                            "encoder().video() failed: {e}"
                        )));
                    }
                };

                encoder.set_width(cfg.width);
                encoder.set_height(cfg.height);
                // Set pix_fmt directly on the raw AVCodecContext using the same
                // runtime value we used for the hwframe pool — avoids the same
                // enum-alias mismatch that broke av_hwframe_ctx_init.
                // encoder.set_format(Pixel::VAAPI) would write the wrong value.
                encoder.set_bit_rate(cfg.bitrate_bps as usize);
                encoder.set_max_bit_rate(cfg.bitrate_bps as usize);
                encoder.set_time_base((1, cfg.fps as i32));
                encoder.set_frame_rate(Some((cfg.fps as i32, 1)));
                // Keyframe roughly every 2 seconds — same default as
                // OpenH264's typical config. Subscriber join still triggers
                // an explicit keyframe via request_keyframe().
                encoder.set_gop(cfg.fps.saturating_mul(2).max(1));

                // Inject hw_frames_ctx + pix_fmt on the raw AVCodecContext.
                // av_buffer_ref bumps refcount; the encoder owns the new
                // reference, we keep our own hw_frames_ref live for the
                // session in case we need to allocate fresh upload frames
                // later (we do — every encode call).
                let ctx_mut = encoder.as_mut_ptr();
                (*ctx_mut).pix_fmt = hw_pix_fmt;
                (*ctx_mut).hw_frames_ctx = ffmpeg::ffi::av_buffer_ref(self.hw_frames_ref);
                if (*ctx_mut).hw_frames_ctx.is_null() {
                    self.close();
                    return Err(EncodeError::Runtime(
                        "av_buffer_ref(hw_frames_ref) returned null".into(),
                    ));
                }

                let opened = match encoder.open_as(codec) {
                    Ok(o) => o,
                    Err(e) => {
                        self.close();
                        return Err(EncodeError::Runtime(format!(
                            "h264_vaapi encoder open failed: {e}"
                        )));
                    }
                };
                self.encoder = Some(opened);
            }

            // CPU-side NV12 staging frame, reused across encodes.
            let sw = VideoFrame::new(Pixel::NV12, cfg.width, cfg.height);
            self.sw_frame = Some(sw);
            self.keyframe_requested = false;
            self.frames_encoded = 0;
            debug!(
                width = cfg.width,
                height = cfg.height,
                fps = cfg.fps,
                bitrate_bps = cfg.bitrate_bps,
                "libva encoder initialised"
            );
            self.cfg = Some(cfg);
            Ok(())
        }

        fn encode(&mut self, frame: &RawFrame<'_>) -> Result<Vec<Nal>, EncodeError> {
            let cfg = self
                .cfg
                .as_ref()
                .ok_or_else(|| EncodeError::Runtime("encode before init".into()))?
                .clone();
            let expected = (frame.width as usize) * (frame.height as usize) * 4;
            if frame.data.len() != expected {
                return Err(EncodeError::Invalid(format!(
                    "frame size {} != width*height*4 ({})",
                    frame.data.len(),
                    expected
                )));
            }
            if frame.width != cfg.width || frame.height != cfg.height {
                return Err(EncodeError::Invalid(format!(
                    "frame dims {}x{} != configured {}x{}",
                    frame.width, frame.height, cfg.width, cfg.height
                )));
            }

            // ---- 1. RGBA → NV12 into the reusable CPU staging frame ----
            {
                let sw = self
                    .sw_frame
                    .as_mut()
                    .ok_or_else(|| EncodeError::Runtime("sw_frame missing after init".into()))?;
                rgba_to_nv12_into(frame.data, frame.width, frame.height, sw);
                sw.set_pts(Some(self.frames_encoded as i64));
                if self.keyframe_requested {
                    sw.set_kind(picture::Type::I);
                    self.keyframe_requested = false;
                } else {
                    sw.set_kind(picture::Type::None);
                }
            }

            // Snapshot the sw-frame metadata we need to copy onto the hw
            // frame BEFORE we mut-borrow self.encoder below — the borrow
            // checker can't see that sw_frame and encoder are disjoint
            // fields when both go through `self`.
            let (sw_pts, sw_is_keyframe) = {
                let sw = self.sw_frame.as_ref().unwrap();
                (sw.pts().unwrap_or(0), sw.kind() == picture::Type::I)
            };
            // SAFETY: Frame::as_ptr returns a raw *const AVFrame; we only
            // read it during av_hwframe_transfer_data below and never
            // outlive the sw_frame field. The pointer remains valid for
            // the rest of this encode call.
            let sw_ptr = unsafe { self.sw_frame.as_ref().unwrap().as_ptr() };
            let hw_frames_ref = self.hw_frames_ref;

            // ---- 2. Upload to GPU + send + drain ----
            let mut nals = Vec::new();
            let encoder = self
                .encoder
                .as_mut()
                .ok_or_else(|| EncodeError::Runtime("encoder dropped after init".into()))?;
            unsafe {
                let mut hw_frame_ptr: *mut ffmpeg::ffi::AVFrame = ffmpeg::ffi::av_frame_alloc();
                if hw_frame_ptr.is_null() {
                    return Err(EncodeError::Runtime("av_frame_alloc returned null".into()));
                }
                let rc = ffmpeg::ffi::av_hwframe_get_buffer(hw_frames_ref, hw_frame_ptr, 0);
                if rc < 0 {
                    ffmpeg::ffi::av_frame_free(&mut hw_frame_ptr);
                    return Err(EncodeError::Runtime(format!(
                        "av_hwframe_get_buffer failed ({rc})"
                    )));
                }
                let rc = ffmpeg::ffi::av_hwframe_transfer_data(hw_frame_ptr, sw_ptr, 0);
                if rc < 0 {
                    ffmpeg::ffi::av_frame_free(&mut hw_frame_ptr);
                    return Err(EncodeError::Runtime(format!(
                        "av_hwframe_transfer_data failed ({rc})"
                    )));
                }
                // Carry pict_type + pts onto the GPU frame so the encoder
                // sees the keyframe request and orders packets correctly.
                (*hw_frame_ptr).pts = sw_pts;
                (*hw_frame_ptr).pict_type = if sw_is_keyframe {
                    ffmpeg::ffi::AVPictureType::AV_PICTURE_TYPE_I
                } else {
                    ffmpeg::ffi::AVPictureType::AV_PICTURE_TYPE_NONE
                };

                let send_rc = ffmpeg::ffi::avcodec_send_frame(encoder.as_mut_ptr(), hw_frame_ptr);
                // Release our reference to the hw frame regardless of
                // send result — the encoder ref-bumps internally on success.
                ffmpeg::ffi::av_frame_free(&mut hw_frame_ptr);
                if send_rc < 0 {
                    return Err(EncodeError::Runtime(format!(
                        "avcodec_send_frame failed ({send_rc})"
                    )));
                }

                // Drain everything currently buffered. h264_vaapi may emit
                // 0 packets on the first few frames (reordering / lookahead).
                let mut pkt = ffmpeg::ffi::av_packet_alloc();
                if pkt.is_null() {
                    return Err(EncodeError::Runtime("av_packet_alloc returned null".into()));
                }
                loop {
                    let rc = ffmpeg::ffi::avcodec_receive_packet(encoder.as_mut_ptr(), pkt);
                    if rc == ffmpeg::ffi::AVERROR(ffmpeg::ffi::EAGAIN)
                        || rc == ffmpeg::ffi::AVERROR_EOF
                    {
                        break;
                    }
                    if rc < 0 {
                        ffmpeg::ffi::av_packet_free(&mut pkt);
                        return Err(EncodeError::Runtime(format!(
                            "avcodec_receive_packet failed ({rc})"
                        )));
                    }
                    let data = (*pkt).data;
                    let size = (*pkt).size as usize;
                    if !data.is_null() && size > 0 {
                        let bytes = std::slice::from_raw_parts(data, size);
                        split_annexb_into(bytes, &mut nals);
                    } else {
                        warn!("h264_vaapi returned empty packet");
                    }
                    ffmpeg::ffi::av_packet_unref(pkt);
                }
                ffmpeg::ffi::av_packet_free(&mut pkt);
            }

            self.frames_encoded += 1;
            Ok(nals)
        }

        fn request_keyframe(&mut self) {
            self.keyframe_requested = true;
        }

        fn close(&mut self) {
            self.encoder = None;
            self.sw_frame = None;
            unsafe {
                if !self.hw_frames_ref.is_null() {
                    ffmpeg::ffi::av_buffer_unref(&mut self.hw_frames_ref);
                }
                if !self.hw_device_ref.is_null() {
                    ffmpeg::ffi::av_buffer_unref(&mut self.hw_device_ref);
                }
            }
            self.hw_frames_ref = ptr::null_mut();
            self.hw_device_ref = ptr::null_mut();
            self.cfg = None;
            self.keyframe_requested = false;
            self.frames_encoded = 0;
        }
    }

    impl Drop for Libva {
        fn drop(&mut self) {
            self.close();
        }
    }

    /// BT.601 limited-range RGB → NV12 conversion. Writes the Y plane into
    /// `dst.data_mut(0)` and the interleaved UV plane into `dst.data_mut(1)`,
    /// respecting each plane's `stride`. We do BT.601 (not BT.709) for
    /// consistency with libavutil's default for SD/low-resolution content;
    /// camera tiles in the 768×768 range live in that bucket and matching
    /// the decoder's default avoids a colour-space mismatch in the browser.
    fn rgba_to_nv12_into(rgba: &[u8], width: u32, height: u32, dst: &mut VideoFrame) {
        let w = width as usize;
        let h = height as usize;
        let y_stride = dst.stride(0);
        let uv_stride = dst.stride(1);
        // Snapshot the strides so we can release the immutable borrows
        // before calling data_mut on overlapping planes.
        let (y_plane, uv_plane) = {
            // SAFETY: AVFrame stores Y and UV in distinct plane slots
            // (data[0] and data[1]) — disjoint memory regions. We split
            // into two mutable slices via raw pointer to satisfy the
            // borrow checker without ferrying both through `data_mut`
            // back-to-back. ffmpeg-next's plane indices are validated by
            // its own bounds check above (via the `stride` calls).
            let y_ptr = dst.data_mut(0).as_mut_ptr();
            let y_len = dst.data_mut(0).len();
            let uv_ptr = dst.data_mut(1).as_mut_ptr();
            let uv_len = dst.data_mut(1).len();
            unsafe {
                (
                    std::slice::from_raw_parts_mut(y_ptr, y_len),
                    std::slice::from_raw_parts_mut(uv_ptr, uv_len),
                )
            }
        };

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
                // Average a 2×2 RGB block to reduce chroma aliasing — same
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

    /// Split an Annex-B bytestream into individual NAL units by scanning for
    /// 3-byte (`00 00 01`) and 4-byte (`00 00 00 01`) start codes. Each
    /// emitted `Nal` includes its leading start code so the WebRTC
    /// packetiser can parse the NAL header byte directly.
    fn split_annexb_into(bytes: &[u8], out: &mut Vec<Nal>) {
        // Find start-code offsets, then carve the buffer between consecutive
        // offsets.
        let mut starts: Vec<usize> = Vec::new();
        let mut i = 0;
        while i + 3 <= bytes.len() {
            // 4-byte first so we don't double-count.
            if i + 4 <= bytes.len() && bytes[i..i + 4] == [0, 0, 0, 1] {
                starts.push(i);
                i += 4;
            } else if bytes[i..i + 3] == [0, 0, 1] {
                starts.push(i);
                i += 3;
            } else {
                i += 1;
            }
        }
        if starts.is_empty() {
            // No start codes found — emit the entire packet as a single NAL.
            // h264_vaapi shouldn't produce this, but defensive: better one
            // possibly-malformed NAL than silently dropping data.
            if !bytes.is_empty() {
                out.push(Nal(bytes.to_vec()));
            }
            return;
        }
        for w in starts.windows(2) {
            let (a, b) = (w[0], w[1]);
            out.push(Nal(bytes[a..b].to_vec()));
        }
        // Final NAL runs to end of buffer.
        let last = *starts.last().unwrap();
        out.push(Nal(bytes[last..].to_vec()));
    }

    #[cfg(test)]
    mod tests {
        use super::super::super::{EncodeConfig, EncoderBackend, Nal, RawFrame};
        use super::*;

        fn cfg() -> EncodeConfig {
            EncodeConfig {
                width: 320,
                height: 240,
                fps: 30,
                bitrate_bps: 2_000_000,
            }
        }

        fn synthetic_grey(width: u32, height: u32) -> Vec<u8> {
            // Mid-grey RGBA — easiest signal for an encoder to produce a
            // valid bitstream from. Not a stress test, just "did anything
            // come out at all".
            vec![0x80; (width * height * 4) as usize]
        }

        #[test]
        fn split_annexb_handles_3byte_and_4byte_start_codes() {
            // Two NALs: first with 4-byte start code, second with 3-byte.
            // NAL header bytes 0x67 (SPS) and 0x68 (PPS) are valid H.264.
            let buf = [
                0x00, 0x00, 0x00, 0x01, 0x67, 0xAA, 0xBB, // NAL 1 (SPS-shaped)
                0x00, 0x00, 0x01, 0x68, 0xCC, // NAL 2 (PPS-shaped)
            ];
            let mut out: Vec<Nal> = Vec::new();
            split_annexb_into(&buf, &mut out);
            assert_eq!(out.len(), 2, "expected 2 NALs, got {}", out.len());
            // NAL header is the first byte after the start code. Mask off
            // the forbidden + ref_idc bits to read the nal_unit_type.
            assert_eq!(out[0].0[4] & 0x1F, 7, "first NAL should be SPS (type 7)");
            assert_eq!(out[1].0[3] & 0x1F, 8, "second NAL should be PPS (type 8)");
        }

        #[test]
        fn split_annexb_emits_full_buffer_when_no_start_code() {
            // h264_vaapi shouldn't do this, but the defensive branch must
            // still emit data rather than dropping it on the floor.
            let buf = [0xDE, 0xAD, 0xBE, 0xEF];
            let mut out: Vec<Nal> = Vec::new();
            split_annexb_into(&buf, &mut out);
            assert_eq!(out.len(), 1);
            assert_eq!(out[0].0, buf);
        }

        #[test]
        fn smoke_encode_skips_when_no_vaapi() {
            // The whole point of the gating: if there's no VAAPI on this
            // host (CI runner, dev box without /dev/dri), the test must
            // skip cleanly rather than fail. The other smoke assertions
            // are guarded by the same probe.
            let mut e = Libva::new();
            if !e.is_available() {
                eprintln!("VAAPI not available on this host — skipping smoke test");
                return;
            }
            e.init(cfg()).unwrap();
            let mut got_any_nal = false;
            for n in 0..10 {
                let rgba = synthetic_grey(cfg().width, cfg().height);
                let nals = e
                    .encode(&RawFrame {
                        width: cfg().width,
                        height: cfg().height,
                        data: &rgba,
                        capture_ts_ms: n as f64 * 33.3,
                    })
                    .unwrap();
                if !nals.is_empty() {
                    got_any_nal = true;
                    // Annex-B start code check: real output must start with
                    // 00 00 00 01 or 00 00 01. Catches a future change to
                    // an AVCC length-prefix output that would silently
                    // break the WebRTC packetiser.
                    let first = &nals[0].0;
                    let three = first.len() >= 3 && first[..3] == [0, 0, 1];
                    let four = first.len() >= 4 && first[..4] == [0, 0, 0, 1];
                    assert!(
                        three || four,
                        "first NAL doesn't start with an Annex-B start code: {:?}",
                        &first[..first.len().min(8)]
                    );
                }
            }
            assert!(
                got_any_nal,
                "h264_vaapi produced zero NALs across 10 frames"
            );
        }

        #[test]
        fn keyframe_request_produces_idr() {
            let mut e = Libva::new();
            if !e.is_available() {
                eprintln!("VAAPI not available on this host — skipping IDR test");
                return;
            }
            e.init(cfg()).unwrap();
            // Drive the encoder until it has emitted at least one frame so
            // we're past the initial SPS/PPS+IDR every encoder starts with.
            for n in 0..5 {
                let rgba = synthetic_grey(cfg().width, cfg().height);
                let _ = e.encode(&RawFrame {
                    width: cfg().width,
                    height: cfg().height,
                    data: &rgba,
                    capture_ts_ms: n as f64 * 33.3,
                });
            }
            // Now force an IDR and look for NAL type 5 in the next few
            // frames' output. IDR may not appear on the *immediately* next
            // packet thanks to encoder lookahead, but should within a small
            // window.
            e.request_keyframe();
            let mut saw_idr = false;
            for n in 5..15 {
                let rgba = synthetic_grey(cfg().width, cfg().height);
                let nals = e
                    .encode(&RawFrame {
                        width: cfg().width,
                        height: cfg().height,
                        data: &rgba,
                        capture_ts_ms: n as f64 * 33.3,
                    })
                    .unwrap();
                for nal in &nals {
                    // Skip the start code (3 or 4 bytes) and mask
                    // nal_unit_type out of the header byte.
                    let header_idx = if nal.0.len() >= 4 && nal.0[..4] == [0, 0, 0, 1] {
                        4
                    } else if nal.0.len() >= 3 && nal.0[..3] == [0, 0, 1] {
                        3
                    } else {
                        continue;
                    };
                    if header_idx >= nal.0.len() {
                        continue;
                    }
                    let nut = nal.0[header_idx] & 0x1F;
                    if nut == 5 {
                        saw_idr = true;
                    }
                }
                if saw_idr {
                    break;
                }
            }
            assert!(
                saw_idr,
                "no IDR (NAL type 5) emitted after request_keyframe"
            );
        }

        #[test]
        fn close_is_idempotent() {
            let mut e = Libva::new();
            // close before any init: must not panic.
            e.close();
            e.close();
            if !e.is_available() {
                return;
            }
            e.init(cfg()).unwrap();
            e.close();
            // Double close after a real session: also fine.
            e.close();
        }
    }
}

#[cfg(not(target_os = "linux"))]
mod imp {
    //! Non-Linux fallback: compiles cleanly without ffmpeg, reports
    //! unavailable so `auto_select` walks past us.

    use super::super::{EncodeConfig, EncodeError, EncoderBackend, Nal, RawFrame};

    #[allow(dead_code)]
    pub struct Libva {
        /// Placeholder field — preserves the struct layout so swapping
        /// platforms (e.g. cross-compile target) keeps the Box<dyn> sizes
        /// roughly comparable. Never read.
        initialised: bool,
    }

    impl Libva {
        pub fn new() -> Self {
            Self { initialised: false }
        }
    }

    impl Default for Libva {
        fn default() -> Self {
            Self::new()
        }
    }

    impl EncoderBackend for Libva {
        fn name(&self) -> &'static str {
            "libva (unavailable on non-Linux)"
        }

        fn is_available(&self) -> bool {
            false
        }

        fn init(&mut self, _cfg: EncodeConfig) -> Result<(), EncodeError> {
            Err(EncodeError::Unavailable)
        }

        fn encode(&mut self, _frame: &RawFrame<'_>) -> Result<Vec<Nal>, EncodeError> {
            Err(EncodeError::Unavailable)
        }

        fn request_keyframe(&mut self) {}

        fn close(&mut self) {
            self.initialised = false;
        }
    }
}

pub use imp::Libva;
