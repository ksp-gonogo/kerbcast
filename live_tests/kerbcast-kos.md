# Live test: KerbcastKos kOS addon (goal check #3)

Verifies the `ADDONS:KERBCAST` kerboscript surface end to end on a real KSP flight:
enumerate cameras, zoom, pan, and track a target with a recalculating `AIM` source.
This is the one check that needs the Deck (a real KSP + GPU + kerbcast stream); the
suffix logic and a headless `PRINT ADDONS:KERBCAST:...` run are already covered by
`Plugin/PanAim.Tests`, `Plugin/AimSourceRegistry.Tests`, and `Plugin/KerbcastKos.Tests`.

## Prerequisites

- kerbcast installed and streaming (plugin + sidecar), a browser/gonogo watching the feed.
- kOS installed.
- The addon deployed: build + copy `Kerbcast.Kos.dll` into the KSP install, then
  **restart KSP** (kOS addons load at startup):

  ```sh
  M=~/personal/gonogo/local_docs/syncthing/kspdata
  dotnet build Plugin/KerbcastKos/KerbcastKos.csproj -c Release \
    /p:KspManaged="$M/KSP_Data/Managed" /p:KspGameData="$M/GameData"
  mkdir -p "$M/GameData/KerbcastKos/Plugins"
  cp Plugin/KerbcastKos/bin/Release/Kerbcast.Kos.dll "$M/GameData/KerbcastKos/Plugins/"
  ```
  (Syncthing carries it to the Deck; the plugin `Kerbcast.dll` it links must already be in
  `GameData/Kerbcast/Plugins/`.)

- A vessel with **a `DC.TurretCam`** (steerable, yaw ±135) and at least one zoom-capable
  Hullcam, plus a **kOS CPU** aboard. Optionally a `hc.launchcam` to see pitch (TurretCam is
  yaw-only, so its `PANPITCH` clamps to 0). Set a target (another vessel/body) to exercise `AIM`.

## Script (`kerbcast-kos-test.ks` on the CPU's volume)

```
PRINT "kerbcast available: " + ADDONS:KERBCAST:AVAILABLE.
PRINT "cameras: " + ADDONS:KERBCAST:CAMERAS:LENGTH.
FOR c IN ADDONS:KERBCAST:CAMERAS {
  PRINT "- " + c:NAME + " uid=" + c:UID
      + " zoom=" + c:SUPPORTSZOOM + " pan=" + c:SUPPORTSPAN
      + " fov=" + ROUND(c:FOV,1) + " [" + ROUND(c:FOVMIN,0) + ".." + ROUND(c:FOVMAX,0) + "]".
}

// Zoom: narrow the first zoom-capable camera toward its tight end.
FOR c IN ADDONS:KERBCAST:CAMERAS {
  IF c:SUPPORTSZOOM {
    SET c:FOV TO c:FOVMIN + 5.
    PRINT c:NAME + " fov -> " + ROUND(c:FOV,1).
    BREAK.
  }
}

// Pan: sweep the turret, then continuously track the target.
FOR c IN ADDONS:KERBCAST:CAMERAS {
  IF c:SUPPORTSPAN {
    PRINT "panning " + c:NAME.
    SET c:PANYAW TO 45.   WAIT 2.   // yaw right
    SET c:PANYAW TO -45.  WAIT 2.   // yaw left
    SET c:PANPITCH TO 15. WAIT 2.   // no-op on yaw-only DC.TurretCam; visible on hc.launchcam
    IF HASTARGET {
      PRINT "tracking " + TARGET:NAME.
      SET c:AIM TO { RETURN TARGET:POSITION. }.  // recalculating source, re-evaluated each tick
      WAIT 8.
      c:STOPAIM().                               // stop tracking; camera holds last angle
      PRINT "tracking stopped".
    }
    BREAK.
  }
}
PRINT "done".
```

Run: `RUN kerbcast-kos-test.` (or `RUNPATH`).

## Expected observations

Terminal:
- `kerbcast available: True`.
- `cameras:` equals the vessel's Hullcam count; each line shows plausible flags (the
  `DC.TurretCam` line has `pan=True`; a NavCam-type line has `zoom=True`) and an FOV range.

On the kerbcast stream (browser/gonogo):
- The zoom-capable camera's view **narrows smoothly** to near `FOVMIN` (eased by the plugin's
  slew, not a hard cut).
- The turret camera's view **sweeps right (+45 yaw), then left (-45)**, smoothly. The `PANPITCH`
  line does nothing on `DC.TurretCam` (yaw-only); on `hc.launchcam` the view tilts up ~15.
- With a target set, the turret **tracks the target continuously** as the vessel/target move
  (this is the `AIM` source re-evaluating each tick). After `c:STOPAIM()`, the camera **holds**
  its last angle instead of following.
- Moves are immediate (craft-local, no operator signal delay).

## README examples must run verbatim

The `Example` snippets in `GameData/KerbcastKos/README.md` are the addon's
advertised surface, so they have to work exactly as printed. Copy each one from
that README (do not retype from here; the point is to prove what a user actually
reads) into its own file and run it:

1. **List cameras and capabilities**. Prints one line per camera with FOV and
   `pan` flag. Pass: no exception, one line per Hullcam on the vessel.
2. **Zoom the first zoom-capable camera in**. Pass: that camera's view narrows
   on the stream, no exception.
3. **Track the current target, then stop**. Needs a target set (`HASTARGET` true)
   and a steerable mount aboard. Pass: the mount tracks the target for ~10s, then
   holds its last angle after `c:STOPAIM()`.

If any snippet throws, or a documented suffix (`:NAME`, `:FOV`, `:FOVMIN`,
`:SUPPORTSPAN`, `:AIM`, `:LOOKAT`, `:BORESIGHT`, `:POSITION`, ...) is missing or
misbehaves, the README is wrong or the addon is: fix whichever, and re-run. The
README and the addon ship together, so a drifted example is a release blocker.

### Bonus demo (optional): track a target with the whole craft

The README's `Demo: track a target with the whole craft` script uses `:BORESIGHT`
and `:POSITION` to steer the vessel so a camera holds a moving target, with a
steerable mount fine-tracking on top. It needs a target set **and** control
authority (reaction wheels / RCS) to turn the craft, so it only makes sense in
flight, not on the pad. Not a v1 pass gate, but it's the headline demo, so run
it if you can: pass = the target stays framed on the stream while the craft slews
to follow, and (with a steerable camera) the mount makes the fine corrections.
The steering math is unverified in sim; if the craft points the wrong way or
oscillates, note it back so the demo script can be corrected.

## Pass criteria

All of:

- `AVAILABLE` is true and `CAMERAS` enumerates the vessel's cameras.
- FOV and yaw changes are visible on the stream (eased, not snapped), clamped to
  each camera's limits.
- The `AIM` source visibly tracks a moving target, then holds on `c:STOPAIM()`.
- **Every example in `GameData/KerbcastKos/README.md` runs verbatim** (the section
  above).

Note any suffix that misbehaves (wrong direction, no clamp, exception in the
terminal) back to the addon issue.
