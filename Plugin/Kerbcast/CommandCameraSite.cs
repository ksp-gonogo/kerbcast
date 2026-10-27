using System;
using System.Reflection;
using CommNet;
using UnityEngine;

namespace Kerbcast
{
    /* Where a command camera stands: the body it sits on and body-fixed
       lat/lon/alt, which stay valid through floating-origin shifts and body
       rotation (the world position is recomputed from them every tick). */
    internal sealed class CommandCameraSite
    {
        public string NodeName { get; private set; }
        public string DisplayName { get; private set; }
        public CelestialBody Body { get; private set; }
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        /* Altitude of the lens: the station's own altitude plus the configured offset. */
        public double Altitude { get; private set; }
        public bool IsHome { get; private set; }
        /* Set for a lens standing beside a launch site rather than at a station. */
        public bool IsLaunchSite { get; private set; }
        /* How the site was found, for the log: launchsite, launchsite-transform,
           commnet-fields, commnet-transform, spacecenter or fallback. */
        public string Source { get; private set; }
        /* Set when the camera rides a vessel instead of standing on a body. */
        public Vessel Vantage { get; private set; }
        /* The ridden craft's persistentId; 0 for a lens on a body. Kept apart
           from Vantage, which reads as null once the craft is destroyed. */
        public uint VantagePersistentId { get; private set; }
        public bool HasVantage => VantagePersistentId != 0;
        /* The ridden craft no longer exists, so the lens has no position. */
        public bool VantageGone => HasVantage && (Vantage == null || Vantage.state == Vessel.State.DEAD);
        /* Set for a lens at an arbitrary surface point, not a station or a launch site. */
        public bool IsSurfacePoint { get; private set; }

        /* What kind of place NodeName, the site key, names. */
        public Api.SiteKind Kind =>
            HasVantage ? Api.SiteKind.Vessel
            : IsSurfacePoint ? Api.SiteKind.Surface
            : IsLaunchSite ? Api.SiteKind.LaunchSite
            : Api.SiteKind.Station;

        /* Stock keeps a home's body and coordinates protected, so they are read
           by reflection. The
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
           PickHome chooses with stock's own lat/lon/alt, which
           are only filled while CommNet is enabled for the save; the same home's
           node transform converted through its body; the SpaceCenter object;
           and finally fixed coordinates near the pad on the home body. */
        public static CommandCameraSite ResolveHome(double altitudeOffset)
        {
            CommNetHome home = null;
            try
            {
                home = PickHome(UnityEngine.Object.FindObjectsOfType<CommNetHome>());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] CommNetHome scan failed: {ex.Message}");
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

        /* RealAntennas and similar mods flag many CommNet stations isKSC, so the
           flag alone picks whichever comes first. Among flagged homes (or all,
           if none is flagged) take the one nearest the SpaceCenter object, then
           one named KSC, then the first. */
        private static CommNetHome PickHome(CommNetHome[] homes)
        {
            var candidates = new System.Collections.Generic.List<CommNetHome>();
            foreach (var h in homes)
                if (h != null && h.isKSC) candidates.Add(h);
            if (candidates.Count == 0)
                foreach (var h in homes)
                    if (h != null) candidates.Add(h);
            if (candidates.Count <= 1) return candidates.Count == 1 ? candidates[0] : null;

            var sc = SpaceCenter.Instance;
            if (sc != null)
            {
                CommNetHome nearest = null;
                float best = float.MaxValue;
                foreach (var h in candidates)
                {
                    var t = h.nodeTransform != null ? h.nodeTransform : h.transform;
                    if (t == null) continue;
                    float d = (t.position - sc.transform.position).sqrMagnitude;
                    if (d < best) { best = d; nearest = h; }
                }
                if (nearest != null) return nearest;
            }
            foreach (var h in candidates)
                if (h.nodeName == "KSC" || (h.nodeName ?? "").IndexOf("Kerbal Space Center", StringComparison.OrdinalIgnoreCase) >= 0)
                    return h;
            return candidates[0];
        }

        /* A lens standing `distance` metres from a launch site on `bearingDeg`,
           `height` metres above the ground there. The site is `launchedFrom` (stock pad and runway, Making History and Kerbal
           Konstructs sites), else the stock launch pad. Null when no site is
           known, so the caller can fall back to the home station. `pending` is
           set (with a reason) when a site exists but its coordinates are not
           filled in yet, which is worth retrying rather than falling back. */
        public static CommandCameraSite ResolveLaunchSite(string launchedFrom, double distance, double bearingDeg, double height,
            out string pending, bool fallbackToPad = true)
        {
            pending = null;
            var setup = PSystemSetup.Instance;
            if (setup == null)
            {
                pending = "PSystemSetup not ready";
                return null;
            }

            foreach (var name in fallbackToPad ? new[] { launchedFrom, "LaunchPad" } : new[] { launchedFrom })
            {
                if (string.IsNullOrEmpty(name)) continue;
                try
                {
                    var found = SpawnPointOf(setup, name, out var body, out double lat, out double lon, out string source);
                    if (found == SpawnLookup.Missing) continue;
                    if (found == SpawnLookup.Unset)
                    {
                        if (pending == null) pending = $"'{name}' spawn point unset ({source})";
                        continue;
                    }
                    CommandCameraMath.Destination(lat, lon, bearingDeg, distance, body.Radius,
                        out double lensLat, out double lensLon);
                    var site = OnTerrain(name, setup.GetLaunchSiteDisplayName(name), body,
                        lensLat, lensLon, height, source);
                    site.IsHome = false;
                    site.IsLaunchSite = true;
                    pending = null;
                    return site;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Kerbcast][CMDCAM] launch site '{name}' not usable: {ex.Message}");
                }
            }
            return null;
        }

        private enum SpawnLookup { Missing, Unset, Found }

        /* Stock fills a spawn point's lat/lon from its transform only when the
           facility's upgrade level is applied, which on the first scene of a
           process happens after Kerbcast starts: until then the stored values
           are zeros. So stored values count only when flagged set and not
           (0,0); otherwise the spawn transform is converted directly and
           trusted only if it sits on the terrain at its own coordinates. */
        private static SpawnLookup SpawnPointOf(PSystemSetup setup, string name,
            out CelestialBody body, out double lat, out double lon, out string source)
        {
            body = null; lat = lon = 0;
            source = "launchsite";
            double alt;
            bool set;
            Func<Transform> spawnTransform;
            var facility = setup.GetSpaceCenterFacility(name);
            if (facility != null && facility.spawnPoints != null && facility.spawnPoints.Length > 0)
            {
                var sp = facility.spawnPoints[0];
                sp.GetSpawnPointLatLonAlt(out lat, out lon, out alt);
                set = sp.latlonaltSet;
                body = facility.hostBody;
                spawnTransform = sp.GetSpawnPointTransform;
            }
            else
            {
                var site = setup.GetLaunchSite(name);
                if (site == null || site.spawnPoints == null || site.spawnPoints.Length == 0) return SpawnLookup.Missing;
                var sp = site.spawnPoints[0];
                sp.GetSpawnPointLatLonAlt(out lat, out lon, out alt);
                set = sp.latlonaltSet;
                body = site.Body;
                spawnTransform = sp.GetSpawnPointTransform;
            }
            if (body == null) body = FlightGlobals.GetHomeBody();
            if (body == null) return SpawnLookup.Missing;
            if (set && Plausible(lat, lon)) return SpawnLookup.Found;

            source = set ? "stored-zero" : "stored-unset";
            Transform t = null;
            try { t = spawnTransform(); }
            catch (Exception) { /* facility transform not built yet */ }
            if (t == null) return SpawnLookup.Unset;
            body.GetLatLonAlt(t.position, out double tlat, out double tlon, out double talt);
            if (!Plausible(tlat, tlon) || double.IsNaN(talt)) return SpawnLookup.Unset;
            double ground;
            try { ground = body.TerrainAltitude(tlat, tlon); }
            catch (Exception) { return SpawnLookup.Unset; }
            if (Math.Abs(talt - Math.Max(ground, 0.0)) > MaxSpawnAboveGround) return SpawnLookup.Unset;
            lat = tlat;
            lon = tlon;
            source = "launchsite-transform";
            return SpawnLookup.Found;
        }

        /* A spawn transform further than this from the terrain at its own
           coordinates has not been placed on the planet yet. */
        private const double MaxSpawnAboveGround = 250.0;

        /* Exactly (0,0) is what an unfilled spawn point reads, never a real site. */
        private static bool Plausible(double lat, double lon) =>
            !double.IsNaN(lat) && !double.IsNaN(lon)
            && !(Math.Abs(lat) < 1e-6 && Math.Abs(lon) < 1e-6);

        private static CommandCameraSite FromHome(CommNetHome home, double altitudeOffset)
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
                return new CommandCameraSite
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
                    return new CommandCameraSite
                    {
                        NodeName = node, DisplayName = display, Body = body,
                        Latitude = tlat, Longitude = tlon, Altitude = talt + altitudeOffset,
                        IsHome = home.isKSC, Source = "commnet-transform",
                    };
                }
            }
            return null;
        }

        private static CommandCameraSite OnTerrain(string node, string display, CelestialBody body,
            double lat, double lon, double altitudeOffset, string source)
        {
            double ground = 0;
            try { ground = body.TerrainAltitude(lat, lon); }
            catch (Exception) { /* no PQS: sea level */ }
            /* Over water the lens floats on the sea, not on the seabed. */
            if (body.ocean && ground < 0) ground = 0;
            return new CommandCameraSite
            {
                NodeName = node, DisplayName = display, Body = body,
                Latitude = lat, Longitude = lon, Altitude = ground + altitudeOffset,
                IsHome = true, Source = source,
            };
        }

        /* The CommNet home whose nodeName is `nodeName`, with the lens `height`
           metres above it. Null when there is no such home or it can not be
           placed yet. */
        public static CommandCameraSite ResolveStation(string nodeName, double height)
        {
            CommNetHome[] homes;
            try { homes = UnityEngine.Object.FindObjectsOfType<CommNetHome>(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] CommNetHome scan failed: {ex.Message}");
                return null;
            }
            foreach (var h in homes)
                if (h != null && string.Equals(h.nodeName, nodeName, StringComparison.Ordinal))
                    return FromHome(h, height);
            return null;
        }

        /* A lens `height` metres above the ground (or the sea) at a point on `body`. */
        public static CommandCameraSite ResolveSurface(CelestialBody body, double lat, double lon, double height)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string node = string.Format(inv, "surface:{0}:{1:F5}:{2:F5}", body.bodyName, lat, lon);
            var site = OnTerrain(node, body.bodyName, body, lat, lon, height, "surface");
            site.IsHome = false;
            site.IsSurfacePoint = true;
            return site;
        }

        /* A vessel as the vantage, by persistentId. Null when no such vessel exists. */
        public static CommandCameraSite ResolveVessel(uint persistentId)
        {
            if (persistentId == 0 || FlightGlobals.Vessels == null) return null;
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.persistentId == persistentId) return FromVessel(v);
            return null;
        }

        private static CommandCameraSite FromVessel(Vessel v) => new CommandCameraSite
        {
            NodeName = "vessel:" + v.persistentId,
            DisplayName = v.vesselName,
            Body = v.mainBody,
            Source = "vessel",
            Vantage = v,
            VantagePersistentId = v.persistentId,
        };

        /* Rides the survivor when KSP merges the ridden craft into another (a
           dock or a claw grab). NodeName keeps the original craft's id, so the
           feed keeps its flightId. True when the vantage moved. */
        public bool FollowMerge(uint oldPid, uint newPid)
        {
            uint next = CommandCameraMath.MergedPersistentId(VantagePersistentId, oldPid, newPid);
            if (next == VantagePersistentId) return false;
            if (!FlightGlobals.FindVessel(next, out Vessel survivor) || survivor == null) return false;
            Vantage = survivor;
            VantagePersistentId = next;
            if (survivor.mainBody != null) Body = survivor.mainBody;
            return true;
        }

        /* A vessel as the vantage, found by exact name. Null when no such vessel exists. */
        public static CommandCameraSite ResolveVessel(string name)
        {
            if (string.IsNullOrEmpty(name) || FlightGlobals.Vessels == null) return null;
            foreach (var v in FlightGlobals.Vessels)
            {
                if (v == null || v.vesselName != name) continue;
                return FromVessel(v);
            }
            return null;
        }

        /* The vantage vessel's world position at `ut`, sampled the same way as
           the target's so two moving ends stay on the same tick: its orbit while
           on rails, its live centre of mass while loaded. Follows SOI changes. */
        public bool TryVantagePosition(double ut, out Vector3d world, out string source)
        {
            world = default;
            source = null;
            var v = Vantage;
            if (v == null || v.state == Vessel.State.DEAD || v.mainBody == null) return false;
            Body = v.mainBody;
            return PicturePass.TryCraftPoint(v, ut, out world, out source);
        }

        /* How the lens moves from `ut` on, for the forecast: with its craft
           (on the surface or on its conic) or fixed to its body. */
        public bool TryStationMotion(double ut, out BodyMotion motion)
        {
            motion = default;
            var v = Vantage;
            if (HasVantage)
            {
                if (!TryVantagePosition(ut, out var vantage, out _)) return false;
                if (v.LandedOrSplashed) motion = BodyMotion.OnSurface(v.mainBody, vantage);
                else if (v.orbit != null) motion = BodyMotion.OnOrbit(v.orbit);
                else return false;
                return true;
            }
            if (Body == null) return false;
            motion = BodyMotion.OnSurface(Body, Body.GetWorldSurfacePosition(Latitude, Longitude, Altitude));
            return true;
        }

        /* The key a site at `vantage` will have, before it resolves, only
           where it is certain: a station's CommNet node name, an explicit
           launch site's name (never replaced by the pad), a craft's
           persistentId, a surface point's formatted coordinates. Null when
           only resolving can tell (a launch site taken from the active craft,
           or no vantage), which under a world model reads no scope. */
        public static string KeyFor(CommandVantage vantage)
        {
            if (vantage == null) return null;
            switch (vantage.Kind)
            {
                case CommandVantageKind.Station: return vantage.Name;
                case CommandVantageKind.LaunchSite: return vantage.Name.Length > 0 ? vantage.Name : null;
                case CommandVantageKind.Vessel: return "vessel:" + vantage.PersistentId;
                case CommandVantageKind.Surface:
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "surface:{0}:{1:F5}:{2:F5}", vantage.Body, vantage.Latitude, vantage.Longitude);
                default: return null;
            }
        }

        /* Whether a craft a lens could ride exists now: a live Vessel that is
           not dead, or, in a scene that has not built it, a saved record. The
           lens's own craft is placed by truth, whatever the camera knows. */
        public static bool VantageExists(uint persistentId)
        {
            if (persistentId == 0) return false;
            if (FlightGlobals.FindVessel(persistentId, out Vessel v) && v != null && v.state != Vessel.State.DEAD) return true;
            var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protos == null) return false;
            for (int i = 0; i < protos.Count; i++)
                if (SubjectTrack.RecordCounts(protos[i]) && protos[i].persistentId == persistentId) return true;
            return false;
        }

        /* The body the lens stands on or orbits. */
        public CelestialBody StationBody => HasVantage ? (Vantage != null ? Vantage.mainBody : null) : Body;

        public string VantageName => Vantage != null ? Vantage.vesselName : "";

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

        public override string ToString() => HasVantage
            ? $"node={NodeName} name='{DisplayName}' body={Body?.bodyName} vessel={VantagePersistentId} loaded={(Vantage != null && Vantage.loaded)} source={Source}"
            : $"node={NodeName} name='{DisplayName}' body={Body?.bodyName} lat={Latitude:F5} lon={Longitude:F5} alt={Altitude:F1} home={IsHome} source={Source}";
    }
}
