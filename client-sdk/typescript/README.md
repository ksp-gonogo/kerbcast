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

Commands can be sent as soon as `connect` resolves. Any sent before the
control channel has opened wait for it, and reject if it has not opened
within 10 s or the client disconnects first.

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
- `kind` (`"part"` | `"kerbal"` | `"command"`) and, for a kerbal face
  camera, `crewLocation` (`"seat"` | `"eva"`). Distinguish crew face
  cameras and command cameras (see [Command cameras](#command-cameras))
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

## Command cameras

A command camera is a camera that stands on the ground (or rides a craft)
and watches a craft from there, the way a tracking station would. The
game's settings give each scene one, the Launchpad camera, and a client can
create more at any vantage. Each appears in `client.cameras` with
`kind: "command"` and a `commandCentre` describing where it stands and what
it watches.

A created camera lasts for the rest of the game scene (a flight, or the
Space Center while the game's command camera setting is on); leaving the
scene removes it. Every connected client sees it, and any of them can aim
or destroy it.

The vantage is one of:

- `launchSite`: beside a launch site. `site` names it (`"LaunchPad"`,
  `"Runway"`, a Making History or Kerbal Konstructs site); omit it for the
  active craft's launch site. `distance` (default 500 m), `bearing` from the
  site (default 180, due south) and `height` (default 5 m)
- `station`: at a CommNet ground station, by its node name (`"KSC"`), with
  `height` above it (default 30 m)
- `surface`: at any `latitude` and `longitude` on a `body`, `height` metres
  above the ground there (above the sea over water; default 5 m)
- `vessel`: riding the craft with that `persistentId`. Keep that craft
  beyond physics range of the active one, or the lens sits inside its parts

`createCommandCamera` resolves with the new camera's `CameraState` once the
game has built it and reported what it is watching, normally within a
second or two, so `commandCentre.targetPersistentId` and
`commandCentre.targets` are already filled in. If that report has not come
5 s after the camera appeared, it resolves with the state as it stands. The
request carries a token
(random unless you pass `token`) that the camera reports back as
`commandCentre.token`; that is how the promise finds it, and how any other
code can. A vantage the game can not find (an unknown body, site, station
or craft) builds nothing, and the promise rejects after `timeoutMs`
(default 35 s, since the game waits up to 30 s for a site to be placed).

Lens reach depends on the vantage: a launch site camera has the tracking
camera's lens at every Tracking Station level, a station's lens follows the
Tracking Station's level in career, and surface and vessel vantages zoom
fully.

### A launchpad camera

```ts
import { CommandVantageKind, KerbcastClient } from "@ksp-gonogo/kerbcast";

const client = new KerbcastClient({ host: "192.168.1.74", port: 8088 });
await client.connect([], { slots: 2 });

// 500 m south of the active craft's launch site, 5 m up.
const pad = await client.createCommandCamera(
  { kind: CommandVantageKind.LaunchSite },
  { name: "Pad south" },
);

const video = document.querySelector("video")!;
client.camera(pad.flightId).on("stream", (stream) => {
  video.srcObject = stream;
});
await client.subscribe(pad.flightId);

// Later: the feed ends for every client.
await client.destroyCommandCamera(pad.flightId);
```

`{ kind: CommandVantageKind.LaunchSite, site: "Runway", distance: 800,
bearing: 90, height: 20 }` stands one 800 m east of the runway, 20 m up.

### Between two vantages

A camera can watch a point instead of its craft. Here one stands on a rise
west of the KSC and watches the launch pad, then a particular craft, then
whichever craft is active. The coordinates are illustrative.

```ts
import {
  CommandLookAtKind,
  CommandVantageKind,
  type KerbcastClient,
} from "@ksp-gonogo/kerbcast";

declare const client: KerbcastClient; // connected as above

const hill = await client.createCommandCamera(
  {
    kind: CommandVantageKind.Surface,
    body: "Kerbin",
    latitude: -0.08,
    longitude: -74.75,
    height: 10,
  },
  {
    name: "West ridge",
    // Look at the pad: altitude is metres above sea level, and the margin
    // keeps 60 m of view around the point.
    lookAt: {
      kind: CommandLookAtKind.Surface,
      body: "Kerbin",
      latitude: -0.0972,
      longitude: -74.5577,
      altitude: 70,
      framingMarginM: 60,
    },
  },
);

// Now follow a particular craft, by Vessel.persistentId.
await client.setCommandLookAt(hill.flightId, {
  kind: CommandLookAtKind.Vessel,
  persistentId: 4000000001,
});

// Back to its own target (the active craft), and pick another target.
await client.setCommandLookAt(hill.flightId, null);
await client.setCommandTarget(hill.flightId, 4000000001);
```

A station watching a craft is the same with
`{ kind: CommandVantageKind.Station, station: "KSC" }` and
`targetPersistentId` at creation (or `setCommandTarget` later). The craft
is always drawn where it truly is, so a look-at point that is not the
craft shows it off centre, or not at all.

A command authority registered in game (`KerbcastControl.SetCommandAuthority`)
aims every command camera itself while it holds the registration; look-at
points apply again once it clears.

### In game (C#)

Another KSP mod can do the same through `Kerbcast.KerbcastControl`, with
no sidecar round trip:

```csharp
using Kerbcast;

// Returns the flightId the feed will have, or 0 when refused.
uint pad = KerbcastControl.CreateCommandCamera(CommandVantage.LaunchSite());

uint ridge = KerbcastControl.CreateCommandCamera(
    CommandVantage.Surface("Kerbin", -0.08, -74.75, height: 10), "West ridge");
KerbcastControl.SetCommandLookAt(ridge, CommandAim.Surface("Kerbin", -0.0972, -74.5577, 70),
    framingMarginM: 60);
KerbcastControl.SetCommandLookAt(ridge, null);
KerbcastControl.SetCommandTarget(ridge, vessel.persistentId);

KerbcastControl.DestroyCommandCamera(ridge);
```

- `CommandVantage.LaunchSite(site, distance, bearing, height)`,
  `CommandVantage.Station(nodeName, height)`,
  `CommandVantage.Surface(body, latitude, longitude, height)` and
  `CommandVantage.Vessel(persistentId)` mirror the vantage kinds above, with
  the same defaults
- `CreateCommandCamera(vantage, name = null, targetPersistentId = 0)`
  returns the flightId at once; the feed appears when the vantage resolves.
  `SetCommandTarget`, `SetCommandLookAt` and `SetCommandAim` work on it
  before then too
- `SetCommandLookAt(flightId, aim, framingMarginM = 0)` takes any
  `CommandAim`, including `Orbit` and `PositionAt`
- `DestroyCommandCamera(flightId)` returns false for the settings camera or
  an unknown id
- `CommandFeeds()` lists every built command camera, with `Created` set on
  the ones made at runtime

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
