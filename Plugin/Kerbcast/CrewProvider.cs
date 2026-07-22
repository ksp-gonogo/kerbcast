using System;
using System.Collections.Generic;
using System.Linq;
using Debug = UnityEngine.Debug;

namespace Kerbcast
{
    /* Enumerates seated crew as reporting-only KerbalFaceCameras, one per
       ProtoCrewMember found in a part's protoModuleCrew. Tourists are skipped
       (no operator interest); EVA kerbals are a later stage. Per-camera
       construction is isolated in try/catch so one bad kerbal can't drop the
       rest, matching HullcamProvider. */
    internal sealed class CrewProvider : ICameraSourceProvider
    {
        private readonly string _ringDir;
        private readonly int _ringSlots;
        private readonly int _width;
        private readonly int _height;

        public CrewProvider(string ringDir, int ringSlots, int width, int height)
        {
            _ringDir = ringDir;
            _ringSlots = ringSlots;
            _width = width;
            _height = height;
        }

        public IEnumerable<ICamera> Enumerate(Vessel vessel, IReadOnlyCollection<uint> existingFlightIds)
        {
            var result = new List<ICamera>();
            foreach (var part in vessel.parts)
            {
                foreach (var pcm in part.protoModuleCrew)
                {
                    if (pcm.type == ProtoCrewMember.KerbalType.Tourist) continue;
                    /* persistentID 0 is KSP's "unassigned" sentinel, not a real
                       seated kerbal (seen on dev-tool-spawned crew). Every such
                       entry would also collide at wire-id 0x80000000, so skip. */
                    if (pcm.persistentID == 0) continue;
                    uint flightId = CameraId.KerbalWireId(pcm.persistentID);
                    if (existingFlightIds.Contains(flightId)) continue;

                    try
                    {
                        result.Add(new KerbalFaceCamera(pcm, part, _ringDir, _ringSlots, _width, _height));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Kerbcast] skipping kerbal cam flightId={flightId} (persistentID={pcm.persistentID}) on part {part.partInfo?.name}: {ex.Message}");
                    }
                }
            }
            return result;
        }
    }
}
