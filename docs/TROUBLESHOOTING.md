# Troubleshooting kerbcast

Work top-down: each section assumes the previous ones check out.

## Where to look first

Everything the plugin and the sidecar log goes to **KSP.log** (in the KSP
install root):

- `[Kerbcast]` - the plugin: camera tracking, sidecar lifecycle, settings.
- `[Kerbcast.sidecar]` - the sidecar's own output, captured by the plugin.

The sidecar also answers `http://<bind-address>:<port>/health` with `ok`, the
quickest "is it up" check.

## The web page doesn't load

1. **Have you launched a flight yet this session?** The sidecar starts on the
   first flight scene of a KSP session and then keeps running through every
   later scene (space centre, tracking station, editor) until you quit KSP. If
   you went straight from the main menu to the web page without ever entering
   flight, it hasn't started yet.
2. **Did the sidecar start?** Search KSP.log for `sidecar started pid=`.
   - `no bundled sidecar binary found` - the `GameData/Kerbcast/Sidecar/<rid>/`
     tree is missing or was flattened during extraction. Re-extract the zip at
     the KSP root.
   - `AutoSpawnSidecar=false` - you've disabled auto-launch in settings.cfg;
     start the binary by hand or remove that line.
   - `failed to start sidecar` - read the exception that follows:
     - **macOS:** the binary is unsigned; Gatekeeper quarantines it on first
       run. Clear it: `xattr -d com.apple.quarantine
       GameData/Kerbcast/Sidecar/osx-arm64/kerbcast-sidecar`
     - **Windows:** Windows may warn about the sidecar the first time it
       launches. It's safe to approve; you'll only see it once.
     - **Permission denied (Linux/macOS):** the executable bit was stripped
       (some download/extract tools do this). The plugin chmods automatically;
       if that failed, `chmod +x GameData/Kerbcast/Sidecar/<rid>/kerbcast-sidecar`.
3. **Did it crash right after starting?** Look for `sidecar exited
   unexpectedly (code …)`. The plugin restarts it up to 5 times with a growing
   delay, then gives up (`sidecar crashed 5 times; giving up until the next
   flight scene`); re-entering a flight scene re-arms it and tries again. The
   `[Kerbcast.sidecar]` lines just above the first exit carry the actual error:
   - `binding HTTP signalling endpoint` - the port is already in use (often a
     previous sidecar that never exited, or another app). Change `Port` in
     settings.cfg or kill the stale process (`pkill kerbcast-sidecar`).
4. **Connecting from another device?** The default `BindAddress = 127.0.0.1`
   only accepts the local machine. Set your LAN IP or `0.0.0.0` (see
   [INSTALL.md](INSTALL.md)) and check the KSP machine's firewall allows the
   port.

## The page loads but lists no cameras

- The vessel needs **Hullcam VDS camera parts** (NavCam, HullCam, BoosterCam,
  …) - kerbcast streams those, not the flight camera.
- Hullcam VDS Continued must be installed; check KSP.log for it loading.
- Cameras register when the flight scene loads. Search KSP.log for
  `[Kerbcast]` tracking lines naming each camera part.

## Cameras list but the feed is black / static / frozen

- **Full-frame static (TV noise)** is kerbcast's deliberate signal-loss look:
  the track exists but no frames are arriving - usually the encoder or the
  game is stalled, or you've paused KSP.
- **Frozen frame** after a vessel switch or revert: the old camera was
  destroyed; close and reopen the stream.
- **Black with planets/stars missing** is a per-camera `Layers` override
  dropping SCALED/GALAXY - see the per-camera block in settings.cfg.
- On **Linux/Proton**, Hullcam's stock shaders don't load; kerbcast swaps in a
  prebuilt bundle (`EnableHullcamLinuxShaderSwap = true`, the default). If
  filtered modes (B&W, CRT, night vision) render wrong, make sure
  `GameData/Kerbcast/HullcamShaders/shaders.linux` exists.

## Choppy streams / low game fps

This is the adaptive scaler doing its job: kerbcast caps its own main-thread
cost and staggers captures rather than dropping quality. Tune in settings.cfg
(put your changes in `GameData/Kerbcast/PluginData/settings.cfg` so they
survive updates; see [INSTALL.md](INSTALL.md#configuration)):

- `MaxKerbcastFrameBudgetMs` - main-thread ceiling for capture work.
- `MaxCaptureFps` - per-camera stream-rate ceiling.
- `MinKspFps` - physics floor; below it kerbcast staggers harder.
- Per-camera `Layers` - a docking cam doesn't need SCALED/GALAXY; shedding
  layers cuts that camera's render cost.
- `ThrottleMainScreen` (Pause → Difficulty Settings → Kerbcast) frees the GPU
  from KSP's own flight view while you watch the feeds.

Encoder note: hardware H.264 is Linux-only (VA-API). macOS/Windows encode in
software (OpenH264), which costs more CPU per camera - fewer concurrent
streams before the budget bites.

## A feed drops or goes to static after a scene change or vessel switch

The sidecar runs for the whole KSP session and your browser stays connected
across scene changes, the space centre, and reverts. What changes is the
*camera list*: leaving a vessel destroys its cameras, so those feeds drop to
the signal-loss static look and disappear from the picker. Pick a camera on the
current vessel, or reopen the stream once the new vessel's cameras register.

## Still stuck?

Open an issue at <https://github.com/jonpepler/kerbcast/issues> with the
`[Kerbcast]`/`[Kerbcast.sidecar]` slice of KSP.log and your OS + KSP version.
