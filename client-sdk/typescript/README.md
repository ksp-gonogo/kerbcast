# @jonpepler/kerbcam

TypeScript bindings for the [kerbcam](https://github.com/jonpepler/kerbcam)
sidecar's WebRTC data-channel protocol — the bidirectional control
plane between a streaming client (browser, mission-control SPA,
future Kotlin / Swift consumer) and the kerbcam sidecar daemon that
encodes KSP camera frames.

The Rust types live in
[`sidecar/src/protocol/`](https://github.com/jonpepler/kerbcam/tree/main/sidecar/src/protocol);
[`typeshare`](https://github.com/1Password/typeshare) generates this
TypeScript in `src/index.ts`. CI keeps the two in sync — don't edit
the generated source by hand. The Rust crate and this package share
one SemVer line (Cargo.toml is the source of truth; CI verifies
package.json matches).

## Install

Hosted on GitHub Packages. Add an `.npmrc` pointing the
`@jonpepler` scope at the GitHub registry:

```ini
@jonpepler:registry=https://npm.pkg.github.com
//npm.pkg.github.com/:_authToken=${GITHUB_TOKEN}
```

`GITHUB_TOKEN` needs `read:packages` scope. In GitHub Actions the
auto-injected `secrets.GITHUB_TOKEN` already has it.

```sh
pnpm add @jonpepler/kerbcam
```

## Use

The protocol is JSON-per-message over an `RTCDataChannel` labelled
`kerbcam-control`. The browser opens the channel after the SDP
exchange completes; the sidecar dispatches `ClientMessage` JSON and
pushes `ServerMessage` JSON back.

Messages are tagged unions with `type` + `content`:

```ts
import type { ClientMessage, ServerMessage, CameraState } from "@jonpepler/kerbcam";
import { Layer } from "@jonpepler/kerbcam";

// Client → server
const setLayers: ClientMessage = {
  type: "set-layers",
  content: { flightId: 2592004302, layers: [Layer.Near, Layer.Scaled] },
};
dc.send(JSON.stringify(setLayers));

// Unit variants (no `content`)
dc.send(JSON.stringify({ type: "hello" } satisfies ClientMessage));

// Server → client
dc.onmessage = (ev) => {
  const msg: ServerMessage = JSON.parse(ev.data);
  switch (msg.type) {
    case "camera-snapshot":
      msg.content.cameras.forEach((c: CameraState) => renderCamera(c));
      break;
    case "adaptive-shed":
      console.log(`shed level=${msg.content.level} (KSP ${msg.content.kspFps} fps)`);
      break;
    // ...
  }
};
```

## Per-camera capabilities

`CameraState` carries flags so consumers render controls only for
what each part supports:

- `supportsZoom`, `fov`, `fovMin`, `fovMax` — 19 of 21 stock Hullcam
  VDS parts support runtime FoV changes via `MuMechModuleHullCameraZoom`.
- `supportsPan`, `panYawMin/Max`, `panPitchMin/Max` — reserved for
  the planned kerbcam-side mod extension that adds steerable mounts.
  Always `false` on shipping parts; show pan UI only when this flips.
- `encoderBitrateBps`, `targetBitrateBps` — current encoder bitrate
  and the REMB-driven target. Diverge briefly when receivers'
  bandwidth estimates move.

## Multi-language

`typeshare` also targets Kotlin, Swift, Scala, and Go. Generators
for those land alongside `client-sdk/typescript/` as consumers
materialise. A C# binding would let the KSP plugin share the same
types — pending an upstream `typeshare` C# generator or a
hand-written shim.

## License

[CC BY-NC-SA 4.0](https://github.com/jonpepler/kerbcam/blob/main/LICENSE).

## Versioning

[SemVer](https://semver.org/), interpreted relative to the wire
format. See
[CHANGELOG.md](https://github.com/jonpepler/kerbcam/blob/main/client-sdk/typescript/CHANGELOG.md)
for changes between versions.

While the protocol is at `0.x`, treat any minor bump as potentially
requiring consumer updates — strict SemVer kicks in at `1.0.0`.
