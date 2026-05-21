# @kerbcam/protocol

TypeScript bindings for the kerbcam sidecar's WebRTC data-channel protocol.

The Rust types live in [`sidecar/src/protocol/`](../../sidecar/src/protocol/);
[`typeshare`](https://github.com/1Password/typeshare) generates the
TypeScript in `src/index.ts`. CI keeps the two in sync — don't edit
`src/index.ts` by hand.

## Wire shape

The protocol is JSON-per-message over an `RTCDataChannel` labelled
`kerbcam-control`. The browser opens the channel after the SDP exchange
completes; the sidecar dispatches client messages and pushes server
messages back.

Messages are tagged unions with `type` + `content`:

```ts
const msg: ClientMessage = {
  type: "set-layers",
  content: { flightId: 2592004302, layers: [Layer.Near, Layer.Scaled] },
};
dc.send(JSON.stringify(msg));
```

Unit variants (e.g. `Hello`) carry no `content`:

```ts
dc.send(JSON.stringify({ type: "hello" }));
```

## Multi-language

`typeshare` also targets Kotlin, Swift, Scala, and Go. Generators for
those land alongside `client-sdk/typescript/` as consumers materialise.
A C# binding would let the KSP plugin share the same types — pending an
upstream typeshare addition or a hand-written shim.

## Publishing

Pushed automatically by CI on every change to `sidecar/src/protocol/`.
The repo's version bump policy is currently manual — bump `package.json`
in the same commit as the protocol change.
