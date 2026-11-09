using System;
using System.Collections.Generic;
using System.Reflection;
using CommNet;
using UnityEngine;
using Upgradeables;

namespace Kerbcast
{
    /* The game's switches that decide which ground stations exist, read once per use. */
    internal struct GameStationRules
    {
        public bool ExtraStationsOn;
        public bool OtherLaunchSites;

        /* Stock's defaults with no game loaded. */
        public static GameStationRules Now()
        {
            var rules = new GameStationRules { ExtraStationsOn = true, OtherLaunchSites = true };
            var p = HighLogic.CurrentGame?.Parameters;
            if (p == null) return rules;
            try
            {
                rules.ExtraStationsOn = p.CustomParams<CommNetParams>().enableGroundStations;
                rules.OtherLaunchSites = p.Difficulty.AllowOtherLaunchSites;
            }
            catch (Exception) { /* a game without the CommNet parameters: stock's defaults */ }
            return rules;
        }
    }

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
        /* A CommNet ground station's own flags, which decide whether the game has it switched on (StationEnabled). */
        public bool IsCommNetHome { get; private set; }
        public bool StationIsKsc { get; private set; }
        public bool StationIsPermanent { get; private set; }
        /* Set for a lens standing beside a launch site rather than at a station. */
        public bool IsLaunchSite { get; private set; }
        /* How the site was found, for the log: launchsite, launchsite-transform,
           pqscity, pqscity-prefab, pqscity-facility, pqscity-origin,
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
        /* A launch site lens's site itself: where its craft stand before liftoff. Altitude is the spawn point's. */
        public bool HasLaunchPoint { get; private set; }
        public double LaunchLatitude { get; private set; }
        public double LaunchLongitude { get; private set; }
        public double LaunchAltitude { get; private set; }

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
                    var found = SpawnPointOf(setup, name, out var body, out double lat, out double lon, out double alt, out string source);
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
                    site.HasLaunchPoint = !double.IsNaN(alt);
                    site.LaunchLatitude = lat;
                    site.LaunchLongitude = lon;
                    site.LaunchAltitude = alt;
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
           trusted only if it sits on the terrain at its own coordinates.
           Far from Kerbin stock never builds the facility's model, so the
           spawn transform does not exist and the site is placed from the
           city and the model's prefab instead (PlacedUnbuilt). */
        private static SpawnLookup SpawnPointOf(PSystemSetup setup, string name,
            out CelestialBody body, out double lat, out double lon, out double alt, out string source)
        {
            body = null; lat = lon = alt = 0;
            source = "launchsite";
            bool set;
            Func<Transform> spawnTransform;
            string spawnPath = null;
            LaunchSite site = null;
            var facility = setup.GetSpaceCenterFacility(name);
            if (facility != null && facility.spawnPoints != null && facility.spawnPoints.Length > 0)
            {
                var sp = facility.spawnPoints[0];
                sp.GetSpawnPointLatLonAlt(out lat, out lon, out alt);
                set = sp.latlonaltSet;
                body = facility.hostBody;
                spawnTransform = sp.GetSpawnPointTransform;
                spawnPath = sp.spawnTransformURL;
            }
            else
            {
                facility = null;
                site = setup.GetLaunchSite(name);
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
            if (t != null)
            {
                body.GetLatLonAlt(t.position, out double tlat, out double tlon, out double talt);
                double ground = double.NaN;
                if (Plausible(tlat, tlon) && !double.IsNaN(talt))
                {
                    try { ground = body.TerrainAltitude(tlat, tlon); }
                    catch (Exception) { /* no terrain to check against */ }
                }
                if (!double.IsNaN(ground) && Math.Abs(talt - Math.Max(ground, 0.0)) <= MaxSpawnAboveGround)
                {
                    lat = tlat;
                    lon = tlon;
                    alt = talt;
                    source = "launchsite-transform";
                    return SpawnLookup.Found;
                }
                if (PlacedByCity(t, Vector3.zero, body, out lat, out lon, out alt))
                {
                    source = "pqscity";
                    return SpawnLookup.Found;
                }
            }

            /* Near the site stock builds the facility within a second or so,
               and its own placement beats a worked-out one, so keep waiting. */
            if (body.pqsController == null || body.pqsController.isActive) return SpawnLookup.Unset;
            if (!PlacedUnbuilt(facility, spawnPath, site, body, out lat, out lon, out alt, out string unbuiltSource))
                return SpawnLookup.Unset;
            source = unbuiltSource;
            return SpawnLookup.Found;
        }

        /*
         * A launch site's spawn point while its body's terrain is unbuilt and
         * the spawn transform does not exist. Stock keeps a facility's model
         * as a prefab per upgrade level and instantiates it only when the
         * Space Center is near, so the spawn point's offset is read from the
         * current level's prefab and placed where the model would go. Failing
         * that the facility's own origin stands in, and for a site without a
         * facility (Making History) its city's origin. Those stand within the
         * site's own footprint (half a runway at most), still the right
         * place for a lens that stands 500 m off.
         */
        private static bool PlacedUnbuilt(PSystemSetup.SpaceCenterFacility facility, string spawnPath, LaunchSite site,
            CelestialBody body, out double lat, out double lon, out double alt, out string source)
        {
            lat = lon = alt = double.NaN;
            source = null;
            if (facility != null && facility.facilityTransform != null)
            {
                if (SpawnInPrefab(facility, spawnPath, out Transform model, out Vector3 inModel)
                    && PlacedByCity(model, inModel, body, out lat, out lon, out alt))
                {
                    source = "pqscity-prefab";
                    return true;
                }
                if (PlacedByCity(facility.facilityTransform, Vector3.zero, body, out lat, out lon, out alt))
                {
                    source = "pqscity-facility";
                    return true;
                }
                return false;
            }
            var city = site?.pqsCity;
            if (city != null && PlacedByCity(city.transform, Vector3.zero, body, out lat, out lon, out alt))
            {
                source = "pqscity-origin";
                return true;
            }
            return false;
        }

        /* Where the spawn point sits inside the facility's model, read from the
           current upgrade level's prefab: `model` is the stand-in transform the
           built model will take the place of, `inModel` the spawn point in its
           local space. */
        private static bool SpawnInPrefab(PSystemSetup.SpaceCenterFacility facility, string spawnPath, out Transform model, out Vector3 inModel)
        {
            model = null;
            inModel = Vector3.zero;
            try
            {
                CommandCameraMath.SplitSpawnPath(spawnPath, out string modelName, out string pathInModel);
                var upgradeable = facility.facilityTransform.GetComponent<UpgradeableObject>();
                var levels = upgradeable != null ? upgradeable.UpgradeLevels : null;
                if (levels == null || levels.Length == 0 || pathInModel.Length == 0) return false;

                model = upgradeable.FacilityTransform;
                if (model == null || model.name != modelName) model = facility.facilityTransform.Find(modelName);
                if (model == null) return false;

                double level = 0;
                try { level = facility.GetFacilityLevel(); }
                catch (Exception) { /* no upgrade scenario: the first level's model */ }
                var prefab = levels[CommandCameraMath.FacilityLevelIndex(level, levels.Length)]?.facilityPrefab;
                var spawn = prefab != null ? prefab.transform.Find(pathInModel) : null;
                if (spawn == null) return false;
                for (var x = spawn; x != prefab.transform; x = x.parent)
                {
                    if (x == null) return false;
                    inModel = x.localPosition + x.localRotation * Vector3.Scale(x.localScale, inModel);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] spawn point from the facility's model failed: {ex.Message}");
                return false;
            }
        }

        /* The first `T` on `t` or a parent, inactive ones included: GetComponentInParent skips inactive objects, and far from Kerbin its whole terrain is. */
        private static T InParents<T>(Transform t) where T : Component
        {
            for (var x = t; x != null; x = x.parent)
            {
                var c = x.GetComponent<T>();
                if (c != null) return c;
            }
            return null;
        }

        /*
         * Where the point `local` (in `t`'s own space) stands once its PQSCity
         * places itself, worked out without the city having done so. A scene that starts far
         * from the site (the active craft at Duna) never runs the city's
         * Orientate, so the spawn point keeps no coordinates and its transform
         * sits wherever the prefab left it. This repeats Orientate's placement
         * from the city's own settings, and composes only local transforms:
         * world positions are rounded to kilometres that far from the origin.
         */
        private static bool PlacedByCity(Transform t, Vector3 local, CelestialBody body, out double lat, out double lon, out double alt)
        {
            lat = lon = alt = double.NaN;
            try
            {
                var city = InParents<PQSCity>(t);
                var pqs = city != null ? InParents<PQS>(city.transform) : null;
                if (city == null || pqs == null || city.transform.parent == null) return false;

                var inCity = local;
                for (var x = t; x != city.transform; x = x.parent)
                {
                    if (x == null) return false;
                    inCity = x.localPosition + x.localRotation * Vector3.Scale(x.localScale, inCity);
                }

                var ct = city.transform;
                Vector3d radial = ((Vector3d)city.repositionRadial).normalized;
                var cityPos = (Vector3d)ct.localPosition;
                if (city.repositionToSphere) cityPos = radial * (pqs.radius + city.repositionRadiusOffset);
                if (city.repositionToSphereSurface)
                {
                    double h = pqs.GetSurfaceHeight(radial);
                    if (city.repositionToSphereSurfaceAddHeight) h += city.repositionRadiusOffset;
                    cityPos = radial * h;
                }
                var cityRot = city.reorientToSphere
                    ? Quaternion.FromToRotation(city.reorientInitialUp, ((Vector3)cityPos).normalized) * Quaternion.AngleAxis(city.reorientFinalAngle, Vector3.up)
                    : ct.localRotation;
                Vector3d p = cityPos + (Vector3d)(cityRot * Vector3.Scale(ct.localScale, inCity));

                for (var x = ct.parent; x != pqs.transform; x = x.parent)
                {
                    if (x == null) return false;
                    p = (Vector3d)x.localPosition + (Vector3d)(x.localRotation * Vector3.Scale(x.localScale, (Vector3)p));
                }
                var offset = (Vector3d)pqs.transform.TransformVector((Vector3)p);
                body.GetLatLonAlt(body.position + offset, out lat, out lon, out alt);
                return Plausible(lat, lon) && !double.IsNaN(alt) && Math.Abs(offset.magnitude - pqs.radius) < MaxCityFromSurface;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] spawn point from its PQSCity failed: {ex.Message}");
                return false;
            }
        }

        /* A worked-out spawn point further than this from the PQS's sea-level sphere is not a place on the planet. */
        private const double MaxCityFromSurface = 20000.0;

        /* A spawn transform further than this from the terrain at its own
           coordinates has not been placed on the planet yet. */
        private const double MaxSpawnAboveGround = 250.0;

        /* Exactly (0,0) is what an unfilled spawn point reads, never a real site. */
        private static bool Plausible(double lat, double lon) =>
            !double.IsNaN(lat) && !double.IsNaN(lon)
            && !(Math.Abs(lat) < 1e-6 && Math.Abs(lon) < 1e-6);

        private static CommandCameraSite FromHome(CommNetHome home, double altitudeOffset, bool quiet = false)
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
                    Latitude = lat, Longitude = lon, Altitude = OnGround(body, node, lat, lon, alt, quiet) + altitudeOffset,
                    IsHome = home.isKSC, Source = "commnet-fields",
                    IsCommNetHome = true, StationIsKsc = home.isKSC, StationIsPermanent = home.isPermanent,
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
                        Latitude = tlat, Longitude = tlon, Altitude = OnGround(body, node, tlat, tlon, talt, quiet) + altitudeOffset,
                        IsHome = home.isKSC, Source = "commnet-transform",
                        IsCommNetHome = true, StationIsKsc = home.isKSC, StationIsPermanent = home.isPermanent,
                    };
                }
            }
            return null;
        }

        /* A home's altitude replaced by the ground at its coordinates (the sea over water); the stored one is kept only when no terrain can be read. */
        private static double OnGround(CelestialBody body, string node, double lat, double lon, double alt, bool quiet)
        {
            double ground = double.NaN;
            if (body.pqsController != null)
            {
                try { ground = body.TerrainAltitude(lat, lon); }
                catch (Exception) { /* no terrain to read: keep the stored altitude */ }
                if (body.ocean && ground < 0) ground = 0;
            }
            double placed = CommandCameraMath.StationGroundAltitude(alt, ground);
            if (!quiet && Math.Abs(placed - alt) > 1)
                Debug.Log($"[Kerbcast][CMDCAM] station '{node}' stored altitude {alt:F1}m differs from the ground there ({ground:F1}m); "
                    + "the lens stands on the ground");
            return placed;
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

        /*
         * Every ground station in the scene that can be placed, once per node
         * name as ResolveStation finds them, standing on the ground, with
         * whether the game has it switched on.
         */
        public static void GroundStations(List<GroundStation> into)
        {
            into.Clear();
            CommNetHome[] homes;
            try { homes = UnityEngine.Object.FindObjectsOfType<CommNetHome>(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] CommNetHome scan failed: {ex.Message}");
                return;
            }
            var rules = GameStationRules.Now();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in homes)
            {
                if (h == null || string.IsNullOrEmpty(h.nodeName) || !seen.Add(h.nodeName)) continue;
                CommandCameraSite site;
                try { site = FromHome(h, 0, quiet: true); }
                catch (Exception) { continue; }
                if (site == null) continue;
                into.Add(new GroundStation
                {
                    NodeName = site.NodeName,
                    DisplayName = site.DisplayName,
                    Body = site.Body.bodyName,
                    BodyIndex = site.Body.flightGlobalsIndex,
                    Latitude = site.Latitude,
                    Longitude = site.Longitude,
                    Altitude = site.Altitude,
                    Enabled = site.StationEnabled(rules),
                    DisabledReason = site.StationOffReason(rules),
                });
            }
        }

        /*
         * Whether the game has this ground station switched on. Stock leaves a
         * station it has turned off in the scene (with "Enable Extra
         * Groundstations" off it only skips building its network node), so
         * ResolveStation still finds and places it; this applies the rule
         * stock's CommNetHome.CreateNode uses. Any other site is always on.
         */
        public bool StationEnabled(GameStationRules rules) =>
            !IsCommNetHome || NetworkMath.StationEnabled(rules.ExtraStationsOn, StationIsKsc, StationIsPermanent, rules.OtherLaunchSites);

        /* Why StationEnabled is false, by the same rule; null while it is on. */
        public string StationOffReason(GameStationRules rules) =>
            !IsCommNetHome ? null : NetworkMath.StationOffReason(rules.ExtraStationsOn, StationIsKsc, StationIsPermanent, rules.OtherLaunchSites);

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

        /* The way the lens's craft points: its control point's forward axis,
           else the craft's own. False with no craft to read. */
        public bool TryVantageForward(out Vector3d forward)
        {
            forward = default;
            var v = Vantage;
            if (v == null || v.state == Vessel.State.DEAD) return false;
            var t = v.ReferenceTransform != null ? v.ReferenceTransform : v.transform;
            if (t == null) return false;
            forward = t.up;
            return forward.magnitude > 0.5;
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
        /* A craft vantage outside its body's atmosphere, or at a body with none: where a space lens works. Ground stations and surface points never are. */
        public bool VantageInVacuum
        {
            get
            {
                if (!HasVantage || Vantage == null || Vantage.state == Vessel.State.DEAD) return false;
                var body = Vantage.mainBody;
                return body == null || !body.atmosphere || Vantage.altitude >= body.atmosphereDepth;
            }
        }

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
