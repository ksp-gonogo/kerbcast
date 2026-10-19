using System;
using System.Reflection;
using CommNet;
using UnityEngine;

namespace Kerbcast
{
    /* Where a ground station stands: the body it sits on and body-fixed
       lat/lon/alt, which stay valid through floating-origin shifts and body
       rotation (the world position is recomputed from them every tick). */
    internal sealed class GroundStationSite
    {
        public string NodeName { get; private set; }
        public string DisplayName { get; private set; }
        public CelestialBody Body { get; private set; }
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        /* Altitude of the lens: the station's own altitude plus the configured offset. */
        public double Altitude { get; private set; }
        public bool IsHome { get; private set; }
        /* How the site was found, for the log: commnet-fields, commnet-transform,
           spacecenter or fallback. */
        public string Source { get; private set; }

        /* Stock keeps a home's body and coordinates protected, so they are read
           by reflection, the same route gonogo's CommNetHomeAccess takes. The
           fields are declared on CommNetHome itself, which also covers the
           Extra Ground Stations and Kerbal Konstructs subclasses. */
        private static readonly FieldInfo CommField = Field("comm");
        private static readonly FieldInfo BodyField = Field("body");
        private static readonly FieldInfo LatField = Field("lat");
        private static readonly FieldInfo LonField = Field("lon");
        private static readonly FieldInfo AltField = Field("alt");

        private static FieldInfo Field(string name) =>
            typeof(CommNetHome).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);

        /* Approximate spot about 1 km west-south-west of the KSC launch pad, used
           only when neither a CommNet home nor the SpaceCenter object exists. */
        private const double FallbackLatitude = -0.105;
        private const double FallbackLongitude = -74.567;

        /* The home station (normally KSC). Tries, in order: the CommNetHome
           flagged isKSC (or the first home) with stock's own lat/lon/alt, which
           are only filled while CommNet is enabled for the save; the same home's
           node transform converted through its body; the SpaceCenter object;
           and finally fixed coordinates near the pad on the home body. */
        public static GroundStationSite ResolveHome(double altitudeOffset)
        {
            CommNetHome home = null;
            try
            {
                foreach (var h in UnityEngine.Object.FindObjectsOfType<CommNetHome>())
                {
                    if (h == null) continue;
                    if (h.isKSC) { home = h; break; }
                    if (home == null) home = h;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][GROUNDCAM] CommNetHome scan failed: {ex.Message}");
            }

            if (home != null)
            {
                var site = FromHome(home, altitudeOffset);
                if (site != null) return site;
            }

            var homeBody = FlightGlobals.GetHomeBody();
            if (homeBody == null) return null;

            var sc = SpaceCenter.Instance;
            if (sc != null)
            {
                return OnTerrain("KSC", "KSC", homeBody, sc.Latitude, sc.Longitude, altitudeOffset, "spacecenter");
            }
            return OnTerrain("KSC", "KSC", homeBody, FallbackLatitude, FallbackLongitude, altitudeOffset, "fallback");
        }

        private static GroundStationSite FromHome(CommNetHome home, double altitudeOffset)
        {
            var body = Read<CelestialBody>(BodyField, home)
                ?? home.GetComponentInParent<CelestialBody>()
                ?? FlightGlobals.GetHomeBody();
            if (body == null) return null;

            string node = string.IsNullOrEmpty(home.nodeName) ? "KSC" : home.nodeName;
            string display = Localise(home.displaynodeName, node);

            /* lat/lon/alt are filled by CreateNode, which only runs with CommNet
               on; a home with no comm node has zeros there, not coordinates. */
            if (Read<object>(CommField, home) != null
                && LatField?.GetValue(home) is double lat
                && LonField?.GetValue(home) is double lon
                && AltField?.GetValue(home) is double alt)
            {
                return new GroundStationSite
                {
                    NodeName = node, DisplayName = display, Body = body,
                    Latitude = lat, Longitude = lon, Altitude = alt + altitudeOffset,
                    IsHome = home.isKSC, Source = "commnet-fields",
                };
            }

            var anchor = home.nodeTransform != null ? home.nodeTransform : home.transform;
            if (anchor != null)
            {
                body.GetLatLonAlt(anchor.position, out double tlat, out double tlon, out double talt);
                /* A home under an inactive PQS can sit anywhere; only trust a
                   conversion that lands near the surface. */
                if (!double.IsNaN(talt) && talt > -1000 && talt < 20000)
                {
                    return new GroundStationSite
                    {
                        NodeName = node, DisplayName = display, Body = body,
                        Latitude = tlat, Longitude = tlon, Altitude = talt + altitudeOffset,
                        IsHome = home.isKSC, Source = "commnet-transform",
                    };
                }
            }
            return null;
        }

        private static GroundStationSite OnTerrain(string node, string display, CelestialBody body,
            double lat, double lon, double altitudeOffset, string source)
        {
            double ground = 0;
            try { ground = body.TerrainAltitude(lat, lon); }
            catch (Exception) { /* no PQS: sea level */ }
            return new GroundStationSite
            {
                NodeName = node, DisplayName = display, Body = body,
                Latitude = lat, Longitude = lon, Altitude = ground + altitudeOffset,
                IsHome = true, Source = source,
            };
        }

        /* Stock stores KSC's display name as a localisation tag. */
        private static string Localise(string text, string fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            if (!text.StartsWith("#", StringComparison.Ordinal)) return text;
            try
            {
                var s = KSP.Localization.Localizer.Format(text);
                return string.IsNullOrEmpty(s) || s.StartsWith("#", StringComparison.Ordinal) ? fallback : s;
            }
            catch (Exception) { return fallback; }
        }

        private static T Read<T>(FieldInfo field, object target) where T : class
        {
            if (field == null || target == null) return null;
            try { return field.GetValue(target) as T; }
            catch (Exception) { return null; }
        }

        public override string ToString() =>
            $"node={NodeName} name='{DisplayName}' body={Body?.bodyName} lat={Latitude:F5} lon={Longitude:F5} alt={Altitude:F1} home={IsHome} source={Source}";
    }
}
