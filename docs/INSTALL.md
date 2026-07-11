# Installing kerbcast

Kerbcast is on CKAN. If installing there, skip to Running.

## Requirements

- Kerbal Space Program 1.12.x
- [Hullcam VDS Continued](https://github.com/linuxgurugamer/HullcamVDSContinued)
  (required - kerbcast streams Hullcam camera parts)
- [Harmony 2](https://github.com/KSPModdingLibs/HarmonyKSP)
  (required - the runtime patching library kerbcast builds on)
- A WebRTC-capable browser on the viewing device

## Manual install (GitHub or SpaceDock)

1. Download `kerbcast-vX.Y.Z.zip` from the
   [releases page](https://github.com/ksp-gonogo/kerbcast/releases) or the
   [SpaceDock listing](https://spacedock.info/mod/4366/Kerbcast).
2. Extract it into your KSP install so that `GameData/Kerbcast/` exists
   (the zip already contains the `GameData/` folder - extract at the KSP root).
3. Install Hullcam VDS Continued and Harmony 2 if you haven't already.

## Running

1. Start KSP and launch a flight with one or more Hullcam camera parts on the
   vessel.
2. On the same machine, open `http://127.0.0.1:8088` in a browser. The bundled
   web page lists the vessel's cameras and starts streams when you click them.

> **Windows/MacOS:** the first time the Kerbcast 'sidecar' launches, the OS may warn about
> running it. It's safe to approve; you should only see this on first run.

### Configure Access From Another Device

To watch from **another device**, set
`BindAddress` in `GameData/Kerbcast/PluginData/settings.cfg`:

```
Settings
{
  BindAddress = 0.0.0.0   // or your LAN IP
}
```

then browse to `http://<ksp-machine-ip>:8088` from the other device. See
[Configuration](#configuration) if you want that to survive mod updates.

> **There is no authentication.** Anyone who can reach that address can watch
> the camera feeds. Only bind beyond localhost on a network you trust.

## What's in the bundle

```
GameData/Kerbcast/
├── Plugins/Kerbcast.dll       the KSP plugin
├── Sidecar/<rid>/            one encoder/WebRTC sidecar binary per OS
│   ├── linux-x64/            (+ lib/ with bundled ffmpeg shared libs)
│   ├── osx-arm64/
│   └── win-x64/
├── HullcamShaders/           prebuilt Hullcam shader bundle (Linux fix)
├── kerbcast-shaders           kerbcast's atmospheric-FX shader bundle (Linux)
├── kerbcast-shaders.windows   same bundle, Windows (d3d11) shader variants
├── kerbcast-shaders.osx       same bundle, macOS (metal) shader variants
├── settings.cfg              all configuration, commented
├── Kerbcast.version           KSP-AVC version manifest
└── LICENSE
```

The plugin launches the right sidecar for your OS automatically the first
time a flight scene loads, keeps it running for the whole KSP session (scene
changes, reverts, and trips through the KSC do not restart it, and connected
browsers stay connected), and stops it when KSP exits. Nothing else to run.

## Configuration

See `GameData/Kerbcast/settings.cfg` for a breakdown of each available setting. Apply them to `GameData/Kerbcast/PluginData/settings.cfg` to ensure custom settings persist between updates.

### When settings changes apply

The sidecar runs once per KSP session, so settings split into two groups:

- **Per KSP launch:** `BindAddress`, `Port`, `Width`, `Height`, `BitrateBps`,
  `AutoSpawnSidecar`. These are passed to the sidecar when it starts, so
  editing them mid-game does nothing until you restart KSP. The plugin logs a
  `[Kerbcast]` warning if it notices they changed while a sidecar is running.
- **Per flight-scene entry:** everything else (camera layer/FX/resolution
  overrides, adaptive-performance ceilings, filter toggles). Re-read every
  time a flight loads.

### Adaptive quality (opt-in)

By default kerbcast only adapts _temporally_ under load: it captures fewer of
the streaming cameras per frame, at full resolution, and never touches image
quality on its own. Setting `AdaptiveQuality = true` adds a second, lossy
stage: when the capture staggering has no room left (one camera per frame and
still over budget, or KSP below the `MinKspFps` floor), kerbcast steps render
quality down (resolution first, then FX layers), and steps it back up, one
level at a time, only after roughly 30 seconds of sustained headroom plus a
cooldown after any drop. Quality never rises above the `Width`/`Height` and
layers you configured. Every change is logged to KSP.log as
`[Kerbcast] stagger quality` with the reason.

It ships **off** because the no-quality-shedding behaviour is the measured
Steam Deck (tier-1) baseline; with the flag off, nothing about that baseline
changes.

### Viewer quality requests

Each feed on the web page has a quality menu: Auto, or full / 3/4 / 1/2 / 1/4
of the configured `Width`/`Height`. The request is per camera and shared by
all viewers (last pick wins), and it can only lower quality. The stream always
runs at the minimum of your configured size, the adaptive level, and the
viewer request; the menu marks the camera "throttled" while adaptive quality
holds it below the request. Works with `AdaptiveQuality` on or off.

## Updating / uninstalling

- **Update:** delete `GameData/Kerbcast/` and extract the new zip. If you keep
  user settings in `GameData/Kerbcast/PluginData/`, move that folder aside
  first and restore it after (or delete everything in `GameData/Kerbcast/`
  except `PluginData/`). Direct edits to the shipped `settings.cfg` do not
  survive an update; the `PluginData/` user file is the supported way to
  persist them.
- **Uninstall:** delete `GameData/Kerbcast/`.

If something doesn't work, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
