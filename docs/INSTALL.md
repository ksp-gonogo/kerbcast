# Installing kerbcam

> Each GitHub Release also carries these steps in its notes — if anything here
> disagrees with the notes on the release you downloaded, the release notes win.

## Requirements

- Kerbal Space Program 1.12.x
- [Hullcam VDS Continued](https://github.com/linuxgurugamer/HullcamVDSContinued)
  (required — kerbcam streams Hullcam camera parts)
- A WebRTC-capable browser on the viewing device (any current Firefox, Chrome,
  Safari, or Edge)
- OS tiers: Linux / Steam Deck is tier-1 (hardware H.264 via VA-API). macOS and
  Windows are experimental tier-2 and encode in software.

## Steps

1. Download `kerbcam-vX.Y.Z.zip` from the
   [releases page](https://github.com/jonpepler/kerbcam/releases).
2. Extract it into your KSP install so that `GameData/Kerbcam/` exists
   (the zip already contains the `GameData/` folder — extract at the KSP root).
3. Install Hullcam VDS Continued if you haven't already.
4. Start KSP and launch a flight with one or more Hullcam camera parts on the
   vessel.
5. On the same machine, open `http://127.0.0.1:8088` in a browser. The bundled
   web page lists the vessel's cameras and starts streams when you click them.

To watch from **another device** (the usual mission-control setup), create
`GameData/Kerbcam/PluginData/settings.cfg` (the update-proof user settings
file, see [Configuration](#configuration)) containing:

```
Settings
{
  BindAddress = 0.0.0.0   // or your LAN IP
}
```

then browse to `http://<ksp-machine-ip>:8088` from the other device.

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

The plugin launches the right sidecar for your OS automatically when the
flight scene loads and stops it when you leave. Nothing else to run.

## Configuration

The shipped `GameData/Kerbcam/settings.cfg` documents every field inline:
bind address/port, capture resolution, stream bitrate, adaptive-performance
ceilings, Hullcam filter and atmospheric-FX toggles, and per-camera overrides.

That file is part of the mod bundle, so every update replaces it. For
settings that should survive updates, create
`GameData/Kerbcam/PluginData/settings.cfg` (make the `PluginData` folder
yourself; same format as the shipped file) and put only the keys you want to
change in it, for example:

```
Settings
{
  BindAddress = 0.0.0.0
  BitrateBps = 2500000
}
```

Precedence is per key: the user file wins over the shipped file, and any key
absent from both uses the built-in default. The release zip never contains
`PluginData/`, so the file persists through reinstalls. KSP.log reports which
of the two files were loaded at flight-scene start.

## Updating / uninstalling

- **Update:** delete `GameData/Kerbcam/` and extract the new zip. If you keep
  user settings in `GameData/Kerbcam/PluginData/`, move that folder aside
  first and restore it after (or delete everything in `GameData/Kerbcam/`
  except `PluginData/`). Direct edits to the shipped `settings.cfg` do not
  survive an update; the `PluginData/` user file is the supported way to
  persist them.
- **Uninstall:** delete `GameData/Kerbcam/`.

If something doesn't work, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
