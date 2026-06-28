# kerbcast

A from-scratch, high-performance oriented successor to OCISLY for streaming Kerbal Space Program camera feeds to a browser. Hardware-accelerated H.264 over WebRTC, with full Hullcam VDS camera-type fidelity. Designed with Linux in mind, Windows and MacOS support is a little experimental.

<p align="center">
  <img src="docs/screenshots/main-screen-example.png" alt="kerbcast's web UI: a grid of live Hullcam camera feeds in a browser" width="720">
</p>

## What it does

- Streams HullcamVDS camera sources to a browser via the bundled kerbcast web UI or through [@jonpepler/kerbcast](https://github.com/jonpepler/kerbcast/pkgs/npm/kerbcast), a TypeScript SDK
- Supports performance tweak options to keep fps reasonable through degrading resolution, framerate, and shedding render layers. By default, performance should feel vastly improved over the original OCISLY mod
- Introduces optional shaders for wind and re-entry FX (sorry, no Firefly support yet)
- HullcamVDS implementation enhancements:
  - Honours each camera's `cameraMode` so B&W / CRT / night-vision variants look like they do in the in-game Hullcam GUI
  - Introduces planned but never fully implemented panning options for turret cam and launch cam
  - Supports a data channel for sending control data from clients (eg pan/zoom)
  - Fixes and patches long term issues on Linux

### How it does it

- Captures KSP Hullcam VDS camera feeds inside the game via `AsyncGPUReadback`, with zero stall on the game's main thread
- Encodes them in an out-of-process 'sidecar': software H.264 (OpenH264) as the fallback, with hardware H.264 on Linux / Steam Deck (libva), and NVENC (Windows). VideoToolbox (macOS) is stubbed pending implementation
- Streams them out as WebRTC media tracks, with adaptive bitrate, congestion control, and packet loss recovery for free
- Renders cameras only when a peer is subscribed, so no idle CPU work and no in-game UI required

## Status: Initial Release - expect bugs

Kerbcast is just becoming ready for general consumption. It supports Linux, with limited testing on Windows and MacOS.

Issues and PRs are welcome, particularly if you'd like to 'adopt' macOS or Windows support.

## Install

Grab the latest `kerbcast-*.zip` from the [releases page](https://github.com/jonpepler/kerbcast/releases) and follow the install steps in that release's notes. The steps live with each release rather than here because they can change between versions; the release notes are always correct for the build you download. You'll also need [Hullcam VDS Continued](https://github.com/linuxgurugamer/HullcamVDSContinued).

For the longer version with multi-device setup, configuration, info on what's in the bundle, see [docs/INSTALL.md](docs/INSTALL.md). If something doesn't work, [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) walks the common failures.

## Differences to OCISLY

OCISLY uses `ReadPixels` + `EncodeToJPG` on the game's main thread and ships JPEG over unary gRPC at 30 Hz. That works, but it costs real frame budget, particularly on lower powered devices, and doesn't support the visual character that Hullcam VDS encodes per part. Kerbcast aims to fix both while prioritising high performance through hardware encoding and modern Unity API use.

### Performance

This mod was built initally for getting best performance out of Steam Deck, and it does a good job of it. Regardless of the number of camera renders streaming, Kerbcast can adjust quality and framerate to ensure KSP FPS stays within the ideal range for in-game physics. It also uses a time budget to ensure it doesn't balloon render time. Each stream is SD by default, but can be increased. Render quality will always be able to reduce to ensure streaming continues.

## Toolchain

- Plugin: C# / .NET Framework 4.8, against KSP's Unity 2019.4 LTS assemblies.
- Sidecar: Rust (stable), out-of-process H.264 encoder and WebRTC signalling.
- Protocol: Rust types in `sidecar/src/protocol/`, TypeScript SDK at `client-sdk/typescript/`.

## Companion project

[gonogo](https://github.com/jonpepler/gonogo) - a WIP mission-control browser SPA that consumes kerbcast feeds (and a few other things).

## TODO

- [x] Publish NetKAN metadata to CKAN's indexer
- [x] Create SpaceDock listing
- [ ] KSP Forums post
- [x] User-facing install and quickstart docs

## Future work

- [ ] Better tier-2 OS support
- [x] Support Hullcam VDS cameras that take zoom commands
- [x] Extend Hullcam with a pivotable camera
