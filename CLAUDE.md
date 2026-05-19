# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Sister project

**[gonogo](https://github.com/jonpepler/gonogo)** is the consumer of this mod: a TypeScript SPA that displays kerbcam camera feeds in a mission-control dashboard. The two projects share conventions — commits, semver, performance-budget patterns, the Steam-Deck-to-MacBook topology — and gonogo's `CLAUDE.md` is the canonical record of those.

The full design rationale for kerbcam lives in **gonogo's `local_docs/ocisly_state_and_rebuild.md`** — read it before any non-trivial architectural decision. This CLAUDE.md is an index pointing at it, not a replacement.

## Project status

**Pre-release, personal-use only.** Not yet ready for the broader KSP modding community. Linux/Steam Deck is tier-1; macOS and Windows are experimental tier-2. Public mod-platform listings (CKAN, SpaceDock, KSP Forums) are intentionally deferred. See `README.md` for the public-facing status block.

## Project vision

**kerbcam** is a from-scratch successor to OCISLY (OfCourseIStillLoveYou), the KSP camera-streaming mod. It captures Hullcam VDS camera feeds from KSP and ships them over WebRTC to a browser — fast enough that the Steam Deck doesn't notice, faithful enough that each camera's Hullcam VDS character (B&W, CRT scanlines, night vision, etc.) is preserved.

The headline differences from OCISLY:

- **`AsyncGPUReadback`** instead of `Texture2D.ReadPixels` — capture is zero-stall on KSP's main thread.
- **Hardware H.264 encoding** in an out-of-process sidecar — VCN 2.0 on the Deck, no software JPEG encode.
- **WebRTC end-to-end** — adaptive bitrate, congestion control, packet loss recovery, all for free from `libwebrtc` (via `webrtc-rs`).
- **Hullcam VDS filter fidelity** — read `cameraMode` from each part, reuse the existing in-game filter materials.
- **Subscription-driven lifecycle** — cameras render only when a peer is subscribed; no streaming work when nobody's watching.
- **Adaptive scaling** — `/limits` + `/metrics` + auto-degrade when game framerate drops, with explicit "throttled because X" messages to the operator.

## Architecture in one line

```
KSP (Steam Deck) ── plugin DLL ──┐ shared-mem ring ┌── sidecar (Rust) ── WebRTC ──► browser (MacBook M4)
                  AsyncGPUReadback│                 │  libva H.264
                  Hullcam filter  │                 │  RTCPeerConnection
                                  └──── ◇ ─────────┘
```

One mod download: the plugin and the per-rid sidecar binary ship together in `GameData/KerbCam/`. The plugin auto-launches the sidecar on `Awake()`, kills it on `OnDestroy()`. From the user's perspective: drop in GameData, start KSP, it works.

## Repo layout

Current layout (as of 2026-05-19 — scaffold checkpoint):

```
sidecar/                Rust workspace (built, 16 tests passing)
  Cargo.toml
  README.md
  src/
    lib.rs              public library surface
    main.rs             bin entry: clap CLI, tracing init, encoder selection
    encoder/
      mod.rs            EncoderBackend trait + auto_select factory
      libva.rs          TIER-1 Linux/Deck VA-API (stub — is_available=false)
      videotoolbox.rs   TIER-2 macOS (stub)
      nvenc.rs          TIER-2 Windows/NVIDIA (stub)
      software.rs       OpenH264 / x264 software fallback (stub but encode
                        validates dims; always-available so auto_select never
                        returns None)
    protocol/
      mod.rs            ClientToSidecar + SidecarToClient + CameraMetadata
                        message types, serde-derived
    shared_mem/
      mod.rs            FrameRing skeleton (in-process Mutex backing for now;
                        real mmap pending §8 spike #2)
  tests/
    synthetic_frame_pipeline.rs    integration: synthetic frame → ring → encoder
```

Planned (not yet present):

```
.github/workflows/      CI (Rust + C# + integration harness), release, protocol publish
Plugin/                 KSP-side C# (.csproj → KerbCam.dll). For now the KSP-
                        side work lives in the OCISLY fork (separate repo on
                        the author's machine) — we'll fork those files into
                        Plugin/ once the rebuild proper starts.
protocol/               TypeScript / C# codegen targets — currently the message
                        types are only in sidecar/src/protocol/. Bindings get
                        formalised when gonogo starts consuming them.
GameData/KerbCam/       packaged install tree (plugin.dll + Sidecar/<rid>/binary).
                        Built by CI per-rid + assembled at release time.
live_tests/             Claude-runnable test docs (HTTP endpoints, control-channel
                        shapes) per the rebuild doc §10.4.
```

The strategy / planning context for this project lives in the gonogo repo at `local_docs/kerbcam/` — design decisions, performance baselines, ongoing notes. Update both repos when something architectural shifts.

## Toolchain

- **Plugin:** C# / .NET Framework 4.8, builds against KSP's Unity 2019.4 LTS assemblies. Currently lives in the OCISLY fork (separate repo).
- **Sidecar:** Rust (stable, currently 1.91 via brew). Confirmed-working deps in `sidecar/Cargo.toml`: `tokio`, `anyhow`, `thiserror`, `serde`, `serde_json`, `tracing`, `tracing-subscriber`, `clap`. **Deferred** until specific spikes need them: `webrtc-rs`, `ffmpeg-next` (or `cros-libva`), `shared_memory`, `openh264-sys2`. The trait surface is in place so each can be added in isolation.
- **Protocol:** Schemas in `sidecar/src/protocol/`; bindings to TypeScript/C# get formalised when consumers come online.
- **Workflow:** `cargo build` / `cargo test` for sidecar, `dotnet build` for plugin (via gonogo's `gonogo_claude_tools.sh build ocisly`), `pnpm` only when the TS codegen step lands.

## Workflow

Solo-developer repo. **Conventional Commits + Semantic Versioning.** Direct commits to `main` unless a change is large enough to warrant a feature branch (rare). No `Co-Authored-By: Claude` trailer — write the commit message as if a human authored it. Same convention as gonogo.

Releases: GitHub Actions builds per-rid sidecar binaries on tag, packages `GameData/KerbCam/`, attaches to a GitHub Release. Protocol package publishes to GitHub Packages (not npm) on the same tag.

## OS support tiers

- **Linux (Steam Deck): tier-1.** All perf budgets, all integration tests, all manual smoke tests target the Deck. libva H.264 is the default encoder backend.
- **macOS: tier-2.** `VideoToolboxEncoder` backend exists. Not perf-budgeted. Code may build and run; not held to release-quality.
- **Windows: tier-2.** `NvencEncoder` + `D3D11Encoder` backends. Community-contributable.
- **Software fallback:** `OpenH264Encoder` always present.

When adding tier-2 code, document explicitly that it's tier-2 in the PR description and don't gate CI on its perf budgets.

## Performance budgets

Same pattern as `gonogo`'s `PerfBudget`. Concrete targets are in gonogo's `local_docs/ocisly_state_and_rebuild.md` §4. Headline numbers:

- Main-thread capture cost: < 0.3 ms per camera per frame.
- Hardware encode: < 1.5 ms per camera per frame at 720p30.
- End-to-end glass-to-glass latency on LAN: < 90 ms.
- Concurrent cameras at 30 fps: 6–8 on the Deck.

All numbers are **estimates pending the §8.0 baseline measurement**. The first real deliverable in this repo is the baseline harness against the *current* OCISLY+gonogo stack — every later perf claim is compared against that.

## Hullcam VDS filter integration (Path A)

When the player has Hullcam VDS installed (assume yes), `cameraMode` on each `MuMechModuleHullCamera` selects one of 9 filter classes (`CameraFilterNormal`, `BlackAndWhiteFilm`, `BlackAndWhiteLoResTV`, `BlackAndWhiteHiResTV`, `ColorFilm`, `ColorLoResTV`, `ColorHiResTV`, `DockingCam`, `NightVision`). We reflect into the already-loaded Hullcam VDS assembly to get the filter materials and `Graphics.Blit` through them onto our capture RenderTexture *before* `AsyncGPUReadback.Request()`. Pixel-identical to the in-game Hullcam GUI; main-thread cost is the Blit dispatch only, not shader execution.

If Hullcam VDS isn't installed, fall back to a generic mode (Path C in the rebuild doc) — cameras stream in `Normal` colour with no filtering.

## Testing

- **Unit:** sidecar (`cargo test`), plugin (xUnit or NUnit against mocked Hullcam parts).
- **Integration:** synthetic-frame harness runs on every PR via GitHub Actions on `ubuntu-latest`. Mocks Unity; generates known frames; runs real sidecar binary; asserts WebRTC roundtrip + control-channel + lifecycle.
- **Perf:** gated behind a `perf` label, runs only on Deck/M4 locally. Container-runner timing isn't comparable.
- **Live test for Claude:** `live_tests/kerbcam.md` documents HTTP endpoints, control-channel shapes, and how to verify a running kerbcam. Future Claude sessions should be able to script-test without re-deriving the protocol.

## Status: explicit out-of-scope items

These are deliberately not in v0.1:

- macOS / Windows release-quality polish (tier-2 only)
- Movable cameras (`KerbCamModulePanTilt`)
- Recording-to-disk endpoints
- Audio capture
- Public CKAN / SpaceDock / forum-post distribution

See `README.md`'s TODO area for the future-distribution checklist.

## Things to remember

- **Don't bypass the encoder backend trait.** Every backend implements the same interface; per-OS quirks stay inside the impl. If you find yourself adding OS-specific code outside `backends/`, stop and put it inside one.
- **The protocol is the contract.** Wire formats between plugin↔sidecar (IPC) and sidecar↔browser (WebRTC data channel) are defined in `protocol/`. Code-generated bindings in C# and TypeScript follow from there. Don't hand-edit either side's generated types.
- **Estimates are estimates until measured.** The performance numbers in the rebuild doc are spec-sheet extrapolation. Once the §8.0 baseline lands, replace them with measured values.
