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
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Weak};
use std::time::Instant;

use serde::{Deserialize, Serialize};
use tokio::sync::{Mutex, RwLock};
use tracing::{info, warn};
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;

use crate::encoder::EncoderBackend;
use crate::protocol::{CameraLifecycle, CameraState as ProtocolCameraState, Layer};
use crate::shared_mem::{ControlBlock, MmapFrameRing, MmapRingConfig};

/// On-disk shape of `global.status.json` — the plugin → sidecar push
/// half of the IPC. The plugin rewrites this file at ~1Hz with the
/// current effective state for every tracked camera plus the global
/// KSP framerate and adaptive shed level.
///
/// `Serialize` is derived too so `GET /dumpLogs` can replay buffered
/// snapshots straight back to the harness as JSONL. `PartialEq` powers
/// the dedup that keeps the buffer small (the consume loop polls at
/// ~62Hz; the plugin writes at ~1Hz).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct GlobalStatusFile {
    ksp_fps: f32,
    shed_level: u32,
    #[serde(default)]
    cameras: Vec<PerCameraStatus>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
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

/// In-memory mirror of a camera's control state. The data-channel message
/// handlers mutate this struct and the registry's `flush_control` publishes the
/// full state to the camera's shared-memory control block
/// (`<flight_id>.control.bin`, read by the plugin), so independent setters
/// (SetLayers, SetRenderSize, SetFov, …) compose cleanly without clobbering each
/// other. (`Serialize` is retained only for `/dumpLogs`-style debug output.)
#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ControlState {
    /// Whether at least one peer is subscribed to this camera. Drives
    /// the plugin's subscriber-aware capture skip: when `false` the
    /// plugin disables the per-camera layer renders and short-circuits
    /// Refresh(), so unsubscribed cameras cost nothing on the KSP
    /// side. Always emitted (no `skip_serializing_if`) so the field
    /// is unambiguously present in the JSON — the plugin treats
    /// "field missing" as `false` for safety.
    pub subscribed: bool,
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
    /// Persistent pan velocity, normalised -1..=1 per axis. `None` =
    /// unchanged (the plugin keeps the last rate); `Some(0.0)` = stop.
    /// The plugin integrates these into `pan_yaw`/`pan_pitch` every frame.
    /// Sign matches the absolute pan: +yaw = right, +pitch = up.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pan_yaw_rate: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pan_pitch_rate: Option<f32>,
    /// Persistent zoom velocity, normalised -1..=1. +1 = zoom in (FoV
    /// decreasing). `None` = unchanged; `Some(0.0)` = stop. The plugin
    /// integrates this into the FoV target every frame.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub zoom_rate: Option<f32>,
    /// Monotonic counter bumped on every *absolute* pan command
    /// (`apply_pan_change`). control.json is a full-state snapshot
    /// re-serialised on every command, so the stale `pan_yaw`/`pan_pitch`
    /// ride along on unrelated writes (and on the rate-stop flush itself).
    /// While a rate integrates the plugin's target away from that stale
    /// absolute, re-applying it would snap the camera back. The plugin
    /// therefore applies the absolute pan only when `pan_seq` *changes*,
    /// so a re-serialised stale value is idempotent but a genuine new
    /// absolute (even one re-issuing the same value) still lands. Rate
    /// commands and the disconnect deadman do NOT bump it.
    pub pan_seq: u32,
    /// As `pan_seq`, for absolute FoV (`apply_fov_change`).
    pub fov_seq: u32,
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
    /// Part-destruction lifecycle. Reflects the camera's last-known
    /// state from `info.json`. Destroyed cameras remain in the list so
    /// the UI can show a "SIGNAL LOST" badge until the next vessel change
    /// clears the registry. `"active"` for live cameras.
    pub lifecycle: CameraLifecycle,
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
///
/// When the plugin destroys a Hullcam part it rewrites this file with
/// `lifecycle: "destroyed"` and removes the `.ring` file. The sidecar
/// must detect the transition and clean up.
#[derive(Debug, Clone, Deserialize)]
struct InfoManifest {
    #[allow(dead_code)] // echo-only — we already know the flight_id from the filename
    flight_id: u32,
    /// Absent / unknown values treated as `Active` per the protocol spec.
    #[serde(default)]
    lifecycle: CameraLifecycle,
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
    /// Current lifecycle state. Set once to `Destroyed` on first detection
    /// of `lifecycle: "destroyed"` in info.json; never flips back to `Active`.
    /// Stored as an `AtomicBool` (true = destroyed) for lock-free reads in
    /// the encode path.
    pub destroyed: AtomicBool,
    /// Tracks whether we've already sent the destruction notification to
    /// peers. Prevents duplicate broadcasts when the consume loop re-reads
    /// the tombstone on a subsequent tick before info.json is deleted.
    pub destruction_broadcast_sent: AtomicBool,
    /// Sidecar-side mirror of the plugin's control file. Mutated by
    /// data-channel messages (SetLayers / SetRenderSize); the registry's
    /// `write_control` flushes the full struct to disk on each change so
    /// fields stay in sync — setting layers doesn't clobber an
    /// already-set render size.
    pub control: Mutex<ControlState>,
    /// Persistent shared-memory control block this camera's state is written
    /// to (`<flight_id>.control.bin`, replacing the old `.control.json`).
    /// Lazily created on first flush and kept for the camera's life so the
    /// seqlock counter keeps advancing across writes (recreating it would
    /// reset the counter and the plugin's change detection would miss a write).
    /// A `std::sync::Mutex`, not the tokio one — the mmap write is synchronous
    /// and never held across an await.
    pub control_block: std::sync::Mutex<Option<ControlBlock>>,
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

    /// Remove a *specific, still-alive* track — the explicit `Unsubscribe`
    /// path of the dynamic slot model. Unlike the consume loop's pruner
    /// (which collects Weaks that have gone dead because a peer dropped),
    /// here the track Arc is still owned by the peer as a reusable slot, so
    /// it won't appear dead; we match it by pointer, drop its Weak, and
    /// decrement the subscriber count ourselves. The fan-out pruner therefore
    /// never double-counts it (it's already out of the list). Returns the
    /// subscriber count after the decrement; removing a track this camera
    /// isn't feeding is a no-op.
    pub async fn remove_track(&self, track: &Arc<TrackLocalStaticSample>) -> usize {
        let mut tracks = self.tracks.write().await;
        let before = tracks.len();
        tracks.retain(|w| !w.upgrade().is_some_and(|t| Arc::ptr_eq(&t, track)));
        let removed = before - tracks.len();
        drop(tracks);
        if removed > 0 {
            self.subscribers.fetch_sub(removed, Ordering::AcqRel);
        }
        self.subscribers.load(Ordering::Acquire)
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
    /// Capped ring of every distinct `global.status.json` snapshot seen
    /// since the last `reset_run_logs()`. Powers the harness's
    /// `GET /dumpLogs` endpoint so perf runs can replay the full
    /// kspFps / shedLevel / per-camera-render-size timeline without
    /// polling during the measurement window.
    ///
    /// We dedupe on whole-struct equality so re-reads between plugin
    /// writes (the consume loop polls faster than the plugin writes)
    /// don't produce duplicate entries. Cap is generous (12k = ~3.5h
    /// at 1Hz) so a full test never overflows; in practice the harness
    /// resets before each run.
    status_log: Mutex<Vec<StatusLogEntry>>,
    /// `Instant` anchor for `StatusLogEntry::t_ms`. Set at registry
    /// construction so timestamps are relative to sidecar startup —
    /// not wall-clock, immune to NTP slews mid-run.
    epoch: Instant,
    /// Profiling override: when true, every live camera is kept
    /// `subscribed` (so the plugin renders + reports telemetry) even with
    /// no peer attached. Lets `POST /profile/render` exercise the real
    /// per-camera render/readback cost from any scene without a streaming
    /// client. Render-only by design — no peer means no tracks, so the
    /// consume loop never encodes; we measure the plugin's KSP-frametime
    /// cost cleanly, not the sidecar encode path.
    force_render: AtomicBool,
}

const STATUS_LOG_CAP: usize = 12_288;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StatusLogEntry {
    /// Monotonic sidecar timestamp (millis since the sidecar's first
    /// status read). Stable across the test window even if wall-clock
    /// shifts; doesn't try to encode KSP mission time.
    pub t_ms: u64,
    pub status: GlobalStatusFileExport,
}

/// Public re-export of the on-disk status shape so external consumers
/// (the harness) can deserialize each `/dumpLogs` entry without
/// depending on internal field privacy. Same shape as the on-disk
/// `global.status.json`.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GlobalStatusFileExport {
    pub ksp_fps: f32,
    pub shed_level: u32,
    #[serde(default)]
    pub cameras: Vec<PerCameraStatusExport>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PerCameraStatusExport {
    pub flight_id: u32,
    pub render_width: u32,
    pub render_height: u32,
    pub operator_width: u32,
    pub operator_height: u32,
    #[serde(default)]
    pub layers: Vec<Layer>,
    #[serde(default)]
    pub operator_layers: Vec<Layer>,
    #[serde(default)]
    pub fov: f32,
    #[serde(default)]
    pub pan_yaw: f32,
    #[serde(default)]
    pub pan_pitch: f32,
}

impl From<&GlobalStatusFile> for GlobalStatusFileExport {
    fn from(s: &GlobalStatusFile) -> Self {
        Self {
            ksp_fps: s.ksp_fps,
            shed_level: s.shed_level,
            cameras: s
                .cameras
                .iter()
                .map(|c| PerCameraStatusExport {
                    flight_id: c.flight_id,
                    render_width: c.render_width,
                    render_height: c.render_height,
                    operator_width: c.operator_width,
                    operator_height: c.operator_height,
                    layers: c.layers.clone(),
                    operator_layers: c.operator_layers.clone(),
                    fov: c.fov,
                    pan_yaw: c.pan_yaw,
                    pan_pitch: c.pan_pitch,
                })
                .collect(),
        }
    }
}

impl CameraRegistry {
    pub fn new(shm_dir: PathBuf, ring_cfg: MmapRingConfig) -> Self {
        Self {
            shm_dir,
            ring_cfg,
            cameras: RwLock::new(HashMap::new()),
            last_status: Mutex::new(None),
            status_log: Mutex::new(Vec::new()),
            epoch: Instant::now(),
            force_render: AtomicBool::new(false),
        }
    }

    /// The shm directory the plugin writes rings + `global.status.json` into.
    /// Used by `GET /profile` to serve the latest telemetry snapshot.
    pub fn shm_dir(&self) -> &std::path::Path {
        self.shm_dir.as_path()
    }

    /// Profiling override (see the field). `POST /profile/render` toggles it.
    pub fn set_force_render(&self, on: bool) {
        self.force_render.store(on, Ordering::Release);
    }

    pub fn force_render(&self) -> bool {
        self.force_render.load(Ordering::Acquire)
    }

    /// Per-tick subscription bookkeeping. Normally: a camera that has lost its
    /// last peer-track is flushed `subscribed=false` so the plugin sleeps it.
    /// Under the force-render profiling override, every live camera is instead
    /// kept `subscribed=true` so it renders without a peer. `set_subscribed` is
    /// idempotent (no flush without a transition), so calling this each consume
    /// tick is cheap. Replaces the old free-standing `maybe_sleep_idle_cameras`
    /// so the force-render branch is unit-testable.
    pub async fn refresh_idle_subscriptions(&self, cameras: &[Arc<CameraState>]) {
        let force = self.force_render();
        for cam in cameras {
            if cam.destroyed.load(Ordering::Acquire) {
                continue;
            }
            if force {
                self.set_subscribed(cam.flight_id, true).await;
            } else if cam.subscribers.load(Ordering::Acquire) == 0 {
                self.set_subscribed(cam.flight_id, false).await;
            }
        }
    }

    /// Clear the run-logs ring. The harness fires `POST /dumpLogs/reset`
    /// at the start of each run so it gets a clean window unaffected by
    /// pre-run sidecar warmup.
    pub async fn reset_run_logs(&self) {
        self.status_log.lock().await.clear();
    }

    /// Snapshot of every buffered status entry since the last reset.
    /// The harness fires `GET /dumpLogs` after `[BASELINE-DONE]` and
    /// writes the result alongside the Telemachus / kOS log slices.
    pub async fn dump_run_logs(&self) -> Vec<StatusLogEntry> {
        self.status_log.lock().await.clone()
    }

    /// Walk `shm_dir`, attach any new `<flight_id>.ring` files, poll
    /// info.json for existing cameras (to catch `lifecycle: "destroyed"`
    /// tombstones written by the plugin), and drop rings that have
    /// disappeared under normal teardown. Tolerant of the directory not
    /// yet existing — the plugin may not have created it.
    ///
    /// Returns the set of flight IDs that transitioned to `Destroyed`
    /// this tick and have not yet had their broadcast sent. Callers
    /// (the consume loop) use this to fan `camera-state-changed` out to
    /// all peers, then call `acknowledge_destruction` to delete the
    /// tombstone and mark the broadcast as sent.
    pub async fn rescan(&self) -> Vec<u32> {
        let mut found: HashMap<u32, PathBuf> = HashMap::new();
        let mut entries = match tokio::fs::read_dir(&self.shm_dir).await {
            Ok(e) => e,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Vec::new(),
            Err(e) => {
                warn!(dir = %self.shm_dir.display(), error = %e, "rescan read_dir failed");
                return Vec::new();
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

        // --- Step 1: poll info.json for EXISTING cameras to catch destroyed
        // tombstones BEFORE the ring-disappearance retain runs.
        //
        // Ordering from the protocol doc:
        //   plugin writes `lifecycle: "destroyed"` → closes ring → deletes ring file
        //
        // The ring file is gone by the time we see it. If we checked ring
        // existence first, we'd drop the camera silently without ever reading
        // the tombstone. Re-reading info.json for every known camera at ~1Hz
        // is cheap (6 files, ~200 bytes each).
        let mut newly_destroyed: Vec<u32> = Vec::new();
        for (id, cam) in cameras.iter() {
            if cam.destroyed.load(Ordering::Acquire) {
                // Already flagged — check if broadcast is still pending.
                if !cam.destruction_broadcast_sent.load(Ordering::Acquire) {
                    newly_destroyed.push(*id);
                }
                continue;
            }
            let manifest = read_manifest(&self.shm_dir, *id).await;
            if manifest.lifecycle == CameraLifecycle::Destroyed {
                cam.destroyed.store(true, Ordering::Release);
                warn!(
                    flight_id = id,
                    part_title = %cam.part_title,
                    "camera part destroyed — stopping encode, notifying peers",
                );
                newly_destroyed.push(*id);
            }
        }

        // --- Step 2: attach new rings (skips destroyed cameras; they have
        // no ring file, so they won't appear in `found`).
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
                            destroyed: AtomicBool::new(false),
                            destruction_broadcast_sent: AtomicBool::new(false),
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
                            control_block: std::sync::Mutex::new(None),
                        }),
                    );
                }
                Err(e) => {
                    warn!(flight_id = id, path = %path.display(), error = %e, "failed to open ring");
                }
            }
        }

        // --- Step 3: drop cameras that have disappeared under normal
        // teardown (ring file gone AND not a pending destroyed camera).
        // Destroyed cameras are retained so the UI can display SIGNAL LOST.
        let mut removed = Vec::new();
        cameras.retain(|id, cam| {
            if cam.destroyed.load(Ordering::Acquire) {
                // Destroyed — keep in registry (visible in /cameras as destroyed).
                return true;
            }
            let still = found.contains_key(id);
            if !still {
                removed.push(*id);
            }
            still
        });
        for id in removed {
            info!(flight_id = id, "camera ring removed (normal teardown)");
        }

        newly_destroyed
    }

    /// Mark the destruction broadcast as sent and delete the info.json
    /// tombstone. Called by the consume loop after broadcasting
    /// `camera-state-changed` for a destroyed camera.
    ///
    /// Deleting the file is our acknowledgment to the plugin that we've
    /// seen the tombstone; the protocol requires this because the plugin
    /// may exit immediately after writing it.
    pub async fn acknowledge_destruction(&self, flight_id: u32) {
        if let Some(cam) = self.cameras.read().await.get(&flight_id) {
            cam.destruction_broadcast_sent
                .store(true, Ordering::Release);
        }
        let path = self.shm_dir.join(format!("{flight_id}.info.json"));
        match tokio::fs::remove_file(&path).await {
            Ok(()) => info!(flight_id, "info.json tombstone deleted"),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                // Already deleted (e.g. by a previous sidecar instance).
            }
            Err(e) => {
                warn!(flight_id, error = %e, "failed to delete info.json tombstone");
            }
        }
    }

    pub async fn list(&self) -> Vec<CameraInfo> {
        let cams = self.cameras.read().await;
        let mut out: Vec<_> = cams
            .values()
            .map(|s| CameraInfo {
                flight_id: s.flight_id,
                lifecycle: if s.destroyed.load(Ordering::Acquire) {
                    CameraLifecycle::Destroyed
                } else {
                    CameraLifecycle::Active
                },
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
                lifecycle: if cam.destroyed.load(Ordering::Acquire) {
                    CameraLifecycle::Destroyed
                } else {
                    CameraLifecycle::Active
                },
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

        // Push the snapshot to the run-log ring iff it's actually new
        // (the consume loop polls way faster than the plugin writes).
        // Capped at STATUS_LOG_CAP entries; oldest evicted when full.
        if last.as_ref() != Some(&parsed) {
            let entry = StatusLogEntry {
                t_ms: self.epoch.elapsed().as_millis() as u64,
                status: GlobalStatusFileExport::from(&parsed),
            };
            let mut log = self.status_log.lock().await;
            if log.len() >= STATUS_LOG_CAP {
                log.remove(0);
            }
            log.push(entry);
        }

        *last = Some(parsed);
        delta
    }

    /// Flush a camera's in-memory `ControlState` into its shared-memory
    /// control block (`<flight_id>.control.bin`), published under a seqlock
    /// the plugin reads each frame. Replaces the old JSON-file-and-rename
    /// path; the block is collision-proof (a monotonic seq, not mtime) so a
    /// rapid rate=0 stop right after a drag can't be missed. The block is
    /// created on first flush and persists for the camera's life so the
    /// seqlock counter keeps advancing. No-op if the camera is gone.
    pub async fn flush_control(&self, flight_id: u32, state: &ControlState) -> std::io::Result<()> {
        let Some(cam) = self.get(flight_id).await else {
            return Ok(());
        };
        let mut guard = cam.control_block.lock().unwrap_or_else(|e| e.into_inner());
        if guard.is_none() {
            let path = self.shm_dir.join(format!("{flight_id}.control.bin"));
            *guard = Some(ControlBlock::create(&path)?);
        }
        guard.as_mut().expect("just created if absent").write(state);
        Ok(())
    }

    /// Update the `subscribed` flag in the cam's ControlState and flush
    /// the whole struct to `<flight_id>.control.json`. Called from the
    /// peer subscribe path (true on add_track) and the consume loop's
    /// dead-track pruner (false on last-release). The plugin polls
    /// control.json by mtime, so the next tick after this flush is
    /// when the camera wakes / sleeps.
    pub async fn set_subscribed(&self, flight_id: u32, subscribed: bool) {
        let Some(cam) = self.get(flight_id).await else {
            return;
        };
        let snapshot = {
            let mut ctrl = cam.control.lock().await;
            if ctrl.subscribed == subscribed {
                return; // no transition, no flush
            }
            ctrl.subscribed = subscribed;
            ctrl.clone()
        };
        if let Err(e) = self.flush_control(flight_id, &snapshot).await {
            warn!(flight_id, error = %e, "set_subscribed flush failed");
        }
    }

    /// Zero a camera's persistent pan/zoom rates (the disconnect deadman).
    /// Called from the dead-peer cleanup so a browser that dropped mid-hold
    /// doesn't leave the camera drifting to its travel limit. Writes
    /// `Some(0.0)` (an explicit stop) rather than `None` (which the plugin
    /// reads as "unchanged" and would leave the last non-zero rate running).
    /// Leaves the absolute `pan_yaw`/`fov` untouched so the camera holds its
    /// last framed position. No-op + no flush if all three are already
    /// stopped (`Some(0.0)` or `None`).
    pub async fn zero_rates(&self, flight_id: u32) {
        let Some(cam) = self.get(flight_id).await else {
            return;
        };
        let snapshot = {
            let mut ctrl = cam.control.lock().await;
            let already_stopped = |r: Option<f32>| r.unwrap_or(0.0) == 0.0;
            if already_stopped(ctrl.pan_yaw_rate)
                && already_stopped(ctrl.pan_pitch_rate)
                && already_stopped(ctrl.zoom_rate)
            {
                return; // nothing to stop, no flush
            }
            ctrl.pan_yaw_rate = Some(0.0);
            ctrl.pan_pitch_rate = Some(0.0);
            ctrl.zoom_rate = Some(0.0);
            ctrl.clone()
        };
        if let Err(e) = self.flush_control(flight_id, &snapshot).await {
            warn!(flight_id, error = %e, "zero_rates flush failed");
        }
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
        lifecycle: CameraLifecycle::Active,
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

#[cfg(test)]
mod tests {
    use super::*;

    /// `read_manifest` returns `Destroyed` when info.json has
    /// `lifecycle: "destroyed"` — the tombstone the plugin writes on part
    /// destruction. This is the primitive the rescan detection loop calls
    /// on every existing camera at ~1Hz.
    #[tokio::test]
    async fn read_manifest_detects_destroyed_lifecycle() {
        let dir = tempfile::tempdir().expect("tempdir");
        let shm = dir.path();
        let flight_id: u32 = 9_001;

        let content = format!(
            r#"{{"lifecycle":"destroyed","flight_id":{flight_id},"part_name":"testPart","part_title":"Test Camera","camera_name":"Cam 1","vessel_name":"Kerbal X","supports_zoom":false,"fov":60.0,"fov_min":10.0,"fov_max":90.0,"supports_pan":false,"pan_yaw_min":0.0,"pan_yaw_max":0.0,"pan_pitch_min":0.0,"pan_pitch_max":0.0}}"#,
        );
        tokio::fs::write(shm.join(format!("{flight_id}.info.json")), content)
            .await
            .unwrap();

        let manifest = read_manifest(shm, flight_id).await;
        assert_eq!(
            manifest.lifecycle,
            CameraLifecycle::Destroyed,
            "read_manifest should return Destroyed when info.json has lifecycle=destroyed"
        );
    }

    /// Missing info.json (normal teardown — plugin deletes it entirely)
    /// defaults to `Active`. The sidecar then drops the camera silently
    /// via the ring-disappearance retain path; no broadcast needed.
    #[tokio::test]
    async fn read_manifest_missing_defaults_to_active() {
        let dir = tempfile::tempdir().expect("tempdir");
        let shm = dir.path();
        // No file written.
        let manifest = read_manifest(shm, 9_002).await;
        assert_eq!(
            manifest.lifecycle,
            CameraLifecycle::Active,
            "missing info.json should default to Active (normal teardown path)"
        );
    }

    /// Unknown / future lifecycle values that serde can't map to a known
    /// variant cause a parse error. The error path returns the `empty`
    /// manifest (lifecycle = Active) for forward compatibility.
    #[tokio::test]
    async fn unknown_lifecycle_defaults_to_active() {
        let dir = tempfile::tempdir().expect("tempdir");
        let shm = dir.path();
        let flight_id: u32 = 9_003;

        let content = format!(
            r#"{{"lifecycle":"future-unknown-value","flight_id":{flight_id},"part_name":"","part_title":"","camera_name":"","vessel_name":"","supports_zoom":false,"fov":0.0,"fov_min":0.0,"fov_max":0.0,"supports_pan":false,"pan_yaw_min":0.0,"pan_yaw_max":0.0,"pan_pitch_min":0.0,"pan_pitch_max":0.0}}"#,
        );
        tokio::fs::write(shm.join(format!("{flight_id}.info.json")), content)
            .await
            .unwrap();

        let manifest = read_manifest(shm, flight_id).await;
        assert_eq!(
            manifest.lifecycle,
            CameraLifecycle::Active,
            "unknown lifecycle in info.json should be treated as Active"
        );
    }

    /// `add_track` / `remove_track` drive the subscriber count that gates
    /// encoding — the efficiency premise of the dynamic slot model. The
    /// transitions the slot pool relies on: subscribe -> 1, a second slot on
    /// the same camera -> 2, unsubscribe one -> 1, last viewer -> 0; and
    /// removing a track the camera isn't feeding is a no-op.
    #[tokio::test]
    async fn subscriber_count_transitions() {
        use webrtc::api::media_engine::MIME_TYPE_H264;
        use webrtc::rtp_transceiver::rtp_codec::RTCRtpCodecCapability;

        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        MmapFrameRing::create(&dir.path().join("1.ring"), cfg).expect("create ring");
        let registry = CameraRegistry::new(dir.path().to_path_buf(), cfg);
        registry.rescan().await;
        let cam = registry.get(1).await.expect("camera attached from ring");

        let mk = || {
            Arc::new(TrackLocalStaticSample::new(
                RTCRtpCodecCapability {
                    mime_type: MIME_TYPE_H264.to_owned(),
                    ..Default::default()
                },
                "video".to_owned(),
                "stream".to_owned(),
            ))
        };
        let a = mk();
        let b = mk();

        assert_eq!(cam.add_track(a.clone()).await, 1, "first subscribe");
        assert_eq!(
            cam.add_track(b.clone()).await,
            2,
            "second slot, same camera"
        );
        assert_eq!(cam.remove_track(&a).await, 1, "unsubscribe one slot");
        assert_eq!(cam.remove_track(&b).await, 0, "last viewer leaves");
        assert_eq!(
            cam.remove_track(&a).await,
            0,
            "removing an unbound track is a no-op"
        );
    }

    // The force-render profiling override keeps a peerless camera subscribed
    // (so the plugin renders + reports telemetry), and clearing it releases the
    // camera again — the cleanup path POST /profile/render?on=false relies on.
    #[tokio::test]
    async fn force_render_overrides_idle_subscription() {
        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        MmapFrameRing::create(&dir.path().join("1.ring"), cfg).expect("create ring");
        let registry = CameraRegistry::new(dir.path().to_path_buf(), cfg);
        registry.rescan().await;
        let cam = registry.get(1).await.expect("camera attached from ring");
        let cams = vec![cam.clone()];

        // Default: no force, no peer-tracks → camera stays unsubscribed.
        registry.refresh_idle_subscriptions(&cams).await;
        assert!(
            !cam.control.lock().await.subscribed,
            "idle camera is not subscribed by default"
        );

        // Force-render on → subscribed even with zero subscribers (renders for
        // profiling, no peer needed).
        registry.set_force_render(true);
        registry.refresh_idle_subscriptions(&cams).await;
        assert!(
            cam.control.lock().await.subscribed,
            "force_render keeps the camera subscribed without a peer"
        );

        // Force-render off → cleanup releases the camera again.
        registry.set_force_render(false);
        registry.refresh_idle_subscriptions(&cams).await;
        assert!(
            !cam.control.lock().await.subscribed,
            "clearing force_render releases the camera (cleanup path)"
        );
    }

    /// The persistent rate fields serialise to the camelCase keys the
    /// plugin parses (`panYawRate`/`panPitchRate`/`zoomRate`), and `None`
    /// (the "unchanged" sentinel) is omitted so the plugin keeps the last
    /// rate rather than reading a spurious zero.
    #[test]
    fn control_state_serialises_rate_fields_camel_case() {
        let ctrl = ControlState {
            subscribed: true,
            pan_yaw_rate: Some(0.5),
            pan_pitch_rate: Some(-1.0),
            zoom_rate: Some(0.0),
            ..Default::default()
        };
        let s = serde_json::to_string(&ctrl).unwrap();
        assert!(s.contains("\"panYawRate\":0.5"), "got {s}");
        assert!(s.contains("\"panPitchRate\":-1"), "got {s}");
        assert!(s.contains("\"zoomRate\":0"), "got {s}");

        // None on every rate => keys omitted (plugin treats absence as
        // "rate unchanged"). The seq counters are always present (the
        // plugin keys absolute-apply off their *change*, so absence would
        // be ambiguous).
        let blank = ControlState::default();
        let s = serde_json::to_string(&blank).unwrap();
        assert!(!s.contains("panYawRate"), "got {s}");
        assert!(!s.contains("zoomRate"), "got {s}");
        assert!(s.contains("\"panSeq\":0"), "got {s}");
        assert!(s.contains("\"fovSeq\":0"), "got {s}");
    }

    /// The disconnect deadman: `zero_rates` writes an explicit `Some(0.0)`
    /// stop on every rate axis (not `None`, which the plugin would read as
    /// "unchanged" and keep running), flushes it to the control file, and
    /// leaves the absolute pan/fov targets untouched.
    #[tokio::test]
    async fn zero_rates_writes_explicit_stop() {
        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        MmapFrameRing::create(&dir.path().join("1.ring"), cfg).expect("create ring");
        let registry = CameraRegistry::new(dir.path().to_path_buf(), cfg);
        registry.rescan().await;
        let cam = registry.get(1).await.expect("camera attached from ring");

        // Arrange: a camera mid-hold with a non-zero rate and a framed
        // absolute pan we expect to survive the deadman.
        {
            let mut ctrl = cam.control.lock().await;
            ctrl.pan_yaw_rate = Some(0.8);
            ctrl.zoom_rate = Some(-0.5);
            ctrl.pan_yaw = Some(12.0);
            ctrl.pan_seq = 3;
            ctrl.fov_seq = 4;
        }

        registry.zero_rates(1).await;

        let ctrl = cam.control.lock().await;
        assert_eq!(ctrl.pan_yaw_rate, Some(0.0));
        assert_eq!(ctrl.pan_pitch_rate, Some(0.0));
        assert_eq!(ctrl.zoom_rate, Some(0.0));
        assert_eq!(ctrl.pan_yaw, Some(12.0), "absolute pan must be untouched");
        // The deadman must NOT bump the absolute seqs — otherwise the plugin
        // would re-apply the stale absolute and snap back to it on the very
        // stop the deadman is issuing.
        assert_eq!(ctrl.pan_seq, 3, "deadman must not bump pan_seq");
        assert_eq!(ctrl.fov_seq, 4, "deadman must not bump fov_seq");

        // The stop reached the shared-memory control block (the plugin reads
        // it each frame). Decode the raw bytes and confirm the zeroed rates
        // and the surviving absolute pan / seqs.
        let bytes = tokio::fs::read(dir.path().join("1.control.bin"))
            .await
            .expect("control block flushed");
        let snap = crate::shared_mem::control::decode(&bytes).expect("decodes");
        assert_eq!(snap.pan_yaw_rate, Some(0.0));
        assert_eq!(snap.pan_pitch_rate, Some(0.0));
        assert_eq!(snap.zoom_rate, Some(0.0));
        assert_eq!(snap.pan_yaw, Some(12.0), "absolute pan must be untouched");
        assert_eq!(snap.pan_seq, 3, "deadman must not bump pan_seq");
        assert_eq!(snap.fov_seq, 4, "deadman must not bump fov_seq");
    }
}
