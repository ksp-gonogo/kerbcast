using System;
using System.Collections.Generic;
using System.Linq;
using HullcamVDS;
using Debug = UnityEngine.Debug;

namespace Kerbcast
{
    /* Today's discovery: every MuMechModuleHullCamera on the vessel's parts,
       one KerbcastCamera each, wire-id via CameraId.Synthetic. A part can
       carry multiple camera modules (booster fwd+aft), so iterate all. */
    internal sealed class HullcamProvider : ICameraSourceProvider
    {
        private readonly KerbcastSettings _settings;
        private readonly string _ringDir;
        private readonly int _ringSlots;

        public HullcamProvider(KerbcastSettings settings, string ringDir, int ringSlots)
        {
            _settings = settings;
            _ringDir = ringDir;
            _ringSlots = ringSlots;
        }

        public IEnumerable<ICamera> Enumerate(Vessel vessel, IReadOnlyCollection<uint> existingFlightIds)
        {
            var result = new List<ICamera>();
            foreach (var part in vessel.parts)
            {
                int moduleIdx = 0;
                foreach (var hullcam in part.Modules.OfType<MuMechModuleHullCamera>())
                {
                    var mount = new HullcamMountSource(hullcam);
                    uint flightId = CameraId.Synthetic(part.flightID, moduleIdx, hullcam.cameraName);
                    if (existingFlightIds.Contains(flightId))
                    {
                        moduleIdx++;
                        continue;
                    }

                    try
                    {
                        var partName = mount.PartName;
                        var initialLayers = _settings.GetInitialLayers(partName);
                        var enableFx = _settings.GetEnableAtmosphericFx(partName);
                        var fxLayers = _settings.GetAtmosphericFxLayers(partName);
                        var (renderW, renderH) = _settings.GetRenderSize(partName);
                        result.Add(new KerbcastCamera(
                            mount,
                            flightId,
                            _ringDir,
                            _ringSlots,
                            _settings.Width,
                            _settings.Height,
                            renderW,
                            renderH,
                            initialLayers,
                            enableFx,
                            fxLayers));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Kerbcast] skipping camera flightId={flightId} on part {part.partInfo?.name} (part.flightID={part.flightID}): {ex.Message}");
                    }
                    moduleIdx++;
                }
            }
            return result;
        }
    }
}
