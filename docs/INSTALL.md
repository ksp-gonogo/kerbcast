# Installing kerbcam

> Each GitHub Release also carries these steps in its notes - if anything here
> disagrees with the notes on the release you downloaded, the release notes win.

## Requirements

- Kerbal Space Program 1.12.x
- [Hullcam VDS Continued](https://github.com/linuxgurugamer/HullcamVDSContinued)
  (required - kerbcam streams Hullcam camera parts)
- A WebRTC-capable browser on the viewing device (any current Firefox, Chrome,
  Safari, or Edge)
- OS tiers: Linux / Steam Deck is tier-1 (hardware H.264 via VA-API). macOS and
  Windows are experimental tier-2 and encode in software.

## Steps

1. Download `kerbcam-vX.Y.Z.zip` from the
   [releases page](https://github.com/jonpepler/kerbcam/releases).
2. Extract it into your KSP install so that `GameData/Kerbcam/` exists
   (the zip already contains the `GameData/` folder - extract at the KSP root).
3. Install Hullcam VDS Continued if you haven't already.
4. Start KSP and launch a flight with one or more Hullcam camera parts on the
   vessel.
5. On the same machine, open `http://127.0.0.1:8088` in a browser. The bundled
   web page lists the vessel's cameras and starts streams when you click them.

> **Windows:** the first time the sidecar launches, Windows may warn about
> running it. It's safe to approve; you'll only see this on first run.

To watch from **another device**, set
`BindAddress` in `GameData/Kerbcam/settings.cfg`:

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
GameData/Kerbcam/
├── Plugins/Kerbcam.dll       the KSP plugin
├── Sidecar/<rid>/            one encoder/WebRTC sidecar binary per OS
│   ├── linux-x64/            (+ lib/ with bundled ffmpeg shared libs)
│   ├── osx-arm64/
│   └── win-x64/
├── HullcamShaders/           prebuilt Hullcam shader bundle (Linux fix)
├── kerbcam-shaders           kerbcam's atmospheric-FX shader bundle (Linux)
├── kerbcam-shaders.windows   same bundle, Windows (d3d11) shader variants
├── kerbcam-shaders.osx       same bundle, macOS (metal) shader variants
├── settings.cfg              all configuration, commented
├── Kerbcam.version           KSP-AVC version manifest
└── LICENSE
```

The plugin launches the right sidecar for your OS automatically the first
time a flight scene loads, keeps it running for the whole KSP session (scene
changes, reverts, and trips through the KSC do not restart it, and connected
browsers stay connected), and stops it when KSP exits. Nothing else to run.

## Configuration

Edit `GameData/Kerbcam/settings.cfg` like any other KSP mod config. Every
field is commented inline: bind address/port, capture resolution, stream
bitrate, adaptive-performance ceilings, Hullcam filter and atmospheric-FX
toggles, and per-camera overrides.

Updates re-extract `GameData/Kerbcam/`, so direct edits to that file are lost
on the next version. To keep changes across updates, put only the keys you're
changing in `GameData/Kerbcam/PluginData/settings.cfg` instead.

### When settings changes apply

The sidecar runs once per KSP session, so settings split into two groups:

- **Per KSP launch:** `BindAddress`, `Port`, `Width`, `Height`, `BitrateBps`,
  `AutoSpawnSidecar`. These are passed to the sidecar when it starts, so
  editing them mid-game does nothing until you restart KSP. The plugin logs a
  `[Kerbcam]` warning if it notices they changed while a sidecar is running.
- **Per flight-scene entry:** everything else (camera layer/FX/resolution
  overrides, adaptive-performance ceilings, filter toggles). Re-read every
  time a flight loads.

### Adaptive quality (opt-in)

By default kerbcam only adapts _temporally_ under load: it captures fewer of
the streaming cameras per frame, at full resolution, and never touches image
quality on its own. Setting `AdaptiveQuality = true` adds a second, lossy
stage: when the capture staggering has no room left (one camera per frame and
still over budget, or KSP below the `MinKspFps` floor), kerbcam steps render
quality down (resolution first, then FX layers), and steps it back up, one
level at a time, only after roughly 30 seconds of sustained headroom plus a
cooldown after any drop. Quality never rises above the `Width`/`Height` and
layers you configured. Every change is logged to KSP.log as
`[Kerbcam] stagger quality` with the reason.

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

- **Update:** delete `GameData/Kerbcam/` and extract the new zip. If you keep
  user settings in `GameData/Kerbcam/PluginData/`, move that folder aside
  first and restore it after (or delete everything in `GameData/Kerbcam/`
  except `PluginData/`). Direct edits to the shipped `settings.cfg` do not
  survive an update; the `PluginData/` user file is the supported way to
  persist them.
- **Uninstall:** delete `GameData/Kerbcam/`.

If something doesn't work, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
