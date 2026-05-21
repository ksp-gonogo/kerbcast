# Changelog

Version line is shared with the Rust sidecar (`sidecar/Cargo.toml`).
CI verifies the two agree before any publish.

## 0.2.0 — 2026-05-21

New `set-degrade` client message and matching `degradeLevel` field
on `CameraState`. Lets consumers request artificial signal
degradation per camera (e.g. when in-game CommNet signal weakens);
sidecar applies `max` across active subscribers and squeezes the
encoder bitrate + skips a fraction of frames to produce the
macroblocking + stuttering aesthetic of real signal loss. Also a
real CPU optimisation: if every consumer wants degraded video, the
encoder runs lighter.

```ts
const msg: ClientMessage = {
  type: "set-degrade",
  content: { flightId: 2592004302, level: 0.7 },
};
dc.send(JSON.stringify(msg));
```

### Wire-format additions

- `ClientMessage::SetDegrade { flightId, level }` — level clamps to
  `[0.0, 1.0]`; out-of-range values are clamped by the sidecar.
- `CameraState.degradeLevel` — sidecar-pushed effective degrade
  (max across subscribers).

Additive: existing 0.1.0 consumers keep working — they just don't
see the new field unless they read it.

## 0.1.0 — 2026-05-21

Initial release as `@jonpepler/kerbcam` on GitHub Packages.
TypeScript bindings for the kerbcam sidecar's WebRTC data-channel
protocol, generated from Rust types via
[typeshare](https://github.com/1Password/typeshare).

### Client → server messages

- `hello` — handshake; server replies with `Hello` + `CameraSnapshot`.
- `set-layers` — operator layer mask per camera (`NEAR` / `SCALED` /
  `GALAXY`).
- `set-render-size` — operator capture dimensions per camera (even
  pixels, capped at the ring's allocated max).
- `set-fov` — operator FoV in degrees (silently rejected for fixed-FoV
  parts; clients should clamp to `fovMin`/`fovMax` from the camera's
  `CameraState`).
- `set-pan` — reserved for the future Hullcam VDS pan/tilt extension.
  Rejected today on every shipping part (`supportsPan == false`).
- `request-keyframe` — recover the H.264 stream after packet loss.

### Server → client messages

- `hello` — reply to client `hello` with sidecar version + encoder
  backend name.
- `camera-snapshot` — full state of every currently-attached camera.
  Sent on handshake and after structural changes (vessel switch, ring
  added / removed).
- `camera-state-changed` — single-camera delta on operator request or
  adaptive event.
- `adaptive-shed` — KSP-FPS adaptive shedding level changed; carries a
  human-readable `reason` so client UIs can show *why* quality
  changed rather than just *that* it did.
- `error` — malformed / invalid client message.

### Per-camera capabilities

`CameraState` exposes feature flags so clients can render controls
only for what each part advertises:

- `supportsZoom`, `fov`, `fovMin`, `fovMax` — true for parts whose
  Hullcam module is `MuMechModuleHullCameraZoom` (19 of 21 stock
  Hullcam VDS parts).
- `supportsPan`, `panYawMin`/`Max`, `panPitchMin`/`Max`, `panYaw`,
  `panPitch` — reserved; always false on shipping parts.
- `encoderBitrateBps`, `targetBitrateBps` — current encoder bitrate
  and REMB-driven target. Diverge briefly when receivers' bandwidth
  estimates move; converge after the consume loop's next bitrate
  threshold check.

### Wire format

JSON-per-message over an `RTCDataChannel` labelled `kerbcam-control`,
opened by the browser after SDP exchange completes. Messages use
adjacent tagging — `{ "type": "set-layers", "content": { ... } }` —
because that's the discriminator shape typeshare can faithfully model
across TypeScript / Kotlin / Swift / Scala / Go. Unit variants
(`hello`) carry no `content`.
