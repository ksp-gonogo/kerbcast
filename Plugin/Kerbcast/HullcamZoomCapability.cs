using HullcamVDS;

namespace Kerbcast
{
    /* Hullcam zoom capability. Limits come from the MuMechModuleHullCameraZoom
       subclass, FovMax clamped by the part's FovMaxCap. Core clamps requests to
       [FovMin, FovMax]; SetFov applies the value to the module (which also drives
       the Hullcam right-click GUI). */
    public sealed class HullcamZoomCapability : IZoomCapability
    {
        private readonly MuMechModuleHullCamera _cam;
        private readonly float _fovMin;
        private readonly float _fovMax;

        private HullcamZoomCapability(MuMechModuleHullCamera cam, float fovMin, float fovMax)
        {
            _cam = cam; _fovMin = fovMin; _fovMax = fovMax;
        }

        /* Returns null only when the module isn't the zoom subclass. A
           zero-width authored range (e.g. DC.munCam 25/25) still yields a
           capability with FovMin == FovMax; KerbcastCamera decides whether
           that range is wide enough to call "supports zoom". */
        public static HullcamZoomCapability TryCreate(MuMechModuleHullCamera cam, float? fovMaxCap)
        {
            var zoom = cam as MuMechModuleHullCameraZoom;
            if (zoom == null) return null;
            float min = zoom.cameraFoVMin;
            float max = zoom.cameraFoVMax;
            if (fovMaxCap.HasValue && max > fovMaxCap.Value) max = fovMaxCap.Value;
            return new HullcamZoomCapability(cam, min, max);
        }

        public float FovMin => _fovMin;
        public float FovMax => _fovMax;
        public float Fov => _cam.cameraFoV;
        public void SetFov(float fov) => _cam.cameraFoV = fov;
    }
}
