using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Debug = UnityEngine.Debug;

namespace Kerbcast
{
    /* One ground camera beside the active craft's launch site (or at the home
       CommNet station, per GroundStationAnchor), watching whichever craft is
       active. Not tied to any vessel: it returns
       the same camera whatever vessel is scanned, and KerbcastCore keeps it
       across vessel switches. Only registered when EnableGroundStationCamera
       is on. The site is resolved once per flight scene. */
    internal sealed class GroundStationProvider : ICameraSourceProvider
    {
        private readonly KerbcastSettings _settings;
        private readonly string _ringDir;
        private readonly int _ringSlots;
        private GroundStationSite _site;
        private bool _resolveFailedLogged;

        public GroundStationProvider(KerbcastSettings settings, string ringDir, int ringSlots)
        {
            _settings = settings;
            _ringDir = ringDir;
            _ringSlots = ringSlots;
        }

        public IEnumerable<ICamera> Enumerate(Vessel vessel, IReadOnlyCollection<uint> existingFlightIds)
        {
            var result = new List<ICamera>();
            if (_site == null)
            {
                if (_settings.GroundStationAnchor == "launchsite")
                {
                    _site = GroundStationSite.ResolveLaunchSite(FlightGlobals.ActiveVessel ?? vessel,
                        _settings.GroundStationPadDistance, _settings.GroundStationPadBearing,
                        _settings.GroundStationPadHeight);
                    if (_site != null)
                        Debug.Log($"[Kerbcast][GROUNDCAM] launch site resolved: {_site} distance={_settings.GroundStationPadDistance:F0} bearing={_settings.GroundStationPadBearing:F0} height={_settings.GroundStationPadHeight:F1}");
                    else
                        Debug.LogWarning("[Kerbcast][GROUNDCAM] no launch site found; using the home station");
                }
                if (_site == null)
                {
                    _site = GroundStationSite.ResolveHome(_settings.GroundStationAltitudeOffset);
                    if (_site == null)
                    {
                        if (!_resolveFailedLogged)
                            Debug.LogWarning("[Kerbcast][GROUNDCAM] no home station or home body found; ground camera not created");
                        _resolveFailedLogged = true;
                        return result;
                    }
                    Debug.Log($"[Kerbcast][GROUNDCAM] home station resolved: {_site}");
                }
            }

            uint flightId = CameraId.GroundStationWireId(_site.NodeName);
            if (existingFlightIds.Contains(flightId)) return result;

            GroundStationMount mount = null;
            try
            {
                mount = new GroundStationMount(_site, flightId, (float)_settings.GroundStationFovMax);
                var cam = new KerbcastCamera(
                    mount,
                    flightId,
                    _ringDir,
                    _ringSlots,
                    _settings.Width,
                    _settings.Height,
                    _settings.Width,
                    _settings.Height,
                    CameraLayers.All,
                    enableAtmosphericFx: false,
                    fxLayers: AtmoFxLayers.None,
                    manifestKind: "ground_station",
                    manifestExtraFields: ManifestFields(_site));
                try
                {
                    cam.Rig = new GroundStationRig(cam, mount,
                        _settings.GroundStationFrameFraction,
                        _settings.GroundStationFovMin,
                        _settings.GroundStationFovMax);
                }
                catch
                {
                    cam.Dispose();
                    throw;
                }
                result.Add(cam);
                Debug.Log($"[Kerbcast][GROUNDCAM] ground camera cam={flightId} created for {CameraId.GroundCentreId(_site.NodeName)}");
            }
            catch (Exception ex)
            {
                mount?.DestroyAnchor();
                Debug.LogWarning($"[Kerbcast][GROUNDCAM] ground camera cam={flightId} not created: {ex}");
            }
            return result;
        }

        /* Station identity for /cameras. station_id is gonogo's centre id, so a
           feed lines up with its command centre with no mapping table. */
        private static string ManifestFields(GroundStationSite s)
        {
            var inv = CultureInfo.InvariantCulture;
            return $"  \"station_id\": \"{Esc(CameraId.GroundCentreId(s.NodeName))}\",\n"
                + $"  \"station_node_name\": \"{Esc(s.NodeName)}\",\n"
                + $"  \"station_name\": \"{Esc(s.DisplayName)}\",\n"
                + $"  \"station_body\": \"{Esc(s.Body != null ? s.Body.bodyName : "")}\",\n"
                + $"  \"station_latitude\": {s.Latitude.ToString("R", inv)},\n"
                + $"  \"station_longitude\": {s.Longitude.ToString("R", inv)},\n"
                + $"  \"station_altitude\": {s.Altitude.ToString("R", inv)}";
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
