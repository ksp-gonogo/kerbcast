using System.Collections.Generic;

namespace Kerbcast.Kos
{
    /* In-game adapter over the static Kerbcast.KerbcastControl facade. This
       is the ONLY file that references UnityEngine / Kerbcast.dll types, so
       it JITs only when a real KerbcastAddon.Control is used (never in a
       test that presets the seam with a fake). */
    internal sealed class RealKerbcastControl : IKerbcastControl
    {
        public bool IsActive => Kerbcast.KerbcastControl.IsActive;

        public IReadOnlyList<KosCameraView> CamerasFor(object vessel)
        {
            var views = Kerbcast.KerbcastControl.CamerasFor(vessel as global::Vessel);
            var outp = new List<KosCameraView>(views.Count);
            foreach (var v in views) outp.Add(ToKos(v));
            return outp;
        }

        public KosCameraView ViewOf(uint id)
        {
            var v = Kerbcast.KerbcastControl.ViewOf(id);
            return v == null ? null : ToKos(v);
        }

        public bool SetFov(uint id, float fov) => Kerbcast.KerbcastControl.SetFov(id, fov);

        public bool SetPan(uint id, float yaw, float pitch) => Kerbcast.KerbcastControl.SetPan(id, yaw, pitch);

        public bool AimAt(uint id, double x, double y, double z) =>
            Kerbcast.KerbcastControl.AimAt(id, new UnityEngine.Vector3((float)x, (float)y, (float)z));

        public bool SetTrackMode(uint id, int mode) => Kerbcast.KerbcastControl.SetTrackMode(id, mode);

        public int GetTrackMode(uint id) => Kerbcast.KerbcastControl.GetTrackMode(id);

        static KosCameraView ToKos(Kerbcast.KerbcastCameraView v) => new KosCameraView
        {
            FlightId = v.FlightId, PartFlightId = v.PartFlightId, CameraName = v.CameraName, PartName = v.PartName,
            PartTitle = v.PartTitle, SupportsZoom = v.SupportsZoom, SupportsPan = v.SupportsPan, Fov = v.Fov,
            FovMin = v.FovMin, FovMax = v.FovMax, PanYaw = v.PanYaw, PanPitch = v.PanPitch, PanYawMin = v.PanYawMin,
            PanYawMax = v.PanYawMax, PanPitchMin = v.PanPitchMin, PanPitchMax = v.PanPitchMax,
            BoresightX = v.BoresightX, BoresightY = v.BoresightY, BoresightZ = v.BoresightZ,
            PositionX = v.PositionX, PositionY = v.PositionY, PositionZ = v.PositionZ,
            TrackMode = v.TrackMode,
            PartHandle = v.Part,
        };
    }
}
