using System.Collections.Generic;

namespace Kerbcam
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
        /// <summary>Roll offset (degrees) applied to the near-camera base
        /// rotation around the view axis. Use 180 to correct an upside-down
        /// feed when cameraForward points opposite to the default camera Z.</summary>
        public float CameraRollDeg;
        /// <summary>When true, the sign of the yaw angle applied to the mesh
        /// joint transform is negated. Set this for cameras whose
        /// <c>cameraForward</c> points in the −Z direction in the joint's local
        /// frame (i.e. <c>cameraForward.z &lt; 0</c>). Without the negation,
        /// rotating the joint by +Y (counterclockwise from above) sweeps a
        /// backward-facing camera to the left — opposite of what the operator
        /// expects for +panYaw. Cameras with <c>cameraForward.z &gt; 0</c>
        /// face the joint's +Z and sweep right under +Y rotation, which is
        /// already correct, so they leave this false.</summary>
        public bool YawMeshInvert;

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
            // solar panels and landing gear. The near camera is also parented to
            // TopJoint so stream and mesh move together. Limits from the
            // commented-out servo block: hardMinMaxLimits = -177, 177 — trimmed
            // to ±135 to leave a dead zone around the mount's cable entry.
            //
            // YawMeshInvert = true: TurretCam's cameraForward = (0,0,-1), meaning
            // the lens faces the −Z direction in TopJoint's local frame. Unity's
            // Euler(0,+Y,0) rotates the joint CCW when viewed from above, which
            // sweeps −Z toward −X — turning the camera LEFT when the operator
            // commands +panYaw (pan right). Negating the joint angle corrects this
            // so the on-screen view and the mesh both sweep right on +panYaw.
            // The near camera's own localRotation is not touched (it is parented to
            // the joint and carries _baseRotation only when a yaw joint is present),
            // so only the joint-drive line in Refresh() is affected.
            ["DC.TurretCam"] = new PanCapability
            {
                YawMin = -135f, YawMax = 135f,
                PitchMin = 0f,  PitchMax = 0f,
                SlewDegPerSec = 180f,
                PanRateDegPerSec = 25f,
                YawTransformName = "TopJoint",
                PitchTransformName = "",
                YawMeshInvert = true,
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
            // +panYaw. Confirmed correct in live KSP; no YawMeshInvert needed.
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
                // (grep "[Kerbcam] model transforms for hc.launchcam") and update
                // to the localY of hc_launchcam's first visible geometry.
                PitchPivotLocalY = 0.3f,
                // cameraForward = 0,0,+1 is opposite to TurretCam's 0,0,-1;
                // the model's local frame ends up with Y inverted, flipping the
                // feed upside-down. 180° roll corrects it.
                CameraRollDeg = 180f,
            },
            // NavCam and NightVision: cameraForward = 0,1,0 / cameraUp = 0,0,-1.
            // Same model-frame Y-inversion as launchcam; 180° roll corrects it.
            // No pan capability.
            ["hc.navcam"] = new PanCapability { CameraRollDeg = 180f },
            ["hc.nightvision"] = new PanCapability { CameraRollDeg = 180f },

            // Stock docking ports patched by HullcamVDS DockingPortCameraPatch.cfg.
            // Those with cameraForward = 0,1,0 / cameraUp = 0,0,-1 need the same
            // 180° roll correction. The lateral and mk2 ports use cameraForward =
            // 0,0,-1 (same as TurretCam) so they are left uncorrected.
            ["dockingPort1"]    = new PanCapability { CameraRollDeg = 180f },
            ["dockingPort2"]    = new PanCapability { CameraRollDeg = 180f },
            ["dockingPort3"]    = new PanCapability { CameraRollDeg = 180f },
            ["dockingPortLarge"] = new PanCapability { CameraRollDeg = 180f },

            // Hullcam Deluxe — the base MuMechModuleHullCamera variant. Cfg
            // `name = mumech_hullcam`; KSP normalises underscore→dot in
            // partInfo.name, so the table key matches as "mumech.hullcam"
            // (same convention as the hc.* entries above). cameraForward
            // matches TurretCam's (0,0,-1) but the model's local frame is
            // Y-inverted — empirical 180° roll correction.
            ["mumech.hullcam"] = new PanCapability { CameraRollDeg = 180f },
        };

        public static PanCapability ForPart(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return None;
            var hit = Table.TryGetValue(partName, out var cap);
            UnityEngine.Debug.Log($"[Kerbcam] PartCapabilities.ForPart('{partName}') → {(hit ? "matched" : "no match")} (roll={(hit ? cap.CameraRollDeg : 0):F0}°)");
            return hit ? cap : None;
        }
    }
}
