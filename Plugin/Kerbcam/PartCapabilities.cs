using System.Collections.Generic;

namespace Kerbcam
{
    internal struct PanCapability
    {
        public float YawMin;
        public float YawMax;
        public float PitchMin;
        public float PitchMax;
        /// <summary>Degrees per second during interpolation slew.</summary>
        public float SlewDegPerSec;
        /// <summary>Named transform in the part mesh that rotates for yaw.
        /// Empty string means no mesh animation for yaw.</summary>
        public string YawTransformName;
        /// <summary>Named transform in the part mesh that rotates for pitch.
        /// Empty string means no mesh animation for pitch.</summary>
        public string PitchTransformName;

        public bool SupportsPan => YawMin != YawMax || PitchMin != PitchMax;
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
            ["DC.TurretCam"] = new PanCapability
            {
                YawMin = -135f, YawMax = 135f,
                PitchMin = 0f,  PitchMax = 0f,
                SlewDegPerSec = 180f,
                YawTransformName = "TopJoint",
                PitchTransformName = "",
            },

            // LaunchCam: single joint (hc_launchcam) that carries both yaw and
            // pitch — hc_launchbase is the fixed base sibling. Both transform
            // names point to the same node; Refresh() detects this and applies
            // a compound Euler(-pitch, yaw, 0) in one assignment so they don't
            // fight. Near cam is parented to the joint and uses _baseRotation
            // only (joint carries all rotation). Yaw sign may need verification
            // in-game — cameraForward = 0,0,+1 is opposite TurretCam's 0,0,-1
            // so positive yaw may appear to go left; flip YawMin/YawMax if so.
            ["hc.launchcam"] = new PanCapability
            {
                YawMin = -180f, YawMax = 180f,
                PitchMin = -45f, PitchMax = 60f,
                SlewDegPerSec = 90f,
                YawTransformName = "hc_launchcam",
                PitchTransformName = "hc_launchcam",
            },
        };

        public static PanCapability ForPart(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return None;
            return Table.TryGetValue(partName, out var cap) ? cap : None;
        }
    }
}
