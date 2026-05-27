# kerbcam

A from-scratch successor to OCISLY for streaming Kerbal Space Program camera feeds to a browser. Hardware-accelerated H.264 over WebRTC, with full Hullcam VDS camera-type fidelity. Designed with Linux in mind.

## What it does

- Streams HullcamVDS camera sources to a browser via a simple html page or through [@jonpepler/kerbcam](https://github.com/jonpepler/kerbcam/pkgs/npm/kerbcam), a TypeScript package
- Supports performance tweak options to keep fps reasonable through degrading resolution, shedding render layers, and adding 'realistic' noise at sour era ce
- Honours each camera's `cameraMode` so B&W / CRT / night-vision variants look like they do in the in-game Hullcam GUI
- Supports a data channel for sending control data (eg zoom)
- Fixes and patches long term issues with OCISLY and Hullcam on Linux

### How it does it

- Captures KSP Hullcam VDS camera feeds inside the game via `AsyncGPUReadback` — zero stall on the game's main thread
- Encodes them in hardware (libva on Linux / Steam Deck; VideoToolbox / NVENC on other tier-2 platforms) in an bggprocess 'sidecar'
- Streams them out as WebRTC media tracks — adaptive bitrate, congestion control, packet loss recovery for free
- Renders cameras only when a peer is subscribed — no idle CPU work, no in-game UI required

## Status: pre-release, personal-use only

kerbcam isn't ready for general consumption yet.

- Linux / Steam Deck support is tier-1.
- macOS and Windows are experimental tier-2 — code paths exist, polish does not.
- Mod-platform listings (CKAN, SpaceDock, KSP Forums) will come at 1.0.

Issues and PRs are welcome, particularly if you'd like to 'adopt' macOS or Windows support.

## Differences to OCISLY

OCISLY uses `ReadPixels` + `EncodeToJPG` on the game's main thread and ships JPEG over unary gRPC at 30 Hz. That works, but it costs real frame budget, particularly on lower powered devices, and doesn't support the visual character that Hullcam VDS encodes per part. Kerbcam aims to fix both while prioritising high performance through hardware encoding and modern Unity API use.

### Performance

Measured on a Steam Deck (AMD Van Gogh APU) running KSP 1.12, launchpad scene, 6 Hullcam cameras, OpenGL / Mesa:

| Scenario                               | Cameras streaming | fps mean | fps p50 |
| -------------------------------------- | ----------------- | -------- | ------- |
| No camera mod                          | 0                 | 56.2     | 56.5    |
| OCISLY                                 | 6                 | 13.4     | 9.0     |
| kerbcam, 6 cams attached, 0 connected  | 0                 | 56.9     | 56.7    |
| kerbcam, 6 cams streaming (h264_vaapi) | 6                 | 24.99    | 34.24   |

The idle row (0 connected) shows that cameras attached to a vessel cost nothing until a stream to it is opened. kerbcam streaming (25 fps) is roughly double OCISLY (13 fps) at the same camera count, with `AsyncGPUReadback` keeping frame capture off the main thread and AMD VCN handling H.264 encode out-of-process via VA-API, specifically tested on Steam Deck.

## Toolchain

- Plugin: C# / .NET Framework 4.8, against KSP's Unity 2019.4 LTS assemblies.
- Sidecar: Rust (stable), out-of-process H.264 encoder and WebRTC signalling.
- Protocol: Rust types in `sidecar/src/protocol/`, TypeScript SDK at `client-sdk/typescript/`.

## Companion project

[gonogo](https://github.com/jonpepler/gonogo) - a mission-control browser SPA that consumes kerbcam feeds (and a few other things). Not required to use kerbcam (any WebRTC-capable browser will do).

## TODO

- [ ] Codesign and notarise macOS sidecar binaries (Apple notarytool workflow)
- [ ] Establish SmartScreen reputation for Windows binaries
- [ ] Publish NetKAN metadata to CKAN's indexer
- [ ] Create SpaceDock listing
- [ ] KSP Forums post
- [ ] User-facing install and quickstart docs

## Future work

- [ ] Better tier-2 OS support
- [x] Support Hullcam VDS cameras that take zoom commands
- [ ] Extend Hullcam with a pivotable camera
