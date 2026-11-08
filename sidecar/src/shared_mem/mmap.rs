//! File-backed mmap'd frame ring buffer — the cross-process / cross-language
//! handoff between the KSP plugin (C# writer) and the sidecar (Rust reader).
//!
//! Wire layout is a hard contract: changing it requires bumping
//! `LAYOUT_VERSION` and updating the C# writer in lockstep.
//!
//! ```text
//! offset   size   field
//! ------   ----   -----
//! 0        8      u64 magic = 0x4B45524243414D31  ("KERBCAM1", legacy, pinned for compat)
//! 8        4      u32 version (this layout = 2)
//! 12       4      u32 slot_count
//! 16       4      u32 max_width  (per-slot pixel capacity)
//! 20       4      u32 max_height
//! 24       4      u32 atomic write_index (most-recently-written slot)
//! 28       4      padding
//! 32       8      u64 atomic sequence (monotonic; wraps only after 2^64)
//! 40       4      u32 meta_slot_bytes (per-slot metadata capacity; 0 = none)
//! 44       (HEADER_SIZE - 44) padding to align first slot
//!
//! 4096 + (slot_idx * SLOT_SIZE):    slot start
//!   +0   4      u32 width
//!   +4   4      u32 height
//!   +8   4      u32 stride_bytes
//!   +12  4      u32 capture_epoch (plugin's UT discontinuity counter at render)
//!   +16  8      f64 capture_ts_ms
//!   +24  8      u64 sequence (matches header.sequence when this slot was written)
//!   +32  8      f64 capture_ut (KSP universal time at render; NaN = unknown)
//!   +40  4      u32 meta_len (bytes of this frame's metadata; 0 = none)
//!   +44  4      f32 fov_deg (vertical field of view at render; 0 = unknown)
//!   +48  8      f64 live_light_time_s (the frame's live bound; 0 = no delay, NaN = unknown)
//!   +56  8      padding
//!   +64  ...    RGBA8 pixels, max_width * max_height * 4 bytes
//!
//! 4096 + slot_count * SLOT_SIZE + (slot_idx * meta_slot_bytes):
//!   per-slot metadata, `meta_len` bytes of UTF-8 JSON: one object with the
//!   frame's `boundaryM`, a command camera's `command` state and its
//!   `anchors`, an array of `FrameAnchor` each carrying its `owner`. A bare
//!   array of anchors (the first 1.12 builds) is read too.
//! ```
//!
//! The metadata fields arrived in 1.12 without a version bump. Writers
//! before 1.12 leave them zero (the header is zeroed and the slot padding
//! never written), which reads as "no metadata, fov unknown, no delay": a
//! writer that old had no light speed, so nothing it drew was delayed. A
//! reader before 1.12 maps only the slots, so it never sees the trailer,
//! and ignores the three slot fields.
//!
//! The version stays 2 because both readers refuse any other: a bump would
//! turn that graceful mix into no picture at all. A later change that old
//! readers can ignore takes the header padding at 44 (zero from every writer
//! so far) as a minor version; one they cannot ignore bumps `LAYOUT_VERSION`,
//! and the reader that brings it accepts 2 as well.
//!
//! Sync protocol (seqlock-style, one-writer / one-or-more-readers):
//!
//! - **Writer**: bumps `header.sequence` (fetch_add Release), picks
//!   `next = (write_index + 1) % slot_count`, writes the whole slot, stores
//!   `slot.sequence` = new seq (Release), then publishes
//!   `header.write_index = next` (Release).
//! - **Reader**: snapshots `header.sequence` (Acquire), loads
//!   `header.write_index` (Acquire), reads the slot, then re-checks
//!   `slot.sequence` matches the snapshot — if it changed mid-read the writer
//!   has clobbered our slot, so retry on the new write_index. Bounded retries
//!   protect against pathological writers; in practice a single retry
//!   suffices because the ring has 4 slots.

use std::fs::OpenOptions;
use std::path::Path;
use std::sync::atomic::{fence, AtomicU32, AtomicU64, Ordering};
use std::sync::Arc;

use memmap2::{MmapMut, MmapOptions};
use thiserror::Error;

use crate::protocol::{FrameAnchor, FrameCommandState};

pub const MAGIC: u64 = 0x4B45_5242_4341_4D31; // "KERBCAM1" legacy ring magic; value pinned for plugin wire-format compat, not renamed
pub const LAYOUT_VERSION: u32 = 2;

/// Attempts a read makes before giving up on a writer that keeps lapping it.
const MAX_READ_RETRIES: u32 = 4;

/// Header is one page so the first slot is page-aligned. Keeps the writer's
/// per-slot `pixels` write inside one page-aligned region per slot, which
/// helps the kernel coalesce dirty pages and is friendly to cross-process
/// cache coherence.
pub const HEADER_SIZE: usize = 4096;

const HEADER_OFF_MAGIC: usize = 0;
const HEADER_OFF_VERSION: usize = 8;
const HEADER_OFF_SLOT_COUNT: usize = 12;
const HEADER_OFF_MAX_WIDTH: usize = 16;
const HEADER_OFF_MAX_HEIGHT: usize = 20;
const HEADER_OFF_WRITE_INDEX: usize = 24;
const HEADER_OFF_SEQUENCE: usize = 32;
const HEADER_OFF_META_SLOT_BYTES: usize = 40;

const SLOT_OFF_WIDTH: usize = 0;
const SLOT_OFF_HEIGHT: usize = 4;
const SLOT_OFF_STRIDE: usize = 8;
const SLOT_OFF_CAPTURE_EPOCH: usize = 12;
const SLOT_OFF_CAPTURE_TS: usize = 16;
const SLOT_OFF_SEQUENCE: usize = 24;
const SLOT_OFF_CAPTURE_UT: usize = 32;
const SLOT_OFF_META_LEN: usize = 40;
const SLOT_OFF_FOV: usize = 44;
const SLOT_OFF_LIVE_LIGHT_TIME: usize = 48;
const SLOT_OFF_PIXELS: usize = 64;
const SLOT_PIXELS_ALIGNMENT_PAD: usize = 0; // SLOT_OFF_PIXELS is already 64, 32-aligned

/// Largest per-slot metadata capacity a reader accepts from a header, so a
/// corrupt header cannot drive a wild mapping.
pub const MAX_META_SLOT_BYTES: u32 = 1 << 20;

/// Mission-time stamp of one rendered frame: the KSP universal time it was
/// rendered at plus the plugin's discontinuity epoch at that moment, and
/// what the game reported about how it was drawn.
#[derive(Debug, Clone, PartialEq)]
pub struct CaptureStamp {
    /// `None` when the writer had no universal time (stored as NaN).
    pub ut: Option<f64>,
    pub epoch: u32,
    /// Vertical field of view at render, degrees; `None` when unreported.
    pub fov: Option<f64>,
    /// The frame's size in pixels; `None` outside a ring read.
    pub size: Option<(u32, u32)>,
    /// Where each anchor landed in the frame, every owner's, each tagged
    /// with its owner. `None` when the writer reported none.
    pub anchors: Option<Arc<Vec<FrameAnchor>>>,
    /// The frame's live bound in seconds (see `FrameStampPayload`). 0 with
    /// no light speed; `None` when the writer could not read it, or outside
    /// a ring read.
    pub live_light_time_s: Option<f64>,
    /// How far light travels in one 60 fps frame at the light speed: the
    /// picture's boundary distance. `None` with no light speed.
    pub boundary_m: Option<f64>,
    /// A command camera's state as this frame shows it.
    pub command: Option<Arc<FrameCommandState>>,
}

impl CaptureStamp {
    pub const UNKNOWN: CaptureStamp = CaptureStamp {
        ut: None,
        epoch: 0,
        fov: None,
        size: None,
        anchors: None,
        live_light_time_s: None,
        boundary_m: None,
        command: None,
    };

    /// A stamp with only the UT and epoch known.
    pub fn at(ut: Option<f64>, epoch: u32) -> Self {
        Self {
            ut,
            epoch,
            ..Self::UNKNOWN
        }
    }
}

/// What a slot's metadata carries beside the slot fields.
#[derive(Debug, Default, PartialEq)]
struct SlotMeta {
    anchors: Option<Arc<Vec<FrameAnchor>>>,
    boundary_m: Option<f64>,
    command: Option<Arc<FrameCommandState>>,
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct MetaObject {
    #[serde(default)]
    anchors: Vec<FrameAnchor>,
    #[serde(default)]
    boundary_m: Option<f64>,
    /// Read as a value first, so a command state this reader cannot take
    /// (an aim kind it does not know, say) costs only itself.
    #[serde(default)]
    command: Option<serde_json::Value>,
}

#[derive(serde::Deserialize)]
#[serde(untagged)]
enum MetaShape {
    Object(Box<MetaObject>),
    Anchors(Vec<FrameAnchor>),
}

/// A slot's metadata bytes. Unreadable metadata reads as none: a frame is
/// never dropped over its metadata.
fn parse_meta(bytes: &[u8]) -> SlotMeta {
    if bytes.is_empty() {
        return SlotMeta::default();
    }
    let (anchors, boundary_m, command) = match serde_json::from_slice::<MetaShape>(bytes) {
        Ok(MetaShape::Object(o)) => {
            let o = *o;
            (o.anchors, o.boundary_m, o.command)
        }
        Ok(MetaShape::Anchors(a)) => (a, None, None),
        Err(e) => {
            tracing::debug!(error = %e, "frame metadata unreadable; ignored");
            return SlotMeta::default();
        }
    };
    let command = command.and_then(|v| match serde_json::from_value::<FrameCommandState>(v) {
        Ok(c) => Some(Arc::new(c)),
        Err(e) => {
            tracing::debug!(error = %e, "frame command state unreadable; ignored");
            None
        }
    });
    SlotMeta {
        anchors: (!anchors.is_empty()).then(|| Arc::new(anchors)),
        boundary_m: boundary_m.filter(|b| b.is_finite() && *b > 0.0),
        command,
    }
}

#[derive(Debug, Error)]
pub enum MmapRingError {
    #[error("io: {0}")]
    Io(#[from] std::io::Error),
    #[error("file is too small to hold the configured ring: {got} < {need} bytes")]
    FileTooSmall { got: u64, need: u64 },
    #[error("magic mismatch: got 0x{got:016x}, expected 0x{:016x}", MAGIC)]
    BadMagic { got: u64 },
    #[error(
        "layout version mismatch: got {got}, this build expects {}",
        LAYOUT_VERSION
    )]
    BadVersion { got: u32 },
    #[error("layout dimensions mismatch: file says {got_w}x{got_h} slots={got_slots}, opened with {req_w}x{req_h} slots={req_slots}")]
    LayoutMismatch {
        got_w: u32,
        got_h: u32,
        got_slots: u32,
        req_w: u32,
        req_h: u32,
        req_slots: u32,
    },
    #[error(
        "ring header declares implausible geometry: {slot_count} slots of {max_width}x{max_height}"
    )]
    BadGeometry {
        slot_count: u32,
        max_width: u32,
        max_height: u32,
    },
    #[error("frame size mismatch: got {got} bytes, expected {expected}")]
    SizeMismatch { got: usize, expected: usize },
    #[error("frame too large: {width}x{height} exceeds capacity {max_w}x{max_h}")]
    TooLarge {
        width: u32,
        height: u32,
        max_w: u32,
        max_h: u32,
    },
    #[error("reader gave up after {retries} retries — writer is clobbering slots faster than we can read")]
    ReadStarved { retries: u32 },
    #[error("ring header names slot {index} of {slot_count}")]
    BadWriteIndex { index: u32, slot_count: u32 },
}

/// Configuration for `MmapFrameRing::create_or_open` and friends. The
/// dimensions determine the file's total size; opening an existing file
/// with mismatched dimensions is an error.
#[derive(Debug, Clone, Copy)]
pub struct MmapRingConfig {
    pub slot_count: u32,
    pub max_width: u32,
    pub max_height: u32,
}

impl MmapRingConfig {
    pub fn slot_bytes(&self) -> usize {
        SLOT_OFF_PIXELS
            + SLOT_PIXELS_ALIGNMENT_PAD
            + (self.max_width as usize) * (self.max_height as usize) * 4
    }

    pub fn total_bytes(&self) -> usize {
        HEADER_SIZE + (self.slot_count as usize) * self.slot_bytes()
    }
}

pub struct MmapFrameRing {
    mmap: MmapMut,
    cfg: MmapRingConfig,
    /// Per-slot metadata capacity, from the header; 0 when the writer
    /// reserved none or the file is too short to hold it.
    meta_slot_bytes: usize,
}

/// One frame's worth of data borrowed from a slot. Cheap to construct (no
/// copy of the pixel buffer — caller can `pixels.to_vec()` if it needs to
/// outlive the slot). Pixels are RGBA8.
#[derive(Debug, Clone)]
pub struct FrameView {
    pub width: u32,
    pub height: u32,
    pub stride_bytes: u32,
    pub capture_ts_ms: f64,
    pub sequence: u64,
    pub capture: CaptureStamp,
    /// Owned copy of the RGBA pixels. We always copy out before returning
    /// because the underlying mmap slot can be overwritten by the writer at
    /// any time.
    pub pixels: Vec<u8>,
}

impl MmapFrameRing {
    /// Create a fresh file (or open one and truncate to the correct size),
    /// zero it, and write the layout header. Use this from the side that
    /// "owns" the ring — i.e. the writer side that's responsible for cleanup.
    pub fn create(path: &Path, cfg: MmapRingConfig) -> Result<Self, MmapRingError> {
        Self::create_with_meta(path, cfg, 0)
    }

    /// `create` with `meta_slot_bytes` of per-frame metadata after the slots.
    pub fn create_with_meta(
        path: &Path,
        cfg: MmapRingConfig,
        meta_slot_bytes: u32,
    ) -> Result<Self, MmapRingError> {
        let total = cfg.total_bytes() + cfg.slot_count as usize * meta_slot_bytes as usize;
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(true)
            .open(path)?;
        file.set_len(total as u64)?;
        let mmap = unsafe { MmapOptions::new().len(total).map_mut(&file)? };
        let mut ring = Self {
            mmap,
            cfg,
            meta_slot_bytes: meta_slot_bytes as usize,
        };
        ring.write_header()?;
        Ok(ring)
    }

    /// Open an existing file. Validates the magic, version, and dimensions
    /// against `cfg`. Use this from the reader side (sidecar) that joins a
    /// ring already created by the writer (KSP plugin).
    pub fn open(path: &Path, cfg: MmapRingConfig) -> Result<Self, MmapRingError> {
        let total = cfg.total_bytes();
        let file = OpenOptions::new().read(true).write(true).open(path)?;
        let meta = file.metadata()?;
        if meta.len() < total as u64 {
            return Err(MmapRingError::FileTooSmall {
                got: meta.len(),
                need: total as u64,
            });
        }
        /* The metadata trailer is optional: a writer before 1.12 reserves
        none, and a header claiming more than the file holds is ignored. */
        let header = unsafe { MmapOptions::new().len(HEADER_SIZE).map(&file)? };
        let claimed = u32::from_le_bytes(
            header[HEADER_OFF_META_SLOT_BYTES..HEADER_OFF_META_SLOT_BYTES + 4]
                .try_into()
                .unwrap(),
        );
        drop(header);
        let meta_total = cfg.slot_count as u64 * claimed as u64;
        let meta_slot_bytes =
            if claimed <= MAX_META_SLOT_BYTES && meta.len() >= total as u64 + meta_total {
                claimed as usize
            } else {
                0
            };
        let map_len = total + cfg.slot_count as usize * meta_slot_bytes;
        let mmap = unsafe { MmapOptions::new().len(map_len).map_mut(&file)? };
        let ring = Self {
            mmap,
            cfg,
            meta_slot_bytes,
        };
        ring.validate_header()?;
        Ok(ring)
    }

    /// Open an existing ring, taking its geometry (slot count and per-slot
    /// pixel capacity) from the file's own header rather than a caller-supplied
    /// config. Cameras allocate different-sized rings — a 512² kerbal face ring
    /// is smaller than a full-tier part-camera ring — so a reader can't assume
    /// one global geometry; it learns each ring's shape from the header the
    /// writer stamped.
    pub fn open_self_describing(path: &Path) -> Result<Self, MmapRingError> {
        let file = OpenOptions::new().read(true).write(true).open(path)?;
        let meta = file.metadata()?;
        if meta.len() < HEADER_SIZE as u64 {
            return Err(MmapRingError::FileTooSmall {
                got: meta.len(),
                need: HEADER_SIZE as u64,
            });
        }
        // Peek the header page to read the stamped geometry. Magic and version
        // are checked here so a garbage header can't drive a wild allocation
        // size before the full open re-validates.
        let header = unsafe { MmapOptions::new().len(HEADER_SIZE).map(&file)? };
        let got_magic = u64::from_le_bytes(
            header[HEADER_OFF_MAGIC..HEADER_OFF_MAGIC + 8]
                .try_into()
                .unwrap(),
        );
        if got_magic != MAGIC {
            return Err(MmapRingError::BadMagic { got: got_magic });
        }
        let got_version = u32::from_le_bytes(
            header[HEADER_OFF_VERSION..HEADER_OFF_VERSION + 4]
                .try_into()
                .unwrap(),
        );
        if got_version != LAYOUT_VERSION {
            return Err(MmapRingError::BadVersion { got: got_version });
        }
        let slot_count = u32::from_le_bytes(
            header[HEADER_OFF_SLOT_COUNT..HEADER_OFF_SLOT_COUNT + 4]
                .try_into()
                .unwrap(),
        );
        let max_width = u32::from_le_bytes(
            header[HEADER_OFF_MAX_WIDTH..HEADER_OFF_MAX_WIDTH + 4]
                .try_into()
                .unwrap(),
        );
        let max_height = u32::from_le_bytes(
            header[HEADER_OFF_MAX_HEIGHT..HEADER_OFF_MAX_HEIGHT + 4]
                .try_into()
                .unwrap(),
        );
        drop(header);
        // A torn/corrupt header (matching magic+version by luck) could carry
        // wild dims; reject implausible geometry before total_bytes() can
        // overflow and a later slot slice panics. The plugin never writes
        // anything near these bounds (tier max well under 8K, ≤ a few slots).
        const MAX_DIM: u32 = 8192;
        const MAX_SLOTS: u32 = 64;
        if slot_count == 0
            || slot_count > MAX_SLOTS
            || max_width == 0
            || max_width > MAX_DIM
            || max_height == 0
            || max_height > MAX_DIM
        {
            return Err(MmapRingError::BadGeometry {
                slot_count,
                max_width,
                max_height,
            });
        }
        let cfg = MmapRingConfig {
            slot_count,
            max_width,
            max_height,
        };
        Self::open(path, cfg)
    }

    /// The ring's geometry as it was created / opened. After
    /// `open_self_describing` this reflects the writer's actual per-camera
    /// dimensions, which may be smaller than any global default.
    pub fn config(&self) -> MmapRingConfig {
        self.cfg
    }

    fn write_header(&mut self) -> Result<(), MmapRingError> {
        let put_u32 = |buf: &mut [u8], off: usize, v: u32| {
            buf[off..off + 4].copy_from_slice(&v.to_le_bytes());
        };
        let put_u64 = |buf: &mut [u8], off: usize, v: u64| {
            buf[off..off + 8].copy_from_slice(&v.to_le_bytes());
        };
        let header = &mut self.mmap[..HEADER_SIZE];
        // Zero the whole header first so any read-during-init sees a
        // well-defined "no frames yet" state (write_index=0, sequence=0).
        header.fill(0);
        // Then stamp the constants.
        put_u64(header, HEADER_OFF_MAGIC, MAGIC);
        put_u32(header, HEADER_OFF_VERSION, LAYOUT_VERSION);
        put_u32(header, HEADER_OFF_SLOT_COUNT, self.cfg.slot_count);
        put_u32(header, HEADER_OFF_MAX_WIDTH, self.cfg.max_width);
        put_u32(header, HEADER_OFF_MAX_HEIGHT, self.cfg.max_height);
        put_u32(
            header,
            HEADER_OFF_META_SLOT_BYTES,
            self.meta_slot_bytes as u32,
        );
        Ok(())
    }

    fn validate_header(&self) -> Result<(), MmapRingError> {
        let header = &self.mmap[..HEADER_SIZE];
        let got_magic = u64::from_le_bytes(
            header[HEADER_OFF_MAGIC..HEADER_OFF_MAGIC + 8]
                .try_into()
                .unwrap(),
        );
        if got_magic != MAGIC {
            return Err(MmapRingError::BadMagic { got: got_magic });
        }
        let got_version = u32::from_le_bytes(
            header[HEADER_OFF_VERSION..HEADER_OFF_VERSION + 4]
                .try_into()
                .unwrap(),
        );
        if got_version != LAYOUT_VERSION {
            return Err(MmapRingError::BadVersion { got: got_version });
        }
        let got_slots = u32::from_le_bytes(
            header[HEADER_OFF_SLOT_COUNT..HEADER_OFF_SLOT_COUNT + 4]
                .try_into()
                .unwrap(),
        );
        let got_w = u32::from_le_bytes(
            header[HEADER_OFF_MAX_WIDTH..HEADER_OFF_MAX_WIDTH + 4]
                .try_into()
                .unwrap(),
        );
        let got_h = u32::from_le_bytes(
            header[HEADER_OFF_MAX_HEIGHT..HEADER_OFF_MAX_HEIGHT + 4]
                .try_into()
                .unwrap(),
        );
        if got_slots != self.cfg.slot_count
            || got_w != self.cfg.max_width
            || got_h != self.cfg.max_height
        {
            return Err(MmapRingError::LayoutMismatch {
                got_w,
                got_h,
                got_slots,
                req_w: self.cfg.max_width,
                req_h: self.cfg.max_height,
                req_slots: self.cfg.slot_count,
            });
        }
        Ok(())
    }

    fn header_atomic_u32(&self, offset: usize) -> &AtomicU32 {
        // Safe because the header bytes are pinned by the mmap for the
        // lifetime of `self`, the offsets are constants known to be aligned
        // (offset/4-aligned by construction), and we've validated the file
        // length covers the header in create()/open().
        let ptr = self.mmap.as_ptr() as *const AtomicU32;
        unsafe { &*ptr.add(offset / 4) }
    }

    fn header_atomic_u64(&self, offset: usize) -> &AtomicU64 {
        let ptr = self.mmap.as_ptr() as *const AtomicU64;
        unsafe { &*ptr.add(offset / 8) }
    }

    fn slot_offset(&self, slot_idx: u32) -> usize {
        HEADER_SIZE + (slot_idx as usize) * self.cfg.slot_bytes()
    }

    fn meta_offset(&self, slot_idx: u32) -> usize {
        self.cfg.total_bytes() + (slot_idx as usize) * self.meta_slot_bytes
    }

    /// Per-slot metadata capacity this ring was opened with.
    pub fn meta_slot_bytes(&self) -> usize {
        self.meta_slot_bytes
    }

    fn slot_atomic_u64(&self, slot_idx: u32, intra_slot_offset: usize) -> &AtomicU64 {
        let base = self.mmap.as_ptr();
        let slot_byte_offset = self.slot_offset(slot_idx) + intra_slot_offset;
        debug_assert_eq!(slot_byte_offset % 8, 0, "slot atomic must be 8-aligned");
        // Same safety story as header_atomic_u64.
        unsafe { &*(base.add(slot_byte_offset) as *const AtomicU64) }
    }

    /// Writer side. Pushes one frame into the next slot. Returns the
    /// monotonic sequence number assigned to this frame; the reader can
    /// detect dropped frames by gap in successive sequences.
    pub fn produce(
        &mut self,
        width: u32,
        height: u32,
        capture_ts_ms: f64,
        rgba: &[u8],
    ) -> Result<u64, MmapRingError> {
        self.produce_stamped(width, height, capture_ts_ms, CaptureStamp::UNKNOWN, rgba)
    }

    /// `produce` with the frame's mission-time stamp.
    pub fn produce_stamped(
        &mut self,
        width: u32,
        height: u32,
        capture_ts_ms: f64,
        capture: CaptureStamp,
        rgba: &[u8],
    ) -> Result<u64, MmapRingError> {
        self.produce_with_meta(width, height, capture_ts_ms, &capture, &[], rgba)
    }

    /// `produce_stamped` plus the frame's metadata bytes. The stamp's `fov`
    /// and live bound are written; its `size`, `anchors`, boundary and
    /// command state are not (`meta` carries those). Metadata beyond the ring's capacity is dropped.
    pub fn produce_with_meta(
        &mut self,
        width: u32,
        height: u32,
        capture_ts_ms: f64,
        capture: &CaptureStamp,
        meta: &[u8],
        rgba: &[u8],
    ) -> Result<u64, MmapRingError> {
        let expected = (width as usize) * (height as usize) * 4;
        if rgba.len() != expected {
            return Err(MmapRingError::SizeMismatch {
                got: rgba.len(),
                expected,
            });
        }
        if width > self.cfg.max_width || height > self.cfg.max_height {
            return Err(MmapRingError::TooLarge {
                width,
                height,
                max_w: self.cfg.max_width,
                max_h: self.cfg.max_height,
            });
        }

        // Bump the sequence number first so any concurrent reader sees a
        // sequence > the slot's old sequence and retries.
        let new_seq = self
            .header_atomic_u64(HEADER_OFF_SEQUENCE)
            .fetch_add(1, Ordering::Release)
            + 1;
        let next_slot = (self
            .header_atomic_u32(HEADER_OFF_WRITE_INDEX)
            .load(Ordering::Acquire)
            + 1)
            % self.cfg.slot_count;

        // Write the slot header (width, height, stride, ts) — non-atomic
        // because no reader will trust this slot until slot.sequence is
        // updated below.
        let slot_start = self.slot_offset(next_slot);
        let slot_bytes = self.cfg.slot_bytes();
        let put_u32 = |buf: &mut [u8], off: usize, v: u32| {
            buf[off..off + 4].copy_from_slice(&v.to_le_bytes());
        };
        let put_f64 = |buf: &mut [u8], off: usize, v: f64| {
            buf[off..off + 8].copy_from_slice(&v.to_le_bytes());
        };
        let slot = &mut self.mmap[slot_start..slot_start + slot_bytes];
        put_u32(slot, SLOT_OFF_WIDTH, width);
        put_u32(slot, SLOT_OFF_HEIGHT, height);
        put_u32(slot, SLOT_OFF_STRIDE, width * 4);
        put_u32(slot, SLOT_OFF_CAPTURE_EPOCH, capture.epoch);
        put_f64(slot, SLOT_OFF_CAPTURE_TS, capture_ts_ms);
        put_f64(slot, SLOT_OFF_CAPTURE_UT, capture.ut.unwrap_or(f64::NAN));
        let meta_len = if meta.len() <= self.meta_slot_bytes {
            meta.len()
        } else {
            0
        };
        put_u32(slot, SLOT_OFF_META_LEN, meta_len as u32);
        slot[SLOT_OFF_FOV..SLOT_OFF_FOV + 4]
            .copy_from_slice(&(capture.fov.unwrap_or(0.0) as f32).to_le_bytes());
        put_f64(
            slot,
            SLOT_OFF_LIVE_LIGHT_TIME,
            capture.live_light_time_s.unwrap_or(f64::NAN),
        );
        // Pixels.
        slot[SLOT_OFF_PIXELS..SLOT_OFF_PIXELS + rgba.len()].copy_from_slice(rgba);
        if meta_len > 0 {
            let at = self.meta_offset(next_slot);
            self.mmap[at..at + meta_len].copy_from_slice(&meta[..meta_len]);
        }

        // Publish: write the slot's sequence number first (Release), then
        // publish the new write_index (also Release). Readers do the reverse:
        // load write_index (Acquire), then verify slot.sequence didn't change.
        self.slot_atomic_u64(next_slot, SLOT_OFF_SEQUENCE)
            .store(new_seq, Ordering::Release);
        self.header_atomic_u32(HEADER_OFF_WRITE_INDEX)
            .store(next_slot, Ordering::Release);
        Ok(new_seq)
    }

    /// The newest sequence the writer has claimed, 0 before any frame. Cheap
    /// (no pixel copy), so a reader can skip `latest` when nothing is new. The
    /// writer claims a sequence before filling its slot, so a frame read right
    /// after may still carry the previous one.
    pub fn latest_sequence(&self) -> u64 {
        self.header_atomic_u64(HEADER_OFF_SEQUENCE)
            .load(Ordering::Acquire)
    }

    /// The sequence of the newest frame whose write has ended, 0 before any
    /// frame. Unlike `latest_sequence` it does not move when the writer claims
    /// a frame, only once the frame can be read.
    pub fn published_sequence(&self) -> u64 {
        let write_index = self
            .header_atomic_u32(HEADER_OFF_WRITE_INDEX)
            .load(Ordering::Acquire);
        if write_index >= self.cfg.slot_count {
            return 0;
        }
        self.slot_atomic_u64(write_index, SLOT_OFF_SEQUENCE)
            .load(Ordering::Acquire)
    }

    /* The two halves of a write, apart, so a test can stand between the claim and the publish as a reader does. */
    #[cfg(test)]
    pub(crate) fn claim_for_test(&self) -> u64 {
        self.header_atomic_u64(HEADER_OFF_SEQUENCE)
            .fetch_add(1, Ordering::Release)
            + 1
    }

    #[cfg(test)]
    pub(crate) fn publish_for_test(&self, seq: u64) {
        let next_slot = (self
            .header_atomic_u32(HEADER_OFF_WRITE_INDEX)
            .load(Ordering::Acquire)
            + 1)
            % self.cfg.slot_count;
        self.slot_atomic_u64(next_slot, SLOT_OFF_SEQUENCE)
            .store(seq, Ordering::Release);
        self.header_atomic_u32(HEADER_OFF_WRITE_INDEX)
            .store(next_slot, Ordering::Release);
    }

    /// Reader side. Returns the most-recently-published frame, or `None` if
    /// nothing has been produced yet. Implements the seqlock retry protocol
    /// — if the writer clobbers our slot mid-read we bail and re-try once
    /// against the new write_index; pathological writers (faster than we
    /// can copy SLOT_COUNT frames) trip `ReadStarved`.
    pub fn latest(&self) -> Result<Option<FrameView>, MmapRingError> {
        for _ in 0..MAX_READ_RETRIES {
            let header_seq_before = self
                .header_atomic_u64(HEADER_OFF_SEQUENCE)
                .load(Ordering::Acquire);
            if header_seq_before == 0 {
                return Ok(None);
            }
            let write_index = self
                .header_atomic_u32(HEADER_OFF_WRITE_INDEX)
                .load(Ordering::Acquire);
            /* Every slot access below is offset by it, unchecked. */
            if write_index >= self.cfg.slot_count {
                return Err(MmapRingError::BadWriteIndex {
                    index: write_index,
                    slot_count: self.cfg.slot_count,
                });
            }
            /* The first frame is claimed but not yet published, as on a fresh
            ring at a camera's first stream or a vessel switch. Nothing to
            read yet, which is not the writer lapping the reader. */
            if self
                .slot_atomic_u64(write_index, SLOT_OFF_SEQUENCE)
                .load(Ordering::Acquire)
                == 0
            {
                return Ok(None);
            }
            if let Some(frame) = self.read_slot(write_index) {
                return Ok(Some(frame));
            }
        }
        Err(MmapRingError::ReadStarved {
            retries: MAX_READ_RETRIES,
        })
    }

    /**
     * The oldest frame newer than `after` that is still in the ring, or `None`
     * when nothing newer has been published. A reader that polls slower than
     * the writer now and then (two readbacks can land in one game frame, a
     * microsecond apart) takes the frames it missed in capture order instead
     * of jumping to the newest and losing the one before it.
     *
     * The writer claims a sequence before filling the slot it reuses, and that
     * slot keeps showing its old sequence until the write ends, so a slot is
     * only a candidate while the writer has not claimed the sequence that
     * reuses it.
     */
    pub fn next_after(&self, after: u64) -> Result<Option<FrameView>, MmapRingError> {
        let slots = self.cfg.slot_count as u64;
        for _ in 0..MAX_READ_RETRIES {
            let claimed = self
                .header_atomic_u64(HEADER_OFF_SEQUENCE)
                .load(Ordering::Acquire);
            let oldest = (0..self.cfg.slot_count)
                .map(|idx| {
                    let seq = self
                        .slot_atomic_u64(idx, SLOT_OFF_SEQUENCE)
                        .load(Ordering::Acquire);
                    (seq, idx)
                })
                .filter(|&(seq, _)| seq > after && seq <= claimed && seq + slots > claimed)
                .min();
            let Some((_, idx)) = oldest else {
                return Ok(None);
            };
            if let Some(frame) = self.read_slot(idx) {
                return Ok(Some(frame));
            }
        }
        Err(MmapRingError::ReadStarved {
            retries: MAX_READ_RETRIES,
        })
    }

    /// One slot's frame, or `None` when it is unpublished, implausible, or
    /// the writer reached it while it was being copied.
    fn read_slot(&self, idx: u32) -> Option<FrameView> {
        let slot_start = self.slot_offset(idx);
        let slot_bytes = self.cfg.slot_bytes();
        let slot = &self.mmap[slot_start..slot_start + slot_bytes];

        let slot_seq_before = self
            .slot_atomic_u64(idx, SLOT_OFF_SEQUENCE)
            .load(Ordering::Acquire);
        /* Claimed by the writer but not yet published. */
        if slot_seq_before == 0 {
            return None;
        }

        let width =
            u32::from_le_bytes(slot[SLOT_OFF_WIDTH..SLOT_OFF_WIDTH + 4].try_into().unwrap());
        let height = u32::from_le_bytes(
            slot[SLOT_OFF_HEIGHT..SLOT_OFF_HEIGHT + 4]
                .try_into()
                .unwrap(),
        );
        let stride_bytes = u32::from_le_bytes(
            slot[SLOT_OFF_STRIDE..SLOT_OFF_STRIDE + 4]
                .try_into()
                .unwrap(),
        );
        let capture_ts_ms = f64::from_le_bytes(
            slot[SLOT_OFF_CAPTURE_TS..SLOT_OFF_CAPTURE_TS + 8]
                .try_into()
                .unwrap(),
        );
        let capture_epoch = u32::from_le_bytes(
            slot[SLOT_OFF_CAPTURE_EPOCH..SLOT_OFF_CAPTURE_EPOCH + 4]
                .try_into()
                .unwrap(),
        );
        let capture_ut = f64::from_le_bytes(
            slot[SLOT_OFF_CAPTURE_UT..SLOT_OFF_CAPTURE_UT + 8]
                .try_into()
                .unwrap(),
        );
        let fov = f32::from_le_bytes(slot[SLOT_OFF_FOV..SLOT_OFF_FOV + 4].try_into().unwrap());
        let live_light_time = f64::from_le_bytes(
            slot[SLOT_OFF_LIVE_LIGHT_TIME..SLOT_OFF_LIVE_LIGHT_TIME + 8]
                .try_into()
                .unwrap(),
        );
        let meta_len = u32::from_le_bytes(
            slot[SLOT_OFF_META_LEN..SLOT_OFF_META_LEN + 4]
                .try_into()
                .unwrap(),
        ) as usize;
        let pixel_bytes = (width as usize) * (height as usize) * 4;
        if pixel_bytes > slot_bytes - SLOT_OFF_PIXELS {
            /* Corrupt, or written bigger than capacity: the caller's retry loop
            reads the ring again. */
            tracing::warn!(width, height, "slot pixel size > slot capacity; retrying");
            return None;
        }
        let pixels = slot[SLOT_OFF_PIXELS..SLOT_OFF_PIXELS + pixel_bytes].to_vec();
        /* Copied under the same seqlock check as the pixels; parsed after. */
        let meta = if meta_len > 0 && meta_len <= self.meta_slot_bytes {
            let at = self.meta_offset(idx);
            self.mmap[at..at + meta_len].to_vec()
        } else {
            Vec::new()
        };

        /* Verify the slot wasn't clobbered while we were copying: its
        sequence is unchanged, and the writer has not claimed the sequence
        that reuses it (it may be filling the slot with the old sequence
        still showing). The fence keeps the copy before both loads. */
        fence(Ordering::Acquire);
        let slot_seq_after = self
            .slot_atomic_u64(idx, SLOT_OFF_SEQUENCE)
            .load(Ordering::Acquire);
        let claimed_after = self
            .header_atomic_u64(HEADER_OFF_SEQUENCE)
            .load(Ordering::Acquire);
        if slot_seq_after == slot_seq_before
            && claimed_after < slot_seq_before + self.cfg.slot_count as u64
        {
            let meta = parse_meta(&meta);
            return Some(FrameView {
                width,
                height,
                stride_bytes,
                capture_ts_ms,
                sequence: slot_seq_before,
                capture: CaptureStamp {
                    ut: capture_ut.is_finite().then_some(capture_ut),
                    epoch: capture_epoch,
                    fov: (fov.is_finite() && fov > 0.0).then_some(fov as f64),
                    size: Some((width, height)),
                    anchors: meta.anchors,
                    live_light_time_s: (live_light_time.is_finite() && live_light_time >= 0.0)
                        .then_some(live_light_time),
                    boundary_m: meta.boundary_m,
                    command: meta.command,
                },
                pixels,
            });
        }
        tracing::debug!(slot = idx, "slot clobbered during read, retrying");
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::env::temp_dir;
    use std::fs;

    fn tmp_path(suffix: &str) -> std::path::PathBuf {
        let pid = std::process::id();
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        temp_dir().join(format!("kerbcast-mmaptest-{pid}-{nanos}-{suffix}"))
    }

    fn small_cfg() -> MmapRingConfig {
        MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        }
    }

    /// Cross-language layout contract: the committed fixture is written by
    /// the C# plugin writer (Plugin/MmapFrameRing.Tests `--write-fixture`,
    /// which also asserts regen == committed on every run). Reading it back
    /// exactly here means the two language sides of the ring layout cannot
    /// silently drift. Copied to a temp path first — open() maps read-write.
    #[test]
    fn csharp_written_fixture_reads_back_exactly() {
        let fixture = concat!(env!("CARGO_MANIFEST_DIR"), "/testdata/frame_ring_v2.ring");
        let path = tmp_path("csfixture");
        fs::copy(fixture, &path).unwrap();

        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 32,
            max_height: 18,
        };
        let ring = MmapFrameRing::open(&path, cfg).unwrap();
        let frame = ring.latest().unwrap().expect("fixture must contain frames");

        // Second of the two fixture frames: pattern (i*11+29)&0xFF @ 1235.5,
        // UT 86400.25 in epoch 7.
        assert_eq!(frame.width, 32);
        assert_eq!(frame.height, 18);
        assert_eq!(frame.stride_bytes, 32 * 4);
        assert_eq!(frame.sequence, 2);
        assert_eq!(frame.capture_ts_ms, 1235.5);
        assert_eq!(frame.capture.ut, Some(86400.25));
        assert_eq!(frame.capture.epoch, 7);
        assert_eq!(frame.capture.fov, None, "a pre-1.12 slot reads fov unknown");
        assert_eq!(frame.capture.anchors, None, "and no anchors");
        assert_eq!(frame.capture.live_light_time_s, Some(0.0), "and no delay");
        assert_eq!(ring.meta_slot_bytes(), 0);
        let want: Vec<u8> = (0..32usize * 18 * 4)
            .map(|i| ((i * 11 + 29) & 0xFF) as u8)
            .collect();
        assert_eq!(
            frame.pixels, want,
            "latest frame pixels must match the C# writer's second pattern"
        );

        drop(ring);
        fs::remove_file(&path).ok();
    }

    #[test]
    fn create_then_latest_is_none_until_produced() {
        let path = tmp_path("empty");
        let ring = MmapFrameRing::create(&path, small_cfg()).unwrap();
        assert!(ring.latest().unwrap().is_none());
        drop(ring);
        fs::remove_file(&path).ok();
    }

    #[test]
    fn a_write_index_past_the_slots_is_refused() {
        let path = tmp_path("badindex");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        writer.produce(8, 8, 1.0, &vec![0u8; 8 * 8 * 4]).unwrap();
        writer
            .header_atomic_u32(HEADER_OFF_WRITE_INDEX)
            .store(small_cfg().slot_count, Ordering::Release);
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        assert!(matches!(
            reader.latest(),
            Err(MmapRingError::BadWriteIndex {
                index: 4,
                slot_count: 4
            })
        ));
        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    #[test]
    fn produce_then_latest_roundtrips_in_one_process() {
        let path = tmp_path("roundtrip");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let pixels: Vec<u8> = (0..(8u32 * 8 * 4)).map(|i| (i % 256) as u8).collect();
        let seq = writer.produce(8, 8, 1234.5, &pixels).unwrap();
        assert_eq!(seq, 1);

        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        let frame = reader.latest().unwrap().unwrap();
        assert_eq!(frame.width, 8);
        assert_eq!(frame.height, 8);
        assert_eq!(frame.stride_bytes, 32);
        assert_eq!(frame.capture_ts_ms, 1234.5);
        assert_eq!(frame.sequence, 1);
        assert_eq!(frame.pixels, pixels);

        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    #[test]
    fn capture_stamp_roundtrips_and_unknown_ut_reads_none() {
        let path = tmp_path("stamp");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let pixels = vec![0u8; 4 * 4 * 4];
        let stamp = CaptureStamp::at(Some(123_456.789), 3);
        writer.produce_stamped(4, 4, 1.0, stamp, &pixels).unwrap();
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        let read = reader.latest().unwrap().unwrap().capture;
        assert_eq!((read.ut, read.epoch), (Some(123_456.789), 3));
        assert_eq!(read.size, Some((4, 4)));

        writer.produce(4, 4, 2.0, &pixels).unwrap();
        let read = reader.latest().unwrap().unwrap().capture;
        assert_eq!((read.ut, read.epoch, read.fov), (None, 0, None));
        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    /// The C# writer's metadata fixture: fov and the live bound ride the
    /// slot, the boundary, command state and anchor results the trailer.
    #[test]
    fn csharp_written_meta_fixture_reads_back_exactly() {
        let fixture = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/testdata/frame_ring_v2_meta.ring"
        );
        let path = tmp_path("csmetafixture");
        fs::copy(fixture, &path).unwrap();
        let ring = MmapFrameRing::open_self_describing(&path).unwrap();
        assert_eq!(ring.meta_slot_bytes(), 1024);
        let frame = ring.latest().unwrap().expect("fixture must contain frames");
        assert_eq!(frame.sequence, 2);
        assert_eq!(frame.capture.ut, Some(86400.25));
        assert_eq!(frame.capture.fov, Some(42.5));
        assert_eq!(frame.capture.live_light_time_s, Some(2.5));
        assert_eq!(frame.capture.boundary_m, Some(0.05));
        let command = frame
            .capture
            .command
            .clone()
            .expect("command state in the trailer");
        assert!(command.has_fix);
        assert_eq!(command.range_m, Some(1500.5));
        assert_eq!(command.subject_persistent_id, 42);
        assert_eq!(command.world_model.as_deref(), Some("model"));
        assert_eq!(command.world_model_seq, Some(9.0));
        assert_eq!(command.subject_ut, Some(86398.75));
        assert!(command.waiting_for_target);
        assert_eq!(
            command.authority.as_ref().map(|a| a.owner.as_str()),
            Some("mission")
        );
        /* The plugin's encoder sets every field: each key it writes reads
        back and writes out again with the same value. */
        let raw: serde_json::Value = {
            let idx = ring
                .header_atomic_u32(HEADER_OFF_WRITE_INDEX)
                .load(Ordering::Acquire);
            let slot = ring.slot_offset(idx) + SLOT_OFF_META_LEN;
            let len = u32::from_le_bytes(ring.mmap[slot..slot + 4].try_into().unwrap()) as usize;
            let at = ring.meta_offset(idx);
            serde_json::from_slice(&ring.mmap[at..at + len]).unwrap()
        };
        let written = raw["command"].as_object().expect("command object").clone();
        let back = serde_json::to_value(&*command).unwrap();
        let back = back.as_object().unwrap().clone();
        for (key, value) in &written {
            let got = back
                .get(key)
                .unwrap_or_else(|| panic!("'{key}' is lost on the way through"));
            let same = match (value.as_f64(), got.as_f64()) {
                (Some(a), Some(b)) => a == b,
                _ => value == got,
            };
            assert!(same, "'{key}': wrote {value}, read back {got}");
        }
        assert_eq!(written.len(), back.len(), "and nothing is added");
        let anchors = frame.capture.anchors.expect("anchors in the trailer");
        assert_eq!(anchors.len(), 1);
        let a = &anchors[0];
        assert_eq!(a.id, "port");
        assert_eq!(a.owner.as_deref(), Some("peer:7:3"));
        assert_eq!((a.x, a.y), (Some(16.5), Some(9.25)));
        assert!(a.in_frame && a.behind == Some(false));
        assert_eq!(a.distance_m, Some(120.0));
        assert_eq!(a.forward.as_deref(), Some(&[0.0, 0.0, 1.0][..]));
        assert_eq!(a.occluded, Some(false));
        drop(ring);
        fs::remove_file(&path).ok();
    }

    /// The first 1.12 builds wrote a bare anchor array; an object carries
    /// any of its parts; anything else is no metadata, never a lost frame.
    #[test]
    fn metadata_reads_either_shape() {
        let bare = parse_meta(br#"[{"id":"a","inFrame":true}]"#);
        assert_eq!(bare.anchors.map(|a| a.len()), Some(1));
        let object = parse_meta(br#"{"boundaryM":0.05,"command":{"hasFix":false,"frameHeightPx":0,"framingFits":true,"occluded":false,"occluder":"","sunlit":false,"subjectPersistentId":0}}"#);
        assert_eq!(object.boundary_m, Some(0.05));
        assert!(object.anchors.is_none());
        assert!(!object.command.expect("command").has_fix);
        assert_eq!(parse_meta(b"{\"anchors\":7}"), SlotMeta::default());
        let odd = parse_meta(
            br#"{"command":{"aimKind":"warp","hasFix":true},"anchors":[{"id":"a","inFrame":true}]}"#,
        );
        assert!(
            odd.command.is_none(),
            "a command state this reader cannot take is dropped"
        );
        assert_eq!(
            odd.anchors.map(|a| a.len()),
            Some(1),
            "and costs the anchors nothing"
        );
        assert_eq!(parse_meta(b""), SlotMeta::default());
    }

    /// A writer older than 1.12 leaves the live bound zero: no delay. One
    /// that could not read it writes NaN, which stays unknown.
    #[test]
    fn live_bound_roundtrips_and_reads_zero_when_unwritten() {
        let path = tmp_path("light");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let pixels = vec![0u8; 4 * 4 * 4];
        let stamp = CaptureStamp {
            live_light_time_s: Some(23.25),
            ..CaptureStamp::at(Some(5.0), 1)
        };
        writer.produce_stamped(4, 4, 1.0, stamp, &pixels).unwrap();
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        assert_eq!(
            reader.latest().unwrap().unwrap().capture.live_light_time_s,
            Some(23.25)
        );
        writer.produce(4, 4, 2.0, &pixels).unwrap();
        assert_eq!(
            reader.latest().unwrap().unwrap().capture.live_light_time_s,
            None,
            "a stamp with no live bound is written as unknown"
        );
        let zero = CaptureStamp {
            live_light_time_s: Some(0.0),
            ..CaptureStamp::at(Some(6.0), 1)
        };
        writer.produce_stamped(4, 4, 3.0, zero, &pixels).unwrap();
        assert_eq!(
            reader.latest().unwrap().unwrap().capture.live_light_time_s,
            Some(0.0)
        );
        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    #[test]
    fn metadata_roundtrips_and_clears_on_a_frame_without_it() {
        let path = tmp_path("meta");
        let mut writer = MmapFrameRing::create_with_meta(&path, small_cfg(), 256).unwrap();
        let pixels = vec![0u8; 4 * 4 * 4];
        let stamp = CaptureStamp {
            fov: Some(30.0),
            ..CaptureStamp::at(Some(5.0), 1)
        };
        let meta = br#"[{"id":"a","owner":"x","x":1,"y":2,"inFrame":true,"behind":false,"apparentRadiusPx":3,"angleOffAxis":4}]"#;
        writer
            .produce_with_meta(4, 4, 1.0, &stamp, meta, &pixels)
            .unwrap();
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        let read = reader.latest().unwrap().unwrap().capture;
        assert_eq!(read.fov, Some(30.0));
        let anchors = read.anchors.unwrap();
        assert_eq!(anchors[0].id, "a");
        assert_eq!(anchors[0].owner.as_deref(), Some("x"));

        /* A frame without metadata, written into the next slot, carries none. */
        writer.produce(4, 4, 2.0, &pixels).unwrap();
        assert_eq!(reader.latest().unwrap().unwrap().capture.anchors, None);

        /* Metadata past the capacity is dropped, never truncated mid-JSON. */
        let big = vec![b' '; 300];
        writer
            .produce_with_meta(4, 4, 3.0, &stamp, &big, &pixels)
            .unwrap();
        assert_eq!(reader.latest().unwrap().unwrap().capture.anchors, None);
        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    /// A header claiming a trailer the file cannot hold (a corrupt header, or
    /// a file cut short) opens without metadata rather than failing.
    #[test]
    fn a_trailer_the_file_cannot_hold_is_ignored() {
        let path = tmp_path("short-meta");
        let writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        drop(writer);
        {
            let file = OpenOptions::new()
                .read(true)
                .write(true)
                .open(&path)
                .unwrap();
            let mut map = unsafe { MmapOptions::new().len(HEADER_SIZE).map_mut(&file).unwrap() };
            map[HEADER_OFF_META_SLOT_BYTES..HEADER_OFF_META_SLOT_BYTES + 4]
                .copy_from_slice(&4096u32.to_le_bytes());
        }
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        assert_eq!(reader.meta_slot_bytes(), 0);
        drop(reader);
        fs::remove_file(&path).ok();
    }

    #[test]
    fn open_with_mismatched_layout_fails() {
        let path = tmp_path("mismatch");
        let _writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let bigger = MmapRingConfig {
            slot_count: 4,
            max_width: 128,
            max_height: 128,
        };
        // `unwrap_err` would require MmapFrameRing: Debug, which we don't
        // implement (MmapMut isn't Debug). Match on the result instead.
        match MmapFrameRing::open(&path, bigger) {
            Ok(_) => panic!("expected open to fail with mismatched dims"),
            Err(MmapRingError::FileTooSmall { .. } | MmapRingError::LayoutMismatch { .. }) => {}
            Err(other) => panic!("unexpected error: {other:?}"),
        }
        fs::remove_file(&path).ok();
    }

    #[test]
    fn open_self_describing_reads_geometry_from_header() {
        // A ring created at one geometry opens self-describing at that same
        // geometry without the reader knowing the dims in advance — the case
        // that broke kerbal (512²) rings when the reader assumed global dims.
        let path = tmp_path("self-describing");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 512,
            max_height: 512,
        };
        let _writer = MmapFrameRing::create(&path, cfg).unwrap();
        let reader = MmapFrameRing::open_self_describing(&path).unwrap();
        let got = reader.config();
        assert_eq!(got.slot_count, 4);
        assert_eq!(got.max_width, 512);
        assert_eq!(got.max_height, 512);
        drop(reader);
        fs::remove_file(&path).ok();
    }

    #[test]
    fn open_self_describing_rejects_bad_magic() {
        let path = tmp_path("self-describing-bad");
        std::fs::write(&path, vec![0u8; HEADER_SIZE + 16]).unwrap();
        match MmapFrameRing::open_self_describing(&path) {
            Ok(_) => panic!("expected open to fail on a zeroed (bad-magic) header"),
            Err(MmapRingError::BadMagic { .. }) => {}
            Err(other) => panic!("unexpected error: {other:?}"),
        }
        fs::remove_file(&path).ok();
    }

    #[test]
    fn open_self_describing_rejects_implausible_geometry() {
        // A valid magic+version header but with a wild max_width (as a torn
        // write could leave) is refused before total_bytes() can overflow.
        let path = tmp_path("self-describing-wild");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        let _writer = MmapFrameRing::create(&path, cfg).unwrap();
        // Stomp the max_width field (offset 16) with a huge value in place.
        {
            use std::io::{Seek, SeekFrom, Write};
            let mut f = OpenOptions::new().write(true).open(&path).unwrap();
            f.seek(SeekFrom::Start(HEADER_OFF_MAX_WIDTH as u64))
                .unwrap();
            f.write_all(&u32::MAX.to_le_bytes()).unwrap();
        }
        match MmapFrameRing::open_self_describing(&path) {
            Ok(_) => panic!("expected open to reject wild geometry"),
            Err(MmapRingError::BadGeometry { .. }) => {}
            Err(other) => panic!("unexpected error: {other:?}"),
        }
        fs::remove_file(&path).ok();
    }

    #[test]
    fn produce_then_overwrite_returns_latest() {
        let path = tmp_path("overwrite");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let pixels_a = vec![0xAAu8; 4 * 4 * 4];
        let pixels_b = vec![0xBBu8; 4 * 4 * 4];
        writer.produce(4, 4, 0.0, &pixels_a).unwrap();
        writer.produce(4, 4, 1.0, &pixels_b).unwrap();

        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        let frame = reader.latest().unwrap().unwrap();
        assert_eq!(frame.sequence, 2);
        assert_eq!(frame.pixels, pixels_b);

        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    #[test]
    fn next_after_takes_unread_frames_in_capture_order() {
        let path = tmp_path("next-after");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        assert!(reader.next_after(0).unwrap().is_none(), "empty ring");
        let pixels = vec![0u8; 4 * 4 * 4];
        for n in 1..=3 {
            writer.produce(4, 4, n as f64, &pixels).unwrap();
        }
        let first = reader.next_after(1).unwrap().unwrap();
        assert_eq!((first.sequence, first.capture_ts_ms), (2, 2.0));
        let second = reader.next_after(first.sequence).unwrap().unwrap();
        assert_eq!((second.sequence, second.capture_ts_ms), (3, 3.0));
        assert!(reader.next_after(3).unwrap().is_none(), "caught up");
        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    #[test]
    fn next_after_skips_frames_the_ring_no_longer_holds() {
        let path = tmp_path("next-after-lapped");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let pixels = vec![0u8; 4 * 4 * 4];
        for n in 1..=10 {
            writer.produce(4, 4, n as f64, &pixels).unwrap();
        }
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        assert_eq!(reader.next_after(2).unwrap().unwrap().sequence, 7);
        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    /// A writer that has claimed the next sequence is filling the oldest slot
    /// while it still shows its old sequence, so that slot is passed over.
    #[test]
    fn next_after_never_reads_the_slot_being_refilled() {
        let path = tmp_path("next-after-claimed");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let pixels = vec![0u8; 4 * 4 * 4];
        for n in 1..=4 {
            writer.produce(4, 4, n as f64, &pixels).unwrap();
        }
        writer
            .header_atomic_u64(HEADER_OFF_SEQUENCE)
            .fetch_add(1, Ordering::Release);
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        assert_eq!(reader.next_after(0).unwrap().unwrap().sequence, 2);
        assert!(
            reader.next_after(4).unwrap().is_none(),
            "the claimed frame is not published yet"
        );
        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    /// A first frame that is claimed but not yet published reads as nothing
    /// yet, not as a writer lapping the reader.
    #[test]
    fn latest_is_none_while_the_first_frame_is_unpublished() {
        let path = tmp_path("latest-first-claim");
        let writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        writer.claim_for_test();
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        assert!(reader.latest().unwrap().is_none());
        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    /// The check after the copy: once the writer claims the sequence that
    /// reuses a slot, a copy of that slot is refused even though its own
    /// sequence has not changed yet.
    #[test]
    fn read_slot_refuses_a_slot_whose_reuse_is_claimed() {
        let path = tmp_path("read-slot-claimed");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let pixels = vec![0u8; 4 * 4 * 4];
        let mut slot_of = std::collections::HashMap::new();
        for n in 1..=4u64 {
            writer.produce(4, 4, n as f64, &pixels).unwrap();
            let idx = writer
                .header_atomic_u32(HEADER_OFF_WRITE_INDEX)
                .load(Ordering::Acquire);
            slot_of.insert(n, idx);
        }
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        assert_eq!(reader.read_slot(slot_of[&1]).unwrap().sequence, 1);
        writer
            .header_atomic_u64(HEADER_OFF_SEQUENCE)
            .fetch_add(1, Ordering::Release);
        assert!(reader.read_slot(slot_of[&1]).is_none(), "being refilled");
        assert_eq!(reader.read_slot(slot_of[&2]).unwrap().sequence, 2);
        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }

    #[test]
    fn produce_rejects_size_mismatch() {
        let path = tmp_path("size-mismatch");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let err = writer.produce(8, 8, 0.0, &[0u8; 10]).unwrap_err();
        assert!(matches!(err, MmapRingError::SizeMismatch { .. }));
        fs::remove_file(&path).ok();
    }

    #[test]
    fn produce_rejects_too_large() {
        let path = tmp_path("too-large");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let big = vec![0u8; 128 * 128 * 4];
        let err = writer.produce(128, 128, 0.0, &big).unwrap_err();
        assert!(matches!(err, MmapRingError::TooLarge { .. }));
        fs::remove_file(&path).ok();
    }

    #[test]
    fn wrap_around_ring_keeps_latest_correct() {
        let path = tmp_path("wrap");
        let mut writer = MmapFrameRing::create(&path, small_cfg()).unwrap();
        let pixels = vec![0u8; 4 * 4 * 4];
        for n in 1..=20 {
            let seq = writer.produce(4, 4, n as f64, &pixels).unwrap();
            assert_eq!(seq, n);
        }
        let reader = MmapFrameRing::open(&path, small_cfg()).unwrap();
        let frame = reader.latest().unwrap().unwrap();
        assert_eq!(frame.sequence, 20);
        assert_eq!(frame.capture_ts_ms, 20.0);
        drop(reader);
        drop(writer);
        fs::remove_file(&path).ok();
    }
}
