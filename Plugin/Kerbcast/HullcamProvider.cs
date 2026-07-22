using System.Collections.Generic;
using System.Linq;
using HullcamVDS;

namespace Kerbcast
{
    /* Today's discovery: every MuMechModuleHullCamera on the vessel's parts,
       one MountDescriptor each, wire-id via CameraId.Synthetic. A part can
       carry multiple camera modules (booster fwd+aft), so iterate all. */
    public sealed class HullcamProvider : ICameraSourceProvider
    {
        public IEnumerable<MountDescriptor> Enumerate(Vessel vessel)
        {
            foreach (var part in vessel.parts)
            {
                int moduleIdx = 0;
                foreach (var hullcam in part.Modules.OfType<MuMechModuleHullCamera>())
                {
                    var mount = new HullcamMountSource(hullcam);
                    uint flightId = CameraId.Synthetic(part.flightID, moduleIdx, hullcam.cameraName);
                    yield return new MountDescriptor(mount, flightId, mount.PartName);
                    moduleIdx++;
                }
            }
        }
    }
}
