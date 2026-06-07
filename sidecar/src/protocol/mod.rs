//! WebRTC data-channel protocol between the sidecar and a streaming
//! client (browser / gonogo / future Kotlin or Swift consumer).
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
//! `@kerbcam/protocol`.
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

/// Layer mask. Mirrors `Kerbcam.CameraLayers` on the plugin side.
/// Receiving clients use this for both the rendered-layer status reports
/// and per-camera layer requests.
#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "UPPERCASE")]
pub enum Layer {
    Near,
    Scaled,
    Galaxy,
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

/// Per-camera snapshot pushed by the sidecar on every state change
/// (operator API call, adaptive shed, vessel change). Same shape served
/// by `GET /cameras` so client UIs can treat the two interchangeably.
///
/// Capability fields (`supports_zoom`, `supports_pan`) let clients
/// render controls only for features each part actually offers — a
/// fixed-FoV camera shouldn't get a zoom slider, a non-steerable one
/// shouldn't get a pan stick.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
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
    /// Whether the part supports pan/tilt (kerbcam-side mod extension —
    /// no stock Hullcam parts are steerable, but the extended mod adds
    /// pan to specific parts). False on every shipping part today;
    /// clients should hide pan controls until this flips true.
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
    /// Effective degrade level (max across subscribers' SetDegrade
    /// requests). 0.0 = perfect, 1.0 = max degradation. Applied
    /// alongside operator render-size + adaptive shed; the encoder
    /// multiplies its effective bitrate by `(1 - 0.7 * level)` and
    /// skips fan-out for a fraction of frames at high levels.
    pub degrade_level: f32,
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
    /// 0.0 = perfect quality, 1.0 = maximum degradation. Caps and
    /// is per-subscriber: the sidecar applies max across active
    /// subscribers so the noisiest consumer's request wins (same
    /// pattern as REMB picking the min bandwidth).
    pub level: f32,
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
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CameraSnapshotPayload {
    pub cameras: Vec<CameraState>,
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

#[typeshare]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub enum ErrorSource {
    #[default]
    Sidecar,
    Client,
}

#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ErrorPayload {
    pub message: String,
    #[serde(default)]
    pub source: ErrorSource,
}

/// Messages sent FROM the client TO the sidecar.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", content = "content", rename_all = "kebab-case")]
pub enum ClientMessage {
    /// First message on a new data channel. Sidecar replies with `Hello`
    /// + a `CameraSnapshot` so the client gets server version + every
    /// camera's full state on one round-trip.
    Hello,
    /// Override the operator layer mask for one camera. Server-wide
    /// state — all consumers see the change.
    SetLayers(SetLayersPayload),
    /// Override the operator render size for one camera. Even pixels
    /// only (H.264 chroma); server caps at the ring's allocated max.
    SetRenderSize(SetRenderSizePayload),
    /// Set the camera's field-of-view (degrees). Silently ignored for
    /// parts whose Hullcam module is the fixed base (`supportsZoom ==
    /// false`); clients are expected to clamp to `fovMin / fovMax`
    /// from the camera's `CameraState` before sending.
    SetFov(SetFovPayload),
    /// Pan/tilt the camera (yaw and pitch, both degrees from the
    /// part's resting forward). No stock Hullcam parts support this
    /// yet — the message is ignored until the planned mod extension
    /// adds steerable mounts to specific parts. Clients should hide
    /// pan controls until `supportsPan == true` for the camera.
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
    /// 1.0 = maximum degradation. Per-subscriber: the sidecar
    /// applies max across active subscribers (slowest consumer
    /// wins, same pattern as REMB bandwidth). Lets consumers signal
    /// to the sidecar "I want to look like the in-game CommNet
    /// antenna is struggling"; sidecar takes the opportunity to
    /// reduce bitrate + skip frames, which both creates the
    /// signal-loss aesthetic AND saves encoder CPU.
    SetDegrade(SetDegradePayload),
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
}

/// Messages sent FROM the sidecar TO the client.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", content = "content", rename_all = "kebab-case")]
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
}

#[cfg(test)]
mod tests {
    use super::*;

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
    fn camera_state_snapshot_uses_camel_case_keys() {
        let snap = ServerMessage::CameraSnapshot(CameraSnapshotPayload {
            cameras: vec![CameraState {
                flight_id: 99,
                lifecycle: CameraLifecycle::Active,
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
            }],
        });
        let s = serde_json::to_string(&snap).unwrap();
        assert!(s.contains("\"type\":\"camera-snapshot\""));
        assert!(s.contains("\"flightId\":99"));
        assert!(s.contains("\"vesselName\":\"Perf Test 1\""));
        assert!(s.contains("\"operatorWidth\":768"));
        assert!(s.contains("\"renderWidth\":384"));
        assert!(s.contains("\"supportsZoom\":true"));
        assert!(s.contains("\"supportsPan\":false"));
        assert!(s.contains("\"fovMin\":30"));
        assert!(s.contains("\"fovMax\":100"));
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
}
