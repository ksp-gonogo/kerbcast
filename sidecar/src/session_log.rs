/**
 * A log file per sidecar session that survives a game relaunch. The newest
 * `KEEP_SESSIONS` files stay in the folder and each is capped at
 * `MAX_FILE_BYTES`, so a long session cannot fill the disk.
 */
use std::fs::{self, File};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use tracing_subscriber::fmt::MakeWriter;

/// Sessions kept on disk, the current one included.
pub const KEEP_SESSIONS: usize = 5;

/// Size cap per session file. Past it, further lines are dropped.
pub const MAX_FILE_BYTES: u64 = 20 * 1024 * 1024;

const PREFIX: &str = "sidecar-";
const SUFFIX: &str = ".log";

/**
 * Writes to a file until `cap` bytes, then drops everything after one
 * closing line. Write errors never surface: logging must not stop the
 * sidecar.
 */
pub struct CappedFile<W: Write> {
    inner: W,
    written: u64,
    cap: u64,
    capped: bool,
}

impl<W: Write> CappedFile<W> {
    pub fn new(inner: W, cap: u64) -> Self {
        Self {
            inner,
            written: 0,
            cap,
            capped: false,
        }
    }
}

impl<W: Write> Write for CappedFile<W> {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        if self.capped {
            return Ok(buf.len());
        }
        if self.written + buf.len() as u64 > self.cap {
            self.capped = true;
            let _ = self
                .inner
                .write_all(b"log size cap reached, later lines dropped\n");
            return Ok(buf.len());
        }
        let _ = self.inner.write_all(buf);
        self.written += buf.len() as u64;
        Ok(buf.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        let _ = self.inner.flush();
        Ok(())
    }
}

/// Shared handle the tracing file layer clones for each event.
#[derive(Clone)]
pub struct SessionWriter(Arc<Mutex<CappedFile<File>>>);

impl Write for SessionWriter {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        match self.0.lock() {
            Ok(mut f) => f.write(buf),
            Err(_) => Ok(buf.len()),
        }
    }

    fn flush(&mut self) -> io::Result<()> {
        match self.0.lock() {
            Ok(mut f) => f.flush(),
            Err(_) => Ok(()),
        }
    }
}

impl<'a> MakeWriter<'a> for SessionWriter {
    type Writer = SessionWriter;

    fn make_writer(&'a self) -> Self::Writer {
        self.clone()
    }
}

/// `sidecar-YYYYMMDD-HHMMSS.log` in UTC, so names sort by start time.
pub fn session_file_name(unix_secs: u64) -> String {
    let days = (unix_secs / 86_400) as i64;
    let rem = unix_secs % 86_400;
    // Civil-from-days (Howard Hinnant), valid for the whole u64 range we use.
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!(
        "{PREFIX}{year:04}{month:02}{day:02}-{:02}{:02}{:02}{SUFFIX}",
        rem / 3_600,
        rem % 3_600 / 60,
        rem % 60
    )
}

/// Deletes the oldest session files so at most `keep` remain.
pub fn prune(dir: &Path, keep: usize) {
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    let mut names: Vec<String> = entries
        .filter_map(|e| e.ok())
        .filter_map(|e| e.file_name().into_string().ok())
        .filter(|n| n.starts_with(PREFIX) && n.ends_with(SUFFIX))
        .collect();
    names.sort();
    let excess = names.len().saturating_sub(keep);
    for name in names.into_iter().take(excess) {
        let _ = fs::remove_file(dir.join(name));
    }
}

/**
 * Creates this session's file in `dir` and prunes older ones so `keep`
 * remain, the new one included. Returns the path with the writer.
 */
pub fn open_session(
    dir: &Path,
    unix_secs: u64,
    keep: usize,
    cap: u64,
) -> io::Result<(PathBuf, SessionWriter)> {
    fs::create_dir_all(dir)?;
    prune(dir, keep.saturating_sub(1));
    let path = dir.join(session_file_name(unix_secs));
    let file = File::create(&path)?;
    Ok((
        path,
        SessionWriter(Arc::new(Mutex::new(CappedFile::new(file, cap)))),
    ))
}

/// Opens this session's file stamped with the current time.
pub fn open_now(dir: &Path) -> io::Result<(PathBuf, SessionWriter)> {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    open_session(dir, secs, KEEP_SESSIONS, MAX_FILE_BYTES)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_are_utc_and_sort_by_time() {
        assert_eq!(session_file_name(0), "sidecar-19700101-000000.log");
        assert_eq!(
            session_file_name(1_709_210_096),
            "sidecar-20240229-123456.log"
        );
        assert!(session_file_name(1_000) < session_file_name(2_000_000_000));
    }

    #[test]
    fn keeps_only_the_newest_sessions() {
        let dir = tempfile::tempdir().unwrap();
        for i in 0..8u64 {
            open_session(dir.path(), 1_700_000_000 + i * 60, 5, 1024).unwrap();
        }
        fs::write(dir.path().join("notes.txt"), "x").unwrap();
        let mut names: Vec<String> = fs::read_dir(dir.path())
            .unwrap()
            .map(|e| e.unwrap().file_name().into_string().unwrap())
            .filter(|n| n.starts_with(PREFIX))
            .collect();
        names.sort();
        assert_eq!(names.len(), 5);
        assert_eq!(names[0], session_file_name(1_700_000_000 + 3 * 60));
        assert_eq!(names[4], session_file_name(1_700_000_000 + 7 * 60));
        assert!(dir.path().join("notes.txt").exists());
    }

    #[test]
    fn stops_writing_at_the_cap() {
        let dir = tempfile::tempdir().unwrap();
        let (path, mut w) = open_session(dir.path(), 1, 5, 100).unwrap();
        for _ in 0..50 {
            w.write_all(b"0123456789\n").unwrap();
        }
        w.flush().unwrap();
        let text = fs::read_to_string(path).unwrap();
        assert!(text.len() < 100 + 64, "file grew to {}", text.len());
        assert!(text.ends_with("later lines dropped\n"));
        assert_eq!(text.matches("dropped").count(), 1);
    }
}
