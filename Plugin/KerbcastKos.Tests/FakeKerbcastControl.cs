using System.Collections.Generic;
using System.Linq;

namespace Kerbcast.Kos.Tests
{
    /* Headless stand-in for the control seam. Holds KosCameraViews in a
       dictionary; reads return the stored view (or null once "gone"); writes
       both RECORD the call (Last*) and mutate the stored view so a read-back
       reflects the change (matching the facade's live-slew semantics). No
       Unity/KSP linkage, so presetting KerbcastAddon.Control to this keeps
       RealKerbcastControl from ever JITting. */
    internal sealed class FakeKerbcastControl : IKerbcastControl
    {
        readonly Dictionary<uint, KosCameraView> cams = new Dictionary<uint, KosCameraView>();

        public (uint id, float fov)? LastSetFov;
        public (uint id, float yaw, float pitch)? LastSetPan;
        public (uint id, double x, double y, double z)? LastAim;
        public (uint id, int mode)? LastSetTrackMode;

        public bool IsActive => true;

        /* Seed a camera; returns it so callers can tweak fields inline. */
        public KosCameraView Seed(KosCameraView view)
        {
            cams[view.FlightId] = view;
            return view;
        }

        public IReadOnlyList<KosCameraView> CamerasFor(object vessel) => cams.Values.ToList();

        public KosCameraView ViewOf(uint flightId) =>
            cams.TryGetValue(flightId, out var v) ? v : null;

        public bool SetFov(uint flightId, float fov)
        {
            LastSetFov = (flightId, fov);
            var v = ViewOf(flightId);
            if (v == null) return false;
            v.Fov = fov;
            return true;
        }

        public bool SetPan(uint flightId, float yaw, float pitch)
        {
            LastSetPan = (flightId, yaw, pitch);
            var v = ViewOf(flightId);
            if (v == null) return false;
            v.PanYaw = yaw;
            v.PanPitch = pitch;
            return true;
        }

        public bool AimAt(uint flightId, double x, double y, double z)
        {
            LastAim = (flightId, x, y, z);
            return ViewOf(flightId) != null;
        }

        public bool SetTrackMode(uint flightId, int mode)
        {
            LastSetTrackMode = (flightId, mode);
            var v = ViewOf(flightId);
            // Mirror the facade gate: only pan+zoom cameras honour a set.
            if (v == null || !(v.SupportsPan && v.SupportsZoom)) return false;
            v.TrackMode = mode;
            return true;
        }

        public int GetTrackMode(uint flightId) => ViewOf(flightId)?.TrackMode ?? 0;
    }
}
