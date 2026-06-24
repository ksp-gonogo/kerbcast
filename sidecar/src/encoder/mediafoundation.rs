//! Windows Media Foundation hardware H.264 backend, TIER-2 experimental.
//!
//! Vendor-generic hardware encode on Windows: MFTEnumEx with
//! MFT_ENUM_FLAG_HARDWARE finds whichever H.264 encoder MFT the GPU
//! driver registered (AMD VCN, Intel Quick Sync, NVIDIA NVENC all ship
//! one), so a single backend covers every vendor. The motivating machine
//! is an AMD RX 9070 XT, which previously fell through to openh264
//! software encode because the NVENC stub only ever targeted NVIDIA.
//!
//! On every other platform this collapses to a compile-only stub that
//! reports `is_available() == false` so `auto_select` walks past us,
//! mirroring how libva.rs gates its Linux implementation. The `windows`
//! crate dependency is gated to `cfg(windows)` in `Cargo.toml`.
//!
//! ## Implementation notes (Windows path)
//!
//! Hardware encoder MFTs are async MFTs (MF_TRANSFORM_ASYNC == 1): they
//! must be unlocked via MF_TRANSFORM_ASYNC_UNLOCK and driven by the
//! METransformNeedInput / METransformHaveOutput events from their
//! IMFMediaEventGenerator instead of blind ProcessInput/ProcessOutput
//! calls. The flow per frame is:
//!
//! 1. RGBA8 input converted to NV12 directly into a locked
//!    IMFMediaBuffer (shared `convert::rgba_to_nv12_planes`).
//! 2. Wait for a NeedInput credit (events are also drained for
//!    HaveOutput along the way), then ProcessInput.
//! 3. Give the encoder a short bounded window to emit this frame's
//!    output, then drain any remaining events without blocking. Zero
//!    NALs out of one encode() call is fine per the trait contract;
//!    the output arrives on the next call's drain.
//! 4. Output samples carry Annex-B bytestream which we split into `Nal`
//!    units with the shared `annexb::split_annexb_into`.
//!
//! A synchronous-MFT fallback path (plain ProcessInput, then drain
//! ProcessOutput until MF_E_TRANSFORM_NEED_MORE_INPUT) exists for
//! completeness; in practice MFT_ENUM_FLAG_HARDWARE only returns async
//! MFTs.
//!
//! Keyframes are requested via ICodecAPI's CODECAPI_AVEncVideoForceKeyFrame
//! ahead of the next ProcessInput. Rate control is CBR at the configured
//! bitrate with CODECAPI_AVLowLatencyMode on and a 2-second GOP, matching
//! the libva backend's settings.

/// Pack two u32 values into the high/low halves of a u64, the layout
/// Media Foundation uses for MF_MT_FRAME_SIZE (width:height) and
/// MF_MT_FRAME_RATE (numerator:denominator).
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
pub(crate) fn pack_u32_pair(high: u32, low: u32) -> u64 {
    ((high as u64) << 32) | (low as u64)
}

/// Sample timestamp for frame `n` at `fps`, in Media Foundation's 100ns
/// units. Computed from the frame index (not wall clock) so encoder
/// timestamps are perfectly monotonic regardless of capture jitter,
/// matching how the libva backend assigns pts.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
pub(crate) fn frame_time_100ns(frame_index: u64, fps: u32) -> i64 {
    debug_assert!(fps > 0);
    ((frame_index as i64) * 10_000_000) / (fps.max(1) as i64)
}

/// Per-frame duration at `fps` in 100ns units.
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
pub(crate) fn frame_duration_100ns(fps: u32) -> i64 {
    debug_assert!(fps > 0);
    10_000_000 / (fps.max(1) as i64)
}

/// Keyframe roughly every 2 seconds, the same default as the libva
/// backend. Subscriber join still triggers an explicit keyframe via
/// request_keyframe().
#[cfg_attr(not(target_os = "windows"), allow(dead_code))]
pub(crate) fn gop_size(fps: u32) -> u32 {
    fps.saturating_mul(2).max(1)
}

#[cfg(target_os = "windows")]
mod imp {
    use std::mem::ManuallyDrop;
    use std::ptr;
    use std::sync::OnceLock;
    use std::time::{Duration, Instant};
    use tracing::{debug, warn};
    use windows::core::Interface;
    use windows::Win32::Foundation::VARIANT_TRUE;
    use windows::Win32::Media::MediaFoundation::{
        eAVEncCommonRateControlMode_CBR, eAVEncH264VProfile_Base, CODECAPI_AVEncCommonMeanBitRate,
        CODECAPI_AVEncCommonRateControlMode, CODECAPI_AVEncMPVGOPSize,
        CODECAPI_AVEncVideoForceKeyFrame, CODECAPI_AVLowLatencyMode, ICodecAPI, IMFActivate,
        IMFMediaBuffer, IMFMediaEventGenerator, IMFSample, IMFTransform, MEError,
        METransformHaveOutput, METransformNeedInput, MFCreateMediaType, MFCreateMemoryBuffer,
        MFCreateSample, MFMediaType_Video, MFStartup, MFTEnumEx, MFT_FRIENDLY_NAME_Attribute,
        MFVideoFormat_H264, MFVideoFormat_NV12, MFVideoInterlace_Progressive, MFSTARTUP_FULL,
        MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG, MFT_ENUM_FLAG_HARDWARE,
        MFT_ENUM_FLAG_SORTANDFILTER, MFT_MESSAGE_COMMAND_FLUSH, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,
        MFT_MESSAGE_NOTIFY_END_OF_STREAM, MFT_MESSAGE_NOTIFY_END_STREAMING,
        MFT_MESSAGE_NOTIFY_START_OF_STREAM, MFT_OUTPUT_DATA_BUFFER,
        MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES, MFT_OUTPUT_STREAM_PROVIDES_SAMPLES,
        MFT_REGISTER_TYPE_INFO, MF_EVENT_FLAG_NO_WAIT, MF_E_NO_EVENTS_AVAILABLE,
        MF_E_TRANSFORM_NEED_MORE_INPUT, MF_E_TRANSFORM_STREAM_CHANGE, MF_MT_AVG_BITRATE,
        MF_MT_DEFAULT_STRIDE, MF_MT_FRAME_RATE, MF_MT_FRAME_SIZE, MF_MT_INTERLACE_MODE,
        MF_MT_MAJOR_TYPE, MF_MT_MPEG2_PROFILE, MF_MT_PIXEL_ASPECT_RATIO, MF_MT_SUBTYPE,
        MF_TRANSFORM_ASYNC, MF_TRANSFORM_ASYNC_UNLOCK, MF_VERSION,
    };
    use windows::Win32::System::Com::{CoInitializeEx, CoTaskMemFree, COINIT_MULTITHREADED};
    use windows::Win32::System::Variant::{VARIANT, VARIANT_0_0, VARIANT_0_0_0, VT_BOOL, VT_UI4};

    use super::super::annexb::split_annexb_into;
    use super::super::convert::rgba_to_nv12_planes;
    use super::super::{EncodeConfig, EncodeError, EncoderBackend, Nal, RawFrame};
    use super::{frame_duration_100ns, frame_time_100ns, gop_size, pack_u32_pair};

    /// How long encode() will wait for the MFT to hand out a NeedInput
    /// credit before declaring the session wedged. Generous: a healthy
    /// hardware encoder signals NeedInput within a millisecond or two.
    const NEED_INPUT_TIMEOUT: Duration = Duration::from_millis(500);

    /// How long after feeding a frame we poll for that frame's output
    /// before returning. Keeps steady-state latency sub-frame without
    /// stalling the consume loop when the encoder pipelines deeper.
    const OUTPUT_GRACE: Duration = Duration::from_millis(10);

    pub struct MediaFoundation {
        cfg: Option<EncodeConfig>,
        transform: Option<IMFTransform>,
        /// Some(..) when the MFT is async (the normal hardware case);
        /// None drives the synchronous fallback path.
        event_gen: Option<IMFMediaEventGenerator>,
        codec_api: Option<ICodecAPI>,
        /// True when the MFT allocates its own output samples
        /// (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES); hardware MFTs do.
        output_provides_samples: bool,
        /// Output buffer size hint from GetOutputStreamInfo, used when we
        /// must allocate the output sample ourselves.
        output_buffer_size: u32,
        input_stream_id: u32,
        output_stream_id: u32,
        /// Unconsumed METransformNeedInput events carried across encode()
        /// calls. The MFT may queue several credits while we are away.
        need_input_credits: u32,
        keyframe_requested: bool,
        frames_submitted: u64,
    }

    // SAFETY: the COM interfaces held here are only touched from the
    // consume-loop task that owns this backend instance (behind the
    // camera's encoder Mutex). Media Foundation transforms are
    // free-threaded objects; we never share them across threads
    // concurrently. Same pattern as the Libva backend's raw AVBufferRef
    // pointers.
    unsafe impl Send for MediaFoundation {}

    impl MediaFoundation {
        pub fn new() -> Self {
            Self {
                cfg: None,
                transform: None,
                event_gen: None,
                codec_api: None,
                output_provides_samples: false,
                output_buffer_size: 0,
                input_stream_id: 0,
                output_stream_id: 0,
                need_input_credits: 0,
                keyframe_requested: false,
                frames_submitted: 0,
            }
        }

        /// Probe whether a hardware H.264 encoder MFT is registered on
        /// this host. Cached in a OnceLock: auto_select calls
        /// is_available() per encoder session and enumeration walks the
        /// registry. Never panics; CI runners and GPU-less hosts get
        /// `false` and fall through to the software backend.
        fn probe() -> bool {
            static CACHE: OnceLock<bool> = OnceLock::new();
            *CACHE.get_or_init(|| {
                if !mf_runtime_init() {
                    return false;
                }
                match enumerate_hw_h264_encoders() {
                    Ok(activates) => !activates.is_empty(),
                    Err(e) => {
                        debug!(error = %e, "MFTEnumEx(hardware H.264) failed during probe");
                        false
                    }
                }
            })
        }
    }

    impl Default for MediaFoundation {
        fn default() -> Self {
            Self::new()
        }
    }

    /// Process-wide Media Foundation startup. MFShutdown is deliberately
    /// never called: the sidecar uses MF for its entire lifetime and the
    /// OS reclaims everything at process exit.
    fn mf_runtime_init() -> bool {
        static INIT: OnceLock<bool> = OnceLock::new();
        *INIT.get_or_init(|| unsafe {
            // Best-effort COM init for this thread. S_FALSE (already
            // initialised) and RPC_E_CHANGED_MODE (different apartment
            // model already active) are both fine for MF usage.
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
            match MFStartup(MF_VERSION, MFSTARTUP_FULL) {
                Ok(()) => true,
                Err(e) => {
                    warn!(error = %e, "MFStartup failed; Media Foundation backend disabled");
                    false
                }
            }
        })
    }

    /// Enumerate hardware H.264 encoder MFTs, best match first
    /// (MFT_ENUM_FLAG_SORTANDFILTER puts the preferred encoder at
    /// index 0).
    fn enumerate_hw_h264_encoders() -> windows::core::Result<Vec<IMFActivate>> {
        unsafe {
            let output_type = MFT_REGISTER_TYPE_INFO {
                guidMajorType: MFMediaType_Video,
                guidSubtype: MFVideoFormat_H264,
            };
            let mut activates: *mut Option<IMFActivate> = ptr::null_mut();
            let mut count: u32 = 0;
            MFTEnumEx(
                MFT_CATEGORY_VIDEO_ENCODER,
                MFT_ENUM_FLAG(MFT_ENUM_FLAG_HARDWARE.0 | MFT_ENUM_FLAG_SORTANDFILTER.0),
                None,
                Some(&output_type),
                &mut activates,
                &mut count,
            )?;
            let mut out = Vec::with_capacity(count as usize);
            if !activates.is_null() {
                for i in 0..count as usize {
                    // take() moves the interface out of the CoTaskMem
                    // array, leaving None behind, so freeing the array
                    // below cannot double-release.
                    if let Some(activate) = (*activates.add(i)).take() {
                        out.push(activate);
                    }
                }
                CoTaskMemFree(Some(activates as *const _));
            }
            Ok(out)
        }
    }

    /// Friendly name of an MFT activation entry, for logs ("AMDh264Encoder
    /// MFT", "H264 Encoder MFT", ...). Best effort.
    fn activate_friendly_name(activate: &IMFActivate) -> Option<String> {
        unsafe {
            let mut value = windows::core::PWSTR::null();
            let mut len: u32 = 0;
            activate
                .GetAllocatedString(&MFT_FRIENDLY_NAME_Attribute, &mut value, &mut len)
                .ok()?;
            if value.is_null() {
                return None;
            }
            let name = value.to_string().ok();
            CoTaskMemFree(Some(value.as_ptr() as *const _));
            name
        }
    }

    fn variant_u32(value: u32) -> VARIANT {
        let mut var = VARIANT::default();
        var.Anonymous.Anonymous = ManuallyDrop::new(VARIANT_0_0 {
            vt: VT_UI4,
            wReserved1: 0,
            wReserved2: 0,
            wReserved3: 0,
            Anonymous: VARIANT_0_0_0 { ulVal: value },
        });
        var
    }

    fn variant_bool(value: bool) -> VARIANT {
        let mut var = VARIANT::default();
        var.Anonymous.Anonymous = ManuallyDrop::new(VARIANT_0_0 {
            vt: VT_BOOL,
            wReserved1: 0,
            wReserved2: 0,
            wReserved3: 0,
            Anonymous: VARIANT_0_0_0 {
                boolVal: if value {
                    VARIANT_TRUE
                } else {
                    windows::Win32::Foundation::VARIANT_FALSE
                },
            },
        });
        var
    }

    /// Set an ICodecAPI property, warning instead of failing: support for
    /// individual properties varies by vendor and none of ours are
    /// load-bearing for correctness (rate-control defaults still encode).
    fn set_codec_property(api: &ICodecAPI, guid: &windows::core::GUID, value: VARIANT, what: &str) {
        unsafe {
            if let Err(e) = api.SetValue(guid, &value) {
                debug!(property = what, error = %e, "ICodecAPI::SetValue rejected (vendor-dependent, continuing)");
            }
        }
    }

    impl EncoderBackend for MediaFoundation {
        fn name(&self) -> &'static str {
            "h264 mft (media foundation)"
        }

        fn is_available(&self) -> bool {
            Self::probe()
        }

        fn is_hardware(&self) -> bool {
            true
        }

        fn init(&mut self, cfg: EncodeConfig) -> Result<(), EncodeError> {
            if cfg.width == 0 || cfg.height == 0 {
                return Err(EncodeError::Invalid("zero-sized dimensions".into()));
            }
            if cfg.fps == 0 {
                return Err(EncodeError::Invalid("fps == 0".into()));
            }
            // NV12 requires even dimensions for chroma subsampling.
            if !cfg.width.is_multiple_of(2) || !cfg.height.is_multiple_of(2) {
                return Err(EncodeError::Invalid(format!(
                    "dimensions must be even (got {}x{})",
                    cfg.width, cfg.height
                )));
            }

            // Make sure any previous session is fully torn down before we
            // start (init-after-init must work).
            self.close();

            if !mf_runtime_init() {
                return Err(EncodeError::Unavailable);
            }

            unsafe {
                let activates = enumerate_hw_h264_encoders()
                    .map_err(|e| EncodeError::Runtime(format!("MFTEnumEx failed: {e}")))?;
                let activate = activates.first().ok_or(EncodeError::Unavailable)?;
                let mft_name =
                    activate_friendly_name(activate).unwrap_or_else(|| "<unnamed MFT>".to_string());

                let transform: IMFTransform = activate.ActivateObject().map_err(|e| {
                    EncodeError::Runtime(format!("MFT activation failed ({mft_name}): {e}"))
                })?;

                // Async MFT detection + mandatory unlock. Hardware MFTs
                // are async; a missing attribute store or attribute means
                // a synchronous MFT and the fallback path.
                let mut is_async = false;
                if let Ok(attrs) = transform.GetAttributes() {
                    is_async = attrs.GetUINT32(&MF_TRANSFORM_ASYNC).unwrap_or(0) == 1;
                    if is_async {
                        attrs
                            .SetUINT32(&MF_TRANSFORM_ASYNC_UNLOCK, 1)
                            .map_err(|e| {
                                EncodeError::Runtime(format!(
                                    "MF_TRANSFORM_ASYNC_UNLOCK failed: {e}"
                                ))
                            })?;
                    }
                }

                // Stream IDs. E_NOTIMPL from GetStreamIDs means the MFT
                // uses consecutive zero-based IDs.
                let mut input_ids = [0u32; 1];
                let mut output_ids = [0u32; 1];
                let (input_stream_id, output_stream_id) =
                    match transform.GetStreamIDs(&mut input_ids, &mut output_ids) {
                        Ok(()) => (input_ids[0], output_ids[0]),
                        Err(_) => (0, 0),
                    };

                // Rate control + latency knobs before media types: some
                // encoders latch rate-control mode at SetOutputType.
                let codec_api: Option<ICodecAPI> = transform.cast().ok();
                if let Some(api) = &codec_api {
                    set_codec_property(
                        api,
                        &CODECAPI_AVEncCommonRateControlMode,
                        variant_u32(eAVEncCommonRateControlMode_CBR.0 as u32),
                        "rate-control-mode=CBR",
                    );
                    set_codec_property(
                        api,
                        &CODECAPI_AVEncCommonMeanBitRate,
                        variant_u32(cfg.bitrate_bps),
                        "mean-bitrate",
                    );
                    set_codec_property(
                        api,
                        &CODECAPI_AVLowLatencyMode,
                        variant_bool(true),
                        "low-latency-mode",
                    );
                    set_codec_property(
                        api,
                        &CODECAPI_AVEncMPVGOPSize,
                        variant_u32(gop_size(cfg.fps)),
                        "gop-size",
                    );
                } else {
                    debug!("MFT does not expose ICodecAPI; using encoder defaults");
                }

                // Encoders need the output type configured before the
                // input type.
                let output_type = MFCreateMediaType()
                    .map_err(|e| EncodeError::Runtime(format!("MFCreateMediaType: {e}")))?;
                output_type
                    .SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)
                    .and_then(|()| output_type.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_H264))
                    .and_then(|()| output_type.SetUINT32(&MF_MT_AVG_BITRATE, cfg.bitrate_bps))
                    .and_then(|()| {
                        output_type
                            .SetUINT64(&MF_MT_FRAME_SIZE, pack_u32_pair(cfg.width, cfg.height))
                    })
                    .and_then(|()| {
                        output_type.SetUINT64(&MF_MT_FRAME_RATE, pack_u32_pair(cfg.fps, 1))
                    })
                    .and_then(|()| {
                        output_type.SetUINT64(&MF_MT_PIXEL_ASPECT_RATIO, pack_u32_pair(1, 1))
                    })
                    .and_then(|()| {
                        output_type
                            .SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)
                    })
                    .and_then(|()| {
                        // Baseline keeps us inside what every browser's
                        // H.264 decoder accepts, same envelope as the
                        // openh264 fallback produces.
                        output_type
                            .SetUINT32(&MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base.0 as u32)
                    })
                    .map_err(|e| {
                        EncodeError::Runtime(format!("building H.264 output type failed: {e}"))
                    })?;
                transform
                    .SetOutputType(output_stream_id, &output_type, 0)
                    .map_err(|e| EncodeError::Runtime(format!("SetOutputType failed: {e}")))?;

                let input_type = MFCreateMediaType()
                    .map_err(|e| EncodeError::Runtime(format!("MFCreateMediaType: {e}")))?;
                input_type
                    .SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)
                    .and_then(|()| input_type.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_NV12))
                    .and_then(|()| {
                        input_type
                            .SetUINT64(&MF_MT_FRAME_SIZE, pack_u32_pair(cfg.width, cfg.height))
                    })
                    .and_then(|()| {
                        input_type.SetUINT64(&MF_MT_FRAME_RATE, pack_u32_pair(cfg.fps, 1))
                    })
                    .and_then(|()| {
                        input_type
                            .SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)
                    })
                    .and_then(|()| {
                        // Our NV12 staging buffer is tightly packed.
                        input_type.SetUINT32(&MF_MT_DEFAULT_STRIDE, cfg.width)
                    })
                    .map_err(|e| {
                        EncodeError::Runtime(format!("building NV12 input type failed: {e}"))
                    })?;
                transform
                    .SetInputType(input_stream_id, &input_type, 0)
                    .map_err(|e| EncodeError::Runtime(format!("SetInputType failed: {e}")))?;

                // Who allocates output samples? Hardware MFTs provide
                // their own; if not, we allocate per ProcessOutput call
                // using the advertised size.
                let stream_info = transform
                    .GetOutputStreamInfo(output_stream_id)
                    .map_err(|e| EncodeError::Runtime(format!("GetOutputStreamInfo: {e}")))?;
                let provides = stream_info.dwFlags
                    & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.0 as u32
                        | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES.0 as u32)
                    != 0;

                let event_gen = if is_async {
                    Some(transform.cast::<IMFMediaEventGenerator>().map_err(|e| {
                        EncodeError::Runtime(format!(
                            "async MFT without IMFMediaEventGenerator: {e}"
                        ))
                    })?)
                } else {
                    None
                };

                transform
                    .ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0)
                    .and_then(|()| transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0))
                    .map_err(|e| {
                        EncodeError::Runtime(format!("MFT begin-streaming failed: {e}"))
                    })?;

                debug!(
                    width = cfg.width,
                    height = cfg.height,
                    fps = cfg.fps,
                    bitrate_bps = cfg.bitrate_bps,
                    mft = %mft_name,
                    is_async,
                    provides_output_samples = provides,
                    "Media Foundation encoder initialised"
                );

                self.transform = Some(transform);
                self.event_gen = event_gen;
                self.codec_api = codec_api;
                self.output_provides_samples = provides;
                self.output_buffer_size = stream_info.cbSize;
                self.input_stream_id = input_stream_id;
                self.output_stream_id = output_stream_id;
            }

            self.need_input_credits = 0;
            self.keyframe_requested = false;
            self.frames_submitted = 0;
            self.cfg = Some(cfg);
            Ok(())
        }

        fn encode(&mut self, frame: &RawFrame<'_>) -> Result<Vec<Nal>, EncodeError> {
            /* Snapshot the dims/fps rather than cloning the whole EncodeConfig
            each frame: only these scalars are read, and the borrow must drop
            before the encoder is used mutably below. */
            let (cfg_width, cfg_height, cfg_fps) = {
                let cfg = self
                    .cfg
                    .as_ref()
                    .ok_or_else(|| EncodeError::Runtime("encode before init".into()))?;
                (cfg.width, cfg.height, cfg.fps)
            };
            let expected = (frame.width as usize) * (frame.height as usize) * 4;
            if frame.data.len() != expected {
                return Err(EncodeError::Invalid(format!(
                    "frame size {} != width*height*4 ({})",
                    frame.data.len(),
                    expected
                )));
            }
            if frame.width != cfg_width || frame.height != cfg_height {
                return Err(EncodeError::Invalid(format!(
                    "frame dims {}x{} != configured {}x{}",
                    frame.width, frame.height, cfg_width, cfg_height
                )));
            }
            let transform = self
                .transform
                .clone()
                .ok_or_else(|| EncodeError::Runtime("encoder dropped after init".into()))?;

            let sample = self.build_input_sample(frame, cfg_fps)?;

            if self.keyframe_requested {
                if let Some(api) = &self.codec_api {
                    set_codec_property(
                        api,
                        &CODECAPI_AVEncVideoForceKeyFrame,
                        variant_u32(1),
                        "force-keyframe",
                    );
                }
                self.keyframe_requested = false;
            }

            let mut nals = Vec::new();
            match self.event_gen.clone() {
                Some(event_gen) => self.encode_async(&transform, &event_gen, sample, &mut nals)?,
                None => self.encode_sync(&transform, sample, &mut nals)?,
            }

            self.frames_submitted += 1;
            Ok(nals)
        }

        fn request_keyframe(&mut self) {
            self.keyframe_requested = true;
        }

        fn close(&mut self) {
            if let Some(transform) = self.transform.take() {
                unsafe {
                    // Best effort teardown; the MFT is released when the
                    // last COM reference drops below.
                    let _ = transform.ProcessMessage(
                        MFT_MESSAGE_NOTIFY_END_OF_STREAM,
                        self.input_stream_id as usize,
                    );
                    let _ = transform.ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
                    let _ = transform.ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
                }
            }
            self.event_gen = None;
            self.codec_api = None;
            self.output_provides_samples = false;
            self.output_buffer_size = 0;
            self.need_input_credits = 0;
            self.cfg = None;
            self.keyframe_requested = false;
            self.frames_submitted = 0;
        }
    }

    impl Drop for MediaFoundation {
        fn drop(&mut self) {
            self.close();
        }
    }

    impl MediaFoundation {
        /// RGBA frame to an NV12 IMFSample, converting directly into the
        /// locked media buffer (no intermediate staging copy).
        fn build_input_sample(
            &self,
            frame: &RawFrame<'_>,
            fps: u32,
        ) -> Result<IMFSample, EncodeError> {
            unsafe {
                let w = frame.width as usize;
                let h = frame.height as usize;
                let nv12_len = w * h + (w * h) / 2;
                let buffer: IMFMediaBuffer = MFCreateMemoryBuffer(nv12_len as u32)
                    .map_err(|e| EncodeError::Runtime(format!("MFCreateMemoryBuffer: {e}")))?;
                let mut data_ptr: *mut u8 = ptr::null_mut();
                buffer
                    .Lock(&mut data_ptr, None, None)
                    .map_err(|e| EncodeError::Runtime(format!("IMFMediaBuffer::Lock: {e}")))?;
                {
                    let dst = std::slice::from_raw_parts_mut(data_ptr, nv12_len);
                    let (y_plane, uv_plane) = dst.split_at_mut(w * h);
                    rgba_to_nv12_planes(
                        frame.data,
                        frame.width,
                        frame.height,
                        y_plane,
                        w,
                        uv_plane,
                        w,
                    );
                }
                buffer
                    .Unlock()
                    .map_err(|e| EncodeError::Runtime(format!("IMFMediaBuffer::Unlock: {e}")))?;
                buffer
                    .SetCurrentLength(nv12_len as u32)
                    .map_err(|e| EncodeError::Runtime(format!("SetCurrentLength: {e}")))?;

                let sample = MFCreateSample()
                    .map_err(|e| EncodeError::Runtime(format!("MFCreateSample: {e}")))?;
                sample
                    .AddBuffer(&buffer)
                    .map_err(|e| EncodeError::Runtime(format!("AddBuffer: {e}")))?;
                sample
                    .SetSampleTime(frame_time_100ns(self.frames_submitted, fps))
                    .map_err(|e| EncodeError::Runtime(format!("SetSampleTime: {e}")))?;
                sample
                    .SetSampleDuration(frame_duration_100ns(fps))
                    .map_err(|e| EncodeError::Runtime(format!("SetSampleDuration: {e}")))?;
                Ok(sample)
            }
        }

        /// Async-MFT path: spend NeedInput credits (waiting for one if
        /// necessary), feed the frame, then give the encoder a short
        /// window to emit output before returning.
        fn encode_async(
            &mut self,
            transform: &IMFTransform,
            event_gen: &IMFMediaEventGenerator,
            sample: IMFSample,
            nals: &mut Vec<Nal>,
        ) -> Result<(), EncodeError> {
            // Phase 1: feed the input. Drain whatever events arrive while
            // we wait for a NeedInput credit.
            let deadline = Instant::now() + NEED_INPUT_TIMEOUT;
            loop {
                if self.need_input_credits > 0 {
                    self.need_input_credits -= 1;
                    unsafe {
                        transform
                            .ProcessInput(self.input_stream_id, &sample, 0)
                            .map_err(|e| {
                                EncodeError::Runtime(format!("ProcessInput failed: {e}"))
                            })?;
                    }
                    break;
                }
                match self.poll_event(event_gen, transform, nals)? {
                    EventPoll::Handled => {}
                    EventPoll::Empty => {
                        if Instant::now() >= deadline {
                            return Err(EncodeError::Runtime(
                                "timed out waiting for METransformNeedInput".into(),
                            ));
                        }
                        std::thread::sleep(Duration::from_millis(1));
                    }
                }
            }

            // Phase 2: short grace window for this frame's output, then
            // drain remaining queued events without blocking. Output that
            // misses the window is collected on the next encode() call.
            let grace_deadline = Instant::now() + OUTPUT_GRACE;
            let mut got_output = false;
            loop {
                let before = nals.len();
                match self.poll_event(event_gen, transform, nals)? {
                    EventPoll::Handled => {
                        got_output |= nals.len() > before;
                    }
                    EventPoll::Empty => {
                        if got_output || Instant::now() >= grace_deadline {
                            break;
                        }
                        std::thread::sleep(Duration::from_millis(1));
                    }
                }
            }
            Ok(())
        }

        /// Synchronous-MFT fallback: push input, drain output until the
        /// transform asks for more input.
        fn encode_sync(
            &mut self,
            transform: &IMFTransform,
            sample: IMFSample,
            nals: &mut Vec<Nal>,
        ) -> Result<(), EncodeError> {
            unsafe {
                transform
                    .ProcessInput(self.input_stream_id, &sample, 0)
                    .map_err(|e| EncodeError::Runtime(format!("ProcessInput failed: {e}")))?;
            }
            while self.process_output_once(transform, nals)? {}
            Ok(())
        }

        /// Pull at most one event off the MFT's queue and handle it.
        fn poll_event(
            &mut self,
            event_gen: &IMFMediaEventGenerator,
            transform: &IMFTransform,
            nals: &mut Vec<Nal>,
        ) -> Result<EventPoll, EncodeError> {
            let event = unsafe { event_gen.GetEvent(MF_EVENT_FLAG_NO_WAIT) };
            let event = match event {
                Ok(ev) => ev,
                Err(e) if e.code() == MF_E_NO_EVENTS_AVAILABLE => return Ok(EventPoll::Empty),
                Err(e) => {
                    return Err(EncodeError::Runtime(format!("GetEvent failed: {e}")));
                }
            };
            let met = unsafe { event.GetType() }
                .map_err(|e| EncodeError::Runtime(format!("IMFMediaEvent::GetType: {e}")))?;
            if met == METransformNeedInput.0 as u32 {
                self.need_input_credits += 1;
            } else if met == METransformHaveOutput.0 as u32 {
                self.process_output_once(transform, nals)?;
            } else if met == MEError.0 as u32 {
                return Err(EncodeError::Runtime("MFT signalled MEError".into()));
            }
            // Other events (stream/sink notifications) are irrelevant here.
            Ok(EventPoll::Handled)
        }

        /// One ProcessOutput round trip. Returns Ok(true) when a sample
        /// was produced, Ok(false) when the transform needs more input.
        /// Handles MF_E_TRANSFORM_STREAM_CHANGE by re-binding the first
        /// available output type and retrying.
        fn process_output_once(
            &mut self,
            transform: &IMFTransform,
            nals: &mut Vec<Nal>,
        ) -> Result<bool, EncodeError> {
            unsafe {
                loop {
                    let provided = if self.output_provides_samples {
                        None
                    } else {
                        Some(self.allocate_output_sample()?)
                    };
                    let mut buffers = [MFT_OUTPUT_DATA_BUFFER {
                        dwStreamID: self.output_stream_id,
                        pSample: ManuallyDrop::new(provided),
                        dwStatus: 0,
                        pEvents: ManuallyDrop::new(None),
                    }];
                    let mut status: u32 = 0;
                    let result = transform.ProcessOutput(0, &mut buffers, &mut status);
                    // Reclaim ownership from the ManuallyDrop fields
                    // unconditionally so neither samples nor event
                    // collections leak on any branch.
                    let out_sample = ManuallyDrop::take(&mut buffers[0].pSample);
                    let _events = ManuallyDrop::take(&mut buffers[0].pEvents);

                    match result {
                        Ok(()) => {
                            if let Some(sample) = out_sample {
                                self.copy_sample_nals(&sample, nals)?;
                            }
                            return Ok(true);
                        }
                        Err(e) if e.code() == MF_E_TRANSFORM_NEED_MORE_INPUT => {
                            return Ok(false);
                        }
                        Err(e) if e.code() == MF_E_TRANSFORM_STREAM_CHANGE => {
                            // The encoder renegotiates its output type
                            // (typically once, right after streaming
                            // starts). Accept the first offered type and
                            // retry.
                            let new_type = transform
                                .GetOutputAvailableType(self.output_stream_id, 0)
                                .map_err(|e2| {
                                    EncodeError::Runtime(format!(
                                        "GetOutputAvailableType after stream change: {e2}"
                                    ))
                                })?;
                            transform
                                .SetOutputType(self.output_stream_id, &new_type, 0)
                                .map_err(|e2| {
                                    EncodeError::Runtime(format!(
                                        "SetOutputType after stream change: {e2}"
                                    ))
                                })?;
                            if let Ok(info) = transform.GetOutputStreamInfo(self.output_stream_id) {
                                self.output_provides_samples = info.dwFlags
                                    & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.0 as u32
                                        | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES.0 as u32)
                                    != 0;
                                self.output_buffer_size = info.cbSize;
                            }
                            continue;
                        }
                        Err(e) => {
                            return Err(EncodeError::Runtime(format!("ProcessOutput failed: {e}")));
                        }
                    }
                }
            }
        }

        /// Allocate an output sample for MFTs that don't provide their
        /// own (rare for hardware encoders, required for the sync path).
        fn allocate_output_sample(&self) -> Result<IMFSample, EncodeError> {
            unsafe {
                let size = if self.output_buffer_size > 0 {
                    self.output_buffer_size
                } else {
                    // No hint: a compressed frame can't plausibly exceed
                    // the raw NV12 size of the configured resolution.
                    let cfg = self.cfg.as_ref();
                    let (w, h) = cfg.map(|c| (c.width, c.height)).unwrap_or((1920, 1080));
                    (w * h * 3) / 2
                };
                let buffer = MFCreateMemoryBuffer(size)
                    .map_err(|e| EncodeError::Runtime(format!("MFCreateMemoryBuffer: {e}")))?;
                let sample = MFCreateSample()
                    .map_err(|e| EncodeError::Runtime(format!("MFCreateSample: {e}")))?;
                sample
                    .AddBuffer(&buffer)
                    .map_err(|e| EncodeError::Runtime(format!("AddBuffer: {e}")))?;
                Ok(sample)
            }
        }

        /// Copy an output sample's Annex-B payload and split it into NALs.
        fn copy_sample_nals(
            &self,
            sample: &IMFSample,
            nals: &mut Vec<Nal>,
        ) -> Result<(), EncodeError> {
            unsafe {
                let buffer = sample
                    .ConvertToContiguousBuffer()
                    .map_err(|e| EncodeError::Runtime(format!("ConvertToContiguousBuffer: {e}")))?;
                let mut data_ptr: *mut u8 = ptr::null_mut();
                let mut current_len: u32 = 0;
                buffer
                    .Lock(&mut data_ptr, None, Some(&mut current_len))
                    .map_err(|e| EncodeError::Runtime(format!("IMFMediaBuffer::Lock: {e}")))?;
                if !data_ptr.is_null() && current_len > 0 {
                    let bytes = std::slice::from_raw_parts(data_ptr, current_len as usize);
                    split_annexb_into(bytes, nals);
                } else {
                    warn!("Media Foundation encoder returned an empty output sample");
                }
                buffer
                    .Unlock()
                    .map_err(|e| EncodeError::Runtime(format!("IMFMediaBuffer::Unlock: {e}")))?;
                Ok(())
            }
        }
    }

    enum EventPoll {
        /// An event was pulled and handled (credit added, output drained,
        /// or irrelevant event discarded).
        Handled,
        /// The event queue is empty right now.
        Empty,
    }
}

#[cfg(not(target_os = "windows"))]
mod imp {
    //! Non-Windows fallback: compiles cleanly without the `windows`
    //! crate, reports unavailable so `auto_select` walks past us.

    use super::super::{EncodeConfig, EncodeError, EncoderBackend, Nal, RawFrame};

    #[allow(dead_code)]
    pub struct MediaFoundation {
        /// Placeholder field, mirroring the Libva stub's layout trick.
        /// Never read.
        initialised: bool,
    }

    impl MediaFoundation {
        pub fn new() -> Self {
            Self { initialised: false }
        }
    }

    impl Default for MediaFoundation {
        fn default() -> Self {
            Self::new()
        }
    }

    impl EncoderBackend for MediaFoundation {
        fn name(&self) -> &'static str {
            "media foundation (unavailable on non-Windows)"
        }

        fn is_available(&self) -> bool {
            false
        }

        fn is_hardware(&self) -> bool {
            true
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

pub use imp::MediaFoundation;

#[cfg(test)]
mod tests {
    use super::super::EncoderBackend;
    use super::*;

    #[test]
    fn pack_u32_pair_matches_mf_layout() {
        // MF_MT_FRAME_SIZE packs width into the high half, height into
        // the low half.
        assert_eq!(pack_u32_pair(1280, 720), (1280u64 << 32) | 720);
        assert_eq!(pack_u32_pair(0, 0), 0);
        assert_eq!(pack_u32_pair(u32::MAX, u32::MAX), u64::MAX);
    }

    #[test]
    fn frame_times_are_monotonic_and_match_duration() {
        let fps = 30;
        let dur = frame_duration_100ns(fps);
        assert_eq!(dur, 333_333);
        let mut last = -1i64;
        for n in 0..120u64 {
            let t = frame_time_100ns(n, fps);
            assert!(t > last, "timestamps must be strictly increasing");
            last = t;
        }
        // Frame 30 at 30fps is exactly one second.
        assert_eq!(frame_time_100ns(30, 30), 10_000_000);
    }

    #[test]
    fn gop_is_two_seconds_with_a_floor_of_one() {
        assert_eq!(gop_size(30), 60);
        assert_eq!(gop_size(1), 2);
        assert_eq!(gop_size(0), 1);
        // Saturates rather than overflowing.
        assert_eq!(gop_size(u32::MAX), u32::MAX);
    }

    #[test]
    fn stub_reports_unavailable_off_windows() {
        // On Windows this instead exercises the real probe, which must
        // not panic on hosts without a hardware encoder MFT.
        let backend = MediaFoundation::new();
        let _ = backend.is_available();
        #[cfg(not(target_os = "windows"))]
        assert!(!backend.is_available());
    }

    #[cfg(not(target_os = "windows"))]
    #[test]
    fn stub_init_errors_unavailable() {
        use super::super::{EncodeConfig, EncodeError, EncoderBackend};
        let mut backend = MediaFoundation::new();
        let err = backend
            .init(EncodeConfig {
                width: 64,
                height: 64,
                fps: 30,
                bitrate_bps: 500_000,
            })
            .unwrap_err();
        assert!(matches!(err, EncodeError::Unavailable));
    }
}
