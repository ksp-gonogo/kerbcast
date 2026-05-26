# @jonpepler/kerbcam

TypeScript client for the kerbcam sidecar. Used by browsers and other
clients that consume the sidecar's H.264 streams.

`KerbcamClient` owns the WebRTC peer, the `kerbcam-control` data
channel, and the per-camera state cache and `MediaStream`s.
Consumers get a high-level RPC surface plus typed event
subscriptions. The wire-format types are still exported for anyone
rolling their own transport.

The Rust crate and this package ship the same SemVer version,
bumped together by `./scripts/bump-version.sh`.

## Install

Hosted on GitHub Packages. In `.npmrc`:

```ini
@jonpepler:registry=https://npm.pkg.github.com
//npm.pkg.github.com/:_authToken=${GITHUB_TOKEN}
```

`GITHUB_TOKEN` needs `read:packages`. CI's auto-injected
`secrets.GITHUB_TOKEN` already has it.

```sh
npm add @jonpepler/kerbcam
```

## Use

```ts
import { KerbcamClient, Layer } from "@jonpepler/kerbcam";

const client = new KerbcamClient({ host: "192.168.1.74", port: 8088 });
await client.connect();

const cam = client.camera(2592004302);
await cam.setLayers([Layer.Near, Layer.Scaled]);
await cam.setFov(35);
await cam.setRenderSize(384, 384);
await cam.setDegrade(0.5);

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
import type { ClientMessage } from "@jonpepler/kerbcam";

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
- `supportsPan`, `panYawMin/Max`, `panPitchMin/Max`. Reserved for the
  planned mod extension that adds steerable mounts. Always `false`
  right now.
- `encoderBitrateBps`, `targetBitrateBps`. Current encoder bitrate
  and the REMB-driven target. They diverge briefly when receivers'
  bandwidth estimates move.
- `degradeLevel`. Effective signal-degradation level (max across
  subscribers). Sidecar applies it via bitrate squeeze plus frame
  skip, producing the macroblocking and stuttering aesthetic of
  in-game CommNet signal loss. Also a real CPU optimisation when
  every consumer wants degraded video.

## Multi-language

`typeshare` also generates Kotlin, Swift, Scala, and Go. Those
land alongside `client-sdk/typescript/` as consumers appear. A C#
binding is pending an upstream `typeshare` C# generator or a
hand-written shim.

## License

[CC BY-NC-SA 4.0](https://github.com/jonpepler/kerbcam/blob/main/LICENSE).

## Versioning

[SemVer](https://semver.org/) against the wire format and client
API. See [CHANGELOG.md](https://github.com/jonpepler/kerbcam/blob/main/client-sdk/typescript/CHANGELOG.md).
While the protocol is at `0.x`, any minor bump may require consumer
updates. Strict SemVer applies at `1.0.0`.
