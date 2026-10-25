//! Shared-memory ring buffer for the cross-process KSP plugin → sidecar
//! frame handoff.
//!
//! Two implementations live here:
//!
//! - [`mmap::MmapFrameRing`] is the real one — file-backed mmap with a
//!   seqlock-style sync protocol, designed to bridge to a C# Mono writer
//!   using `System.IO.MemoryMappedFiles.MemoryMappedFile`. The binary
//!   layout is the cross-language contract; see `mmap.rs` for the field
//!   table.
//! - [`FrameRing`] (below) is the in-process Mutex-backed model — useful
//!   for unit tests of consumer code that doesn't need a real second
//!   process, and as a comparison reference when debugging the mmap path.

pub mod control;
pub mod mmap;
pub use control::ControlBlock;
pub use mmap::{CaptureStamp, FrameView, MmapFrameRing, MmapRingConfig, MmapRingError};

use std::sync::Mutex;

/// 4 KiB header (one page) followed by SLOTS frame slots. Each slot stores
/// width/height/stride/timestamp + a fixed RGBA buffer sized for the
/// maximum supported resolution (1280×720, picked to bound shared-mem
/// allocation — the sidecar reconfigures the encoder for smaller crops
/// without re-allocating shared memory).
pub const MAX_WIDTH: u32 = 1280;
pub const MAX_HEIGHT: u32 = 720;
pub const SLOT_PIXEL_CAPACITY: usize = (MAX_WIDTH as usize) * (MAX_HEIGHT as usize) * 4;
pub const SLOTS: usize = 4;

/// In-process stand-in for the eventual mmap-backed ring. The shape mirrors
/// what the real shm layout will be — a producer (KSP) writes a slot atomically,
/// the consumer (sidecar) reads the most-recently-written slot's index. Real
/// version uses atomics + memory fences; this Mutex-backed one is just for
/// unit tests of the consumer-side code path.
pub struct FrameRing {
    inner: Mutex<RingInner>,
}

struct RingInner {
    slots: Vec<FrameSlot>,
    write_index: usize,
    sequence: u64,
}

#[derive(Clone)]
pub struct FrameSlot {
    pub width: u32,
    pub height: u32,
    pub stride_bytes: u32,
    pub capture_ts_ms: f64,
    pub sequence: u64,
    pub pixels: Vec<u8>,
}

impl FrameRing {
    pub fn new() -> Self {
        let empty = FrameSlot {
            width: 0,
            height: 0,
            stride_bytes: 0,
            capture_ts_ms: 0.0,
            sequence: 0,
            pixels: Vec::new(),
        };
        Self {
            inner: Mutex::new(RingInner {
                slots: vec![empty; SLOTS],
                write_index: 0,
                sequence: 0,
            }),
        }
    }

    /// Producer-side. Writes one frame into the next slot. Sequence number
    /// monotonically increases so the consumer can detect dropped frames.
    pub fn produce(
        &self,
        width: u32,
        height: u32,
        capture_ts_ms: f64,
        rgba: &[u8],
    ) -> Result<u64, ProduceError> {
        let expected = (width as usize) * (height as usize) * 4;
        if rgba.len() != expected {
            return Err(ProduceError::SizeMismatch {
                got: rgba.len(),
                expected,
            });
        }
        if width > MAX_WIDTH || height > MAX_HEIGHT {
            return Err(ProduceError::TooLarge { width, height });
        }
        let mut guard = self.inner.lock().expect("ring poisoned");
        let idx = guard.write_index;
        guard.sequence += 1;
        let seq = guard.sequence;
        let slot = &mut guard.slots[idx];
        slot.width = width;
        slot.height = height;
        slot.stride_bytes = width * 4;
        slot.capture_ts_ms = capture_ts_ms;
        slot.sequence = seq;
        slot.pixels.clear();
        slot.pixels.extend_from_slice(rgba);
        guard.write_index = (idx + 1) % SLOTS;
        Ok(seq)
    }

    /// Consumer-side. Returns the most-recently-written slot, or `None` if
    /// nothing has been produced yet. Real shm version returns a borrow; the
    /// in-process version clones for simplicity.
    pub fn latest(&self) -> Option<FrameSlot> {
        let guard = self.inner.lock().expect("ring poisoned");
        if guard.sequence == 0 {
            return None;
        }
        let last_written = (guard.write_index + SLOTS - 1) % SLOTS;
        Some(guard.slots[last_written].clone())
    }
}

impl Default for FrameRing {
    fn default() -> Self {
        Self::new()
    }
}

#[derive(Debug, thiserror::Error)]
pub enum ProduceError {
    #[error("frame size mismatch: got {got} bytes, expected {expected}")]
    SizeMismatch { got: usize, expected: usize },
    #[error("frame too large: {width}x{height} exceeds {MAX_WIDTH}x{MAX_HEIGHT}")]
    TooLarge { width: u32, height: u32 },
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_ring_has_no_latest() {
        let r = FrameRing::new();
        assert!(r.latest().is_none());
    }

    #[test]
    fn produce_then_latest_returns_what_was_written() {
        let r = FrameRing::new();
        let buf = vec![0xAB; 4 * 4 * 4];
        let seq = r.produce(4, 4, 123.4, &buf).unwrap();
        assert_eq!(seq, 1);
        let got = r.latest().unwrap();
        assert_eq!(got.width, 4);
        assert_eq!(got.height, 4);
        assert_eq!(got.capture_ts_ms, 123.4);
        assert_eq!(got.sequence, 1);
        assert_eq!(got.pixels, buf);
    }

    #[test]
    fn sequence_increments_per_produce() {
        let r = FrameRing::new();
        let buf = vec![0u8; 4 * 4 * 4];
        for n in 1..=10 {
            let seq = r.produce(4, 4, 0.0, &buf).unwrap();
            assert_eq!(seq, n);
        }
        assert_eq!(r.latest().unwrap().sequence, 10);
    }

    #[test]
    fn ring_wraps_and_overwrites_oldest() {
        let r = FrameRing::new();
        let buf_a = vec![0xAA; 4 * 4 * 4];
        let buf_b = vec![0xBB; 4 * 4 * 4];
        for _ in 0..SLOTS {
            r.produce(4, 4, 0.0, &buf_a).unwrap();
        }
        r.produce(4, 4, 0.0, &buf_b).unwrap();
        // The most-recent slot should now hold buf_b.
        let got = r.latest().unwrap();
        assert_eq!(got.pixels, buf_b);
    }

    #[test]
    fn produce_rejects_mismatched_size() {
        let r = FrameRing::new();
        let err = r.produce(4, 4, 0.0, &[0u8; 10]).unwrap_err();
        assert!(matches!(err, ProduceError::SizeMismatch { .. }));
    }

    #[test]
    fn produce_rejects_oversize() {
        let r = FrameRing::new();
        let too_big = (MAX_WIDTH + 1) * (MAX_HEIGHT + 1) * 4;
        let buf = vec![0u8; too_big as usize];
        let err = r
            .produce(MAX_WIDTH + 1, MAX_HEIGHT + 1, 0.0, &buf)
            .unwrap_err();
        assert!(matches!(err, ProduceError::TooLarge { .. }));
    }
}
