# KerbcastKos

A [kOS](https://github.com/KSP-KOS/KOS) addon for [kerbcast](https://github.com/ksp-gonogo/kerbcast). It lets a
kerboscript running on a vessel's own CPU enumerate that vessel's kerbcast
cameras and control them: set field of view, pan steerable mounts, and
target-track a moving point.

## Requirements

- kerbcast (the camera mod itself)
- kOS

Install both alongside this addon.

## API

Everything hangs off `ADDONS:KERBCAST`.

| Suffix | Type | Meaning |
|---|---|---|
| `:AVAILABLE` | bool | True when the addon and kerbcast are both live. |
| `:CAMERAS` | list | The active vessel's kerbcast cameras. |
| `:CAMERA(uid)` | camera | One camera by its UID string (a part's `UID`). |

Each camera has:

| Suffix | Type | Meaning |
|---|---|---|
| `:UID` | string | Stable id (the camera part's `UID`). |
| `:NAME` | string | Display name. |
| `:SUPPORTSZOOM` | bool | Whether FOV can change. |
| `:SUPPORTSPAN` | bool | Whether the mount can pan (a fixed camera cannot). |
| `:FOV` | scalar | Field of view, degrees. Settable. Eases to the target. |
| `:FOVMIN` / `:FOVMAX` | scalar | FOV limits for this camera. |
| `:PANYAW` | scalar | Yaw angle, degrees. Settable on steerable mounts. |
| `:PANPITCH` | scalar | Pitch angle, degrees. Settable on steerable mounts. |
| `:PANYAWMIN` / `:PANYAWMAX` | scalar | Yaw limits. |
| `:PANPITCHMIN` / `:PANPITCHMAX` | scalar | Pitch limits. |
| `:AIM` | vector or delegate | Set to a **vector** to aim once at that point and hold; set to a **callback** returning a vector (e.g. `{ RETURN TARGET:POSITION. }`) to track it continuously. Targets are ship-relative, like `TARGET:POSITION`. |
| `:STOPAIM()` | method | Stop tracking; the mount holds its last angle. |
| `:LOOKAT(vector)` | bool | Aim once at a point (same as `SET cam:AIM TO vector`), returning whether it was accepted. |
| `:BORESIGHT` | vector | World-space unit forward of the stream (where it currently points). |
| `:POSITION` | vector | Lens position in the same frame as `TARGET:POSITION`. |
| `:TRACK` | string | Auto-track mode: `"none"`, `"vessel"` (the active vessel), or `"target"` (its target). `"active"` / `"activevessel"` alias `"vessel"`; anything unrecognised reads as `"none"`. Settable on pan+zoom cameras only (a no-op otherwise); the camera then auto-aims and auto-zooms to follow. Synchronous: the set applies at once. Linked with the browser: a mode set here shows in every viewer, and a viewer-set mode reads back here. |

`:BORESIGHT` and `:POSITION` let a script steer the *vessel* to hold a target the
mount alone can't reach: rotate the craft so `:BORESIGHT` lines up with
`TARGET:POSITION - cam:POSITION`, optionally with a steerable mount fine-tracking
on top.

Sets are clamped to each camera's limits, and pan/aim sets are ignored on
cameras that cannot pan. FOV and pan ease toward their targets rather than
snapping, so writing a value every tick tracks smoothly.

## Example

```
// List the cameras and their capabilities.
FOR c IN ADDONS:KERBCAST:CAMERAS {
  PRINT c:NAME + " (fov " + ROUND(c:FOV,0) + ", pan " + c:SUPPORTSPAN + ")".
}

// Zoom the first zoom-capable camera in.
FOR c IN ADDONS:KERBCAST:CAMERAS {
  IF c:SUPPORTSZOOM { SET c:FOV TO c:FOVMIN + 5. BREAK. }
}

// Track the current target with a steerable mount, then stop.
FOR c IN ADDONS:KERBCAST:CAMERAS {
  IF c:SUPPORTSPAN AND HASTARGET {
    SET c:AIM TO { RETURN TARGET:POSITION. }.  // re-evaluated each tick
    WAIT 10.
    c:STOPAIM().                               // stop tracking, hold last angle
    BREAK.
  }
}
```

The `:AIM` callback is the intended way to follow an ARBITRARY point: it is a
recalculating source, so as the target and vessel move, the mount keeps
pointing at it. `:LOOKAT(v)` is the one-shot equivalent for a fixed point.

For the common case of following the active vessel or its target, `:TRACK` is
higher level: `SET cam:TRACK TO "target".` and the camera auto-aims and
auto-zooms to keep it framed, no per-tick callback and no `LOCK STEERING`
caveat. `SET cam:TRACK TO "none".` stops it. Because the track mode is shared
with the browser, a mode you set here also lights up in the web viewer (and a
mode set there reads back through `:TRACK`). If both `:AIM`/`:LOOKAT` and
`:TRACK` are set on one camera, the browser-linked `:TRACK` wins each frame it
has a valid target.

> **Run continuous `:AIM` tracking from a program, not the terminal.** A kOS
> callback (`{ ... }`) lives only as long as the context that created it, and
> typing `LOCK STEERING` / `UNLOCK STEERING` at the interpreter can leave the CPU
> in a state where scheduled callbacks (`:AIM` tracking included) quietly stop
> running. This is a kOS quirk, not something the addon can prevent. If tracking
> goes unresponsive, `REBOOT.` the CPU to clear it. Putting your script in a
> `.ks` file and running it with `RUN` avoids the problem entirely: the callback
> lives for the whole run, and steering stays out of the interpreter. Setting
> `:AIM` to a plain vector, or using `:LOOKAT(v)`, is a one-shot and is not
> affected.

## Demo: track a target with the whole craft

`:BORESIGHT` and `:POSITION` let the *vessel* help point a camera, so even a
fixed camera can hold a moving target. This rotates the hull so the camera's
boresight stays on the target; if the camera is also steerable, its mount
fine-tracks on top, so vessel and camera move together. Needs a target set and
enough control authority (reaction wheels / RCS) to turn the craft.

```
IF NOT HASTARGET {
  PRINT "Set a target first.".
} ELSE {
  SET cam TO ADDONS:KERBCAST:CAMERAS[0].   // the camera to keep on target

  // Camera pointing relative to the hull. Constant for a fixed mount, so
  // capture it once; the vessel then carries the camera onto the target.
  SET mount TO SHIP:FACING:INVERSE * LOOKDIRUP(cam:BORESIGHT, SHIP:FACING:TOPVECTOR).

  // Attitude that puts the camera's boresight on the target, recomputed live.
  LOCK aimDir TO LOOKDIRUP(TARGET:POSITION - cam:POSITION, SHIP:UP:FOREVECTOR).
  LOCK STEERING TO aimDir * mount:INVERSE.

  // If the camera can pan, let the mount fine-track too (camera + vessel).
  IF cam:SUPPORTSPAN { SET cam:AIM TO { RETURN TARGET:POSITION. }. }

  PRINT "Tracking " + TARGET:NAME + " with the vessel.".
  WAIT 30.

  IF cam:SUPPORTSPAN { cam:STOPAIM(). }
  UNLOCK STEERING.
  PRINT "Done.".
}
```

On the stream the target stays framed while the craft slews to follow it; with a
steerable camera the mount makes the fine corrections and the hull handles the
coarse rotation.

## Licence

KerbcastKos is **GPL-3.0-only** because it links kOS (GPLv3). This is separate
from kerbcast's own CC BY-NC-SA licence; see `NOTICE-KOS.txt`. The full licence
text is in `LICENSE`.
