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
//! 8        4      u32 version (this layout = 1)
//! 12       4      u32 slot_count
//! 16       4      u32 max_width  (per-slot pixel capacity)
//! 20       4      u32 max_height
//! 24       4      u32 atomic write_index (most-recently-written slot)
//! 28       4      padding
//! 32       8      u64 atomic sequence (monotonic; wraps only after 2^64)
//! 40       (HEADER_SIZE - 40) padding to align first slot
//!
//! 4096 + (slot_idx * SLOT_SIZE):    slot start
//!   +0   4      u32 width
//!   +4   4      u32 height
//!   +8   4      u32 stride_bytes
//!   +12  4      padding
//!   +16  8      f64 capture_ts_ms
//!   +24  8      u64 sequence (matches header.sequence when this slot was written)
//!   +32  ...    RGBA8 pixels, max_width * max_height * 4 bytes
//! ```
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
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};

use memmap2::{MmapMut, MmapOptions};
use thiserror::Error;

pub const MAGIC: u64 = 0x4B45_5242_4341_4D31; // "KERBCAM1" legacy ring magic; value pinned for plugin wire-format compat, not renamed
pub const LAYOUT_VERSION: u32 = 1;

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

const SLOT_OFF_WIDTH: usize = 0;
const SLOT_OFF_HEIGHT: usize = 4;
const SLOT_OFF_STRIDE: usize = 8;
const SLOT_OFF_CAPTURE_TS: usize = 16;
const SLOT_OFF_SEQUENCE: usize = 24;
const SLOT_OFF_PIXELS: usize = 32;
const SLOT_PIXELS_ALIGNMENT_PAD: usize = 0; // SLOT_OFF_PIXELS is already 32, 32-aligned

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
        let total = cfg.total_bytes();
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(true)
            .open(path)?;
        file.set_len(total as u64)?;
        let mmap = unsafe { MmapOptions::new().len(total).map_mut(&file)? };
        let mut ring = Self { mmap, cfg };
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
        let mmap = unsafe { MmapOptions::new().len(total).map_mut(&file)? };
        let ring = Self { mmap, cfg };
        ring.validate_header()?;
        Ok(ring)
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
        put_f64(slot, SLOT_OFF_CAPTURE_TS, capture_ts_ms);
        // Pixels.
        slot[SLOT_OFF_PIXELS..SLOT_OFF_PIXELS + rgba.len()].copy_from_slice(rgba);

        // Publish: write the slot's sequence number first (Release), then
        // publish the new write_index (also Release). Readers do the reverse:
        // load write_index (Acquire), then verify slot.sequence didn't change.
        self.slot_atomic_u64(next_slot, SLOT_OFF_SEQUENCE)
            .store(new_seq, Ordering::Release);
        self.header_atomic_u32(HEADER_OFF_WRITE_INDEX)
            .store(next_slot, Ordering::Release);
        Ok(new_seq)
    }

    /// Reader side. Returns the most-recently-published frame, or `None` if
    /// nothing has been produced yet. Implements the seqlock retry protocol
    /// — if the writer clobbers our slot mid-read we bail and re-try once
    /// against the new write_index; pathological writers (faster than we
    /// can copy SLOT_COUNT frames) trip `ReadStarved`.
    pub fn latest(&self) -> Result<Option<FrameView>, MmapRingError> {
        const MAX_RETRIES: u32 = 4;
        for retry in 0..MAX_RETRIES {
            let header_seq_before = self
                .header_atomic_u64(HEADER_OFF_SEQUENCE)
                .load(Ordering::Acquire);
            if header_seq_before == 0 {
                return Ok(None);
            }
            let write_index = self
                .header_atomic_u32(HEADER_OFF_WRITE_INDEX)
                .load(Ordering::Acquire);
            let slot_start = self.slot_offset(write_index);
            let slot_bytes = self.cfg.slot_bytes();
            let slot = &self.mmap[slot_start..slot_start + slot_bytes];

            let slot_seq_before = self
                .slot_atomic_u64(write_index, SLOT_OFF_SEQUENCE)
                .load(Ordering::Acquire);
            if slot_seq_before == 0 {
                // Writer claimed the slot via write_index but hasn't yet
                // published a sequence — re-poll.
                continue;
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
            let pixel_bytes = (width as usize) * (height as usize) * 4;
            if pixel_bytes > slot_bytes - SLOT_OFF_PIXELS {
                // Corrupt / writer wrote bigger-than-capacity. Re-poll once.
                tracing::warn!(width, height, "slot pixel size > slot capacity; retrying");
                continue;
            }
            let pixels = slot[SLOT_OFF_PIXELS..SLOT_OFF_PIXELS + pixel_bytes].to_vec();

            // Verify the slot wasn't clobbered while we were copying. If
            // slot.sequence changed, the writer's lapped us — retry.
            let slot_seq_after = self
                .slot_atomic_u64(write_index, SLOT_OFF_SEQUENCE)
                .load(Ordering::Acquire);
            if slot_seq_after == slot_seq_before {
                return Ok(Some(FrameView {
                    width,
                    height,
                    stride_bytes,
                    capture_ts_ms,
                    sequence: slot_seq_before,
                    pixels,
                }));
            }
            tracing::debug!(retry, "slot clobbered during read, retrying");
        }
        Err(MmapRingError::ReadStarved {
            retries: MAX_RETRIES,
        })
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
        let fixture = concat!(env!("CARGO_MANIFEST_DIR"), "/testdata/frame_ring_v1.ring");
        let path = tmp_path("csfixture");
        fs::copy(fixture, &path).unwrap();

        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 32,
            max_height: 18,
        };
        let ring = MmapFrameRing::open(&path, cfg).unwrap();
        let frame = ring.latest().unwrap().expect("fixture must contain frames");

        // Second of the two fixture frames: pattern (i*11+29)&0xFF @ 1235.5.
        assert_eq!(frame.width, 32);
        assert_eq!(frame.height, 18);
        assert_eq!(frame.stride_bytes, 32 * 4);
        assert_eq!(frame.sequence, 2);
        assert_eq!(frame.capture_ts_ms, 1235.5);
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
