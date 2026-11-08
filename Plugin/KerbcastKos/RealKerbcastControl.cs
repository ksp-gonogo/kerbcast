using System.Collections.Generic;
using Kerbcast.Api;

namespace Kerbcast.Kos
{
    /* In-game adapter over Kerbcast.API. This is the ONLY file that references
       Kerbcast.API or KSP types, so it JITs only when a real
       KerbcastAddon.Control is used (never in a test that presets the seam
       with a fake). kOS calls arrive on the main thread, as the API requires. */
    internal sealed class RealKerbcastControl : IKerbcastControl
    {
        static ICameras Cameras => KerbcastApi.Instance?.Cameras;

        public bool IsActive => KerbcastApi.Instance != null;

        public IReadOnlyList<KosCameraView> CamerasFor(object vessel)
        {
            var outp = new List<KosCameraView>();
            var cams = Cameras;
            if (cams == null || !(vessel is global::Vessel v)) return outp;
            foreach (var view in cams.CamerasFor(v.persistentId)) outp.Add(ToKos(view));
            return outp;
        }

        public KosCameraView ViewOf(uint id)
        {
            var v = Cameras?.View(id);
            return v == null ? null : ToKos(v);
        }

        public bool SetFov(uint id, float fov) => Cameras?.SetFov(id, fov) ?? false;

        public bool SetPan(uint id, float yaw, float pitch) => Cameras?.SetPan(id, yaw, pitch) ?? false;

        public bool AimAt(uint id, double x, double y, double z) => Cameras?.AimAt(id, new Vec3d(x, y, z)) ?? false;

        public bool SetTrackMode(uint id, int mode) => Cameras?.SetTrackMode(id, (TrackMode)mode) ?? false;

        public int GetTrackMode(uint id) => (int)(Cameras?.GetTrackMode(id) ?? TrackMode.None);

        static KosCameraView ToKos(CameraView v) => new KosCameraView
        {
            FlightId = v.FlightId, PartFlightId = v.PartFlightId, CameraName = v.Name, PartName = v.PartName,
            PartTitle = v.PartTitle, SupportsZoom = v.SupportsZoom, SupportsPan = v.SupportsPan, Fov = (float)v.Fov,
            FovMin = (float)v.FovMin, FovMax = (float)v.FovMax, PanYaw = (float)v.PanYaw, PanPitch = (float)v.PanPitch,
            PanYawMin = (float)v.PanYawMin, PanYawMax = (float)v.PanYawMax, PanPitchMin = (float)v.PanPitchMin,
            PanPitchMax = (float)v.PanPitchMax,
            BoresightX = (float)v.Boresight.X, BoresightY = (float)v.Boresight.Y, BoresightZ = (float)v.Boresight.Z,
            PositionX = (float)v.Position.X, PositionY = (float)v.Position.Y, PositionZ = (float)v.Position.Z,
            TrackMode = (int)v.TrackMode,
        };
    }
}
