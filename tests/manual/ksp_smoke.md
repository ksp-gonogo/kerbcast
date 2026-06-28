# KSP smoke test — run before each release

Manual checklist against a real KSP install (the Deck for tier-1 sign-off).
Automated CI covers the sidecar, protocol, plugin units, and web page; this
covers the one thing it can't — kerbcast inside a live game. Budget ~15 min.

Setup: a save with a launchpad vessel carrying ≥3 Hullcam parts (at least one
zoom-capable, one with a non-Normal `cameraMode`), plus a second vessel in
orbit. Install the release-candidate zip clean (delete `GameData/Kerbcast/`
first). Browser on a second device on the LAN, `BindAddress` set accordingly.

## Lifecycle

- [ ] Flight scene load: KSP.log shows `sidecar started pid=`, no `[Kerbcast]`
      errors; `/health` answers from the second device.
- [ ] `/cameras` lists every Hullcam part on the vessel within a second or two.
- [ ] Open streams for all cameras in the bundled web page: all show live
      video; filtered modes (B&W / CRT / night vision) match the in-game
      Hullcam GUI look.
- [ ] Leave to Space Centre: sidecar exits (log line), no orphan
      `kerbcast-sidecar` process (`pgrep`).
- [ ] Re-enter flight: sidecar relaunches, streams reconnect.
- [ ] Crash recovery: `kill -9` the sidecar mid-stream. Plugin logs the exit,
      restarts it within ~5 s, streams resume after a browser reconnect.

## Flight events

- [ ] Vessel switch (`[`/`]`): old vessel's cameras drop from `/cameras` /
      go destroyed; new vessel's appear.
- [ ] Staging that destroys a camera-carrying stage: its feed shows the
      destroyed/tombstone state, no plugin exceptions in KSP.log, other
      feeds unaffected.
- [ ] Undock and recouple: cameras follow their part's vessel correctly.
- [ ] Time-warp: enter ≥1000× and return; physics warp ×4; streams stay
      sane (frozen during high warp is fine, no crash, recover on exit).
- [ ] Revert to launch: cameras re-register, streams reconnect cleanly.
- [ ] Quicksave/quickload (F5/F9): same.

## Controls + adaptive

- [ ] Zoom a zoom-capable camera from the browser: smooth slew, in-game
      Hullcam right-click GUI FoV tracks it; fixed camera rejects with an
      `error` reply, no log spam.
- [ ] Per-camera layer shed (drop NEAR on one camera): takes effect; planets
      and skybox still render on SCALED/GALAXY.
- [ ] Load the game down (max physics warp at low altitude, or many streams):
      adaptive stagger engages (`adaptive-shed` message names a reason), game
      fps stays above the `MinKspFps` floor, and recovery restores rate.
- [ ] `ThrottleMainScreen` toggle (Pause → Difficulty → Kerbcast): main view
      blanks with the operator warning overlay; toggling back restores it.

## Teardown

- [ ] Kill the KSP window outright (the user's "game crashed" path): the
      sidecar exits rather than lingering (it gets SIGTERM/loses the rings —
      verify no orphan process after ~30 s).
- [ ] Atmospheric FX: a reentry (or cheat-menu set orbit → deorbit) shows
      plasma/bowshock layers on cameras with FX enabled, and no FX-related
      errors in KSP.log.

Sign-off: note KSP version, kerbcast version, OS, encoder backend
(`[Kerbcast.sidecar]` init line), and any deviations, in the release PR /
notes.
