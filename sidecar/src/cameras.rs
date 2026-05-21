//! Per-camera state owned by the sidecar.
//!
//! The plugin (writer side) creates one ring file per Hullcam VDS part,
//! keyed by KSP's stable `Part.flightID`. The sidecar (reader side) globs
//! `<shm_dir>/*.ring`, parses the filename's stem as a u32 flight ID, and
//! maintains a registry of `CameraState` keyed by that ID.
//!
//! Each `CameraState` owns one mmap ring, one lazy-initialised
//! `EncoderBackend` (created on first frame so it sees the actual ring
//! dimensions), and a list of `TrackLocalStaticSample` weak refs — one per
//! peer-track subscribed to this camera. The consume loop iterates the
//! registry once per tick, encodes only cameras with `subscribers > 0`,
//! and fans the resulting NALs out to every alive track.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Weak};
use std::time::Instant;

use serde::{Deserialize, Serialize};
use tokio::sync::{Mutex, RwLock};
use tracing::{info, warn};
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;

use crate::encoder::EncoderBackend;
use crate::protocol::Layer;
use crate::shared_mem::{MmapFrameRing, MmapRingConfig};

/// In-memory mirror of the plugin's `<flight_id>.control.json` file.
/// The data-channel message handlers mutate this struct and the
/// registry's `write_control` flushes the full state to disk, so two
/// independent setters compose cleanly.
#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ControlState {
    /// Operator-requested layer mask. Empty = "fall back to settings.cfg
    /// initial mask on the plugin side"; populated = explicit override.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub layers: Vec<Layer>,
    /// Operator-requested render size. None = "use settings.cfg initial".
    #[serde(skip_serializing_if = "Option::is_none")]
    pub width: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub height: Option<u32>,
}

/// Public shape returned by `GET /cameras` — what a browser sees before
/// it picks a subscription set. Operator-readable fields (`part_title`,
/// `camera_name`, `vessel_name`) come from the plugin's `<id>.info.json`
/// manifest; falling back to defaults if the manifest's missing or
/// unreadable.
#[derive(Debug, Clone, Serialize)]
pub struct CameraInfo {
    pub flight_id: u32,
    pub max_width: u32,
    pub max_height: u32,
    pub part_name: String,
    pub part_title: String,
    pub camera_name: String,
    pub vessel_name: String,
}

/// Manifest the plugin writes alongside the ring file. Static for the
/// camera's lifetime — vessel renames mid-flight aren't reflected
/// until the next vessel change.
#[derive(Debug, Clone, Deserialize)]
struct InfoManifest {
    #[allow(dead_code)] // echo-only — we already know the flight_id from the filename
    flight_id: u32,
    #[serde(default)]
    part_name: String,
    #[serde(default)]
    part_title: String,
    #[serde(default)]
    camera_name: String,
    #[serde(default)]
    vessel_name: String,
}

pub struct CameraState {
    pub flight_id: u32,
    pub ring: MmapFrameRing,
    pub max_width: u32,
    pub max_height: u32,
    pub part_name: String,
    pub part_title: String,
    pub camera_name: String,
    pub vessel_name: String,
    /// Sidecar-side mirror of the plugin's control file. Mutated by
    /// data-channel messages (SetLayers / SetRenderSize); the registry's
    /// `write_control` flushes the full struct to disk on each change so
    /// fields stay in sync — setting layers doesn't clobber an
    /// already-set render size.
    pub control: Mutex<ControlState>,
    /// Lazy: created on first encoded frame so the encoder sees the
    /// actual frame dimensions, not the ring's max. Closed + reinit if
    /// the frame dimensions change (the plugin's adaptive downscale
    /// path triggers this).
    pub encoder: Mutex<Option<Box<dyn EncoderBackend>>>,
    /// Width/height the encoder is currently initialised for. The
    /// consume loop compares these against the incoming frame's
    /// dimensions to detect adaptive-downscale resolution changes.
    /// Both 0 when no encoder is running.
    pub encoder_width: AtomicU32,
    pub encoder_height: AtomicU32,
    /// Last wall-clock instant we *encoded* (and emitted NALs to) a frame.
    /// The consume loop uses this to pace encodes against the configured
    /// `fps`, instead of running once per ring write at LateUpdate's
    /// native cadence (40-60 Hz on the Deck) and tagging samples with
    /// duration=1/fps that the receiver can't reconcile.
    pub last_encoded_at: Mutex<Option<Instant>>,
    pub last_sequence: AtomicU64,
    /// Count of peer-tracks currently subscribed. Encoder runs only when > 0.
    pub subscribers: AtomicUsize,
    /// One `TrackLocalStaticSample` per subscribed peer-track. Weak refs so
    /// dropped peers get GC'd from this list naturally — no manual unsub
    /// bookkeeping per peer needed.
    pub tracks: RwLock<Vec<Weak<TrackLocalStaticSample>>>,
}

impl CameraState {
    /// Add a track to this camera and bump the subscriber count. Returns
    /// the count after the increment so the caller can request a keyframe
    /// on the 0→1 transition.
    pub async fn add_track(&self, track: Arc<TrackLocalStaticSample>) -> usize {
        let mut tracks = self.tracks.write().await;
        tracks.push(Arc::downgrade(&track));
        self.subscribers.fetch_add(1, Ordering::AcqRel) + 1
    }

    /// Drop the subscriber count by `n` (used when a peer goes away with
    /// multiple subscribed tracks). Stale weak refs get pruned by the
    /// consume loop on the next tick.
    pub fn release(&self, n: usize) {
        self.subscribers.fetch_sub(n, Ordering::AcqRel);
    }
}

/// Registry of cameras. Owns the rescan logic that discovers new rings
/// and forgets dead ones.
pub struct CameraRegistry {
    shm_dir: PathBuf,
    ring_cfg: MmapRingConfig,
    pub cameras: RwLock<HashMap<u32, Arc<CameraState>>>,
}

impl CameraRegistry {
    pub fn new(shm_dir: PathBuf, ring_cfg: MmapRingConfig) -> Self {
        Self {
            shm_dir,
            ring_cfg,
            cameras: RwLock::new(HashMap::new()),
        }
    }

    /// Walk `shm_dir`, attach any new `<flight_id>.ring` files, drop any
    /// that have disappeared from disk. Tolerant of the directory not yet
    /// existing — the plugin may not have created it.
    pub async fn rescan(&self) {
        let mut found: HashMap<u32, PathBuf> = HashMap::new();
        let mut entries = match tokio::fs::read_dir(&self.shm_dir).await {
            Ok(e) => e,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return,
            Err(e) => {
                warn!(dir = %self.shm_dir.display(), error = %e, "rescan read_dir failed");
                return;
            }
        };
        while let Ok(Some(entry)) = entries.next_entry().await {
            let path = entry.path();
            if path.extension().and_then(|s| s.to_str()) != Some("ring") {
                continue;
            }
            let stem = match path.file_stem().and_then(|s| s.to_str()) {
                Some(s) => s,
                None => continue,
            };
            if let Ok(id) = stem.parse::<u32>() {
                found.insert(id, path);
            }
        }

        let mut cameras = self.cameras.write().await;

        // Attach new rings.
        for (id, path) in &found {
            if cameras.contains_key(id) {
                continue;
            }
            match MmapFrameRing::open(path, self.ring_cfg) {
                Ok(ring) => {
                    let manifest = read_manifest(&self.shm_dir, *id).await;
                    info!(
                        flight_id = id,
                        path = %path.display(),
                        max_dims = format!("{}x{}", self.ring_cfg.max_width, self.ring_cfg.max_height),
                        part_title = %manifest.part_title,
                        vessel = %manifest.vessel_name,
                        "camera ring attached",
                    );
                    cameras.insert(
                        *id,
                        Arc::new(CameraState {
                            flight_id: *id,
                            ring,
                            max_width: self.ring_cfg.max_width,
                            max_height: self.ring_cfg.max_height,
                            part_name: manifest.part_name,
                            part_title: manifest.part_title,
                            camera_name: manifest.camera_name,
                            vessel_name: manifest.vessel_name,
                            encoder: Mutex::new(None),
                            encoder_width: AtomicU32::new(0),
                            encoder_height: AtomicU32::new(0),
                            last_encoded_at: Mutex::new(None),
                            last_sequence: AtomicU64::new(0),
                            subscribers: AtomicUsize::new(0),
                            tracks: RwLock::new(Vec::new()),
                            control: Mutex::new(ControlState::default()),
                        }),
                    );
                }
                Err(e) => {
                    warn!(flight_id = id, path = %path.display(), error = %e, "failed to open ring");
                }
            }
        }

        // Drop rings that have disappeared.
        let mut removed = Vec::new();
        cameras.retain(|id, _| {
            let still = found.contains_key(id);
            if !still {
                removed.push(*id);
            }
            still
        });
        for id in removed {
            info!(flight_id = id, "camera ring removed");
        }
    }

    pub async fn list(&self) -> Vec<CameraInfo> {
        let cams = self.cameras.read().await;
        let mut out: Vec<_> = cams
            .values()
            .map(|s| CameraInfo {
                flight_id: s.flight_id,
                max_width: s.max_width,
                max_height: s.max_height,
                part_name: s.part_name.clone(),
                part_title: s.part_title.clone(),
                camera_name: s.camera_name.clone(),
                vessel_name: s.vessel_name.clone(),
            })
            .collect();
        // Stable ordering for tests + UX (a refresh shouldn't shuffle).
        out.sort_by_key(|c| c.flight_id);
        out
    }

    pub async fn get(&self, flight_id: u32) -> Option<Arc<CameraState>> {
        self.cameras.read().await.get(&flight_id).cloned()
    }

    /// Flush a camera's in-memory `ControlState` to its
    /// `<flight_id>.control.json` file. Atomic rename so the plugin
    /// never observes a half-written file. Carries every field that's
    /// currently set; the plugin parses lazily, so adding new fields
    /// here doesn't break older plugin versions.
    pub async fn flush_control(&self, flight_id: u32, state: &ControlState) -> std::io::Result<()> {
        let dest = self.shm_dir.join(format!("{flight_id}.control.json"));
        let tmp = self.shm_dir.join(format!("{flight_id}.control.json.tmp"));
        let body = serde_json::to_string_pretty(state)?;
        tokio::fs::write(&tmp, body).await?;
        tokio::fs::rename(&tmp, &dest).await?;
        Ok(())
    }

    /// Snapshot of all camera Arcs — used by the consume loop to iterate
    /// without holding the registry's RwLock while encoding.
    pub async fn snapshot(&self) -> Vec<Arc<CameraState>> {
        self.cameras.read().await.values().cloned().collect()
    }
}

/// Read the plugin's `<flight_id>.info.json` next to the ring file.
/// Returns a manifest with empty fields if missing or unparseable —
/// callers fall back to "(no label)" displays. Logging the parse
/// error makes a malformed manifest discoverable without crashing.
async fn read_manifest(shm_dir: &std::path::Path, flight_id: u32) -> InfoManifest {
    let path = shm_dir.join(format!("{flight_id}.info.json"));
    let empty = InfoManifest {
        flight_id,
        part_name: String::new(),
        part_title: String::new(),
        camera_name: String::new(),
        vessel_name: String::new(),
    };
    let bytes = match tokio::fs::read(&path).await {
        Ok(b) => b,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return empty,
        Err(e) => {
            warn!(flight_id, path = %path.display(), error = %e, "info manifest read failed");
            return empty;
        }
    };
    match serde_json::from_slice::<InfoManifest>(&bytes) {
        Ok(m) => m,
        Err(e) => {
            warn!(flight_id, path = %path.display(), error = %e, "info manifest parse failed");
            empty
        }
    }
}
