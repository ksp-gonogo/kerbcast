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

Kerbcast streams H.264 only. A browser that offers none (some Firefox builds
ship without OpenH264) is refused, and `connect` rejects with an
`OfferRefusedError` whose `code` is `ErrorCode.CodecUnsupported` and whose
message says what to change.

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

### Encoded-frame transforms

`cam.setEncodedTransform(factory)` runs your transform on a camera's encoded
frames before the browser decodes them. It always runs before Kerbcast's
static and frame lock, so anything it does (holding frames back, dropping
them) shows up in `cam.mediaStream` with the static still drawn on top.

Kerbcast installs the transform on whichever receiver carries the camera. A
transform instance works on one receiver only, so you pass a factory:
Kerbcast calls it for a fresh instance when the camera is bound, again when a
slot rebind moves the camera to another receiver, and again after a
reconnect. A receiver the camera leaves goes back to passthrough, and frames
the old instance still holds are dropped. `setEncodedTransform(null)`
detaches. It works in both legacy and dynamic slot modes.

It needs `encodedTransforms: true` when the client is constructed, and
`setEncodedTransform` throws without it. The option gives every receiver a
passthrough transform from the start, because Chrome only honours a receiver
transform that was in place before negotiation. It costs a worker hop per
frame for every camera, so leave it off unless you use this. The passthrough
worker loads from a `blob:` URL, so a Content Security Policy must allow
`worker-src blob:`. `setEncodedTransform` also throws when the browser has no
install path; `client.encodedTransformPath` reports which one you will get:

- `"script"`: return an `RTCRtpScriptTransform` (Chrome, Safari and Firefox)
- `"streams"`: return a `TransformStream` of `RTCEncodedVideoFrame`, run on the main thread over `createEncodedStreams()` (older Chrome)

A one-second delay on the `"script"` path:

```ts
const client = new KerbcastClient({ host, port, encodedTransforms: true });
const worker = new Worker(new URL("./delay-worker.js", import.meta.url));

client.camera(2592004302).setEncodedTransform(({ flightId, path }) =>
  path === "script"
    ? new RTCRtpScriptTransform(worker, { flightId, delayMs: 1000 })
    : delayStream(1000),
);

// delay-worker.js
onrtctransform = ({ transformer }) => {
  const { delayMs } = transformer.options;
  transformer.readable
    .pipeThrough(
      new TransformStream({
        transform(frame, controller) {
          setTimeout(() => controller.enqueue(frame), delayMs);
        },
      }),
    )
    .pipeTo(transformer.writable)
    .catch(() => {}); // a replaced instance is aborted
};
```

`delayStream` is the same `TransformStream` built on the main thread. A
factory that throws, or returns the wrong kind for the path, is reported as a
client `error` event carrying the camera's `flightId`.

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
it watches. `commandCentre.siteKey` names the place the lens stands and
`siteKind` says what it is (`station`, `launchSite`, `surface` or `vessel`);
the key stays the same when the craft a `vessel` site rides docks, while
`vantagePersistentId` follows the craft it rides now. `stationId` is
deprecated.

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
  beyond physics range of the active one, or the lens sits inside its parts.
  If the craft docks into another (or is grabbed by a claw), the camera rides
  the combined craft. If the craft is destroyed or recovered, the camera is
  removed and its feed ends

`createCommandCamera` resolves with the new camera's `CameraState` once the
game has built it and reported what it is watching, normally within a
second or two, so `commandCentre.subjectPersistentId` and
`commandCentre.targets` are already filled in. If that report has not come
5 s after the camera appeared, it resolves with the state as it stands. The
request carries a token (random unless you pass `token`) that the camera
reports back as `commandCentre.token`; that is how the promise finds it,
and how any other code can. A vantage the game can not find (an unknown
body, site, station or craft) builds nothing, and the promise rejects after
`timeoutMs` (default 35 s, since the game waits up to 30 s for a site to be
placed).

Lens reach depends on the vantage: a launch site camera has the tracking
camera's lens at every Tracking Station level, a station's lens follows the
Tracking Station's level in career, and surface and vessel vantages zoom
fully.

### Aiming

`client.setCommandAim(flightId, aim)` points any command camera, and `null`
returns it to its default: the active craft in flight, the configured craft
at the Space Center. `createCommandCamera` takes the same `aim` as an option.
The aim is one of:

- `vessel`: follows the craft with that `persistentId` (any entry of
  `commandCentre.targets`, whatever its eligibility)
- `orbit`: follows a conic (`body`, `inclination`, `eccentricity`,
  `semiMajorAxis`, `lan`, `argumentOfPeriapsis` in degrees and metres,
  `meanAnomalyAtEpoch` in radians, `epoch`)
- `positionAt`: follows a craft coasting from a state you give: `position`
  and `velocity` as `[x, y, z]` relative to `body` at `ut`, z up, in KSP's
  fixed inertial frame, carried on ballistically under the body's gravity
  with no thrust. From a KSP `Orbit` that frame is always
  `Planetarium.Zup.LocalToWorld(orbit.getRelativePositionAtUT(ut))` (and the
  same for `getOrbitalVelocityAtUT`, `orbit.pos`, `orbit.vel`); the raw
  values are not, since KSP leaves Zup turned after a low flight. A browser
  client has no `Orbit`, so it takes these vectors from whatever mod or model
  hands it craft states in that frame. A velocity of zero no longer holds a
  point still (it falls); use `surface` for a fixed point. Before 1.12 this
  was Zup's own frame, carried on linearly
- `surface`: holds a point on a `body` (`latitude`, `longitude`, `altitude`
  in metres above sea level), turning with the planet
- `direction`: holds a direction from the camera's local horizon,
  `azimuth` degrees clockwise from north and `elevation` degrees above the
  horizon

The subject is the craft the camera draws (where it truly is, so an aim off
the craft shows it off centre), brackets, sizes its zoom by and reads out. A
`vessel` aim's craft is its subject; `orbit` and `positionAt` take an
optional `subjectPersistentId`; otherwise the default craft stays the
subject. A `direction` aim zooms for the subject's range, so the subject is
framed as it passes through. Every kind takes `framingMarginM`, metres of
view kept around the subject's own size. `commandCentre.aimKind` (absent for
the default) and `commandCentre.subjectPersistentId` report what the game
applied.

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

Here a camera stands on a rise west of the KSC and watches the launch pad,
then a particular craft, then the sky above the pad, then whichever craft
is active. The coordinates are illustrative.

```ts
import {
  CommandAimKind,
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
    // The pad: altitude is metres above sea level, and the margin keeps
    // 60 m of view around the subject.
    aim: {
      kind: CommandAimKind.Surface,
      body: "Kerbin",
      latitude: -0.0972,
      longitude: -74.5577,
      altitude: 70,
      framingMarginM: 60,
    },
  },
);

// Follow a particular craft, by Vessel.persistentId.
await client.setCommandAim(hill.flightId, {
  kind: CommandAimKind.Vessel,
  persistentId: 4000000001,
});

// Hold still, looking east and 30 degrees up, for a launch to climb into.
await client.setCommandAim(hill.flightId, {
  kind: CommandAimKind.Direction,
  azimuth: 90,
  elevation: 30,
});

// Back to the default: the active craft.
await client.setCommandAim(hill.flightId, null);
```

A station watching a craft is the same with
`{ kind: CommandVantageKind.Station, station: "KSC" }` and a `vessel` aim.

A `vessel` aim, or an `orbit` or `positionAt` aim's `subjectPersistentId`,
must name a craft in the camera's `commandCentre.targets`. One that is not
there (a craft merged into another by docking, or destroyed) makes
`setCommandAim` reject with a `CommandTargetUnknownError`; a call that races
the list changing comes back as an `error` event with code
`ErrorCode.CommandTargetUnknown`. Before the game has reported the camera's
list, the aim is sent and the game decides.

### The command lock

A mod in game can take a lock on every command camera's aim
(`KerbcastControl.SetCommandAuthority(owner)`). While it holds the lock only
its own aims are accepted: `setCommandAim` rejects with a
`CommandLockedError` (its `owner` names the holder) whenever the camera's
`commandTrack.authority` reports one, and a call that races the lock being
taken comes back as an `error` event with code `ErrorCode.CommandLocked`.
`createCommandCamera` still creates the camera, at its default aim. When the
lock is released every command camera returns to its default; aims set
before or during the lock are not restored.

The lock holds until its holder releases it (or the scene changes); it never
times out. A holder that stops calling for 5 s blanks every command feed
("Authority not responding") but keeps the lock, so aims from anyone else are
still refused. This is deliberate: a holder that delays commands (one that
applies signal delay, say) can not be bypassed while it is unresponsive.

### In game (C#)

Another KSP mod can do the same through `Kerbcast.KerbcastControl`, with
no sidecar round trip:

```csharp
using Kerbcast;

// Returns the flightId the feed will have, or 0 when refused.
uint pad = KerbcastControl.CreateCommandCamera(CommandVantage.LaunchSite());

uint ridge = KerbcastControl.CreateCommandCamera(
    CommandVantage.Surface("Kerbin", -0.08, -74.75, height: 10), "West ridge",
    CommandAim.Surface("Kerbin", -0.0972, -74.5577, 70, framingMarginM: 60));
KerbcastControl.SetCommandAim(ridge, CommandAim.Vessel(vessel.persistentId));
KerbcastControl.SetCommandAim(ridge, CommandAim.Direction(azimuth: 90, elevation: 30));
KerbcastControl.SetCommandAim(ridge, null); // back to the default

KerbcastControl.DestroyCommandCamera(ridge);
```

Holding the lock:

```csharp
// Re-take it on every scene load; a different owner is refused.
KerbcastControl.SetCommandAuthority("mission-control");

// Only calls passing the owner are accepted now, and each is a heartbeat.
KerbcastControl.SetCommandAim(pad, CommandAim.PositionAt("Kerbin", ut, pos, vel,
    subject: craft.persistentId, framingMarginM: uncertaintyM), "mission-control");
KerbcastControl.SetCommandAim(pad, CommandAim.Vessel(other)); // false: locked

// Every command camera returns to its default.
KerbcastControl.ClearCommandAuthority("mission-control");
```

- `CommandVantage.LaunchSite(site, distance, bearing, height)`,
  `CommandVantage.Station(nodeName, height)`,
  `CommandVantage.Surface(body, latitude, longitude, height)` and
  `CommandVantage.Vessel(persistentId)` mirror the vantage kinds above, with
  the same defaults
- `CommandAim.Vessel(persistentId, framingMarginM = 0)`,
  `Orbit(body, inc, e, sma, lan, argPe, meanAnomalyAtEpoch, epoch, subject = 0, framingMarginM = 0)`,
  `PositionAt(body, ut, pos, vel, subject = 0, framingMarginM = 0)`,
  `Surface(body, lat, lon, alt, framingMarginM = 0)` and
  `Direction(azimuth, elevation, framingMarginM = 0)` mirror the aim kinds
- `CreateCommandCamera(vantage, name = null, aim = null)` returns the
  flightId at once; the feed appears when the vantage resolves, and
  `SetCommandAim` works on it before then too. Under a held lock the aim is
  not applied
- `SetCommandAim(flightId, aim, owner = null)` returns false when the lock
  refuses it, the feed is not a command feed, or the aim is unusable (a
  craft that no longer exists, such as one docked into another, is
  unusable)
- The lock holds until `ClearCommandAuthority` or a scene change, and fails
  closed: a holder that makes no call (`SetCommandAuthority`, `SetCommandAim`
  with its owner, `SetCommandBlank`) for 5 s of real time blanks every
  command feed "Authority not responding" and keeps the lock, so everyone else
  stays refused. `SetCommandBlank(flightId, reason)` blanks one feed
- `DestroyCommandCamera(flightId)` returns false for the settings camera or
  an unknown id
- `CommandFeeds()` lists every built command camera, with its `SiteKey`,
  `SiteKind`, `VantagePersistentId`, `Aim`, `SubjectPersistentId`, and
  `Created` set on the ones made at runtime (`StationId` is obsolete);
  `CommandTargets(flightId)` lists the craft it could follow (a craft docked
  into another drops out; the combined craft keeps the survivor's
  `persistentId`)

## Anchored markers

Anchors mark things in the game on a camera's picture. The game projects
each one through the camera that rendered every frame (its pose and field
of view at that moment, so zoom and pan are exact), and the result rides
that frame's stamp. Markers drawn from it line up with the picture under
any stream delay. There is no live position: everything is tied to frames.

```ts
import { AnchorDirection, AnchorKind } from "@ksp-gonogo/kerbcast";

await client.setAnchors(flightId, [
  { id: "port", kind: AnchorKind.Part, vessel: targetPid, part: portPid, orientation: true },
  { id: "ship", kind: AnchorKind.Vessel, vessel: targetPid, occlusion: true },
  { id: "site", kind: AnchorKind.Surface, body: "Mun", latitude: 2.4, longitude: 81.5 },
  { id: "pro", kind: AnchorKind.Direction, direction: AnchorDirection.TargetRelativeVelocity },
]);
await client.setAnchors(flightId, null); // clear
```

- Kinds: `part` (craft and part persistentId, optional `offset` in the
  part's axes; a docking port is marked at its docking face; follows the
  part through docking), `vessel` (centre of mass; follows a craft merged
  into another), `surface` (body, latitude, longitude, altitude) and
  `direction` (a named direction of a craft, the active one by default, or
  a fixed inertial `vector`), drawn at infinity, and `kinematic` (a point
  you describe with `kinematics`: a `conic` from KSP Orbit elements, a
  `stateVector` coasting ballistically in the same fixed frame as a
  `positionAt` aim, or a `surface` point, each about a named `body`), for a
  last-known ghost, a hypothesis or a predicted point
- `orientation: true` on a part or vessel adds its `forward` and `up` axes
  in camera space (x right, y up, z forward) to every result.
  `occlusion: true` adds `occluded`, at the cost of a ray cast per frame.
  `marginM` pads the radius `apparentRadiusPx` is worked out from
- Anchors belong to the client that set them: other clients never see them,
  the command lock does not apply, and the sidecar drops them when the
  client disconnects. The client keeps them and sends them again on
  reconnect. At most 16 per camera (`MAX_ANCHORS_PER_CAMERA`)
- Needs a 1.12 sidecar and plugin; against an older sidecar they are kept
  but never sent (`client.anchorsSupported`)

Each result lands on the displayed frame through `watchDisplayedFrames`:

```ts
import { anchorPoint, fitFrame, frameToScreen, frameLengthToScreen, findAnchor } from "@ksp-gonogo/kerbcast";

client.watchDisplayedFrames(flightId, video, (frame) => {
  const port = findAnchor(frame.anchors, "port");
  const point = port && anchorPoint(port); // null when the result carries no position
  if (!point || !frame.frameWidth || !frame.frameHeight) return;
  const fitted = fitFrame(video.clientWidth, video.clientHeight,
    frame.frameWidth, frame.frameHeight, "contain"); // or "cover"
  const { x, y } = frameToScreen(point, frame.frameWidth, frame.frameHeight, fitted);
  const r = frameLengthToScreen(port.apparentRadiusPx ?? 0, frame.frameHeight, fitted);
  draw(x, y, r, port.inFrame, port.behind);
});
```

Each result has `x`, `y` (capture pixels from the top left), `inFrame`,
`behind` (then `x`, `y` sit off the frame on the side it is nearest),
`distanceM` (absent for directions), `apparentRadiusPx`, `angleOffAxis`
(degrees), and `forward`, `up`, `occluded` when asked for. Anchors set in
game carry their `owner`; this client's own have none. Every stamp also
gives the frame's `fov` and size, for overlays that do their own maths.

An `observed: true` result carries only what the picture shows: `x`, `y`,
`apparentRadiusPx` (and `forward`, `up`) only while the anchor is in frame and
not occluded, and never `distanceM`, `angleOffAxis` or `behind`. Out of view it
is just `inFrame` and `occluded`. Use `anchorPoint(result)`, which is `null`
when there is no position.

A command camera adds its own anchor: `id: "subject"`, owner `"kerbcast"`
(`COMMAND_SUBJECT_ANCHOR_ID`, `KERBCAST_ANCHOR_OWNER`), a `vessel` anchor on
the craft it watches with occlusion on. It is always observed, so it never
gives away a craft the picture does not show. The React brackets follow it.
A second, `id: "aim"` (`COMMAND_AIM_ANCHOR_ID`), is where the aim point lands
in the frame: the centre, except while the camera turns onto a moved aim.

While a command authority holds the lock, every browser client's anchors
are observed-only by default. The holder can relax that
(`KerbcastControl.SetAnchorPolicy(AnchorPolicy.Full, owner)`, for every
camera or one) and can withhold the subject anchor
(`KerbcastControl.SetSubjectAnchor(false, owner)`). Both return to the
unrestricted behaviour when the lock clears. A `kinematic` anchor is never
observed: it marks what you supplied, not what the game shows. Where it would
be observed, though, its result still withholds the lens's pose: `x`, `y` and
`apparentRadiusPx` only while `inFrame`, and never `distanceM`, `behind` or
`angleOffAxis`. Under the lock
`commandTrack.trueOffsetPx` is likewise sent only while the true craft is in
frame and not occluded.

### State that rides the frame

From 1.12 every frame's stamp carries what that frame shows, so a view that
delays the picture never learns it sooner:

- `liveLightTimeS` on every frame of every camera: the light time covering
  what of the lens's own craft and site the frame draws at its `ut`. It is
  0 with no signal speed. A delayed view releases the frame no sooner than
  its `ut` plus this plus its own delay from the lens. Absent means unknown
  (the game could not read it, or it is older than 1.12): under a signal
  speed, hold the frame. Other loaded craft near the lens are still drawn at
  `ut` and are not covered yet; once craft beyond the boundary are hidden,
  the bound covers everything drawn at `ut`
- `boundaryM`: how far light travels in one 60 fps frame at the signal
  speed, the picture's boundary distance. Absent with no signal speed
- `command` on a command camera's frames: its fix, range, bracket,
  occlusion, sunlight, blank reason, lock owner, subject, the subject's
  refusal (`unknown`, `notPositioned`, `refused`, `stale`) and which world
  model snapshot it read

All three arrive on `DisplayedFrameUt` through `watchDisplayedFrames`.
`effectiveCommandTrack(track, displayUt, frame.command)` uses a frame's own
state whole; the React feed does this for its brackets and readout.

A host with its own delayed playout keeps the `DisplayedFrameUt` it got for
each frame and hands the one it is showing to the React feed as
`displayFrame` (beside, and winning over, `displayUt`): its UT picks the
transition in force and its `command` draws the brackets and readout. A UT
alone carries no frame state, so under a lock `displayUt` by itself shows no
fix. Without either, the feed uses the frame the browser presents, or the
newest stamped frame before it, which lags the picture and so is never early.

While a command lock is held, the camera list's `commandTrack` keeps only its
`ut`, `authority` and `events` (each replayed at its own UT); the other fields
read as their defaults, which a 1.11 client shows as no fix. The targets list
in `commandCentre` carries the `ut` it describes:
`client.commandCentreAt(flightId, displayUt)` gives the list as it stood at
the displayed frame, and `null` while every list sent is newer. With no
displayed UT it gives the newest, except under a lock, where it gives `null`.
Lists are kept back to two hours of UT before the newest
(`COMMAND_CENTRE_MAX_LAG_UT`), at most 600 per camera
(`COMMAND_CENTRE_MAX_ENTRIES`; a UT older than every list kept finds none),
and are forgotten when the camera leaves the
list or the client reconnects.

In game, `KerbcastControl.SetAnchors(flightId, anchors, owner)` does the same
with `AnchorSpec.ForPart`, `ForVessel`, `ForSurface`, `ForDirection` and
`ForVector`. Those results go to every client that asked for anchors, tagged
with `owner`, until replaced, `ClearAnchors(owner)` or a scene change.

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
