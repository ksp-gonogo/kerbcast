using System;
using System.Collections.Generic;
using System.Linq;
using Debug = UnityEngine.Debug;

namespace Kerbcast
{
    /* Enumerates crew as KerbalFaceCameras, one per ProtoCrewMember: seated crew
       from each part's protoModuleCrew, plus EVA kerbals swept from the loaded
       EVA vessels. Tourists are skipped (no operator interest). Dedupe on
       existingFlightIds keeps a seated kerbal who goes EVA on ONE camera/ring
       (that camera switches backend in place). Per-camera construction is
       isolated in try/catch so one bad kerbal can't drop the rest, matching
       HullcamProvider. */
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

            /* EVA kerbals are their own one-part vessels, so the per-vessel seated
               sweep above misses anyone already on EVA (or on a vessel not being
               scanned this pass). Sweep loaded EVA vessels so each gets a
               KerbalFaceCamera at the SAME persistentID-based wire-id. Dedupe on
               existingFlightIds means a kerbal tracked while seated who then walks
               out the hatch is NOT rebuilt here: its existing camera switches to the
               EVA backend in place (KerbalFaceCamera.ResolveLocation), keeping one
               ring with no teardown. */
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded != null)
            {
                foreach (var v in loaded)
                {
                    if (v == null || !v.isEVA) continue;
                    var eva = v.evaController;
                    if (eva == null || eva.part == null) continue;
                    var crew = eva.part.protoModuleCrew;
                    if (crew.Count == 0) continue;
                    var pcm = crew[0];
                    if (pcm == null || pcm.type == ProtoCrewMember.KerbalType.Tourist) continue;
                    if (pcm.persistentID == 0) continue;
                    uint flightId = CameraId.KerbalWireId(pcm.persistentID);
                    if (existingFlightIds.Contains(flightId)) continue;

                    try
                    {
                        result.Add(new KerbalFaceCamera(pcm, eva.part, _ringDir, _ringSlots, _width, _height));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Kerbcast] skipping EVA kerbal cam flightId={flightId} (persistentID={pcm.persistentID}): {ex.Message}");
                    }
                }
            }

            return result;
        }
    }
}
