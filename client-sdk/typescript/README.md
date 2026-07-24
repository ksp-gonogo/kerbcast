# @ksp-gonogo/kerbcast

TypeScript client for the kerbcast sidecar. Used by browsers and other
clients that consume the sidecar's H.264 streams.

`KerbcastClient` owns the WebRTC peer, the `kerbcast-control` data
channel, and the per-camera state cache and `MediaStream`s.
Consumers get a high-level RPC surface plus typed event
subscriptions. The wire-format types are still exported for anyone
rolling their own transport.

The Rust crate and this package ship the same SemVer version,
bumped together by `./scripts/bump-version.sh`.

Building a React app? Use
[`@ksp-gonogo/kerbcast-react`](https://www.npmjs.com/package/@ksp-gonogo/kerbcast-react),
which wraps this client in a ready-made `CameraFeed` component plus
hooks. This package is its core dependency; reach for it directly when
you want the raw client, a non-React consumer, or your own transport.

## Install

Hosted on public npm. No registry route or auth needed:

```sh
npm add @ksp-gonogo/kerbcast
```

## Use

```ts
import { KerbcastClient, Layer } from "@ksp-gonogo/kerbcast";

const client = new KerbcastClient({ host: "192.168.1.74", port: 8088 });
await client.connect();

const cam = client.camera(2592004302);
await cam.setLayers([Layer.Near, Layer.Scaled]);
await cam.setFov(35);
await cam.setRenderSize(384, 384);
await cam.setDegrade(0.5);

// Pan/tilt and zoom, when the camera's mount supports it.
await cam.setPan(10, -5); // absolute yaw/pitch in degrees
await cam.setPanRate(0.5, 0); // persistent velocity, -1..1; 0,0 stops
await cam.setZoomRate(1); // +1 zooms in, holds until superseded

const videoEl = document.querySelector("video")!;
videoEl.srcObject = cam.mediaStream;

cam.on("change", (state) => {
  console.log("camera state", state);
});
client.on("adaptive-shed", (event) => {
  console.log("shed level", event.level, "ksp fps", event.kspFps);
});

// Tear down when done.
client.disconnect();
```

### Pre-handshake discovery

```ts
const cameras = await client.discover();
// Pick the cameras you want, then connect with that subset.
await client.connect(cameras.slice(0, 2).map((c) => c.flightId));
```

### Lower-level access

The wire-format types are also exported for consumers that want to
roll their own transport (a Node.js consumer outside a browser, a
test harness, an alternative-language gateway, etc).

```ts
import type { ClientMessage } from "@ksp-gonogo/kerbcast";

declare const controlChannel: RTCDataChannel;

const setLayers: ClientMessage = {
  type: "set-layers",
  content: { flightId: 2592004302, layers: ["NEAR", "SCALED"] },
};
controlChannel.send(JSON.stringify(setLayers));
```

## Per-camera capabilities

`CameraState` includes capability flags so consumers render only the
controls each part actually supports.

- `supportsZoom`, `fov`, `fovMin`, `fovMax`. 19 of 21 stock Hullcam
  VDS parts support runtime FoV via `MuMechModuleHullCameraZoom`.
- `supportsPan`, `panYawMin/Max`, `panPitchMin/Max`. Whether this
  camera's mount steers. Pan/tilt is fully wired end to end (`setPan`,
  `setPanRate`, and the `PanZoomController` helper below); this flag
  tells you whether a given part will act on it. Stock Hullcam VDS
  mounts are fixed and report `false`; steerable mounts report `true`
  with their travel limits. Hide the pan controls when `false`.
- `encoderBitrateBps`, `targetBitrateBps`. Current encoder bitrate
  and the REMB-driven target. They diverge briefly when receivers'
  bandwidth estimates move.
- `degradeLevel`. Effective signal-degradation level (max across
  subscribers). Sidecar applies it via bitrate squeeze plus frame
  skip, producing the macroblocking and stuttering aesthetic of
  in-game CommNet signal loss. Also a real CPU optimisation when
  every consumer wants degraded video.
- `kind` (`"part"` | `"kerbal"`) and, for a kerbal face camera,
  `crewLocation` (`"seat"` | `"eva"`). Distinguish crew face cameras
  from part cameras.
- `trackMode` (`"none"` | `"activeVessel"` | `"target"`). Server-authoritative
  auto-track state on a pan+zoom camera, so every consumer reflects the same
  tracking state.

## Auto-resolution and targeting

- `client.reportDisplaySize(flightId, width, height)`. Report a consumer's
  displayed pixel size for a camera; the sidecar takes the MAX across consumers
  to size the encode (distinct from `setRenderSize`, the shared operator cap).
  The react feed primitives self-measure and call this for you.
- `client.setTrackTarget(flightId, mode)`. Ask a pan+zoom camera to auto-track
  the active vessel or its target (`TrackMode`), or `"none"` to stop. The
  chosen mode is published back on `CameraState.trackMode`.

## Pan/zoom input helper

Driving pan and zoom from a gamepad stick, nudge buttons, or an FoV
slider means rate deduplication, an analog deadzone, optimistic
accumulators for discrete nudges, a debounced slider, and echo-sync
while idle. `PanZoomController` is a headless state machine that handles
all of that with no DOM or React dependency. A `KerbcastCameraHandle`
satisfies its command sink structurally, so pass a camera straight in:

```ts
import { PanZoomController } from "@ksp-gonogo/kerbcast";

const cam = client.camera(flightId);
const ctrl = new PanZoomController(cam);

ctrl.setPanRate(stickX, stickY); // analog stick
ctrl.nudgePan(1, 0); // discrete "pan right" button
ctrl.nudgeZoom(-1); // discrete "zoom in" button

// Keep the controller's view in sync with echoed camera state.
cam.on("change", (s) => ctrl.syncFromState(s));

ctrl.stop(); // on teardown
```

## Multi-language

`typeshare` also generates Kotlin, Swift, Scala, and Go. Those
land alongside `client-sdk/typescript/` as consumers appear. A C#
binding is pending an upstream `typeshare` C# generator or a
hand-written shim.

## License

[MIT](./LICENSE). This SDK is the interface to kerbcast, so it is permissive:
build what you like against it. The kerbcast mod itself is
[CC BY-NC-SA 4.0](https://github.com/ksp-gonogo/kerbcast/blob/main/LICENSE).

Versions published before 1.6.0 carry the older CC BY-NC-SA 4.0 licence.

## Versioning

[SemVer](https://semver.org/) against the wire format and client
API. See [CHANGELOG.md](https://github.com/ksp-gonogo/kerbcast/blob/main/client-sdk/typescript/CHANGELOG.md).
While the protocol is at `0.x`, any minor bump may require consumer
updates. Strict SemVer applies at `1.0.0`.
