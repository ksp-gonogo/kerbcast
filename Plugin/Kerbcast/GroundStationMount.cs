using UnityEngine;

namespace Kerbcast
{
    /* ICameraMountSource for a camera standing on a body's surface rather than
       on a part. The near and far layers parent to an anchor GameObject that
       GroundStationRig moves and turns every rendered tick; it is never
       parented to the body, because the rig recomputes its world position from
       lat/lon/alt each tick and that already absorbs floating-origin shifts. */
    internal sealed class GroundStationMount : ICameraMountSource
    {
        private readonly GameObject _anchor;
        private readonly float _defaultFov;

        public GroundStationSite Site { get; }

        public GroundStationMount(GroundStationSite site, uint flightId, float defaultFov)
        {
            Site = site;
            _defaultFov = defaultFov;
            _anchor = new GameObject($"Kerbcast_{flightId}_GroundAnchor");
        }

        public Transform PartTransform => _anchor != null ? _anchor.transform : null;
        public Transform FindModelTransform(string name) => null;
        public string CameraTransformName => null;
        public Vector3 CameraPosition => Vector3.zero;
        public Vector3 CameraForward => Vector3.forward;
        public Vector3 CameraUp => Vector3.up;

        public float DefaultFieldOfView => _defaultFov;
        /* Nothing stands within a metre of the lens, and a larger near plane
           buys the near layer depth precision. */
        public float NearClip => 1f;
        public int FilterMode => 0;

        public IZoomCapability Zoom => null;
        public IPanCapability Pan => null;

        /* The tracked craft. Followed live, so the feed survives vessel switches. */
        public Vessel Vessel => FlightGlobals.ActiveVessel;

        public string PartName => "kerbcast.groundStation";
        public string PartTitle => "Ground tracking station";
        public string CameraName => $"{Site.DisplayName} tracking";
        /* A ground camera has no owning craft; the station's name is the stable
           label clients group and key it by. */
        public string VesselDisplayName => Site.DisplayName;

        public bool IsAlive => _anchor != null && Site.Body != null;
        public bool OwnsPart(Part part) => false;
        public Part OwningPart => null;

        public void DestroyAnchor()
        {
            if (_anchor != null) Object.Destroy(_anchor);
        }
    }
}
