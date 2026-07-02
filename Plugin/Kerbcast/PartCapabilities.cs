using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    internal struct PanCapability
    {
        public float YawMin;
        public float YawMax;
        public float PitchMin;
        public float PitchMax;
        /// <summary>Degrees per second during interpolation slew. This is the
        /// fast mesh-follow rate (90–180°/s) the Current value chases the
        /// Target at — it is the final smoothing filter, NOT the framing speed.</summary>
        public float SlewDegPerSec;
        /// <summary>Full-deflection framing speed (degrees/sec) applied to a
        /// normalised pan rate from `set-pan-rate`. Deliberately distinct from
        /// (and slower than) the mesh `SlewDegPerSec`: this is how fast the
        /// operator-commanded *target* advances when holding a pan input; the
        /// slew then tracks that advancing target. 0 disables hold-to-pan.</summary>
        public float PanRateDegPerSec;
        /// <summary>Named transform in the part mesh that rotates for yaw.
        /// Empty string means no mesh animation for yaw.</summary>
        public string YawTransformName;
        /// <summary>Named transform in the part mesh that rotates for pitch.
        /// Empty string means no mesh animation for pitch.</summary>
        public string PitchTransformName;
        /// <summary>Optional fixed-base transform that co-rotates in yaw only
        /// (no pitch). Prevents the moving head from clipping into a symmetric
        /// base when yawing.</summary>
        public string YawBaseTransformName;
        /// <summary>Local Y offset from the yaw transform's parent origin to
        /// the physical hinge point. When non-zero, pitch rotation is applied
        /// around this point (via a localPosition compensation) rather than the
        /// transform origin. Tune from the DumpModelTransforms log.</summary>
        public float PitchPivotLocalY;
        /// <summary>Optional override for the near camera's mount position,
        /// expressed in the YAW JOINT's local frame (not the part frame). Use
        /// for a yaw-only joint whose authored <c>cameraPosition</c> sits well
        /// off the yaw axis: parenting the lens rigidly to the joint would then
        /// orbit it through a wide arc. Placing the mount on (or near) the yaw
        /// axis via this override keeps the lens on-pivot so it rotates in place
        /// while still travelling with the head. Null = re-express the part's
        /// <c>cameraPosition</c> into the joint frame as-is.</summary>
        public Vector3? CameraMountLocal;
        /// <summary>When true, negate the yaw angle applied to the yaw joint
        /// (and, since the near camera is parented rigidly to that joint, the
        /// lens with it). Set for a yaw-only joint whose local frame turns the
        /// rigidly-parented camera opposite to the operator's <c>+panYaw = pan
        /// right</c> convention — TopJoint does this, so without the negation
        /// commanding pan-right sweeps the view left. Negating flips head and
        /// lens together, so it corrects the control direction without
        /// reintroducing any head/lens sign split. Only consulted on the
        /// yaw-only joint path; compound joints (LaunchCam) leave it false.</summary>
        public bool YawInvert;

        /// <summary>Optional cap on the widest usable FoV, overriding the part's
        /// authored cameraFoVMax. Wide-angle parts (TurretCam authors 100) look
        /// fisheye and exaggerate pan parallax from an off-axis mount; clamp to a
        /// rectilinear-sane maximum. Null leaves the authored range untouched.</summary>
        public float? FovMaxCap;

        public bool SupportsPan => YawMin != YawMax || PitchMin != PitchMax;
    }

    /// <summary>
    /// Zoom (FoV) rate capability. Independent of <see cref="PanCapability"/>:
    /// pan and zoom are gated separately (zoom support is runtime-detected from
    /// the <c>MuMechModuleHullCameraZoom</c> subclass, not from a part-name
    /// table), so this is a small dedicated struct rather than an extension of
    /// the pan caps. There is no per-part zoom table — every zoom-capable
    /// camera uses <see cref="Default"/>.
    /// </summary>
    internal struct ZoomCapability
    {
        /// <summary>Full-deflection zoom speed in FoV degrees/sec applied to a
        /// normalised zoom rate from `set-zoom-rate`. +rate zooms IN (FoV
        /// decreases) so this is subtracted from the FoV target. 0 disables
        /// hold-to-zoom.</summary>
        public float ZoomRateDegPerSec;
        /// <summary>Discrete-zoom smoothing rate (degrees/sec) the displayed
        /// FoV slews toward its target at. Drives BOTH discrete `set-fov` and
        /// `set-zoom-rate`. Must be non-zero or FoV freezes (MoveTowards by 0).</summary>
        public float FovSlewDegPerSec;

        /// <summary>Default zoom rates. Must be used in place of
        /// <c>default(ZoomCapability)</c> for any zoom-capable camera — an
        /// all-zero struct would freeze FoV slew (MoveTowards by 0) and disable
        /// hold-to-zoom.</summary>
        public static ZoomCapability Default => new ZoomCapability
        {
            ZoomRateDegPerSec = 20f,
            FovSlewDegPerSec = 60f,
        };
    }

    // Hardcoded capability table keyed by KSP part name (partInfo.name).
    // Pan capability is NOT read from settings.cfg — Camera {} nodes stay
    // PartName / Layers / Width / Height only. This table is the single
    // source of truth for which parts can pan and by how much.
    internal static class PartCapabilities
    {
        public static readonly PanCapability None = default;

        private static readonly Dictionary<string, PanCapability> Table =
            new Dictionary<string, PanCapability>(System.StringComparer.Ordinal)
        {
            // TurretCam: yaw-only steerable mount. Model hierarchy (verified via
            // in-game transform dump): TopJoint contains Body/Lens/Rangefinder/
            // cameraTransform (the visible rotating head); BottomJoint contains
            // only base/col_base (the fixed mount plate). We rotate TopJoint
            // directly via FindModelTransform + localRotation — same technique as
            // solar panels and landing gear. The near camera is parented RIGIDLY
            // to TopJoint so the lens travels WITH the head and the head stays
            // behind the lens through the whole sweep — fixing the "TurretCam
            // sees its own body mid-sweep, mirrored" symptom. Limits from the
            // commented-out servo block: hardMinMaxLimits = -177, 177 — trimmed
            // to ±135 to leave a dead zone around the mount's cable entry.
            //
            // CameraMountLocal pins the lens to the model's authored optical node
            // (~0.047, 0.046, -0.200, already in TopJoint's local frame) rather
            // than the part's authored cameraPosition (~0.5 lateral, well off the
            // yaw axis). On-axis, the rigidly-parented lens rotates in place
            // instead of orbiting the joint — so we get the rigid head-mount
            // without reintroducing the wide-arc "rotates about the wrong point"
            // behaviour that the earlier in-place fix removed.
            //
            // Pan direction: the yaw sense was originally calibrated against the
            // mirrored feed (the old capture readback flipped horizontally). Now
            // that the readback flips vertically instead, the streamed image is
            // no longer mirrored, so TopJoint's rotation matches the operator's
            // +panYaw = pan-right convention directly and no inversion is needed.
            // Inverting it here sweeps the view the wrong way.
            ["DC.TurretCam"] = new PanCapability
            {
                YawMin = -135f, YawMax = 135f,
                PitchMin = 0f,  PitchMax = 0f,
                // Slew halved from 180 to match LaunchCam: under staggered
                // capture the mesh advances every frame but only streams on
                // permit frames, so a fast slew streams big per-frame jumps
                // (judder). 90 lets the current chase the target gradually.
                SlewDegPerSec = 90f,
                PanRateDegPerSec = 25f,
                YawTransformName = "TopJoint",
                PitchTransformName = "",
                CameraMountLocal = new Vector3(0.047f, 0.046f, -0.200f),
                YawInvert = false,
                // Authored cameraFoVMax is 100, which reads as fisheye and
                // magnifies the off-axis-mount pan parallax. Cap to 70.
                FovMaxCap = 70f,
            },

            // LaunchCam: single joint (hc_launchcam) that carries both yaw and
            // pitch — hc_launchbase is the fixed base sibling. Both transform
            // names point to the same node; Refresh() detects this and applies
            // a compound Euler(-pitch, yaw, 0) in one assignment so they don't
            // fight. Near cam is parented to the joint and uses _baseRotation
            // only (joint carries all rotation).
            // Yaw direction: cameraForward = 0,0,+1 means the lens faces +Z in
            // the joint's local frame. Unity Euler(0,+Y,0) rotates CCW from
            // above, sweeping +Z toward +X — turning the camera RIGHT for
            // +panYaw. Confirmed correct in live KSP.
            ["hc.launchcam"] = new PanCapability
            {
                YawMin = -180f, YawMax = 180f,
                PitchMin = -45f, PitchMax = 60f,
                SlewDegPerSec = 90f,
                PanRateDegPerSec = 25f,
                YawTransformName = "hc_launchcam",
                PitchTransformName = "hc_launchcam",
                YawBaseTransformName = "hc_launchbase",
                // Approximate hinge height — verify from DumpModelTransforms log
                // (grep "[Kerbcast] model transforms for hc.launchcam") and update
                // to the localY of hc_launchcam's first visible geometry.
                PitchPivotLocalY = 0.3f,
            },
            // No per-part CameraRollDeg entries: the upside-down feed on the
            // Deck was a global AsyncGPUReadback vertical inversion on
            // bottom-left-origin (OpenGL) graphics APIs, not a per-part model
            // quirk. It is corrected once for every camera in the capture
            // pipeline (see the graphicsUVStartsAtTop V-flip in
            // KerbcastCamera). The old per-part 180 roll entries (navcam,
            // nightvision, docking ports, mumech.hullcam, launchcam) only
            // masked that flip for the specific parts they were tuned against,
            // and left every other camera inverted.
        };

        public static PanCapability ForPart(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return None;
            var hit = Table.TryGetValue(partName, out var cap);
            if (KerbcastSettings.DebugCameraLogging)
                UnityEngine.Debug.Log($"[Kerbcast] PartCapabilities.ForPart('{partName}') {(hit ? "matched" : "no match")}");
            return hit ? cap : None;
        }
    }
}
