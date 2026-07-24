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

An imperative `CameraFeedHandle` (via `ref`) exposes `stepCamera`,
`setPanAxis`, `setZoomRate`, `nudgePan`, and `nudgeZoom` for wiring feeds
to a gamepad or keyboard.

`enableTracking` shows a tri-state auto-track control (off / active-vessel /
target) on pan+zoom cameras, and `CameraFeed` disables its manual pan/zoom
while a camera is tracking. `cameraFilter` narrows the selectable cameras
(picker, stepper, auto-latch). `showActions={false}` hides the whole action
bar without affecting resolution reporting.

## `KerbalFaceFeed`

A single-kerbal face-camera primitive: one crew face in a square frame, keyed
by `flightId`, never remounting its `<video>` on re-layout. Compose a name
label, badge, or overlay via `children`. `size` is LAYOUT-ONLY: the feed always
self-measures its rendered box and reports that for auto-resolution, so stream
quality follows how large it is shown (pass `reportSize={false}` to opt out).

## Hooks

- `useReportDisplaySize(flightId, ref)`. Self-measure an element and drive the
  client's per-consumer `reportDisplaySize` (auto-resolution). The feed
  primitives use it internally; multiple views of one camera from a client
  collapse to a single MAX report.

- `useKerbcastCameras(): CameraState[]`. The live camera registry,
  re-rendering as cameras appear, change, or are destroyed.
- `useKerbcastStream(flightId): MediaStream | null`. The video stream
  for one camera, acquiring and releasing its subscription slot. This is
  what `CameraFeed` uses internally.
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
