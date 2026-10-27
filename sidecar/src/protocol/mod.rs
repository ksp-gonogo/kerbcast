//! WebRTC data-channel protocol between the sidecar and a streaming
//! client (a browser, or a future Kotlin or Swift consumer).
//!
//! Wire format: JSON-per-message over an `RTCDataChannel` opened by the
//! browser *after* the SDP exchange and track negotiation are complete.
//! The data channel exists for per-camera operational state — layer
//! masks, render-size, keyframe requests, future zoom — that the
//! receiving client wants to influence in real time without
//! renegotiating SDP.
//!
//! Single source of truth: this module's types carry `#[typeshare]`
//! annotations. The `typeshare` CLI reads them and emits TypeScript
//! (and Kotlin / Swift / Scala / Go when those consumers materialise)
//! into `client-sdk/`. The CI workflow runs typeshare on every push to
//! `sidecar/src/protocol/**` and publishes the resulting npm package as
//! `@kerbcast/protocol`.
//!
//! All types use camelCase JSON keys to match TypeScript convention.
//! Enums use adjacent tagging (`tag = "type", content = "content"`)
//! because that's the discriminator form `typeshare` can faithfully
//! mirror in TypeScript:
//!
//!   { "type": "set-layers", "content": { "flightId": 123, "layers": [...] } }
//!
//! Unit variants (e.g. `Hello`) emit just `{ "type": "hello" }`.

use serde::{Deserialize, Serialize};
use typeshare::typeshare;

/// Layer mask. Mirrors `Kerbcast.CameraLayers` on the plugin side.
/// Receiving clients use this for both the rendered-layer status reports
/// and per-camera layer requests.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "UPPERCASE")]
pub enum Layer {
    Near,
    Scaled,
    Far,
    Galaxy,
}

/// Viewer-selectable resolution preset. Each maps to a fraction of the
/// operator-configured render size (the settings.cfg Width/Height ceiling):
/// full = 1.0, threeQuarter = 0.75, half = 0.5, quarter = 0.25. The steps
/// mirror the plugin's shed-table resolution ladder so the viewer clamp and
/// the adaptive controller move on the same grid, and a preset can never
/// exceed the ring's allocated max (every scale is <= 1.0 of the ceiling).
///
/// Presets, not freeform WxH: a fixed menu keeps the unauthenticated
/// viewer-writable surface tiny and aspect-correct by construction.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum QualityPreset {
    Full,
    ThreeQuarter,
    Half,
    Quarter,
}

impl QualityPreset {
    /// Plugin-side viewer level this preset maps to (index into the
    /// plugin's `QualityClamp.ViewerScales` table).
    pub fn viewer_level(self) -> u32 {
        match self {
            QualityPreset::Full => 0,
            QualityPreset::ThreeQuarter => 1,
            QualityPreset::Half => 2,
            QualityPreset::Quarter => 3,
        }
    }

    pub fn from_viewer_level(level: u32) -> Option<Self> {
        match level {
            0 => Some(QualityPreset::Full),
            1 => Some(QualityPreset::ThreeQuarter),
            2 => Some(QualityPreset::Half),
            3 => Some(QualityPreset::Quarter),
            _ => None,
        }
    }

    /// Fraction of the operator render size this preset targets.
    pub fn scale(self) -> f32 {
        match self {
            QualityPreset::Full => 1.0,
            QualityPreset::ThreeQuarter => 0.75,
            QualityPreset::Half => 0.5,
            QualityPreset::Quarter => 0.25,
        }
    }
}

/// Lifecycle state of a camera. Transmitted in `CameraState` so clients
/// can react to part destruction without a new message type.
///
/// Destroyed is a terminal state — the sidecar never transitions a camera
/// back to Active after writing the tombstone.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub enum CameraLifecycle {
    #[default]
    Active,
    Destroyed,
}

/// How hard a camera is currently being captured. Reported per camera in
/// `CameraState` so a consumer renders what is actually happening rather than
/// predicting it from delay arithmetic.
///
/// `Background` and `Off` only occur when background capture is enabled; with it off
/// (the default) a subscribed camera is `Full` and an unsubscribed one is `Off`,
/// which is exactly today's behaviour under the new names.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub enum CaptureState {
    /// Capturing at the primary rate: an ordinary live feed.
    #[default]
    Full,
    /// Promoted and on the way to the primary rate, but not there yet — either
    /// awaiting the forced keyframe or still rate-limited by the capture budget.
    Ramping,
    /// Background capture: recent but sparse. A consumer should present this as
    /// a reduced-rate channel, not as a live feed.
    Background,
    /// Not being captured at all — unsubscribed, or the background layer has
    /// been shed under load. A consumer must NOT keep presenting the last frame
    /// as though it were a live low-rate channel.
    Off,
}

/// Distinguishes an existing Hullcam part camera (`Part`) from a
/// per-kerbal face camera (`Kerbal`) and a fixed command
/// camera watching a craft from a CommNet station (`Command`).
///
/// Consumers must treat a value they do not recognise as "some other kind of
/// camera" rather than failing: new kinds are added without a protocol bump.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub enum CameraKind {
    #[default]
    Part,
    Kerbal,
    Command,
}

/// Where a command camera stands, what it watches and what it could watch.
/// Present only when `CameraState.kind == Command`.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandCentreInfo {
    /// `"ground:"` followed by `node_name`.
    ///
    /// @deprecated Use `site_key` and `site_kind`.
    pub station_id: String,
    /// The site's node name: a CommNet home's `nodeName` (KSC's is `"KSC"`),
    /// or the generated key of a launch site, surface point or craft site.
    pub node_name: String,
    /// Stable key of the place the lens stands; the same as `node_name`, and
    /// unchanged when the craft a vessel site rides docks.
    #[serde(default)]
    pub site_key: String,
    /// What kind of place `site_key` names.
    #[serde(default)]
    pub site_kind: SiteKind,
    /// For a `vessel` site, `Vessel.persistentId` of the craft the lens rides
    /// now, which follows it through docking; 0 for every other kind.
    #[serde(default)]
    pub vantage_persistent_id: u32,
    /// The station's name as the player reads it in game, or `node_name` when
    /// it has none.
    pub display_name: String,
    /// Body the station sits on.
    pub body: String,
    pub latitude: f64,
    pub longitude: f64,
    /// Lens altitude above sea level in metres: the station's own altitude
    /// plus the plugin's configured offset.
    pub altitude: f64,
    /// How the camera aims, as set by `SetCommandAim`. `None` for its
    /// default: the active craft in flight, the configured craft at the Space
    /// Center.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub aim_kind: Option<CommandAimKind>,
    /// `Vessel.persistentId` of the camera's subject, the craft it draws,
    /// brackets and reads out; 0 for none. A `vessel` aim's craft, an `orbit`
    /// or `positionAt` aim's subject, otherwise the default craft.
    pub subject_persistent_id: u32,
    /// Station to the subject in metres, to 3 significant figures. `None`
    /// without a position for it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub range_m: Option<f64>,
    /// The Tracking Station's level, 1-based (stock 1..=3). The highest level
    /// outside career.
    pub tracking_level: u32,
    /// UT the subject, range and targets list describe: the first status
    /// write that showed them. A delayed client shows them only once the
    /// frame it displays reaches this UT. Absent from plugins and sidecars
    /// older than 1.12.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ut: Option<f64>,
    /// Every craft the camera could watch, eligible first, then nearest
    /// first. Any of them can be aimed at with a `vessel` aim; eligibility
    /// only reports what the tracking network would allow. A control channel
    /// message has a size limit, so with hundreds of craft the list over it
    /// is cut from the end and `targets_omitted` counts what was left out.
    /// A cut list keeps the feed's subject, appended at the end out of the
    /// eligible-then-nearest order when it fell in the cut part.
    pub targets: Vec<CommandTarget>,
    /// How many craft were cut from the end of `targets` to fit the message.
    /// Absent when none were.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub targets_omitted: Option<u32>,
    /// True for a camera made at runtime (`CreateCommandCamera`, or the
    /// plugin's `KerbcastControl.CreateCommandCamera`), false for the one
    /// from the game's settings. Only created cameras can be destroyed.
    /// Older sidecars omit it.
    #[serde(default)]
    pub created: bool,
    /// The `token` of the `CreateCommandCamera` request that made this
    /// camera, so its creator can find it in the camera list. `None` for the
    /// settings camera and for cameras created in game.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token: Option<String>,
}

/// What kind of place a command camera's site is.
///
/// Consumers must treat a value they do not recognise as some other kind of
/// site: new kinds are added without a protocol bump.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub enum SiteKind {
    /// A CommNet ground station.
    #[default]
    Station,
    /// Beside a launch site.
    LaunchSite,
    /// A point on a body's surface.
    Surface,
    /// Riding a craft.
    Vessel,
}

/// Which kind of place a created command camera stands at.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum CommandVantageKind {
    /// Beside a launch site, the way the settings camera stands.
    LaunchSite,
    /// At a CommNet ground station.
    Station,
    /// At any point on a body's surface.
    Surface,
    /// Riding a craft.
    Vessel,
}

/// Where a created command camera stands. `kind` says which fields apply;
/// absent optional fields take the defaults given on each.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandVantage {
    pub kind: CommandVantageKind,
    /// `launchSite`: the site's name (`"LaunchPad"`, `"Runway"`, a Making
    /// History or Kerbal Konstructs site). Absent or empty: the active
    /// craft's launch site, else the stock pad.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub site: Option<String>,
    /// `launchSite`: metres from the site to the lens. Default 500.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub distance: Option<f64>,
    /// `launchSite`: compass bearing in degrees from the site to the lens.
    /// Default 180 (due south).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub bearing: Option<f64>,
    /// Metres above the ground under the lens (above the sea over water);
    /// for `station`, above the station itself. Defaults: 5 for `launchSite`
    /// and `surface`, 30 for `station`. Unused for `vessel`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub height: Option<f64>,
    /// `station`: the CommNet ground station's node name, e.g. `"KSC"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub station: Option<String>,
    /// `surface`: the body's name, e.g. `"Kerbin"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub body: Option<String>,
    /// `surface`: degrees, -90..=90.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub latitude: Option<f64>,
    /// `surface`: degrees.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub longitude: Option<f64>,
    /// `vessel`: the craft's `Vessel.persistentId`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub persistent_id: Option<u32>,
}

/// How a command camera aims.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum CommandAimKind {
    /// Follows a craft, which is the subject.
    Vessel,
    /// Follows a conic the game propagates.
    Orbit,
    /// Holds a point on a body, carried round by its rotation.
    Surface,
    /// Follows a position the caller propagated, extrapolated linearly.
    PositionAt,
    /// Holds a direction from the camera's local horizon.
    Direction,
}

/// Where a command camera points. `kind` says which fields apply. With no
/// subject (`surface`, `direction`, or `orbit` and `positionAt` without
/// `subjectPersistentId`), the camera's default craft stays the subject.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandAim {
    pub kind: CommandAimKind,
    /// `vessel`: the craft's `Vessel.persistentId`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub persistent_id: Option<u32>,
    /// `orbit`, `surface` and `positionAt`: the reference body's name.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub body: Option<String>,
    /// `orbit`: degrees.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub inclination: Option<f64>,
    /// `orbit`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub eccentricity: Option<f64>,
    /// `orbit`: metres.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub semi_major_axis: Option<f64>,
    /// `orbit`: longitude of the ascending node, degrees.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub lan: Option<f64>,
    /// `orbit`: degrees.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub argument_of_periapsis: Option<f64>,
    /// `orbit`: radians.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mean_anomaly_at_epoch: Option<f64>,
    /// `orbit`: UT the mean anomaly is given at.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub epoch: Option<f64>,
    /// `surface`: degrees, -90..=90.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub latitude: Option<f64>,
    /// `surface`: degrees.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub longitude: Option<f64>,
    /// `surface`: metres above sea level. Default 0.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub altitude: Option<f64>,
    /// `positionAt`: UT of the sample.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ut: Option<f64>,
    /// `positionAt`: `[x, y, z]` metres from the body centre at `ut`, z up, in
    /// KSP's fixed inertial frame, the one `Orbit.OrbitFrame` and
    /// `Orbit.UpdateFromFixedVectors` work in: always
    /// `Planetarium.Zup.LocalToWorld(orbit.getRelativePositionAtUT(ut))`.
    /// Propagated ballistically under the body's gravity, no thrust.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub position: Option<Vec<f64>>,
    /// `positionAt`: `[x, y, z]` metres per second in the same frame:
    /// `Planetarium.Zup.LocalToWorld(orbit.getOrbitalVelocityAtUT(ut))`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub velocity: Option<Vec<f64>>,
    /// `direction`: compass bearing in degrees from north, clockwise.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub azimuth: Option<f64>,
    /// `direction`: degrees above the horizon, -90..=90.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub elevation: Option<f64>,
    /// `orbit` and `positionAt`: the craft the path belongs to, drawn and
    /// bracketed as the subject. Absent: the default craft.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub subject_persistent_id: Option<u32>,
    /// Metres of view kept around the subject's own size when framing. Default 0.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub framing_margin_m: Option<f64>,
    /// `vessel`: accept a craft whose position is too uncertain for the view
    /// to frame, as a deliberate search of the region it may be in; without
    /// it such an aim is refused as `tooUncertain`. Ignored on other kinds.
    /// Default false.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub allow_uncertain: Option<bool>,
}

/// Whether a command camera's tracking network could watch a craft. Only
/// `NotTrackable` applies outside career (or with the tracking rules off),
/// and the world-model codes only while a command authority's world model
/// governs the feed.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum CommandTargetEligibility {
    Ok,
    /// Beyond the Tracking Station's range at its current level.
    TooFar,
    /// An unowned object, and the current level tracks none.
    LevelTooLow,
    /// An unowned object the player has found but not tracked.
    NotTrackable,
    /// The world model knows the craft but not where it is.
    NotPositioned,
    /// The world model refuses to position the craft.
    Refused,
    /// The world model's record is outside its window, or the model is invalid.
    Stale,
    /// The world model is too unsure where the craft is to frame it.
    TooUncertain,
    /// A code this sidecar does not know, from a newer plugin.
    #[serde(other)]
    Unknown,
}

/// One craft a command camera could watch.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandTarget {
    pub persistent_id: u32,
    pub vessel_name: String,
    /// KSP's `VesselType` name, e.g. `"Ship"`, `"Probe"`, `"Debris"`.
    pub vessel_type: String,
    /// Station to craft in metres, to 3 significant figures. `None` without a
    /// position for it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub range_m: Option<f64>,
    pub eligibility: CommandTargetEligibility,
    /// Short display text for `eligibility`.
    pub eligibility_text: String,
}

/// The registered command authority.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandAuthorityState {
    pub owner: String,
}

/// A full snapshot of a command track's discrete fields at the exact UT they
/// changed, so a delayed client can show the state that held at its own
/// displayed UT.
#[typeshare]
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CommandTrackEvent {
    pub ut: f64,
    pub occluded: bool,
    pub occluder: String,
    pub sunlit: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub blank: Option<String>,
    /// True for a transition the plugin expects ahead of `ut` on the current
    /// trajectory. Each write replaces the predictions; recorded events never
    /// carry it.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub predicted: bool,
}

/// What a command camera is aimed at and how well it can see
/// it. The aim point always sits at frame centre; clients draw the target
/// brackets from this. Present only on command cameras.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CommandTrackState {
    /// UT this state was evaluated at.
    pub ut: f64,
    pub has_fix: bool,
    /// Range to the aimed point in metres. `None` without a fix.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub range_m: Option<f64>,
    pub occluded: bool,
    /// Name of the occluding body when `occluded`, otherwise empty.
    pub occluder: String,
    pub sunlit: bool,
    /// Bracket half-extent in capture-frame pixels (craft extent plus the
    /// authority's framing margin), relative to `frame_height_px`. `None` without a fix.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub bracket_half_px: Option<f64>,
    pub frame_height_px: u32,
    /// False when the craft plus the framing margin would not fit even at
    /// the widest FoV.
    pub framing_fits: bool,
    /// The true craft's offset from frame centre in capture-frame pixels.
    /// Non-zero only when an authority aims away from the craft. `None`
    /// without a true fix.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub true_offset_px: Option<f64>,
    /// The registered authority. `None` when standalone.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub authority: Option<CommandAuthorityState>,
    /// Why the feed is blanked; the game sends black frames while set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub blank: Option<String>,
    /// Recent discrete transitions, oldest first.
    pub events: Vec<CommandTrackEvent>,
}

/* Fields the plugin omits take these values. Container-level so the generated
TypeScript keeps every always-serialised field required. */
impl Default for CommandTrackState {
    fn default() -> Self {
        Self {
            ut: 0.0,
            has_fix: false,
            range_m: None,
            occluded: false,
            occluder: String::new(),
            sunlit: false,
            bracket_half_px: None,
            frame_height_px: 0,
            framing_fits: true,
            true_offset_px: None,
            authority: None,
            blank: None,
            events: Vec::new(),
        }
    }
}

/// Which source a kerbal camera is currently rendering: a seated IVA
/// portrait, or the kerbal's own view while on EVA. Only meaningful when
/// `CameraState.kind == Kerbal`.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum CrewLocation {
    Seat,
    Eva,
}

/// Per-camera snapshot pushed by the sidecar on every state change
/// (operator API call, adaptive shed, vessel change). Same shape served
/// by `GET /cameras` so client UIs can treat the two interchangeably.
///
/// Capability fields (`supports_zoom`, `supports_pan`) let clients
/// render controls only for features each part actually offers — a
/// fixed-FoV camera shouldn't get a zoom slider, a non-steerable one
/// shouldn't get a pan stick.
#[typeshare]
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CameraState {
    pub flight_id: u32,
    /// Part-destruction lifecycle. `Active` for live cameras. `Destroyed`
    /// when the plugin reports the Hullcam part was destroyed in-flight
    /// (collision, overheating, decoupling beyond physics range). Once
    /// destroyed the sidecar stops forwarding frames; the client should
    /// surface a "SIGNAL LOST" overlay and keep the last frame visible.
    #[serde(default)]
    pub lifecycle: CameraLifecycle,
    /// Whether this is a Hullcam part camera or a per-kerbal face camera.
    /// Defaults to `Part` so existing payloads are unaffected.
    #[serde(default)]
    pub kind: CameraKind,
    /// How hard this camera is currently being captured. Defaults to `Full` so
    /// existing payloads and plugins predating background capture are unaffected.
    #[serde(default)]
    pub capture_state: CaptureState,
    /// The kerbal's real `ProtoCrewMember.persistentID`: the stable key a
    /// consumer correlates against. `None` for part cameras. Distinct from
    /// `flight_id`, which identifies the camera instance, not the kerbal.
    #[serde(default)]
    pub kerbal_persistent_id: Option<u32>,
    /// Which source a kerbal camera is currently rendering. `None` for
    /// part cameras.
    #[serde(default)]
    pub crew_location: Option<CrewLocation>,
    /// Station identity for a command camera. `None` for
    /// every other kind.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub command_centre: Option<CommandCentreInfo>,
    /// Aim and visibility of a command camera, from the
    /// plugin's last status write. `None` for every other kind, and before
    /// the plugin has reported one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub command_track: Option<CommandTrackState>,
    /// Server-authoritative auto-track state for a pan+zoom camera (the
    /// operator's persistent intent). `None` (the default) = not tracking.
    /// Every browser reflects this; a browser track overrides a kOS aim on the
    /// same camera (send `SetTrackTarget` with `none` to hand back to kOS).
    #[serde(default)]
    pub track_mode: TrackMode,
    pub part_name: String,
    pub part_title: String,
    pub camera_name: String,
    pub vessel_name: String,
    /// Currently-rendered layers (effective after adaptive shedding).
    pub layers: Vec<Layer>,
    /// Operator-requested layer mask — the ceiling adaptive shedding
    /// can reduce below. UIs use this to show "scaled is *requested* but
    /// not *active* right now due to fps".
    pub operator_layers: Vec<Layer>,
    /// Currently-rendered dimensions (effective after adaptive
    /// resolution downscale). Equal to `operatorWidth` × `operatorHeight`
    /// when no auto-shed is in effect.
    pub render_width: u32,
    pub render_height: u32,
    /// Operator-requested render size — ceiling adaptive downscale can
    /// reduce below.
    pub operator_width: u32,
    pub operator_height: u32,
    /// Configured capture size: the most this camera renders at (settings.cfg
    /// `MaxQuality` or `Width`/`Height`, or a kerbal face camera's square
    /// ring). Its aspect is the feed's shape: auto-resolution picks a size at
    /// this aspect and never follows the viewer's box. Absent from a sidecar
    /// that predates the field.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_width: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_height: Option<u32>,
    /// Whether the part's Hullcam module supports runtime FoV changes
    /// (i.e. it's a `MuMechModuleHullCameraZoom`, not the fixed base
    /// `MuMechModuleHullCamera`). 19 of 21 stock parts do.
    pub supports_zoom: bool,
    /// Current effective FoV in degrees.
    pub fov: f32,
    /// FoV bounds the operator can choose between. Wider than the
    /// camera's "default" — these come from the Hullcam part config.
    /// Equal to `fov` when `supports_zoom == false`.
    pub fov_min: f32,
    pub fov_max: f32,
    /// Whether the part supports pan/tilt. True on parts with a steerable
    /// mount in the plugin's `PartCapabilities` table (today: `DC.TurretCam`,
    /// yaw-only; `hc.launchcam`, yaw+pitch); false on fixed-mount parts, which
    /// are the majority. Clients hide pan controls when false.
    pub supports_pan: bool,
    pub pan_yaw: f32,
    pub pan_pitch: f32,
    pub pan_yaw_min: f32,
    pub pan_yaw_max: f32,
    pub pan_pitch_min: f32,
    pub pan_pitch_max: f32,
    /// Current encoder bitrate target in bits per second. Equals
    /// `target_bitrate_bps` after the consume loop applies receiver
    /// feedback; the two diverge briefly between a REMB arriving and
    /// the consume loop's next tick. Zero when no encoder is running
    /// yet (no subscribers).
    pub encoder_bitrate_bps: u32,
    /// Bandwidth target derived from receivers' REMB feedback (min
    /// across active subscribers). Drives the encoder when
    /// significantly diverged. Zero until the first REMB packet
    /// arrives — encoder falls back to the sidecar's CLI default.
    pub target_bitrate_bps: u32,
    /// Effective degrade level (last-writer-wins across subscribers'
    /// SetDegrade requests). 0.0 = perfect, 1.0 = max degradation. Applied
    /// alongside operator render-size + adaptive shed; the encoder
    /// multiplies its effective bitrate by `(1 - 0.7 * level)` and
    /// skips fan-out for a fraction of frames at high levels.
    pub degrade_level: f32,
    /// Viewer-requested resolution preset for this camera. Last write
    /// wins across all connected peers; this broadcast is what keeps
    /// every UI consistent. Absent/None = auto: no viewer clamp, the
    /// operator ceiling and the adaptive controller alone decide.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub viewer_quality: Option<QualityPreset>,
    /// Why the effective resolution sits below what the viewer asked
    /// for (or below the operator ceiling when no preset is set).
    /// `"throttled"` = the adaptive perf machinery is holding the
    /// camera down; the viewer target is honored again on recovery.
    /// Absent/None = the request is fully honored.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub quality_limited_by: Option<String>,
    /// Rate in fps Kerbcast is currently pacing this camera's capture to: the
    /// plugin's primary rate (MaxCaptureFps) while displayed, the background
    /// rate while subscribed but undisplayed, 0 when not capturing.
    #[serde(default)]
    pub target_capture_fps: f32,
    /// Whether any consumer reports a non-zero display size for this camera,
    /// or a consumer forces it (e.g. a full-resolution recording).
    #[serde(default)]
    pub displayed: bool,
    /// Frames the plugin produced into this camera's ring per second, over
    /// roughly the last two seconds, as observed by the sidecar's encode loop.
    #[serde(default)]
    pub capture_fps: f32,
    /// Encoded frames handed to this camera's tracks per second, over the same
    /// window. Below `capture_fps` when the encoder skips frames (encode pacing,
    /// degrade); a viewer receiving less than this is losing frames in transit.
    #[serde(default)]
    pub sent_fps: f32,
    /// Keyframes the encoder emitted for this camera (periodic or requested)
    /// since its ring was attached.
    #[serde(default)]
    #[typeshare(serialized_as = "number")]
    pub keyframes_sent: u64,
    /// PLI/FIR keyframe requests received from viewers for this camera, across
    /// peers, since its ring was attached. Counted before the per-slot debounce,
    /// so it can exceed the keyframes forced in response.
    #[serde(default)]
    #[typeshare(serialized_as = "number")]
    pub keyframe_requests_received: u64,
    /// Largest display size currently held for this camera across consumers.
    /// Absent when no consumer reports one (`GET /cameras` sends null instead).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub display_size: Option<DisplaySizeReport>,
}

/// The per-dimension MAX display size held for a camera across consumers.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DisplaySizeReport {
    pub width: u32,
    pub height: u32,
    /// Milliseconds since the most recent report that contributed to the max.
    pub age_ms: u32,
}

// Wrapper structs for the algebraic-enum content payloads. typeshare's
// TS codegen names them after the variants so the generated TypeScript
// stays readable. Hand-defined Serde attrs put fields in camelCase to
// match the rest of the wire format.

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetLayersPayload {
    pub flight_id: u32,
    pub layers: Vec<Layer>,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetRenderSizePayload {
    pub flight_id: u32,
    pub width: u32,
    pub height: u32,
}

/// Forces a camera to render at the operator ceiling regardless of
/// display-size demand. Per-viewer, OR-aggregated by the sidecar: a feed
/// stays forced while any connected viewer forces it, and releases when
/// the last one clears it (or disconnects). Still yields to the adaptive
/// framerate shed; overrides only the demand term.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetForceFullResolutionPayload {
    pub flight_id: u32,
    pub force: bool,
}

/// Consumer's background-capture request. Global rather than
/// per-camera: it is a statement about how much background cost this consumer
/// wants spent, not about one camera. Zero stops asking.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetBackgroundCaptureFpsPayload {
    /// Requested rate in fps for cameras this consumer is not displaying.
    /// Clamped to the operator ceiling; 0 means no background capture.
    pub fps: f32,
}

/// Which moving thing a pan+zoom camera should auto-aim at. `None` = tracking
/// off (manual pan resumes). `ActiveVessel` and `Target` resolve to a world
/// position from `FlightGlobals` (the active vessel, or its target) with no
/// orbit math. Only surfaces on cameras with BOTH pan and zoom.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub enum TrackMode {
    #[default]
    None,
    ActiveVessel,
    Target,
}

/// Ask a pan+zoom camera to auto-track a moving vessel (or stop). Mirrors
/// `SetRenderSizePayload` in shape. Server-authoritative: the sidecar holds the
/// chosen mode per camera and publishes it in `CameraState.track_mode`, so
/// every browser reflects the same tracking state (never optimistic-local).
///
/// Precedence note (for operators): a browser track OVERRIDES a kOS aim on the
/// same camera; set `mode: none` to hand aiming back to kOS. While tracking, the
/// camera also AUTO-ZOOMS to keep the target framed (distance-driven), owning the
/// zoom over any manual FoV; `mode: none` restores manual zoom untouched.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetTrackTargetPayload {
    pub flight_id: u32,
    pub mode: TrackMode,
}

/// Create a command camera for the rest of the current scene. Server-wide:
/// the camera appears in every consumer's camera list (kind `command`) with
/// `commandCentre.token` set to `token` once the game has built it,
/// normally within a second. A request the game can not satisfy (unknown
/// body, site, station or craft) never produces a camera; the game logs why.
/// A token is used once per scene: a repeat with the same token is ignored.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateCommandCameraPayload {
    /// Chosen by the client, unique per request (1 to 128 characters).
    pub token: String,
    /// Feed name; absent for the default ("Launchpad camera", "<site>
    /// camera", "<station> tracking", "<craft> camera" or "<body> surface
    /// camera").
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    pub vantage: CommandVantage,
    /// Where it points; absent for the default (the active craft in
    /// flight). Not applied while a command authority holds the lock.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub aim: Option<CommandAim>,
}

/// Destroy a command camera made by `CreateCommandCamera` (or created in
/// game). The settings camera can not be destroyed.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DestroyCommandCameraPayload {
    pub flight_id: u32,
}

/// Point a command camera at `aim`, or back at its default with `aim` absent
/// or null. Refused with `ErrorCode::CommandLocked` while a command authority
/// holds the lock.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetCommandAimPayload {
    pub flight_id: u32,
    #[serde(default)]
    pub aim: Option<CommandAim>,
}

/// What an anchor marks.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AnchorKind {
    /// One part of a craft, followed through docking.
    Part,
    /// A craft's centre of mass.
    Vessel,
    /// A point on a body's surface.
    Surface,
    /// A direction, drawn at infinity.
    Direction,
    /// A point moving as `kinematics` says, propagated as a world model's
    /// records are: a last-known ghost, a hypothesis, a predicted point.
    /// Caller-supplied knowledge, so it is never refused and never `observed`.
    /// Where an anchor would be observed, its result still withholds the
    /// lens's pose: `x`, `y` and `apparentRadiusPx` only while `inFrame`, and
    /// never `distanceM`, `behind` or `angleOffAxis`.
    Kinematic,
}

/// How a `kinematic` anchor moves.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AnchorKinematicsKind {
    /// On a conic from KSP Orbit elements.
    Conic,
    /// Coasting ballistically from a state, under the body's gravity.
    StateVector,
    /// Fixed to the body's surface, carried by its rotation.
    Surface,
}

/// A `kinematic` anchor's motion about one body. `kind` says which fields
/// apply. With a light speed set, the point is placed where the light
/// reaching the lens left it.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AnchorKinematics {
    pub kind: AnchorKinematicsKind,
    /// The body's name, e.g. `"Kerbin"`.
    pub body: String,
    /// `conic`: degrees.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub inclination: Option<f64>,
    /// `conic`: not 1; a hyperbola has a negative `semiMajorAxis`, as KSP keeps it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub eccentricity: Option<f64>,
    /// `conic`: metres.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub semi_major_axis: Option<f64>,
    /// `conic`: longitude of the ascending node, degrees.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub lan: Option<f64>,
    /// `conic`: degrees.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub argument_of_periapsis: Option<f64>,
    /// `conic`: radians, at `epoch`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mean_anomaly_at_epoch: Option<f64>,
    /// `conic`: UT.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub epoch: Option<f64>,
    /// `stateVector`: UT of the state.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ut: Option<f64>,
    /// `stateVector`: `[x, y, z]` metres from the body centre, z up, in KSP's
    /// fixed inertial frame (as a `positionAt` aim takes it).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub position: Option<Vec<f64>>,
    /// `stateVector`: `[x, y, z]` metres per second in the same frame.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub velocity: Option<Vec<f64>>,
    /// `surface`: degrees, -90..=90.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub latitude: Option<f64>,
    /// `surface`: degrees.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub longitude: Option<f64>,
    /// `surface`: metres above sea level. Default 0.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub altitude: Option<f64>,
}

/// A direction a `direction` anchor follows, taken from its craft's orbit
/// (not the surface frame).
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AnchorDirection {
    Prograde,
    Retrograde,
    Normal,
    Antinormal,
    RadialIn,
    RadialOut,
    /// The first maneuver node's burn vector.
    Maneuver,
    /// From the craft towards the Sun.
    Sun,
    /// From the craft towards its target.
    Target,
    /// The craft's velocity relative to its target.
    TargetRelativeVelocity,
}

/// Something a client wants marked on a camera's picture. `kind` says which
/// fields apply. The game projects every anchor through the camera that
/// rendered each frame and sends the result with that frame's `FrameStamp`.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Anchor {
    /// The client's own name for the anchor, unique among its anchors on
    /// one camera. 1 to 64 characters.
    pub id: String,
    pub kind: AnchorKind,
    /// `part` and `vessel`: the craft's `Vessel.persistentId`. `direction`:
    /// the craft whose direction is followed; absent for the active craft.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub vessel: Option<u32>,
    /// `part`: the part's `Part.persistentId`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub part: Option<u32>,
    /// `part`: `[x, y, z]` metres from the part's origin in its own axes. A
    /// docking port is marked at its docking face, and the offset is in the
    /// face's axes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub offset: Option<Vec<f64>>,
    /// `surface`: the body's name, e.g. `"Kerbin"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub body: Option<String>,
    /// `surface`: degrees, -90..=90.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub latitude: Option<f64>,
    /// `surface`: degrees.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub longitude: Option<f64>,
    /// `surface`: metres above sea level. Default 0.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub altitude: Option<f64>,
    /// `direction`: a named direction of the craft. Give this or `vector`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub direction: Option<AnchorDirection>,
    /// `direction`: a fixed `[x, y, z]` direction in the inertial frame of
    /// `Orbit.getRelativePositionAtUT` (z up). Give this or `direction`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub vector: Option<Vec<f64>>,
    /// `part` and `vessel`: also report the thing's `forward` and `up` axes
    /// in camera space every frame.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub orientation: Option<bool>,
    /// Also report whether something stands between the lens and the
    /// anchor. Off by default: it costs a ray cast per anchor per frame.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub occlusion: Option<bool>,
    /// Metres added to the marked thing's radius before `apparentRadiusPx`
    /// is worked out, to leave room around it. Default 0.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub margin_m: Option<f64>,
    /// `kinematic`: how the point moves. Refused on any other kind.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kinematics: Option<AnchorKinematics>,
}

/// Replace this client's anchors on one camera, or clear them with
/// `anchors` absent or null. Anchors belong to the client that set them:
/// they are display, not commands, so the command lock does not apply, and
/// they are cleared when the client disconnects. At most
/// `MAX_ANCHORS_PER_CAMERA` per client per camera.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetAnchorsPayload {
    pub flight_id: u32,
    #[serde(default)]
    pub anchors: Option<Vec<Anchor>>,
}

/// Most anchors one client may hold on one camera.
pub const MAX_ANCHORS_PER_CAMERA: usize = 16;

/// Where one anchor landed in one frame. Pixel positions are in the
/// capture frame (`FrameStamp.width` by `FrameStamp.height`, origin top
/// left). Absent for a frame in which the anchor could not be located.
///
/// An `observed` result carries only what the picture shows: `x`, `y`,
/// `apparentRadiusPx` (and `forward` and `up` when asked for) only while the
/// anchor is in frame and not occluded, and never `distanceM`,
/// `angleOffAxis` or `behind`. Kerbcast's own `"subject"` anchor is always
/// observed; a browser client's anchors are observed by default while a
/// command authority holds the lock. A `kinematic` anchor is never observed,
/// but where it would be its result withholds the lens's pose (see
/// `AnchorKind::Kinematic`).
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FrameAnchor {
    /// The anchor's `id` as its owner set it.
    pub id: String,
    /// Who set it, for an anchor this client did not set: the game-side
    /// owner passed to `KerbcastControl.SetAnchors`, or `"kerbcast"` for
    /// Kerbcast's own. Absent for this client's own anchors.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub owner: Option<String>,
    /// True when the result is limited to what the picture shows (see above).
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub observed: bool,
    /// Absent from an observed result that is out of frame or occluded.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub x: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub y: Option<f64>,
    /// Inside the frame and in front of the lens.
    pub in_frame: bool,
    /// Behind the lens. `x` and `y` then sit off the frame on the side the
    /// anchor is nearest. Absent from an observed result.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub behind: Option<bool>,
    /// Lens to anchor in metres. Absent for a `direction` anchor and from an
    /// observed result.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub distance_m: Option<f64>,
    /// The marked thing's radius (plus `marginM`) on screen, in capture
    /// pixels. 0 for a `surface` anchor without a margin, and always 0 for a
    /// `direction` anchor. Absent with `x` and `y`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub apparent_radius_px: Option<f64>,
    /// Degrees between the camera's axis and the anchor. Absent from an
    /// observed result.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub angle_off_axis: Option<f64>,
    /// The thing's forward axis in camera space (x right, y up, z forward),
    /// when the anchor asked for `orientation`. For a craft, the direction
    /// its control point faces; for a docking port, the way the port faces;
    /// for another part, its stack axis.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub forward: Option<Vec<f64>>,
    /// The thing's up axis in camera space, perpendicular to `forward`,
    /// when the anchor asked for `orientation`. Gives its roll.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub up: Option<Vec<f64>>,
    /// Whether something stands between the lens and the anchor, when the
    /// anchor asked for `occlusion`, and always on an observed result.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub occluded: Option<bool>,
}

/// A command camera's state as one frame shows it, taken when the frame was
/// rendered and sent with that frame's `FrameStamp`, so a delayed viewer
/// never learns it before seeing the frame. The fields shared with
/// `CommandTrackState` mean the same; the rest say what the knowledge was.
#[typeshare]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FrameCommandState {
    pub has_fix: bool,
    /// Range to the aimed point in metres. Absent without a fix.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub range_m: Option<f64>,
    /// Bracket half-extent in capture pixels of a `frame_height_px` frame.
    /// Absent without a fix.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub bracket_half_px: Option<f64>,
    pub frame_height_px: u32,
    pub framing_fits: bool,
    /// The drawn craft's offset from frame centre in capture pixels. Under a
    /// lock, only while the picture shows the craft.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub true_offset_px: Option<f64>,
    pub occluded: bool,
    /// Name of the occluding body when `occluded`, otherwise empty.
    pub occluder: String,
    pub sunlit: bool,
    /// Why the feed is blanked; this frame is black.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub blank: Option<String>,
    /// The lock holder, absent with no lock.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub authority: Option<CommandAuthorityState>,
    /// The craft the frame is aimed at; 0 for none.
    pub subject_persistent_id: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub aim_kind: Option<CommandAimKind>,
    /// Why the subject has no position: `unknown`, `notPositioned`,
    /// `refused` or `stale`. Absent when it has one or there is no subject.
    /// Treat an unrecognised value as `refused`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub refusal: Option<String>,
    /// The world model's own code and words for a `refused` subject.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub refusal_code: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub refusal_text: Option<String>,
    /// The instant the subject's known position is taken at: the frame's
    /// UT, or the subject's past instant under a light speed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub subject_ut: Option<f64>,
    /// Where the knowledge came from: `truth`, `waiting`, `model` or `stale`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub world_model: Option<String>,
    /// Sequence of the world model snapshot the frame read, with the UT its
    /// knowledge describes and the scope the feed's site is bound to.
    /// Absent under truth. The sequence is a whole number, sent as a
    /// JSON number.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub world_model_seq: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub knowledge_ut: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scope: Option<String>,
}

/// A consumer reporting its OWN current display size in px for one camera.
/// Unlike `SetRenderSizePayload` (an operator command that sets the shared
/// render size directly, last-writer-wins), this is a per-consumer input the
/// sidecar aggregates MAX-across-consumers to drive auto-resolution
/// (meet-the-minimum-need). Shape mirrors `SetRenderSizePayload`; `width` and
/// `height` are the consumer's display px for this feed.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReportDisplaySizePayload {
    pub flight_id: u32,
    pub width: u32,
    pub height: u32,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetFovPayload {
    pub flight_id: u32,
    pub fov: f32,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetPanPayload {
    pub flight_id: u32,
    pub yaw: f32,
    pub pitch: f32,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetPanRatePayload {
    pub flight_id: u32,
    /// Normalised yaw velocity, -1..=1. +1 = pan right at the part's
    /// full `PanRateDegPerSec`. Persistent until superseded (a new rate,
    /// including zero, replaces it); the plugin integrates it into the
    /// pan target every frame.
    pub yaw_rate: f32,
    /// Normalised pitch velocity, -1..=1. +1 = pan up. Persistent until
    /// superseded.
    pub pitch_rate: f32,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetZoomRatePayload {
    pub flight_id: u32,
    /// Normalised zoom velocity, -1..=1. +1 = zoom IN (FoV decreasing)
    /// at the part's full `ZoomRateDegPerSec`. Persistent until
    /// superseded; the plugin integrates it into the FoV target every
    /// frame.
    pub rate: f32,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetDegradePayload {
    pub flight_id: u32,
    /// 0.0 = perfect quality, 1.0 = maximum degradation. Clamped.
    /// Last-writer-wins: degrade is a property of the camera's game
    /// state (comms strength), shared by all viewers of the single
    /// encoded stream, so the sidecar stores the latest value rather
    /// than reducing across subscribers. A well-behaved driver
    /// re-asserts every tick.
    pub level: f32,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetQualityPayload {
    pub flight_id: u32,
    /// Requested resolution preset, or absent/null for auto (clear the
    /// viewer clamp). Future viewer-quality knobs are added here as
    /// optional fields with serde defaults, so older clients keep
    /// parsing; richer controls (bitrate, ladder toggles) stay
    /// operator-only until deliberately opened up.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub preset: Option<QualityPreset>,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FlightIdPayload {
    pub flight_id: u32,
}

/// Slot↔camera assignment for the dynamic-subscription model. The sidecar
/// negotiates a pool of recv-only video transceivers up front; `Subscribe`
/// binds a camera to a free one and the sidecar announces the binding here.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SlotMapPayload {
    /// Transceiver `mid` carrying this slot's track. Stable for the
    /// connection's life, so the client keys off it rather than the
    /// fragile `onTrack` arrival order — match against
    /// `RTCTrackEvent.transceiver.mid`.
    pub mid: String,
    /// Camera now carried by the slot, or `None` when the slot was freed
    /// by `Unsubscribe`. Serialised as `null` (not omitted) so "freed" is
    /// unambiguous on the wire.
    pub flight_id: Option<u32>,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HelloPayload {
    pub sidecar_version: String,
    pub encoder_backend: String,
    /// True when this sidecar sends `FrameStamp` to a client that asks with
    /// `SetFrameStamps`. Absent from sidecars older than 1.11.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub frame_stamps: Option<bool>,
    /// True when this sidecar takes `SetAnchors` and sends anchor results
    /// with frame stamps to a client that asks with `SetFrameStamps`
    /// `anchors: true`. Absent from sidecars older than 1.12.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub anchors: Option<bool>,
    /// True when this sidecar sends a command camera's per-frame state with
    /// frame stamps to a client that asks with `SetFrameStamps`
    /// `command: true`, and `liveLightTimeS` on every stamp. Absent from
    /// sidecars older than 1.12.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub frame_command: Option<bool>,
    /// The most anchors one client may set on one camera. Absent from
    /// sidecars older than 1.12.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_anchors_per_camera: Option<u32>,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetFrameStampsPayload {
    pub enabled: bool,
    /// Also send each frame's anchor results. Off unless asked for, so a
    /// client that predates anchors never receives them.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub anchors: Option<bool>,
    /// Also send a command camera's state with each of its frames. Off
    /// unless asked for.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub command: Option<bool>,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CameraSnapshotPayload {
    pub cameras: Vec<CameraState>,
    /// How many more cameras complete this snapshot. A snapshot too large
    /// for one message carries the first cameras, and each of the rest
    /// follows straight after it as a `CameraStateChanged`, which a client
    /// that predates this field applies as it would any state change.
    /// Absent when the snapshot is whole.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub follows: Option<u32>,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CameraStateChangedPayload {
    pub state: CameraState,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AdaptiveShedPayload {
    pub level: u32,
    pub ksp_fps: f32,
    pub reason: String,
}

/// Scene-state change: whether KSP is currently in a flight scene. Sent
/// after `Hello` (priming) and whenever the polled in-flight flag flips,
/// so clients can show a calm out-of-flight standby instead of per-camera
/// SIGNAL LOST when the whole scene unloads.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SceneStateChangedPayload {
    pub in_flight: bool,
}

#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub enum ErrorSource {
    #[default]
    Sidecar,
    Client,
}

/// Machine-readable reason for an `ErrorPayload`, so a client can react to a
/// refusal without matching on the wording of `message`.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ErrorCode {
    /// Every video slot is bound; the client negotiated too few transceivers.
    NoFreeSlot,
    /// No camera with the requested flight id is registered. For cameras
    /// requested at connect time this is true as of the offer; the camera may
    /// appear later, so a client can retry with `Subscribe`.
    CameraNotFound,
    /// The named camera exists but its part is gone.
    CameraDestroyed,
    /// A command authority in game holds the command cameras' aim lock.
    CommandLocked,
    /// A command aim names a craft that is not in the camera's latest
    /// `commandCentre.targets`, for example one merged away in a dock.
    CommandTargetUnknown,
    /// `POST /offer` refused: the offer's video carries no H.264 with
    /// packetization-mode=1, the only video Kerbcast sends. A browser
    /// without H.264 support (some Firefox builds lack OpenH264) offers none.
    CodecUnsupported,
}

#[typeshare]
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ErrorPayload {
    pub message: String,
    #[serde(default)]
    pub source: ErrorSource,
    /// Camera the error is about, when it concerns one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub flight_id: Option<u32>,
    /// Reason code for refusals a client may want to act on. Absent for
    /// errors that only carry a human-readable `message`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub code: Option<ErrorCode>,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetThrottleMainScreenPayload {
    /// When `true`, disable the KSP main flight cameras to free GPU for
    /// kerbcast streams. Persists via the per-save difficulty parameter,
    /// matching the in-game Difficulty Settings toggle.
    pub enabled: bool,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsStatePayload {
    /// Effective value of the "Throttle KSP main render" setting as last
    /// reported by the plugin's `global.status.json`. Reflects what the
    /// plugin has *applied*, not just what was last requested.
    pub throttle_main_screen: bool,
    /// KSP mission time (`Planetarium.GetUniversalTime()`, seconds) at
    /// which the video currently being produced was captured. `None` =
    /// no clock yet (old plugin/sidecar, or first status not seen); a
    /// consumer treats that as "unknown" and keeps a live passthrough.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub capture_ut: Option<f64>,
    /// Monotonic counter bumped only on a non-monotonic UT jump (revert,
    /// quickload, scene reload). Consumers resynchronise on any change;
    /// the absolute value is meaningless.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub capture_epoch: Option<u32>,
    /// Current KSP time-warp multiplier, so a consumer can interpolate
    /// `capture_ut` between ~1Hz samples. `None` => treat as 1.0.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub time_warp_rate: Option<f64>,
}

/// Mission-time stamp of one video frame, sent on the control channel just
/// before the frame's RTP packets. A browser matches it to a presented frame
/// by `rtp_timestamp` (`requestVideoFrameCallback` metadata `rtpTimestamp`),
/// which gives the exact UT that frame was rendered at.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FrameStampPayload {
    /// Camera the frame came from.
    pub flight_id: u32,
    /// RTP timestamp (90 kHz clock) the frame's packets carry on this peer's
    /// track for that camera.
    pub rtp_timestamp: u32,
    /// KSP universal time (seconds) the frame was rendered at.
    pub capture_ut: f64,
    /// Capture epoch (see `SettingsStatePayload::capture_epoch`) `capture_ut`
    /// belongs to.
    pub capture_epoch: u32,
    /// Vertical field of view the frame was rendered with, degrees. Absent
    /// when the game did not report one (kerbal cameras, plugins older
    /// than 1.12).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fov: Option<f64>,
    /// The frame's size in capture pixels, the space anchor positions are in.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub width: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub height: Option<u32>,
    /// Where each anchor landed in this frame: the client's own anchors and
    /// those set in game. Only for a client that asked for anchors with
    /// `SetFrameStamps`; absent when there are none.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub anchors: Option<Vec<FrameAnchor>>,
    /// The frame's live bound in seconds: the light time covering what of
    /// the lens's own craft and site it draws at `capture_ut`, the larger of
    /// one 60 fps frame and the light time to the lens's own farthest part.
    /// 0 with no light speed. Other loaded craft near the lens are still
    /// drawn at `capture_ut` and are not covered yet; once craft beyond the
    /// boundary are hidden, the bound covers everything drawn at
    /// `capture_ut`. A delayed viewer releases the frame no sooner than
    /// `capture_ut` plus this plus its own delay from the lens. Absent when
    /// the game could not read it, and from sidecars older than 1.12: under
    /// a light speed that is unknown, and the frame is held.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub live_light_time_s: Option<f64>,
    /// How far light travels in one 60 fps frame at the light speed: craft
    /// nearer the lens than this are drawn at `capture_ut`, farther ones at
    /// their own past instant. Absent with no light speed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub boundary_m: Option<f64>,
    /// Unix wall-clock milliseconds just before the sidecar hands the
    /// frame's packets to the transport. Differences between frames show the
    /// sidecar's own send pacing, apart from what the network adds. Absent
    /// from sidecars older than 1.12.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sent_wall_ms: Option<f64>,
    /// The frame's encoded size in bytes. Absent from sidecars older than 1.12.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub encoded_bytes: Option<u32>,
    /// Whether the frame is a keyframe (IDR). Absent from sidecars older than
    /// 1.12.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub keyframe: Option<bool>,
    /// A command camera's state in this frame. Only for a client that asked
    /// with `SetFrameStamps` `command: true`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub command: Option<FrameCommandState>,
}

/// Messages sent FROM the client TO the sidecar.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", content = "content", rename_all = "kebab-case")]
/* A create-command-camera request is a few hundred bytes against a handful
 * for most variants. Messages are parsed one at a time and dropped, so the
 * size skew costs nothing worth a Box at every match site. */
#[allow(clippy::large_enum_variant)]
pub enum ClientMessage {
    /// First message on a new data channel. Sidecar replies with `Hello`
    /// + a `CameraSnapshot` so the client gets server version + every
    /// camera's full state on one round-trip.
    Hello,
    /// Override the operator layer mask for one camera. Server-wide
    /// state — all consumers see the change.
    SetLayers(SetLayersPayload),
    /// Cap the auto-resolution for one camera. As of Stage 6 this is a
    /// CEILING on the demand-driven size (effective = min(auto max-across-
    /// consumers, this cap)), NOT an authoritative set: it cannot force a
    /// camera larger than current consumers report, only smaller. Even
    /// pixels only (H.264 chroma); server caps at the ring's allocated max.
    SetRenderSize(SetRenderSizePayload),
    /// Auto-track a moving vessel with a pan+zoom camera (or stop). The sidecar
    /// holds the chosen `mode` per camera and publishes it in
    /// `CameraState.track_mode`, so tracking state is server-authoritative and
    /// consistent across browsers. A browser track OVERRIDES a kOS aim on the
    /// same camera; send `mode: none` to hand aiming back to kOS.
    SetTrackTarget(SetTrackTargetPayload),
    /// Create a command camera at a vantage for the rest of the scene.
    /// Server-wide; find the new camera by its `commandCentre.token`.
    CreateCommandCamera(CreateCommandCameraPayload),
    /// Destroy a created command camera. Server-wide.
    DestroyCommandCamera(DestroyCommandCameraPayload),
    /// Point a command camera, or return it to its default. Server-wide.
    SetCommandAim(SetCommandAimPayload),
    /// Replace or clear this client's anchors on one camera. Per client:
    /// other clients never see them, and they go when this client does.
    /// Send only to a sidecar whose `Hello` has `anchors: true`.
    SetAnchors(SetAnchorsPayload),
    /// A consumer reporting its OWN current display size in px; the sidecar
    /// aggregates MAX-across-consumers to drive auto-resolution
    /// (meet-the-minimum-need). Distinct from the operator `SetRenderSize`
    /// command: this is a per-consumer input, not a shared override — a
    /// departing consumer's size is cleared so the max relaxes.
    ReportDisplaySize(ReportDisplaySizePayload),
    /// Force a camera to render at the operator ceiling regardless of
    /// display-size demand. Per-viewer, OR-aggregated by the sidecar: a
    /// feed stays forced while any connected viewer forces it, and
    /// releases when the last one clears it (or disconnects). Still
    /// yields to the adaptive framerate shed; overrides only the demand
    /// term, never the perf-protection ceiling.
    SetForceFullResolution(SetForceFullResolutionPayload),
    /// Ask for background capture: subscribed cameras that this
    /// consumer is NOT displaying keep capturing at a low rate instead of
    /// stopping, so switching to one promotes an already-flowing stream rather
    /// than cold-starting from black.
    ///
    /// Per-consumer and MAX-aggregated, like `ReportDisplaySize`: it runs
    /// at the highest rate any connected consumer asked for, and relaxes when
    /// they lower it or disconnect. A consumer that never sends this pays
    /// nothing, which is why a stock install needs no configuration to serve
    /// both a plain viewer and a consumer that wants background capture.
    ///
    /// The rate is a REQUEST, not a guarantee. It is clamped to the operator's
    /// `BackgroundCaptureFps` ceiling and is the first thing dropped when the
    /// game approaches its framerate floor — watch `CameraState.capture_state`
    /// for what is actually happening. Send 0 to stop asking.
    ///
    /// Receiving the frames is not the same as keeping them: a consumer that
    /// wants history for a camera it isn't showing has to buffer the background
    /// feed itself.
    SetBackgroundCaptureFps(SetBackgroundCaptureFpsPayload),
    /// Set the camera's field-of-view (degrees). Silently ignored for
    /// parts whose Hullcam module is the fixed base (`supportsZoom ==
    /// false`); clients are expected to clamp to `fovMin / fovMax`
    /// from the camera's `CameraState` before sending.
    SetFov(SetFovPayload),
    /// Pan/tilt the camera (yaw and pitch, both degrees from the
    /// part's resting forward). Honored by parts with a steerable mount
    /// (`supportsPan == true`, e.g. `DC.TurretCam` / `hc.launchcam`);
    /// silently ignored for fixed-mount parts. Clients should hide pan
    /// controls until `supportsPan == true` for the camera.
    SetPan(SetPanPayload),
    /// Set a *persistent* pan/tilt velocity (normalised yaw + pitch,
    /// -1..=1). Unlike `SetPan` (a one-shot absolute target), the rate
    /// holds until a new rate — including zero, which stops — supersedes
    /// it; the plugin integrates it into the pan target every frame so
    /// smoothness lives in KSP's frame loop rather than the control-poll
    /// cadence. +yaw = pan right, +pitch = pan up. Ignored (sidecar
    /// replies `Error`) when `supportsPan == false`. Composes with
    /// `SetPan`: an absolute command jumps the target, integration then
    /// continues from there.
    SetPanRate(SetPanRatePayload),
    /// Set a *persistent* zoom velocity (normalised, -1..=1). +1 = zoom
    /// IN (FoV decreasing). Holds until a new rate — including zero —
    /// supersedes it; the plugin integrates it into the FoV target every
    /// frame. Ignored (sidecar replies `Error`) when `supportsZoom ==
    /// false`. Composes with the absolute `SetFov` the same way
    /// `SetPanRate` composes with `SetPan`.
    SetZoomRate(SetZoomRatePayload),
    /// Request artificial signal degradation. 0.0 = perfect quality,
    /// 1.0 = maximum degradation. Last-writer-wins: the sidecar stores
    /// the latest value on the camera (degrade is shared across all
    /// viewers of the one encoded stream), so a departed peer can't
    /// pin it. Lets consumers signal to the sidecar "I want to look
    /// like the in-game CommNet antenna is struggling"; sidecar takes
    /// the opportunity to
    /// reduce bitrate + skip frames, which both creates the
    /// signal-loss aesthetic AND saves encoder CPU.
    SetDegrade(SetDegradePayload),
    /// Set (or clear, with `preset: null`) a camera's viewer-requested
    /// resolution preset. Server-wide, last write wins across peers; the
    /// resulting `CameraStateChanged` broadcast keeps every UI
    /// consistent. The effective resolution is always
    /// min(operator ceiling, adaptive level, viewer preset): a preset
    /// can never raise quality past the operator's settings.cfg
    /// Width/Height, and the adaptive controller's demotes keep
    /// winning until it recovers. Viewers can NOT change bitrate or
    /// toggle the adaptive machinery through this message.
    SetQuality(SetQualityPayload),
    /// Request an IDR (keyframe) on the next encode tick. Browsers send
    /// this when they've dropped enough frames to be unable to decode
    /// the current P-frame chain. Sidecar forwards to the camera's
    /// encoder.
    RequestKeyframe(FlightIdPayload),
    /// Dynamically subscribe to a camera on an already-connected peer. The
    /// sidecar binds it to a free pre-negotiated slot (one of the recv-only
    /// video transceivers from the offer), starts encoding it, forces a
    /// keyframe, and replies with `SlotMap` naming the transceiver `mid` now
    /// carrying it — no renegotiation. Replies with `Error` if no slot is
    /// free (the client should have negotiated enough transceivers up front).
    Subscribe(FlightIdPayload),
    /// Release a camera's slot. The sidecar stops feeding that slot (the
    /// camera sleeps if it has no other subscribers) and replies with
    /// `SlotMap { flightId: null }` for the freed transceiver `mid`.
    Unsubscribe(FlightIdPayload),
    /// Response to `Ping`. Browser sends this immediately on receiving each Ping.
    Pong,
    /// Graceful teardown. The canonical way for a client to leave: the sidecar
    /// immediately releases every camera this peer is feeding (so they sleep
    /// without waiting for ICE to time out), after which the client may close
    /// the connection. The drop-detection reaper remains the fallback for
    /// ungraceful exits (crash / tab close / network loss) that can't send this.
    Disconnect,
    /// Enable or disable the KSP main render throttle globally. Persists
    /// across saves (writes the per-save difficulty parameter). Server-wide:
    /// all peers see the resulting `SettingsState` broadcast.
    SetThrottleMainScreen(SetThrottleMainScreenPayload),
    /// Start or stop `FrameStamp` messages on this peer's control channel.
    /// Off until asked for, so clients that predate stamps never receive
    /// them. Send only to a sidecar whose `Hello` has `frameStamps: true`;
    /// `anchors: true` only to one whose `Hello` has `anchors: true`.
    SetFrameStamps(SetFrameStampsPayload),
}

/// Messages sent FROM the sidecar TO the client.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", content = "content", rename_all = "kebab-case")]
/* CameraState carries per-camera state (now including the kerbal-camera
 * fields), dwarfing the other variants. Boxing it would ripple across
 * every construction site for no behaviour change, so allow the size
 * skew instead. */
#[allow(clippy::large_enum_variant)]
pub enum ServerMessage {
    /// Reply to `Hello`. Carries server version + the active encoder
    /// backend's name.
    Hello(HelloPayload),
    /// Full snapshot of every currently-attached camera. Sent in reply
    /// to `Hello` and on any structural change (vessel switch, ring
    /// added/removed).
    CameraSnapshot(CameraSnapshotPayload),
    /// Single-camera state change. Sent when an operator request applies
    /// or adaptive shedding kicks in. Lets clients update their UI
    /// without re-fetching the whole snapshot.
    CameraStateChanged(CameraStateChangedPayload),
    /// Slot↔camera assignment for the dynamic-subscription model. Sent in
    /// reply to `Subscribe`/`Unsubscribe`: `flightId` is the camera now
    /// carried by transceiver `mid`, or `null` when the slot was freed. The
    /// client maps `mid` to the track it received via `onTrack` and routes
    /// that track to (or away from) the camera's stream — no renegotiation.
    SlotMap(SlotMapPayload),
    /// Adaptive shedding level changed. Reason is human-readable
    /// ("ksp-fps-low", "ksp-fps-recovered") so client UIs can show
    /// *why* quality changed rather than just *that* it did.
    AdaptiveShed(AdaptiveShedPayload),
    /// Malformed / unknown client message. Includes the offending
    /// payload so the client can log it.
    Error(ErrorPayload),
    /// Keepalive probe sent by the sidecar every 5 seconds. Browser responds
    /// with `Pong`; if no Ping arrives within 15s the browser tears down
    /// the connection.
    Ping,
    /// Current value of global settings (throttle state). Sent after `Hello`
    /// so a freshly-connected client shows correct state immediately, and
    /// re-broadcast whenever the polled plugin status shows a change.
    SettingsState(SettingsStatePayload),
    /// Whether KSP is in a flight scene. Primed after `Hello` and
    /// re-broadcast when the polled in-flight flag changes.
    SceneStateChanged(SceneStateChangedPayload),
    /// Capture UT of one frame about to go out on a video track, keyed by
    /// its RTP timestamp. One per frame per track, only to a peer that
    /// enabled them with `SetFrameStamps`.
    FrameStamp(FrameStampPayload),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_stamp_serialises_camel_case() {
        let msg = ServerMessage::FrameStamp(FrameStampPayload {
            flight_id: 7,
            rtp_timestamp: 4_000_000_000,
            capture_ut: 86400.25,
            capture_epoch: 3,
            fov: None,
            width: None,
            height: None,
            anchors: None,
            live_light_time_s: None,
            boundary_m: None,
            sent_wall_ms: None,
            encoded_bytes: None,
            keyframe: None,
            command: None,
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert_eq!(
            s,
            r#"{"type":"frame-stamp","content":{"flightId":7,"rtpTimestamp":4000000000,"captureUt":86400.25,"captureEpoch":3}}"#,
            "a stamp without the 1.12 fields is byte-identical to a 1.11 stamp"
        );
    }

    #[test]
    fn frame_stamp_carries_fov_size_and_anchors() {
        let msg = ServerMessage::FrameStamp(FrameStampPayload {
            flight_id: 7,
            rtp_timestamp: 1,
            capture_ut: 2.0,
            capture_epoch: 0,
            fov: Some(40.0),
            width: Some(1280),
            height: Some(720),
            anchors: Some(vec![FrameAnchor {
                id: "port".into(),
                owner: None,
                observed: false,
                x: Some(640.5),
                y: Some(360.0),
                in_frame: true,
                behind: Some(false),
                distance_m: Some(52.0),
                apparent_radius_px: Some(12.0),
                angle_off_axis: Some(0.1),
                forward: Some(vec![0.0, 0.0, -1.0]),
                up: Some(vec![0.0, 1.0, 0.0]),
                occluded: Some(false),
            }]),
            live_light_time_s: None,
            boundary_m: None,
            sent_wall_ms: None,
            encoded_bytes: None,
            keyframe: None,
            command: None,
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains(r#""fov":40.0,"width":1280,"height":720"#), "{s}");
        assert!(
            s.contains(r#""anchors":[{"id":"port","x":640.5,"y":360.0,"inFrame":true,"behind":false,"distanceM":52.0,"apparentRadiusPx":12.0,"angleOffAxis":0.1,"forward":[0.0,0.0,-1.0],"up":[0.0,1.0,0.0],"occluded":false}]"#),
            "{s}"
        );
    }

    /// An observed result out of view carries no position or range at all,
    /// and the reader takes it as such rather than as zeros.
    #[test]
    fn an_observed_result_out_of_view_carries_no_coordinates() {
        let json =
            r#"{"id":"subject","owner":"kerbcast","observed":true,"inFrame":true,"occluded":true}"#;
        let a: FrameAnchor = serde_json::from_str(json).unwrap();
        assert!(a.observed);
        assert_eq!((a.x, a.y, a.apparent_radius_px), (None, None, None));
        assert_eq!(
            (a.distance_m, a.angle_off_axis, a.behind),
            (None, None, None)
        );
        assert_eq!(
            serde_json::to_string(&a).unwrap(),
            json,
            "and writes back the same"
        );
    }

    #[test]
    fn set_anchors_matches_the_wire() {
        let msg: ClientMessage = serde_json::from_str(
            r#"{"type":"set-anchors","content":{"flightId":9,"anchors":[{"id":"port","kind":"part","vessel":11,"part":22,"orientation":true},{"id":"pro","kind":"direction","direction":"targetRelativeVelocity"}]}}"#,
        )
        .unwrap();
        let ClientMessage::SetAnchors(p) = msg else {
            panic!("expected set-anchors")
        };
        assert_eq!(p.flight_id, 9);
        let anchors = p.anchors.unwrap();
        assert_eq!(anchors[0].kind, AnchorKind::Part);
        assert_eq!(anchors[0].part, Some(22));
        assert_eq!(anchors[0].orientation, Some(true));
        assert_eq!(
            anchors[1].direction,
            Some(AnchorDirection::TargetRelativeVelocity)
        );

        let clear: ClientMessage =
            serde_json::from_str(r#"{"type":"set-anchors","content":{"flightId":9}}"#).unwrap();
        assert!(matches!(
            clear,
            ClientMessage::SetAnchors(SetAnchorsPayload { anchors: None, .. })
        ));
    }

    /// Clients before 1.11 ignore the extra Hello field and never send
    /// `set-frame-stamps`; newer ones read the field and opt in.
    #[test]
    fn hello_announces_frame_stamps_and_clients_opt_in() {
        let hello = ServerMessage::Hello(HelloPayload {
            sidecar_version: "1.11.0".to_owned(),
            encoder_backend: "x".to_owned(),
            frame_stamps: Some(true),
            anchors: Some(true),
            frame_command: Some(true),
            max_anchors_per_camera: Some(16),
        });
        let hello = serde_json::to_string(&hello).unwrap();
        assert!(hello.contains(r#""maxAnchorsPerCamera":16"#));
        assert!(hello.contains(r#""frameStamps":true"#));
        assert!(hello.contains(r#""anchors":true"#));
        assert!(hello.contains(r#""frameCommand":true"#));

        /* A 1.11 client's opt-in never asks for anchors. */
        let msg: ClientMessage =
            serde_json::from_str(r#"{"type":"set-frame-stamps","content":{"enabled":true}}"#)
                .unwrap();
        assert!(matches!(
            msg,
            ClientMessage::SetFrameStamps(SetFrameStampsPayload {
                enabled: true,
                anchors: None,
                command: None
            })
        ));
        let msg: ClientMessage = serde_json::from_str(
            r#"{"type":"set-frame-stamps","content":{"enabled":true,"anchors":true}}"#,
        )
        .unwrap();
        assert!(matches!(
            msg,
            ClientMessage::SetFrameStamps(SetFrameStampsPayload {
                enabled: true,
                anchors: Some(true),
                command: None
            })
        ));
        let msg: ClientMessage = serde_json::from_str(
            r#"{"type":"set-frame-stamps","content":{"enabled":true,"anchors":true,"command":true}}"#,
        )
        .unwrap();
        assert!(matches!(
            msg,
            ClientMessage::SetFrameStamps(SetFrameStampsPayload {
                command: Some(true),
                ..
            })
        ));
    }

    /// The plugin's per-frame command state reads into the wire type, and a
    /// stamp carries it with the live bound and boundary in wire names.
    #[test]
    fn frame_stamp_carries_the_live_bound_and_command_state() {
        let command: FrameCommandState = serde_json::from_str(
            r#"{"hasFix":true,"rangeM":1500.25,"bracketHalfPx":12.5,"frameHeightPx":720,"framingFits":true,"occluded":false,"occluder":"","sunlit":true,"authority":{"owner":"mission"},"subjectPersistentId":42,"aimKind":"vessel","refusal":"stale","subjectUt":86398.123456789,"worldModel":"model","worldModelSeq":9,"knowledgeUt":86390,"scope":"centre"}"#,
        )
        .unwrap();
        assert_eq!(command.aim_kind, Some(CommandAimKind::Vessel));
        assert_eq!(command.world_model_seq, Some(9.0));
        let msg = ServerMessage::FrameStamp(FrameStampPayload {
            flight_id: 7,
            rtp_timestamp: 1,
            capture_ut: 2.0,
            capture_epoch: 0,
            fov: None,
            width: None,
            height: None,
            anchors: None,
            live_light_time_s: Some(23.25),
            boundary_m: Some(0.05),
            sent_wall_ms: Some(1_793_122_552_368.5),
            encoded_bytes: Some(48_213),
            keyframe: Some(true),
            command: Some(command),
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains(r#""liveLightTimeS":23.25,"boundaryM":0.05,"sentWallMs":1793122552368.5,"encodedBytes":48213,"keyframe":true,"command":{"hasFix":true,"rangeM":1500.25"#), "{s}");
        assert!(
            s.contains(r#""subjectUt":86398.123456789"#),
            "UTs keep every digit: {s}"
        );
        assert!(
            !s.contains("trueOffsetPx") && !s.contains("blank"),
            "what does not apply is left out: {s}"
        );
    }

    #[test]
    fn set_track_target_roundtrips() {
        let msg = ClientMessage::SetTrackTarget(SetTrackTargetPayload {
            flight_id: 7,
            mode: TrackMode::ActiveVessel,
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"set-track-target\""));
        assert!(s.contains("\"content\":"));
        assert!(s.contains("\"flightId\":7"));
        assert!(s.contains("\"mode\":\"activeVessel\""));
        let back: ClientMessage = serde_json::from_str(&s).unwrap();
        match back {
            ClientMessage::SetTrackTarget(p) => {
                assert_eq!(p.flight_id, 7);
                assert_eq!(p.mode, TrackMode::ActiveVessel);
            }
            _ => panic!("wrong variant"),
        }

        // The wire tags for each mode + the default.
        assert_eq!(TrackMode::default(), TrackMode::None);
        for (mode, tag) in [
            (TrackMode::None, "none"),
            (TrackMode::ActiveVessel, "activeVessel"),
            (TrackMode::Target, "target"),
        ] {
            assert_eq!(serde_json::to_string(&mode).unwrap(), format!("\"{tag}\""));
        }
    }

    #[test]
    fn set_command_aim_matches_the_wire() {
        let wire = r#"{"type":"set-command-aim","content":{"flightId":2147483905,"aim":{"kind":"vessel","persistentId":4000000001,"framingMarginM":20.0}}}"#;
        let msg: ClientMessage = serde_json::from_str(wire).unwrap();
        match &msg {
            ClientMessage::SetCommandAim(p) => {
                assert_eq!(p.flight_id, 2_147_483_905);
                let aim = p.aim.as_ref().unwrap();
                assert_eq!(aim.kind, CommandAimKind::Vessel);
                assert_eq!(aim.persistent_id, Some(4_000_000_001));
                assert_eq!(aim.framing_margin_m, Some(20.0));
            }
            _ => panic!("wrong variant"),
        }
        assert_eq!(serde_json::to_string(&msg).unwrap(), wire);

        let direction = r#"{"type":"set-command-aim","content":{"flightId":5,"aim":{"kind":"direction","azimuth":90.0,"elevation":30.0}}}"#;
        let msg: ClientMessage = serde_json::from_str(direction).unwrap();
        assert_eq!(serde_json::to_string(&msg).unwrap(), direction);

        let position = r#"{"type":"set-command-aim","content":{"flightId":5,"aim":{"kind":"positionAt","body":"Kerbin","ut":100.0,"position":[1.0,2.0,3.0],"velocity":[0.0,0.0,1.0],"subjectPersistentId":9}}}"#;
        let msg: ClientMessage = serde_json::from_str(position).unwrap();
        assert_eq!(serde_json::to_string(&msg).unwrap(), position);

        /* Returning to the default: absent and null both parse to none. */
        for clear in [
            r#"{"type":"set-command-aim","content":{"flightId":5}}"#,
            r#"{"type":"set-command-aim","content":{"flightId":5,"aim":null}}"#,
        ] {
            match serde_json::from_str::<ClientMessage>(clear).unwrap() {
                ClientMessage::SetCommandAim(p) => assert!(p.aim.is_none()),
                _ => panic!("wrong variant"),
            }
        }
    }

    #[test]
    fn command_camera_requests_match_the_wire() {
        let create = r#"{"type":"create-command-camera","content":{"token":"t1","name":"Hill","vantage":{"kind":"surface","height":3.0,"body":"Kerbin","latitude":-0.05,"longitude":-74.7},"aim":{"kind":"surface","body":"Kerbin","latitude":-0.0972,"longitude":-74.5577,"altitude":70.0}}}"#;
        let msg: ClientMessage = serde_json::from_str(create).unwrap();
        match &msg {
            ClientMessage::CreateCommandCamera(p) => {
                assert_eq!(p.token, "t1");
                assert_eq!(p.vantage.kind, CommandVantageKind::Surface);
                assert_eq!(p.vantage.latitude, Some(-0.05));
                assert!(p.vantage.site.is_none());
                assert_eq!(p.aim.as_ref().unwrap().kind, CommandAimKind::Surface);
            }
            _ => panic!("wrong variant"),
        }
        assert_eq!(serde_json::to_string(&msg).unwrap(), create);

        let pad = r#"{"type":"create-command-camera","content":{"token":"t2","vantage":{"kind":"launchSite"}}}"#;
        assert!(matches!(
            serde_json::from_str::<ClientMessage>(pad).unwrap(),
            ClientMessage::CreateCommandCamera(CreateCommandCameraPayload {
                vantage: CommandVantage {
                    kind: CommandVantageKind::LaunchSite,
                    ..
                },
                ..
            })
        ));

        let destroy = r#"{"type":"destroy-command-camera","content":{"flightId":2147483905}}"#;
        let msg: ClientMessage = serde_json::from_str(destroy).unwrap();
        assert_eq!(serde_json::to_string(&msg).unwrap(), destroy);
    }

    /// An older sidecar's commandCentre has no `created`; it reads as false.
    #[test]
    fn command_centre_created_defaults_false() {
        let c: CommandCentreInfo = serde_json::from_str(
            r#"{"stationId":"ground:KSC","nodeName":"KSC","displayName":"KSC","body":"Kerbin","latitude":0.0,"longitude":0.0,"altitude":0.0,"subjectPersistentId":0,"trackingLevel":1,"targets":[]}"#,
        )
        .unwrap();
        assert!(!c.created);
        assert!(c.token.is_none());
    }

    #[test]
    fn command_target_eligibility_tags() {
        for (e, tag) in [
            (CommandTargetEligibility::Ok, "ok"),
            (CommandTargetEligibility::TooFar, "tooFar"),
            (CommandTargetEligibility::LevelTooLow, "levelTooLow"),
            (CommandTargetEligibility::NotTrackable, "notTrackable"),
        ] {
            assert_eq!(serde_json::to_string(&e).unwrap(), format!("\"{tag}\""));
        }
    }

    #[test]
    fn set_layers_roundtrips() {
        let msg = ClientMessage::SetLayers(SetLayersPayload {
            flight_id: 123,
            layers: vec![Layer::Near, Layer::Scaled],
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"set-layers\""));
        assert!(s.contains("\"content\":"));
        assert!(s.contains("\"flightId\":123"));
        assert!(s.contains("\"NEAR\""));
        assert!(s.contains("\"SCALED\""));
        let back: ClientMessage = serde_json::from_str(&s).unwrap();
        match back {
            ClientMessage::SetLayers(p) => {
                assert_eq!(p.flight_id, 123);
                assert_eq!(p.layers, vec![Layer::Near, Layer::Scaled]);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn report_display_size_roundtrips() {
        // A consumer reporting its own display px (an aggregatable input for
        // auto-resolution), using the adjacently-tagged kebab-case + `content`
        // wrapper convention every ClientMessage variant shares.
        let msg = ClientMessage::ReportDisplaySize(ReportDisplaySizePayload {
            flight_id: 1,
            width: 40,
            height: 40,
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"report-display-size\""));
        assert!(s.contains("\"content\":"));
        assert!(s.contains("\"flightId\":1"));
        assert!(s.contains("\"width\":40"));
        assert!(s.contains("\"height\":40"));
        let back: ClientMessage = serde_json::from_str(&s).unwrap();
        match back {
            ClientMessage::ReportDisplaySize(p) => {
                assert_eq!(p.flight_id, 1);
                assert_eq!(p.width, 40);
                assert_eq!(p.height, 40);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn set_force_full_resolution_roundtrips() {
        let s = r#"{"type":"set-force-full-resolution","content":{"flightId":7,"force":true}}"#;
        let back: ClientMessage = serde_json::from_str(s).unwrap();
        match &back {
            ClientMessage::SetForceFullResolution(p) => {
                assert_eq!(p.flight_id, 7);
                assert!(p.force);
            }
            _ => panic!("wrong variant"),
        }
        let re_serialized = serde_json::to_string(&back).unwrap();
        assert_eq!(re_serialized, s);
    }

    #[test]
    fn camera_state_snapshot_uses_camel_case_keys() {
        let snap = ServerMessage::CameraSnapshot(CameraSnapshotPayload {
            cameras: vec![CameraState {
                flight_id: 99,
                lifecycle: CameraLifecycle::Active,
                kind: CameraKind::Part,
                capture_state: CaptureState::Full,
                kerbal_persistent_id: None,
                command_centre: None,
                command_track: None,
                crew_location: None,
                track_mode: TrackMode::None,
                part_name: "navCam1".into(),
                part_title: "NavCam".into(),
                camera_name: "NavCam".into(),
                vessel_name: "Perf Test 1".into(),
                layers: vec![Layer::Near, Layer::Scaled],
                operator_layers: vec![Layer::Near, Layer::Scaled, Layer::Galaxy],
                render_width: 384,
                render_height: 384,
                operator_width: 768,
                operator_height: 768,
                max_width: None,
                max_height: None,
                supports_zoom: true,
                fov: 60.0,
                fov_min: 30.0,
                fov_max: 100.0,
                supports_pan: false,
                pan_yaw: 0.0,
                pan_pitch: 0.0,
                pan_yaw_min: 0.0,
                pan_yaw_max: 0.0,
                pan_pitch_min: 0.0,
                pan_pitch_max: 0.0,
                encoder_bitrate_bps: 1_500_000,
                target_bitrate_bps: 1_200_000,
                degrade_level: 0.0,
                viewer_quality: Some(QualityPreset::Half),
                quality_limited_by: Some("throttled".into()),
                ..Default::default()
            }],
            follows: None,
        });
        let s = serde_json::to_string(&snap).unwrap();
        assert!(
            !s.contains("follows"),
            "a whole snapshot is unchanged on the wire"
        );
        assert!(s.contains("\"type\":\"camera-snapshot\""));
        assert!(s.contains("\"flightId\":99"));
        assert!(s.contains("\"vesselName\":\"Perf Test 1\""));
        assert!(s.contains("\"operatorWidth\":768"));
        assert!(s.contains("\"renderWidth\":384"));
        assert!(s.contains("\"supportsZoom\":true"));
        assert!(s.contains("\"supportsPan\":false"));
        assert!(s.contains("\"fovMin\":30"));
        assert!(s.contains("\"fovMax\":100"));
        assert!(s.contains("\"viewerQuality\":\"half\""));
        assert!(s.contains("\"qualityLimitedBy\":\"throttled\""));
    }

    /// Unset quality fields are omitted (not null) so the TypeScript
    /// optional-field bindings match the wire, and old payloads without
    /// the fields still deserialize (serde defaults).
    #[test]
    fn camera_state_quality_fields_optional() {
        let state = CameraState {
            flight_id: 1,
            lifecycle: CameraLifecycle::Active,
            kind: CameraKind::Part,
            capture_state: CaptureState::Full,
            kerbal_persistent_id: None,
            command_centre: None,
            command_track: None,
            crew_location: None,
            track_mode: TrackMode::None,
            part_name: String::new(),
            part_title: String::new(),
            camera_name: String::new(),
            vessel_name: String::new(),
            layers: vec![],
            operator_layers: vec![],
            render_width: 0,
            render_height: 0,
            operator_width: 0,
            operator_height: 0,
            max_width: None,
            max_height: None,
            supports_zoom: false,
            fov: 0.0,
            fov_min: 0.0,
            fov_max: 0.0,
            supports_pan: false,
            pan_yaw: 0.0,
            pan_pitch: 0.0,
            pan_yaw_min: 0.0,
            pan_yaw_max: 0.0,
            pan_pitch_min: 0.0,
            pan_pitch_max: 0.0,
            encoder_bitrate_bps: 0,
            target_bitrate_bps: 0,
            degrade_level: 0.0,
            viewer_quality: None,
            quality_limited_by: None,
            ..Default::default()
        };
        let s = serde_json::to_string(&state).unwrap();
        assert!(!s.contains("viewerQuality"), "got {s}");
        assert!(!s.contains("qualityLimitedBy"), "got {s}");

        // Pre-quality-surface JSON (no fields at all) still parses.
        let old = s.clone();
        let back: CameraState = serde_json::from_str(&old).unwrap();
        assert_eq!(back.viewer_quality, None);
        assert_eq!(back.quality_limited_by, None);
    }

    #[test]
    fn camera_state_rate_fields_use_camel_case_keys() {
        let state = CameraState {
            flight_id: 1,
            target_capture_fps: 30.0,
            displayed: true,
            capture_fps: 29.5,
            sent_fps: 28.0,
            keyframes_sent: 4,
            keyframe_requests_received: 2,
            display_size: Some(DisplaySizeReport {
                width: 640,
                height: 360,
                age_ms: 120,
            }),
            ..Default::default()
        };
        let v = serde_json::to_value(&state).unwrap();
        assert_eq!(v["targetCaptureFps"], 30.0);
        assert_eq!(v["displayed"], true);
        assert_eq!(v["captureFps"], 29.5);
        assert_eq!(v["sentFps"], 28.0);
        assert_eq!(v["keyframesSent"], 4);
        assert_eq!(v["keyframeRequestsReceived"], 2);
        assert_eq!(v["displaySize"]["width"], 640);
        assert_eq!(v["displaySize"]["height"], 360);
        assert_eq!(v["displaySize"]["ageMs"], 120);

        let none = serde_json::to_value(CameraState::default()).unwrap();
        assert!(
            none.get("displaySize").is_none(),
            "nothing held -> omitted, matching the optional TS binding"
        );
    }

    /// A payload from a sidecar predating the rate fields still parses.
    #[test]
    fn camera_state_without_rate_fields_parses() {
        let mut v = serde_json::to_value(CameraState::default()).unwrap();
        let obj = v.as_object_mut().unwrap();
        for key in [
            "targetCaptureFps",
            "displayed",
            "captureFps",
            "sentFps",
            "keyframesSent",
            "keyframeRequestsReceived",
            "displaySize",
        ] {
            obj.remove(key);
        }
        let back: CameraState = serde_json::from_value(v).unwrap();
        assert_eq!(back.target_capture_fps, 0.0);
        assert!(!back.displayed);
        assert_eq!(back.keyframes_sent, 0);
        assert!(back.display_size.is_none());
    }

    #[test]
    fn set_quality_roundtrips() {
        let msg = ClientMessage::SetQuality(SetQualityPayload {
            flight_id: 7,
            preset: Some(QualityPreset::Quarter),
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"set-quality\""));
        assert!(s.contains("\"flightId\":7"));
        assert!(s.contains("\"preset\":\"quarter\""));
        match serde_json::from_str::<ClientMessage>(&s).unwrap() {
            ClientMessage::SetQuality(p) => {
                assert_eq!(p.flight_id, 7);
                assert_eq!(p.preset, Some(QualityPreset::Quarter));
            }
            _ => panic!("wrong variant"),
        }

        // Auto: explicit null AND an absent field both clear the preset.
        let null_preset = r#"{"type":"set-quality","content":{"flightId":7,"preset":null}}"#;
        match serde_json::from_str::<ClientMessage>(null_preset).unwrap() {
            ClientMessage::SetQuality(p) => assert_eq!(p.preset, None),
            _ => panic!("wrong variant"),
        }
        let absent_preset = r#"{"type":"set-quality","content":{"flightId":7}}"#;
        match serde_json::from_str::<ClientMessage>(absent_preset).unwrap() {
            ClientMessage::SetQuality(p) => assert_eq!(p.preset, None),
            _ => panic!("wrong variant"),
        }

        // Garbage preset values are a parse error, not a silent default.
        let garbage = r#"{"type":"set-quality","content":{"flightId":7,"preset":"8k"}}"#;
        assert!(serde_json::from_str::<ClientMessage>(garbage).is_err());
    }

    #[test]
    fn quality_preset_levels_roundtrip() {
        for preset in [
            QualityPreset::Full,
            QualityPreset::ThreeQuarter,
            QualityPreset::Half,
            QualityPreset::Quarter,
        ] {
            assert_eq!(
                QualityPreset::from_viewer_level(preset.viewer_level()),
                Some(preset)
            );
            assert!(preset.scale() <= 1.0, "presets can only lower quality");
        }
        assert_eq!(QualityPreset::from_viewer_level(4), None);
    }

    #[test]
    fn set_degrade_roundtrips() {
        let msg = ClientMessage::SetDegrade(SetDegradePayload {
            flight_id: 7,
            level: 0.65,
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"set-degrade\""));
        assert!(s.contains("\"flightId\":7"));
        assert!(s.contains("\"level\":0.65"));
        let back: ClientMessage = serde_json::from_str(&s).unwrap();
        match back {
            ClientMessage::SetDegrade(p) => {
                assert_eq!(p.flight_id, 7);
                assert!((p.level - 0.65).abs() < f32::EPSILON);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn set_fov_roundtrips() {
        let msg = ClientMessage::SetFov(SetFovPayload {
            flight_id: 42,
            fov: 35.5,
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"set-fov\""));
        assert!(s.contains("\"flightId\":42"));
        assert!(s.contains("\"fov\":35.5"));
        let back: ClientMessage = serde_json::from_str(&s).unwrap();
        match back {
            ClientMessage::SetFov(p) => {
                assert_eq!(p.flight_id, 42);
                assert!((p.fov - 35.5).abs() < f32::EPSILON);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn set_pan_rate_roundtrips() {
        let msg = ClientMessage::SetPanRate(SetPanRatePayload {
            flight_id: 42,
            yaw_rate: 0.5,
            pitch_rate: -1.0,
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"set-pan-rate\""));
        assert!(s.contains("\"flightId\":42"));
        assert!(s.contains("\"yawRate\":0.5"));
        assert!(s.contains("\"pitchRate\":-1"));
        let back: ClientMessage = serde_json::from_str(&s).unwrap();
        match back {
            ClientMessage::SetPanRate(p) => {
                assert_eq!(p.flight_id, 42);
                assert!((p.yaw_rate - 0.5).abs() < f32::EPSILON);
                assert!((p.pitch_rate - -1.0).abs() < f32::EPSILON);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn set_zoom_rate_roundtrips() {
        let msg = ClientMessage::SetZoomRate(SetZoomRatePayload {
            flight_id: 7,
            rate: 1.0,
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"set-zoom-rate\""));
        assert!(s.contains("\"flightId\":7"));
        assert!(s.contains("\"rate\":1"));
        let back: ClientMessage = serde_json::from_str(&s).unwrap();
        match back {
            ClientMessage::SetZoomRate(p) => {
                assert_eq!(p.flight_id, 7);
                assert!((p.rate - 1.0).abs() < f32::EPSILON);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn ping_pong_roundtrip() {
        let ping = serde_json::to_string(&ServerMessage::Ping).unwrap();
        assert_eq!(ping, r#"{"type":"ping"}"#);
        let pong = serde_json::to_string(&ClientMessage::Pong).unwrap();
        assert_eq!(pong, r#"{"type":"pong"}"#);
        // Pong should parse back
        let back: ClientMessage = serde_json::from_str(&pong).unwrap();
        assert!(matches!(back, ClientMessage::Pong));
    }

    #[test]
    fn disconnect_roundtrip() {
        let s = serde_json::to_string(&ClientMessage::Disconnect).unwrap();
        assert_eq!(s, r#"{"type":"disconnect"}"#);
        let back: ClientMessage = serde_json::from_str(&s).unwrap();
        assert!(matches!(back, ClientMessage::Disconnect));
    }

    #[test]
    fn unknown_client_variants_fail_cleanly() {
        let bad = r#"{"type":"nonexistent","content":{}}"#;
        let parsed: Result<ClientMessage, _> = serde_json::from_str(bad);
        assert!(parsed.is_err());
    }

    #[test]
    fn hello_unit_variant_omits_content() {
        let msg = ClientMessage::Hello;
        let s = serde_json::to_string(&msg).unwrap();
        // Adjacent-tagged unit variants don't emit a content field.
        assert_eq!(s, r#"{"type":"hello"}"#);
    }

    #[test]
    fn adaptive_shed_serialises_kebab_type() {
        let msg = ServerMessage::AdaptiveShed(AdaptiveShedPayload {
            level: 2,
            ksp_fps: 16.5,
            reason: "ksp-fps-low".into(),
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"adaptive-shed\""));
        assert!(s.contains("\"kspFps\":16.5"));
    }

    #[test]
    fn subscribe_roundtrips() {
        let msg = ClientMessage::Subscribe(FlightIdPayload { flight_id: 7 });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"subscribe\""));
        assert!(s.contains("\"flightId\":7"));
        assert!(matches!(
            serde_json::from_str::<ClientMessage>(&s).unwrap(),
            ClientMessage::Subscribe(FlightIdPayload { flight_id: 7 })
        ));
    }

    #[test]
    fn unsubscribe_roundtrips() {
        let msg = ClientMessage::Unsubscribe(FlightIdPayload { flight_id: 7 });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"unsubscribe\""));
        assert!(s.contains("\"flightId\":7"));
        assert!(matches!(
            serde_json::from_str::<ClientMessage>(&s).unwrap(),
            ClientMessage::Unsubscribe(FlightIdPayload { flight_id: 7 })
        ));
    }

    #[test]
    fn error_carries_camera_and_reason_code() {
        let refused = ServerMessage::Error(ErrorPayload {
            message: "no free slot".into(),
            flight_id: Some(42),
            code: Some(ErrorCode::NoFreeSlot),
            ..Default::default()
        });
        let s = serde_json::to_string(&refused).unwrap();
        assert!(s.contains("\"type\":\"error\""));
        assert!(s.contains("\"flightId\":42"));
        assert!(s.contains("\"code\":\"no-free-slot\""));
    }

    #[test]
    fn plain_error_omits_camera_and_code() {
        let plain = ServerMessage::Error(ErrorPayload {
            message: "parse failed".into(),
            ..Default::default()
        });
        let s = serde_json::to_string(&plain).unwrap();
        assert!(!s.contains("flightId"), "{s}");
        assert!(!s.contains("code"), "{s}");
        // A payload from an older sidecar still parses.
        let old: ErrorPayload =
            serde_json::from_str(r#"{"message":"m","source":"sidecar"}"#).unwrap();
        assert_eq!((old.flight_id, old.code), (None, None));
    }

    #[test]
    fn slot_map_assigned_and_freed_roundtrip() {
        let assigned = ServerMessage::SlotMap(SlotMapPayload {
            mid: "1".into(),
            flight_id: Some(42),
        });
        let s = serde_json::to_string(&assigned).unwrap();
        assert!(s.contains("\"type\":\"slot-map\""));
        assert!(s.contains("\"mid\":\"1\""));
        assert!(s.contains("\"flightId\":42"));

        // Freed: flightId serialises as null (not omitted) so the client
        // distinguishes "slot freed" from a malformed message.
        let freed = ServerMessage::SlotMap(SlotMapPayload {
            mid: "1".into(),
            flight_id: None,
        });
        let s = serde_json::to_string(&freed).unwrap();
        assert!(s.contains("\"flightId\":null"));
        match serde_json::from_str::<ServerMessage>(&s).unwrap() {
            ServerMessage::SlotMap(p) => {
                assert_eq!(p.mid, "1");
                assert_eq!(p.flight_id, None);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn set_throttle_main_screen_roundtrips() {
        let msg =
            ClientMessage::SetThrottleMainScreen(SetThrottleMainScreenPayload { enabled: true });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"set-throttle-main-screen\""));
        assert!(s.contains("\"enabled\":true"));
        let back: ClientMessage = serde_json::from_str(&s).unwrap();
        match back {
            ClientMessage::SetThrottleMainScreen(p) => assert!(p.enabled),
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn settings_state_roundtrips() {
        let msg = ServerMessage::SettingsState(SettingsStatePayload {
            throttle_main_screen: false,
            capture_ut: None,
            capture_epoch: None,
            time_warp_rate: None,
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"settings-state\""));
        assert!(s.contains("\"throttleMainScreen\":false"));
        let back: ServerMessage = serde_json::from_str(&s).unwrap();
        match back {
            ServerMessage::SettingsState(p) => assert!(!p.throttle_main_screen),
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn scene_state_changed_roundtrips() {
        let msg = ServerMessage::SceneStateChanged(SceneStateChangedPayload { in_flight: false });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"type\":\"scene-state-changed\""));
        assert!(s.contains("\"inFlight\":false"));
        let back: ServerMessage = serde_json::from_str(&s).unwrap();
        match back {
            ServerMessage::SceneStateChanged(p) => assert!(!p.in_flight),
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn settings_state_omits_absent_capture_fields() {
        /* No clock present => the three optional fields never hit the wire. */
        let msg = ServerMessage::SettingsState(SettingsStatePayload {
            throttle_main_screen: true,
            capture_ut: None,
            capture_epoch: None,
            time_warp_rate: None,
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(!s.contains("captureUt"));
        assert!(!s.contains("captureEpoch"));
        assert!(!s.contains("timeWarpRate"));
    }

    #[test]
    fn settings_state_without_capture_fields_deserializes_to_none() {
        /* Old-sidecar shape: only throttleMainScreen present. */
        let json = r#"{"type":"settings-state","content":{"throttleMainScreen":false}}"#;
        let back: ServerMessage = serde_json::from_str(json).unwrap();
        match back {
            ServerMessage::SettingsState(p) => {
                assert_eq!(p.capture_ut, None);
                assert_eq!(p.capture_epoch, None);
                assert_eq!(p.time_warp_rate, None);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn settings_state_capture_fields_roundtrip() {
        let msg = ServerMessage::SettingsState(SettingsStatePayload {
            throttle_main_screen: false,
            capture_ut: Some(12345.5),
            capture_epoch: Some(7),
            time_warp_rate: Some(4.0),
        });
        let s = serde_json::to_string(&msg).unwrap();
        assert!(s.contains("\"captureUt\":12345.5"));
        assert!(s.contains("\"captureEpoch\":7"));
        assert!(s.contains("\"timeWarpRate\":4.0"));
        let back: ServerMessage = serde_json::from_str(&s).unwrap();
        match back {
            ServerMessage::SettingsState(p) => {
                assert_eq!(p.capture_ut, Some(12345.5));
                assert_eq!(p.capture_epoch, Some(7));
                assert_eq!(p.time_warp_rate, Some(4.0));
            }
            _ => panic!("wrong variant"),
        }
    }

    /// Pre-kerbal-camera JSON (none of `kind` / `kerbalPersistentId` /
    /// `crewLocation` present) still deserialises, defaulting to a part
    /// camera. Back-compat guarantee for the sidecar's own history and
    /// any consumer built against the pre-Stage-2 wire shape.
    #[test]
    fn camera_state_without_kerbal_fields_defaults_to_part() {
        let json = r#"{
            "flightId": 1,
            "partName": "",
            "partTitle": "",
            "cameraName": "",
            "vesselName": "",
            "layers": [],
            "operatorLayers": [],
            "renderWidth": 0,
            "renderHeight": 0,
            "operatorWidth": 0,
            "operatorHeight": 0,
            "supportsZoom": false,
            "fov": 0.0,
            "fovMin": 0.0,
            "fovMax": 0.0,
            "supportsPan": false,
            "panYaw": 0.0,
            "panPitch": 0.0,
            "panYawMin": 0.0,
            "panYawMax": 0.0,
            "panPitchMin": 0.0,
            "panPitchMax": 0.0,
            "encoderBitrateBps": 0,
            "targetBitrateBps": 0,
            "degradeLevel": 0.0
        }"#;
        let back: CameraState = serde_json::from_str(json).unwrap();
        assert_eq!(back.kind, CameraKind::Part);
        assert_eq!(back.kerbal_persistent_id, None);
        assert_eq!(back.crew_location, None);
        assert_eq!(back.max_width, None);
        assert_eq!(back.max_height, None);
    }

    /// A kerbal face camera round-trips its `kind`, `kerbalPersistentId`
    /// and `crewLocation` intact, honoring the camelCase wire names.
    #[test]
    fn kerbal_camera_state_roundtrips() {
        let state = CameraState {
            flight_id: 42,
            lifecycle: CameraLifecycle::Active,
            kind: CameraKind::Kerbal,
            capture_state: CaptureState::Full,
            kerbal_persistent_id: Some(123456),
            command_centre: None,
            command_track: None,
            crew_location: Some(CrewLocation::Eva),
            track_mode: TrackMode::None,
            part_name: String::new(),
            part_title: String::new(),
            camera_name: "Jebediah Kerman".into(),
            vessel_name: "Perf Test 1".into(),
            layers: vec![],
            operator_layers: vec![],
            render_width: 0,
            render_height: 0,
            operator_width: 0,
            operator_height: 0,
            max_width: None,
            max_height: None,
            supports_zoom: false,
            fov: 0.0,
            fov_min: 0.0,
            fov_max: 0.0,
            supports_pan: false,
            pan_yaw: 0.0,
            pan_pitch: 0.0,
            pan_yaw_min: 0.0,
            pan_yaw_max: 0.0,
            pan_pitch_min: 0.0,
            pan_pitch_max: 0.0,
            encoder_bitrate_bps: 0,
            target_bitrate_bps: 0,
            degrade_level: 0.0,
            viewer_quality: None,
            quality_limited_by: None,
            ..Default::default()
        };
        let s = serde_json::to_string(&state).unwrap();
        assert!(s.contains("\"kind\":\"kerbal\""));
        assert!(s.contains("\"kerbalPersistentId\":123456"));
        assert!(s.contains("\"crewLocation\":\"eva\""));

        let back: CameraState = serde_json::from_str(&s).unwrap();
        assert_eq!(back.kind, CameraKind::Kerbal);
        assert_eq!(back.kerbal_persistent_id, Some(123456));
        assert_eq!(back.crew_location, Some(CrewLocation::Eva));
    }
}
