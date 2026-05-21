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
use crate::protocol::{CameraState as ProtocolCameraState, Layer};
use crate::shared_mem::{MmapFrameRing, MmapRingConfig};

/// On-disk shape of `global.status.json` — the plugin → sidecar push
/// half of the IPC. The plugin rewrites this file at ~1Hz with the
/// current effective state for every tracked camera plus the global
/// KSP framerate and adaptive shed level.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct GlobalStatusFile {
    ksp_fps: f32,
    shed_level: u32,
    #[serde(default)]
    cameras: Vec<PerCameraStatus>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PerCameraStatus {
    flight_id: u32,
    render_width: u32,
    render_height: u32,
    operator_width: u32,
    operator_height: u32,
    #[serde(default)]
    layers: Vec<Layer>,
    #[serde(default)]
    operator_layers: Vec<Layer>,
    #[serde(default)]
    fov: f32,
    #[serde(default)]
    pan_yaw: f32,
    #[serde(default)]
    pan_pitch: f32,
}

/// Diff result from a status poll. Empty vec / None when nothing
/// changed so the consume loop can skip broadcasting.
#[derive(Debug, Default)]
pub struct StatusDelta {
    pub adaptive_shed: Option<(u32, f32)>, // (level, ksp_fps)
    pub changed_cameras: Vec<ProtocolCameraState>,
}

/// In-memory mirror of the plugin's `<flight_id>.control.json` file.
/// The data-channel message handlers mutate this struct and the
/// registry's `flush_control` flushes the full state to disk, so
/// independent setters (SetLayers, SetRenderSize, SetFov) compose
/// cleanly without clobbering each other.
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
    /// Operator-requested camera FoV in degrees. None = "leave Hullcam's
    /// default alone". Ignored for parts where `supports_zoom == false`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fov: Option<f32>,
    /// Operator-requested pan/tilt in degrees. None = "neutral / rest".
    /// Ignored for parts where `supports_pan == false` (every shipping
    /// part today; reserved for the planned mod extension).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pan_yaw: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pan_pitch: Option<f32>,
}

/// Public shape returned by `GET /cameras` — what a browser sees before
/// it picks a subscription set. Operator-readable fields (`part_title`,
/// `camera_name`, `vessel_name`) come from the plugin's `<id>.info.json`
/// manifest; falling back to defaults if the manifest's missing or
/// unreadable. Capability fields tell clients which controls to render
/// per camera (zoom slider / pan stick).
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CameraInfo {
    pub flight_id: u32,
    pub max_width: u32,
    pub max_height: u32,
    pub part_name: String,
    pub part_title: String,
    pub camera_name: String,
    pub vessel_name: String,
    pub supports_zoom: bool,
    pub fov: f32,
    pub fov_min: f32,
    pub fov_max: f32,
    pub supports_pan: bool,
    pub pan_yaw_min: f32,
    pub pan_yaw_max: f32,
    pub pan_pitch_min: f32,
    pub pan_pitch_max: f32,
    pub encoder_bitrate_bps: u32,
    pub target_bitrate_bps: u32,
    pub degrade_level: f32,
}

/// Manifest the plugin writes alongside the ring file. Static for the
/// camera's lifetime — vessel renames mid-flight aren't reflected
/// until the next vessel change. Capability fields are read from the
/// Hullcam module on the plugin side: `supportsZoom = true` iff the
/// part is `MuMechModuleHullCameraZoom`; `supportsPan = true` only once
/// the planned mod extension adds steerable mounts.
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
    #[serde(default)]
    supports_zoom: bool,
    #[serde(default)]
    fov: f32,
    #[serde(default)]
    fov_min: f32,
    #[serde(default)]
    fov_max: f32,
    #[serde(default)]
    supports_pan: bool,
    #[serde(default)]
    pan_yaw_min: f32,
    #[serde(default)]
    pan_yaw_max: f32,
    #[serde(default)]
    pan_pitch_min: f32,
    #[serde(default)]
    pan_pitch_max: f32,
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
    pub supports_zoom: bool,
    pub fov_default: f32,
    pub fov_min: f32,
    pub fov_max: f32,
    pub supports_pan: bool,
    pub pan_yaw_min: f32,
    pub pan_yaw_max: f32,
    pub pan_pitch_min: f32,
    pub pan_pitch_max: f32,
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
    /// Bitrate the current encoder session was initialised with.
    /// 0 = no encoder running yet. The consume loop compares against
    /// `target_bitrate_bps` and reinitialises the encoder when they
    /// diverge significantly (REMB-driven adaptation).
    pub encoder_bitrate: AtomicU32,
    /// Most-recent target bitrate, computed as the min across active
    /// subscribers' REMB estimates (so the slowest receiver doesn't
    /// drop the whole stream). 0 = no REMB received yet; consume loop
    /// falls back to the CLI default.
    pub target_bitrate_bps: AtomicU32,
    /// Per-SSRC REMB estimates from active subscribers. Updated by
    /// the peer's RTCP drain loop; consume loop reads + takes the min
    /// to produce `target_bitrate_bps`.
    pub bandwidth_estimates: Mutex<std::collections::HashMap<u32, u32>>,
    /// Per-SSRC SetDegrade requests from active subscribers
    /// (0.0..=1.0). Like bandwidth_estimates but max-across-
    /// subscribers — the noisiest consumer's request wins, mirroring
    /// the slowest-consumer-wins logic for bandwidth.
    pub degrade_levels: Mutex<std::collections::HashMap<u32, f32>>,
    /// Cached max-across-subscribers degrade level. Recomputed when
    /// any peer's SetDegrade lands; encode loop reads atomically.
    /// Stored as bits-of-f32 in an AtomicU32 to keep the hot path
    /// lock-free (encode_and_fan_out runs per-frame, the f32 is
    /// already bit-pattern-stable for our levels).
    pub effective_degrade: AtomicU32,
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

    /// Record a REMB bandwidth estimate from a subscriber. Identified
    /// by the receiving track's SSRC so per-peer estimates stay
    /// distinct. Recomputes `target_bitrate_bps` as the min across all
    /// known estimates so the encoder targets the slowest receiver.
    pub async fn record_bandwidth_estimate(&self, ssrc: u32, bps: u32) {
        let mut estimates = self.bandwidth_estimates.lock().await;
        estimates.insert(ssrc, bps);
        let min = estimates.values().copied().min().unwrap_or(0);
        self.target_bitrate_bps.store(min, Ordering::Release);
    }

    /// Forget estimates for an SSRC whose track has gone away (peer
    /// dropped). The consume loop will recompute the min on the next
    /// REMB packet; until then the stored target stays stale but the
    /// remaining peers' estimates dominate quickly.
    pub async fn forget_estimate(&self, ssrc: u32) {
        let mut estimates = self.bandwidth_estimates.lock().await;
        estimates.remove(&ssrc);
        let min = estimates.values().copied().min().unwrap_or(0);
        self.target_bitrate_bps.store(min, Ordering::Release);
    }

    /// Record a SetDegrade request from a subscriber. Recomputes
    /// `effective_degrade` as the max across all known requests so
    /// the noisiest consumer wins (mirrors how `target_bitrate_bps`
    /// picks the *slowest* receiver — both are "worst case from any
    /// subscriber" reductions).
    pub async fn record_degrade(&self, ssrc: u32, level: f32) {
        let mut levels = self.degrade_levels.lock().await;
        let clamped = level.clamp(0.0, 1.0);
        levels.insert(ssrc, clamped);
        let max = levels.values().copied().fold(0.0f32, |acc, v| acc.max(v));
        self.effective_degrade
            .store(max.to_bits(), Ordering::Release);
    }

    /// Forget a subscriber's degrade request. Recomputes the max so
    /// the level relaxes once the noisiest consumer drops.
    pub async fn forget_degrade(&self, ssrc: u32) {
        let mut levels = self.degrade_levels.lock().await;
        levels.remove(&ssrc);
        let max = levels.values().copied().fold(0.0f32, |acc, v| acc.max(v));
        self.effective_degrade
            .store(max.to_bits(), Ordering::Release);
    }

    /// Snapshot of the current effective degrade level. Lock-free
    /// read for the per-frame encode path.
    pub fn current_degrade(&self) -> f32 {
        f32::from_bits(self.effective_degrade.load(Ordering::Acquire))
    }
}

/// Registry of cameras. Owns the rescan logic that discovers new rings
/// and forgets dead ones. Also caches the last-seen global status snapshot
/// from the plugin so the consume loop can diff against it.
pub struct CameraRegistry {
    shm_dir: PathBuf,
    ring_cfg: MmapRingConfig,
    pub cameras: RwLock<HashMap<u32, Arc<CameraState>>>,
    last_status: Mutex<Option<GlobalStatusFile>>,
}

impl CameraRegistry {
    pub fn new(shm_dir: PathBuf, ring_cfg: MmapRingConfig) -> Self {
        Self {
            shm_dir,
            ring_cfg,
            cameras: RwLock::new(HashMap::new()),
            last_status: Mutex::new(None),
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
                            supports_zoom: manifest.supports_zoom,
                            fov_default: manifest.fov,
                            fov_min: manifest.fov_min,
                            fov_max: manifest.fov_max,
                            supports_pan: manifest.supports_pan,
                            pan_yaw_min: manifest.pan_yaw_min,
                            pan_yaw_max: manifest.pan_yaw_max,
                            pan_pitch_min: manifest.pan_pitch_min,
                            pan_pitch_max: manifest.pan_pitch_max,
                            encoder: Mutex::new(None),
                            encoder_width: AtomicU32::new(0),
                            encoder_height: AtomicU32::new(0),
                            encoder_bitrate: AtomicU32::new(0),
                            target_bitrate_bps: AtomicU32::new(0),
                            bandwidth_estimates: Mutex::new(std::collections::HashMap::new()),
                            degrade_levels: Mutex::new(std::collections::HashMap::new()),
                            effective_degrade: AtomicU32::new(0),
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
                supports_zoom: s.supports_zoom,
                fov: s.fov_default,
                fov_min: s.fov_min,
                fov_max: s.fov_max,
                supports_pan: s.supports_pan,
                pan_yaw_min: s.pan_yaw_min,
                pan_yaw_max: s.pan_yaw_max,
                pan_pitch_min: s.pan_pitch_min,
                pan_pitch_max: s.pan_pitch_max,
                encoder_bitrate_bps: s.encoder_bitrate.load(Ordering::Acquire),
                target_bitrate_bps: s.target_bitrate_bps.load(Ordering::Acquire),
                degrade_level: s.current_degrade(),
            })
            .collect();
        // Stable ordering for tests + UX (a refresh shouldn't shuffle).
        out.sort_by_key(|c| c.flight_id);
        out
    }

    pub async fn get(&self, flight_id: u32) -> Option<Arc<CameraState>> {
        self.cameras.read().await.get(&flight_id).cloned()
    }

    /// Read `global.status.json` and diff against the cached snapshot.
    /// Returns the set of changes that need broadcasting over the data
    /// channel — per-camera state deltas + adaptive-shed level changes.
    /// Tolerant of the file being absent (plugin hasn't written yet) or
    /// malformed (partial write — atomic rename normally prevents this,
    /// but better to skip a tick than crash).
    pub async fn poll_status(&self) -> StatusDelta {
        let path = self.shm_dir.join("global.status.json");
        let bytes = match tokio::fs::read(&path).await {
            Ok(b) => b,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return StatusDelta::default(),
            Err(e) => {
                warn!(path = %path.display(), error = %e, "status read failed");
                return StatusDelta::default();
            }
        };
        let parsed: GlobalStatusFile = match serde_json::from_slice(&bytes) {
            Ok(s) => s,
            Err(e) => {
                warn!(error = %e, "status parse failed");
                return StatusDelta::default();
            }
        };

        let mut delta = StatusDelta::default();
        let cameras = self.cameras.read().await;
        let mut last = self.last_status.lock().await;

        // Shed-level changes are global — emit one adaptive-shed
        // message whenever the level moves.
        if last
            .as_ref()
            .map(|s| s.shed_level != parsed.shed_level)
            .unwrap_or(true)
        {
            delta.adaptive_shed = Some((parsed.shed_level, parsed.ksp_fps));
        }

        // Per-camera diffs. Only flag cameras whose effective state moved.
        for cam_status in &parsed.cameras {
            let prev = last.as_ref().and_then(|s| {
                s.cameras
                    .iter()
                    .find(|c| c.flight_id == cam_status.flight_id)
            });
            let changed = match prev {
                None => true,
                Some(p) => {
                    p.render_width != cam_status.render_width
                        || p.render_height != cam_status.render_height
                        || p.operator_width != cam_status.operator_width
                        || p.operator_height != cam_status.operator_height
                        || p.layers != cam_status.layers
                        || p.operator_layers != cam_status.operator_layers
                        || (p.fov - cam_status.fov).abs() > 0.01
                        || (p.pan_yaw - cam_status.pan_yaw).abs() > 0.01
                        || (p.pan_pitch - cam_status.pan_pitch).abs() > 0.01
                }
            };
            if !changed {
                continue;
            }
            let Some(cam) = cameras.get(&cam_status.flight_id) else {
                continue;
            };
            delta.changed_cameras.push(ProtocolCameraState {
                flight_id: cam_status.flight_id,
                part_name: cam.part_name.clone(),
                part_title: cam.part_title.clone(),
                camera_name: cam.camera_name.clone(),
                vessel_name: cam.vessel_name.clone(),
                layers: cam_status.layers.clone(),
                operator_layers: cam_status.operator_layers.clone(),
                render_width: cam_status.render_width,
                render_height: cam_status.render_height,
                operator_width: cam_status.operator_width,
                operator_height: cam_status.operator_height,
                supports_zoom: cam.supports_zoom,
                fov: cam_status.fov,
                fov_min: cam.fov_min,
                fov_max: cam.fov_max,
                supports_pan: cam.supports_pan,
                pan_yaw: cam_status.pan_yaw,
                pan_pitch: cam_status.pan_pitch,
                pan_yaw_min: cam.pan_yaw_min,
                pan_yaw_max: cam.pan_yaw_max,
                pan_pitch_min: cam.pan_pitch_min,
                pan_pitch_max: cam.pan_pitch_max,
                encoder_bitrate_bps: cam.encoder_bitrate.load(Ordering::Acquire),
                target_bitrate_bps: cam.target_bitrate_bps.load(Ordering::Acquire),
                degrade_level: cam.current_degrade(),
            });
        }

        *last = Some(parsed);
        delta
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
        supports_zoom: false,
        fov: 0.0,
        fov_min: 0.0,
        fov_max: 0.0,
        supports_pan: false,
        pan_yaw_min: 0.0,
        pan_yaw_max: 0.0,
        pan_pitch_min: 0.0,
        pan_pitch_max: 0.0,
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
