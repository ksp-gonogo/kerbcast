using Kerbcast.Api;

namespace ProbeMod
{
    public static class DockingZoom
    {
        /** <summary>Narrows every docking camera on a craft, the README's first example.</summary> */
        public static int Apply(uint vesselPersistentId, double fov)
        {
            var kerbcast = KerbcastApi.Instance;
            if (kerbcast == null || !KerbcastApi.Supports(ApiFeature.CraftIdentity)) return 0;
            int narrowed = 0;
            foreach (var cam in kerbcast.Cameras.CamerasFor(vesselPersistentId))
            {
                if (cam.HasDockingNode == true && kerbcast.Cameras.SetFov(cam.FlightId, fov)) narrowed++;
            }
            return narrowed;
        }
    }
}
