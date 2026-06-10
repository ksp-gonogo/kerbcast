# Troubleshooting kerbcam

Work top-down: each section assumes the previous ones check out.

## Where to look first

Everything the plugin and the sidecar log goes to **KSP.log** (in the KSP
install root):

- `[Kerbcam]` — the plugin: camera tracking, sidecar lifecycle, settings.
- `[Kerbcam.sidecar]` — the sidecar's own output, captured by the plugin.

The sidecar also answers `http://<bind-address>:<port>/health` with a version
string — the quickest "is it up" check.

## The web page doesn't load

1. **Are you in a flight scene?** The sidecar only runs during flight; it
   stops when you return to the space centre / main menu.
2. **Did the sidecar start?** Search KSP.log for `sidecar started pid=`.
   - `no bundled sidecar binary found` — the `GameData/Kerbcam/Sidecar/<rid>/`
     tree is missing or was flattened during extraction. Re-extract the zip at
     the KSP root.
   - `AutoSpawnSidecar=false` — you've disabled auto-launch in settings.cfg;
     start the binary by hand or remove that line.
   - `failed to start sidecar` — read the exception that follows:
     - **macOS:** the binary is unsigned; Gatekeeper quarantines it on first
       run. Clear it: `xattr -d com.apple.quarantine
       GameData/Kerbcam/Sidecar/osx-arm64/kerbcam-sidecar`
     - **Windows:** SmartScreen may block first execution — allow it once.
     - **Permission denied (Linux/macOS):** the executable bit was stripped
       (some download/extract tools do this). The plugin chmods automatically;
       if that failed, `chmod +x GameData/Kerbcam/Sidecar/<rid>/kerbcam-sidecar`.
3. **Did it crash right after starting?** Look for `sidecar exited (code …)`.
   The plugin restarts it up to 5 times with a growing delay, then gives up
   until the next scene load (`sidecar crashed 5 times — giving up`). The
   `[Kerbcam.sidecar]` lines just above the first exit carry the actual error:
   - `binding HTTP signalling endpoint` — the port is already in use (often a
     previous sidecar that never exited, or another app). Change `Port` in
     settings.cfg or kill the stale process (`pkill kerbcam-sidecar`).
4. **Connecting from another device?** The default `BindAddress = 127.0.0.1`
   only accepts the local machine. Set your LAN IP or `0.0.0.0` (see
   [INSTALL.md](INSTALL.md)) and check the KSP machine's firewall allows the
   port.

## The page loads but lists no cameras

- The vessel needs **Hullcam VDS camera parts** (NavCam, HullCam, BoosterCam,
  …) — kerbcam streams those, not the flight camera.
- Hullcam VDS Continued must be installed; check KSP.log for it loading.
- Cameras register when the flight scene loads. Search KSP.log for
  `[Kerbcam]` tracking lines naming each camera part.

## Cameras list but the feed is black / static / frozen

- **Full-frame static (TV noise)** is kerbcam's deliberate signal-loss look:
  the track exists but no frames are arriving — usually the encoder or the
  game is stalled, or you've paused KSP.
- **Frozen frame** after a vessel switch or revert: the old camera was
  destroyed; close and reopen the stream.
- **Black with planets/stars missing** is a per-camera `Layers` override
  dropping SCALED/GALAXY — see the per-camera block in settings.cfg.
- On **Linux/Proton**, Hullcam's stock shaders don't load; kerbcam swaps in a
  prebuilt bundle (`EnableHullcamLinuxShaderSwap = true`, the default). If
  filtered modes (B&W, CRT, night vision) render wrong, make sure
  `GameData/Kerbcam/HullcamShaders/shaders.linux` exists.

## Choppy streams / low game fps

This is the adaptive scaler doing its job: kerbcam caps its own main-thread
cost and staggers captures rather than dropping quality. Tune in settings.cfg
(put your changes in `GameData/Kerbcam/PluginData/settings.cfg` so they
survive updates; see [INSTALL.md](INSTALL.md#configuration)):

- `MaxKerbcamFrameBudgetMs` — main-thread ceiling for capture work.
- `MaxCaptureFps` — per-camera stream-rate ceiling.
- `MinKspFps` — physics floor; below it kerbcam staggers harder.
- Per-camera `Layers` — a docking cam doesn't need SCALED/GALAXY; shedding
  layers cuts that camera's render cost.
- `ThrottleMainScreen` (Pause → Difficulty Settings → Kerbcam) frees the GPU
  from KSP's own flight view while you watch the feeds.

Encoder note: hardware H.264 is Linux-only (VA-API). macOS/Windows encode in
software (OpenH264), which costs more CPU per camera — fewer concurrent
streams before the budget bites.

## Streams disconnect when leaving the flight scene

Expected: the plugin stops the sidecar on scene exit and relaunches it on the
next flight. Reconnect from the browser after the new scene loads.

## Still stuck?

Open an issue at <https://github.com/jonpepler/kerbcam/issues> with the
`[Kerbcam]`/`[Kerbcam.sidecar]` slice of KSP.log and your OS + KSP version.
