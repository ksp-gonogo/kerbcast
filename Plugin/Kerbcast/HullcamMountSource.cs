using UnityEngine;
using HullcamVDS;

namespace Kerbcast
{
    /* ICameraMountSource backed by a Hullcam VDS module. Placement/identity/
       vessel reproduce exactly the reads KerbcastCamera made off the module.
       Zoom is a HullcamZoomCapability (or null); Pan is filled in Task 3. */
    public sealed class HullcamMountSource : ICameraMountSource
    {
        private readonly MuMechModuleHullCamera _cam;
        private readonly IZoomCapability _zoom;
        private readonly IPanCapability _pan;

        public HullcamMountSource(MuMechModuleHullCamera cam)
        {
            _cam = cam;
            var caps = PartCapabilities.ForPart(cam.part.partInfo?.name ?? "");
            _zoom = HullcamZoomCapability.TryCreate(cam, caps.FovMaxCap);
            _pan = HullcamPanCapability.TryCreate(this, caps);
        }

        public Transform PartTransform => _cam.part.transform;
        public Transform FindModelTransform(string name) => _cam.part.FindModelTransform(name);
        public string CameraTransformName => _cam.cameraTransformName;
        public Vector3 CameraPosition => _cam.cameraPosition;
        public Vector3 CameraForward => _cam.cameraForward;
        public Vector3 CameraUp => _cam.cameraUp;

        public float DefaultFieldOfView => _cam.cameraFoV;
        public float NearClip => _cam.cameraClip;
        public int FilterMode => (int)_cam.cameraMode;

        public IZoomCapability Zoom => _zoom;
        public IPanCapability Pan => _pan;

        public Vessel Vessel => _cam != null ? _cam.vessel : null;

        public string PartName => _cam.part.partInfo?.name ?? "unknown";
        public string PartTitle => _cam.part.partInfo?.title ?? PartName;
        public string CameraName => string.IsNullOrEmpty(_cam.cameraName) ? PartTitle : _cam.cameraName;
        public string VesselDisplayName =>
            _cam.vessel?.GetDisplayName() ?? _cam.vessel?.vesselName ?? "<unknown>";

        public bool IsAlive => _cam != null && _cam.part != null && _cam.vessel != null;
        public bool OwnsPart(Part part) => _cam != null && _cam.part == part;
        public Part OwningPart => _cam != null ? _cam.part : null;
    }
}
