//! A change counter in shared memory: the sidecar bumps it after each write
//! of a file the plugin polls, and the plugin compares one mapped word each
//! frame instead of the file's write time. Mono on Linux reports write times
//! to the whole second, so a file rewritten ten times a second looked
//! unchanged for most of each second.
//!
//! The layout MUST stay in lockstep with `Plugin/Kerbcast/ChangeSignal.cs`:
//!
//! ```text
//!   0  8  u64 magic = CHANGE_SIGNAL_MAGIC
//!   8  8  u64 count, bumped once per write (any change means news)
//! ```
//!
//! The file is opened, never truncated, so a restarted sidecar keeps the
//! same file and counts on from where the last one stopped; a plugin that
//! mapped it keeps seeing the bumps.

use std::fs::OpenOptions;
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};

use memmap2::{MmapMut, MmapOptions};

/// "KCSIG1\0\0" little-endian.
pub const CHANGE_SIGNAL_MAGIC: u64 = 0x0000_3147_4953_434B;
pub const CHANGE_SIGNAL_SIZE: usize = 16;

const H_MAGIC: usize = 0;
const H_COUNT: usize = 8;

pub struct ChangeSignal {
    mmap: MmapMut,
}

impl ChangeSignal {
    pub fn open(path: &Path) -> std::io::Result<Self> {
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(path)?;
        if file.metadata()?.len() < CHANGE_SIGNAL_SIZE as u64 {
            file.set_len(CHANGE_SIGNAL_SIZE as u64)?;
        }
        let mmap = unsafe { MmapOptions::new().len(CHANGE_SIGNAL_SIZE).map_mut(&file)? };
        let signal = Self { mmap };
        /* The magic goes in last, with release ordering, so a reader that
         * sees it also sees the zeroed count. */
        if signal.word(H_MAGIC).load(Ordering::Acquire) != CHANGE_SIGNAL_MAGIC.to_le() {
            signal.word(H_COUNT).store(0, Ordering::Relaxed);
            signal
                .word(H_MAGIC)
                .store(CHANGE_SIGNAL_MAGIC.to_le(), Ordering::Release);
        }
        Ok(signal)
    }

    fn word(&self, offset: usize) -> &AtomicU64 {
        /* The map is page-aligned and both words sit on 8-byte offsets, so
         * the cast is aligned; only this process writes them. */
        let ptr = self.mmap.as_ptr() as *const AtomicU64;
        unsafe { &*ptr.add(offset / 8) }
    }

    fn count_atomic(&self) -> &AtomicU64 {
        self.word(H_COUNT)
    }

    /// Publish one change. Call after the polled file is in place, so a
    /// reader that sees the new count reads the new file.
    pub fn bump(&self) -> u64 {
        self.count_atomic().fetch_add(1, Ordering::Release) + 1
    }

    pub fn count(&self) -> u64 {
        self.count_atomic().load(Ordering::Acquire)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_new_file_starts_at_zero_with_the_magic() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("x.signal");
        let s = ChangeSignal::open(&path).unwrap();
        assert_eq!(s.count(), 0);
        let bytes = std::fs::read(&path).unwrap();
        assert_eq!(bytes.len(), CHANGE_SIGNAL_SIZE);
        assert_eq!(
            u64::from_le_bytes(bytes[0..8].try_into().unwrap()),
            CHANGE_SIGNAL_MAGIC
        );
    }

    #[test]
    fn each_bump_is_visible_in_the_file() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("x.signal");
        let s = ChangeSignal::open(&path).unwrap();
        assert_eq!(s.bump(), 1);
        assert_eq!(s.bump(), 2);
        let bytes = std::fs::read(&path).unwrap();
        assert_eq!(u64::from_le_bytes(bytes[8..16].try_into().unwrap()), 2);
    }

    #[test]
    fn reopening_counts_on_instead_of_restarting() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("x.signal");
        {
            let s = ChangeSignal::open(&path).unwrap();
            s.bump();
            s.bump();
        }
        let s = ChangeSignal::open(&path).unwrap();
        assert_eq!(s.count(), 2);
        assert_eq!(s.bump(), 3);
    }

    #[test]
    fn a_file_without_the_magic_is_reset() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("x.signal");
        std::fs::write(&path, [0xFFu8; 16]).unwrap();
        let s = ChangeSignal::open(&path).unwrap();
        assert_eq!(s.count(), 0);
    }
}
