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

/// Per-camera snapshot pushed by the sidecar on every state change
/// (operator API call, adaptive shed, vessel change). Same shape served
/// by `GET /cameras` so client UIs can treat the two interchangeably.
#[typeshare]
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CameraState {
    pub flight_id: u32,
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
pub struct FlightIdPayload {
    pub flight_id: u32,
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
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ErrorPayload {
    pub message: String,
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
    /// Request an IDR (keyframe) on the next encode tick. Browsers send
    /// this when they've dropped enough frames to be unable to decode
    /// the current P-frame chain. Sidecar forwards to the camera's
    /// encoder.
    RequestKeyframe(FlightIdPayload),
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
    /// Adaptive shedding level changed. Reason is human-readable
    /// ("ksp-fps-low", "ksp-fps-recovered") so client UIs can show
    /// *why* quality changed rather than just *that* it did.
    AdaptiveShed(AdaptiveShedPayload),
    /// Malformed / unknown client message. Includes the offending
    /// payload so the client can log it.
    Error(ErrorPayload),
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
            }],
        });
        let s = serde_json::to_string(&snap).unwrap();
        assert!(s.contains("\"type\":\"camera-snapshot\""));
        assert!(s.contains("\"flightId\":99"));
        assert!(s.contains("\"vesselName\":\"Perf Test 1\""));
        assert!(s.contains("\"operatorWidth\":768"));
        assert!(s.contains("\"renderWidth\":384"));
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
}
