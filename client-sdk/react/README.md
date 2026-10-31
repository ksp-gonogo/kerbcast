# @ksp-gonogo/kerbcast-react

React components and hooks for [kerbcast](https://github.com/ksp-gonogo/kerbcast)
camera feeds. Drop a `<CameraFeed>` into your app and it handles WebRTC
streaming, camera selection, pan/zoom controls, quality throttling,
fullscreen, and the signal-loss and out-of-flight states for you.

This is the React layer on top of
[`@ksp-gonogo/kerbcast`](https://www.npmjs.com/package/@ksp-gonogo/kerbcast),
the core client. Use that package directly if you are not on React or
want the raw client.

## Install

```sh
pnpm add @ksp-gonogo/kerbcast-react @ksp-gonogo/kerbcast
```

Peer dependencies: `react` (18 or 19), `react-dom`, and
`styled-components` (6). The core SDK is a normal dependency and comes
along automatically.

## Quick start

Wrap your tree in a `KerbcastProvider` holding a connected client, then
render feeds. A `flightId` of `null` auto-selects the first live camera.

```tsx
import { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { KerbcastProvider, CameraFeed } from "@ksp-gonogo/kerbcast-react";

const client = new KerbcastClient({ host: "192.168.1.74", port: 8088 });
await client.connect();

function Dashboard() {
  return (
    <KerbcastProvider client={client}>
      <CameraFeed flightId={null} enableFullscreen enableQualityControl />
    </KerbcastProvider>
  );
}
```

## `CameraFeed`

One camera's video with its overlays and controls. It subscribes to the
sidecar while mounted and releases on unmount, so mounting a feed is all
you need to start a stream. It never remounts the `<video>` element as
quality or source changes, so playback stays uninterrupted.

Commonly used props (`CameraFeedProps` has the full set with doc
comments):

- `flightId: number | null`. The camera to show. `null` latches the
  first live camera and re-picks if it dies.
- `onSelectCamera` / `onDisplayedCameraChange`. The user picked a
  camera, or the resolved camera changed (including auto-latch).
- `enableFullscreen`, `enablePictureInPicture`, `enableQualityControl`.
  Opt-in built-in controls in the top-right action bar. Each hides
  itself where the browser lacks support.
- `actions` / `trailingActions`. Your own `FeedAction` buttons in the
  action bar (`trailingActions` sits in the corner, the natural home for
  a close button).
- `showDebugInfo`. Resolution and encoder readout.
- `showStatic`. How a stalled or sourceless feed looks. Defaults to
  auto, which honours `prefers-reduced-motion`.
- `inFlight` / `showStandbyIcon`. Override the in-flight signal for one
  feed, and whether it draws its own standby icon (set `false` when a
  container renders a single shared out-of-flight overlay).
- `useStream`. Override how the stream is sourced. See below.
- `renderSize`. `"auto"` (the default) measures the feed and asks the
  sidecar for a matching resolution; `"none"` leaves resolution alone.

`renderSize="auto"` picks the resolution, never the shape. The feed takes
the camera's configured capture aspect (`CameraState.maxWidth` x
`maxHeight`, from settings.cfg; 16:9 when unknown), centres itself at that
aspect inside whatever box you give it, and asks for the size of that
picture: width rounded up to a multiple of 16, height at the aspect. A
resize that only follows the incoming stream is never reported back, so a
frame you size from the video (or from `renderWidth`/`renderHeight`)
cannot loop with the request. A resize within about a second of the stream
changing that matches its new shape counts as following it. Layout changes
outside that window always report.

The feed frame (the element fullscreen targets) carries two attributes for
hosts and tests:

- `data-kc-signal`: `live`, `stalled` (no frame has left the decoder for
  half a second, or none yet since the source attached) or `none` (no
  source). It is measured after any encoded transform the host installs, so
  a transform that holds frames back shows as `stalled` while it holds them.
  The video keeps presenting frames while it shows static, so its frame
  counts cannot tell a picture from static, and inbound-rtp `framesDecoded`
  averaged over seconds misses gaps this short; this attribute can
- `data-fullscreen`: present while the feed is fullscreen

An imperative `CameraFeedHandle` (via `ref`) exposes `stepCamera`,
`setPanAxis`, `setZoomRate`, `nudgePan`, and `nudgeZoom` for wiring feeds
to a gamepad or keyboard.

`enableTracking` shows a tri-state auto-track control (off / active-vessel /
target) on pan+zoom cameras, and `CameraFeed` disables its manual pan/zoom
while a camera is tracking. `cameraFilter` narrows the selectable cameras
(picker, stepper, auto-latch). `showActions={false}` hides the whole action
bar without affecting resolution reporting.

## Anchored markers

`anchors` marks things in the game on the feed's picture (see the core
SDK's "Anchored markers" for the kinds and fields). The feed sets them on
the camera it shows, clears them when it moves on or unmounts, and draws a
marker where each one lands in every displayed frame: four corner marks
sized from `apparentRadiusPx` (never below 14 px), green when clear, red and
dashed when occluded.

`anchorOverlay` has the same shape as `targetBracketOverlay`: `corners`
restyles the four marks (`top`, `right`, `bottom`, `left`, each
`{ colour, dashed, hidden, shape }`), `render` adds SVG in screen pixels,
`readout` puts text under the marker, and `shape` sets every mark to
`"corners"` or `"triangles"`. Each is called per anchor with the raw result
and a context: its screen geometry, the fitted content rect, the displayed
frame's UT and the screen position of every drawn anchor. `owners` also
draws anchors set in game by those owners. What a colour means is yours to
decide; Kerbcast only reports geometry.

A direction reticle on a docking port: four triangles, each side coloured
from the port's axes.

```tsx
const anchors = useMemo(
  () => [{ id: "port", kind: AnchorKind.Part, vessel: targetPid, part: portPid, orientation: true }],
  [targetPid, portPid],
);

<CameraFeed
  flightId={dockingCam}
  anchors={anchors}
  anchorOverlay={{
    shape: "triangles",
    corners: (a) => {
      /* The port faces the lens along -z; tilt shows in its forward's x and y. */
      const [fx = 0, fy = 0] = a.forward ?? [];
      const side = (off: number) => ({ colour: Math.abs(off) < 0.02 ? "lime" : "orange" });
      return { top: side(fy), bottom: side(fy), left: side(fx), right: side(fx) };
    },
    readout: (a) => (a.distanceM != null ? `${a.distanceM.toFixed(1)} m` : null),
  }}
/>
```

For a video of your own, `AnchorMarkers` draws a frame's anchors over any
`<video>` and `useDisplayedFrame(enabled, flightId, video)` gives the
displayed frame with its results.

On command feeds the target brackets follow the subject's anchor while it
is visible and no command lock is held, so the box stays on the craft where
the picture shows it even while an aim points elsewhere. With the craft out
of frame or occluded, or under the lock, the box is the 1.11 one: centred on
the aim, sized from the aimed range, red and dashed when the aim line is
occluded. `targetBracketOverlay` is unchanged; its context gains `subject`,
the observed anchor, so you can draw it as a second mark.

An observed anchor out of view (see the core SDK) carries no position and
draws no marker; it is still in `ctx.frame.anchors`.

## `KerbalFaceFeed`

A single-kerbal face-camera primitive: one crew face in a square frame, keyed
by `flightId`, never remounting its `<video>` on re-layout. Compose a name
label, badge, or overlay via `children`. `size` is LAYOUT-ONLY: the feed always
self-measures its rendered box and reports that for auto-resolution, so stream
quality follows how large it is shown (pass `reportSize={false}` to opt out).

## Hooks

- `useReportDisplaySize(flightId, ref, options)`. Self-measure an element and
  drive the client's per-consumer `reportDisplaySize` (auto-resolution). The
  feed primitives use it internally; multiple views of one camera from a
  client collapse to a single MAX report. Pass `aspect` for the shape to ask
  for (default 16:9), and `videoRef` / `streamSize` so a resize that follows
  the stream is not reported.

- `useKerbcastCameras(): CameraState[]`. The live camera registry,
  re-rendering as cameras appear, change, or are destroyed.
- `useKerbcastStream(flightId): MediaStream | null`. The video stream
  for one camera, acquiring and releasing its subscription slot. This is
  what `CameraFeed` uses internally. A released slot is kept for
  `KERBCAST_RELEASE_GRACE_MS` (2000 ms), so a feed that remounts inside
  that window picks up the running stream instead of starting it again.
  It is let go at once when another feed is waiting for a slot. Build the
  provider's `subscriptions` with
  `createClientSubscriptions(client, { releaseGraceMs })` for a different
  grace, or 0 for none
- `useKerbcastInFlight(): boolean | undefined`. Whether KSP is in a
  flight scene. `undefined` until the first signal arrives, so you can
  avoid flashing an out-of-flight state on connect.
- `useKerbcastClock(): { captureUt, epoch, warpRate }`. The sidecar's
  mission-time capture clock, for aligning playout to sim time.
- `useKerbcastClient()` and `useKerbcastSubscriptions()`. The client
  and subscription manager from context, for building your own feed UI.

## Standby icon

`StandbyIcon` is the shared rocket-on-the-pad glyph the feed shows out
of flight. Export it for a dashboard-level standby overlay so your own
message and the per-feed indicator use one consistent mark:

```tsx
import { StandbyIcon, useKerbcastInFlight } from "@ksp-gonogo/kerbcast-react";

function StandbyOverlay() {
  const inFlight = useKerbcastInFlight();
  // Only show once we know we are out of flight; stays hidden while
  // the signal is still undefined so it never flashes on connect.
  if (inFlight !== false) return null;
  return (
    <div className="scrim">
      <StandbyIcon size={44} />
      <p>Camera feeds activate in a flight scene.</p>
    </div>
  );
}
```

## Custom stream sourcing

`useStream` lets you route a feed's video through your own pipeline
(delayed playout for comms-delay parity, an alternate transport, a test
double) without the feed knowing. It is called as a hook, so it must be
a stable reference passed consistently across renders.

A replacement takes over the camera's subscription slot: the built-in
`useKerbcastStream` does not run when `useStream` is supplied, so your
hook must either compose `useKerbcastStream` or acquire the slot itself,
or the sidecar is never subscribed and the feed stays black.

```tsx
import { useKerbcastStream, type CameraStreamHook } from "@ksp-gonogo/kerbcast-react";

// Module scope: a stable reference. Composes the built-in hook so the
// subscription slot is still acquired exactly once.
const useDelayedStream: CameraStreamHook = (flightId) => {
  const live = useKerbcastStream(flightId);
  return useDelayedPlayout(live); // your own wrapper
};

<CameraFeed flightId={null} useStream={useDelayedStream} />;
```

## License

[MIT](./LICENSE). This SDK is the interface to kerbcast, so it is permissive:
build what you like against it. The kerbcast mod itself is
[CC BY-NC-SA 4.0](https://github.com/ksp-gonogo/kerbcast/blob/main/LICENSE).

Versions published before 1.6.0 carry the older CC BY-NC-SA 4.0 licence.

## Versioning

Tracks `@ksp-gonogo/kerbcast` exactly; use matching versions of both.
`./scripts/bump-version.sh` bumps them atomically. See
[CHANGELOG.md](https://github.com/ksp-gonogo/kerbcast/blob/main/client-sdk/react/CHANGELOG.md).
