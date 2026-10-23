using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Debug = UnityEngine.Debug;
using Time = UnityEngine.Time;

namespace Kerbcast
{
    /* The scene's command cameras: the default one from settings, when
       EnableCommandCamera is on, plus any created at runtime through
       KerbcastControl.CreateCommandCamera or a client.

       The default stands beside a launch site (or at the home CommNet station,
       or on a vessel, per CommandCameraAnchor). In flight it watches whichever
       craft is active; at the Space Center it watches the craft
       CommandCameraTargetName picks. A created camera stands at its own
       CommandVantage and watches the same default craft until told otherwise.
       KerbcastControl.SetCommandTarget (or a client) can choose another for
       any of them.

       None is tied to a vessel: each is returned whatever vessel is scanned,
       and KerbcastCore keeps them across vessel switches. Each site is
       resolved once, retried for a short while if stock has not placed it
       yet. Created cameras last until destroyed or the scene ends. */
    internal sealed class CommandCameraProvider : ICameraSourceProvider
    {
        private readonly KerbcastSettings _settings;
        private readonly string _ringDir;
        private readonly int _ringSlots;
        private readonly bool _spaceCenter;
        private readonly CommandAuthority _authority;
        private readonly List<Feed> _feeds = new List<Feed>();
        private int _nextSerial = 1;

        private const float SiteRetryInterval = 1f;
        private const float SiteRetrySeconds = 30f;

        /* The camera copies KSP's own cameras, and at the Space Center the
           scaled one appears a moment after the scene wakes. Built without it,
           the scaled layer falls back to Unity defaults for the whole scene. */
        private static readonly string[] SourceCameras = { "Camera 00", "Camera ScaledSpace", "GalaxyCamera" };
        private const float CameraWaitSeconds = 15f;

        /* A site exists but is not placed yet, or a camera was just created;
           KerbcastCore enumerates again once RetryDue. */
        public bool RetryPending => _feeds.Any(f => f.RetryPending);
        public bool RetryDue => _feeds.Any(f => f.RetryDue);

        public CommandCameraProvider(KerbcastSettings settings, string ringDir, int ringSlots, bool spaceCenter,
            CommandAuthority authority, bool withDefault = true)
        {
            _authority = authority;
            _settings = settings;
            _ringDir = ringDir;
            _ringSlots = ringSlots;
            _spaceCenter = spaceCenter;
            if (withDefault) _feeds.Add(new Feed(this, null, null, null, 0));
        }

        /* Queues a camera at `vantage`, built on the next enumeration once its
           site resolves. Returns the flightId it will have, or 0 with `error`
           set when the vantage is malformed or names an unknown body. `taken`
           is every flightId in use, so the new one collides with none. */
        public uint Create(CommandVantage vantage, string name, uint targetPersistentId, string token,
            IReadOnlyCollection<uint> taken, out string error)
        {
            error = vantage == null ? "no vantage" : vantage.Invalid();
            if (error != null) return 0;
            if (vantage.Kind == CommandVantageKind.Surface && FlightGlobals.GetBodyByName(vantage.Body) == null)
            {
                error = $"no body named '{vantage.Body}'";
                return 0;
            }
            uint flightId;
            do
            {
                flightId = CameraId.CommandCameraWireId(CameraId.CreatedCommandCameraKey(_nextSerial++));
            }
            while (taken.Contains(flightId) || _feeds.Any(f => f.FlightId == flightId));

            var feed = new Feed(this, vantage, name, token, flightId);
            if (targetPersistentId != 0) feed.SetTarget(targetPersistentId);
            _feeds.Add(feed);
            Debug.Log($"[Kerbcast][CMDCAM] command camera cam={flightId} requested at {vantage}"
                + (string.IsNullOrEmpty(name) ? "" : $" name='{name}'")
                + (string.IsNullOrEmpty(token) ? "" : $" token={token}"));
            return flightId;
        }

        /* Forgets a created camera, built or not. False for the default camera
           or an unknown id. The caller disposes the built camera. */
        public bool Destroy(uint flightId)
        {
            int i = _feeds.FindIndex(f => f.Created && f.FlightId == flightId);
            if (i < 0) return false;
            _feeds.RemoveAt(i);
            Debug.Log($"[Kerbcast][CMDCAM] command camera cam={flightId} destroyed");
            return true;
        }

        public bool IsCreated(uint flightId) => _feeds.Any(f => f.Created && f.FlightId == flightId);

        /* A created camera that is not built yet. */
        public bool IsPending(uint flightId) => _feeds.Any(f => f.Created && f.FlightId == flightId && !f.Built);

        /* A token already in use by a created camera this scene. */
        public bool HasToken(string token) =>
            !string.IsNullOrEmpty(token) && _feeds.Any(f => f.Token == token);

        /* Choices made before a created camera is built, applied when it is. */
        public bool SetPendingTarget(uint flightId, uint persistentId)
        {
            var feed = _feeds.FirstOrDefault(f => f.Created && f.FlightId == flightId && !f.Built);
            if (feed == null) return false;
            feed.SetTarget(persistentId);
            return true;
        }

        public bool SetPendingLookAt(uint flightId, CommandAim aim, Orbit orbit, double framingMarginM)
        {
            var feed = _feeds.FirstOrDefault(f => f.Created && f.FlightId == flightId && !f.Built);
            if (feed == null) return false;
            feed.LookAt = aim;
            feed.LookAtOrbit = orbit;
            feed.LookAtMarginM = framingMarginM;
            return true;
        }

        public IEnumerable<ICamera> Enumerate(Vessel vessel, IReadOnlyCollection<uint> existingFlightIds)
        {
            var result = new List<ICamera>();
            foreach (var feed in _feeds.ToList())
            {
                feed.Enumerate(vessel, existingFlightIds, result);
                if (feed.Dropped) _feeds.Remove(feed);
            }
            return result;
        }

        /* One command camera: the default (Vantage null, driven by settings)
           or a created one. */
        private sealed class Feed
        {
            private readonly CommandCameraProvider _p;
            public readonly CommandVantage Vantage;
            public readonly string Name;
            public readonly string Token;
            public bool Created => Vantage != null;
            /* Fixed at creation for a created camera; the default's comes from its site. */
            public uint FlightId { get; private set; }
            public bool Built { get; private set; }
            /* A created camera whose site never resolved; removed after this enumeration. */
            public bool Dropped { get; private set; }

            public CommandAim LookAt;
            public Orbit LookAtOrbit;
            public double LookAtMarginM;
            private uint _pendingTarget;

            private CommandCameraTarget _target;
            private CommandCameraSite _site;
            private bool _resolveFailedLogged;
            private int _siteAttempts;
            private float _siteFirstAttemptAt;
            private float _nextSiteAttemptAt;
            private float _cameraWaitStart = -1f;

            public bool RetryPending { get; private set; }
            public bool RetryDue => RetryPending && Time.realtimeSinceStartup >= _nextSiteAttemptAt;

            private KerbcastSettings S => _p._settings;
            private string Label => Created ? $"command camera cam={FlightId}" : "command camera";

            public Feed(CommandCameraProvider p, CommandVantage vantage, string name, string token, uint flightId)
            {
                _p = p;
                Vantage = vantage;
                Name = name;
                Token = token ?? "";
                FlightId = flightId;
                if (Created)
                {
                    RetryPending = true;
                    _nextSiteAttemptAt = Time.realtimeSinceStartup;
                }
            }

            public void SetTarget(uint persistentId)
            {
                _pendingTarget = persistentId;
                _target?.Choose(persistentId);
            }

            private void Retry(float now)
            {
                RetryPending = true;
                _nextSiteAttemptAt = now + SiteRetryInterval;
            }

            public void Enumerate(Vessel vessel, IReadOnlyCollection<uint> existingFlightIds, List<ICamera> result)
            {
                if (_target == null)
                {
                    /* A vessel vantage in flight can watch a named craft other than the active one. */
                    bool named = _p._spaceCenter
                        || (!Created && S.CommandCameraAnchor == "vessel" && S.CommandCameraTargetName.Length > 0);
                    _target = named
                        ? CommandCameraTarget.Chosen(S.CommandCameraTargetName)
                        : CommandCameraTarget.ActiveVessel();
                    if (_pendingTarget != 0) _target.Choose(_pendingTarget);
                }
                if (_site == null && !(Created ? ResolveCreated(vessel) : ResolveDefault(vessel))) return;

                uint flightId = Created ? FlightId : CameraId.CommandCameraWireId(_site.NodeName);
                FlightId = flightId;
                if (existingFlightIds.Contains(flightId)) return;

                var missing = SourceCameras.Where(n => KerbcastCamera.FindKspCamerasByName(n).Count == 0).ToList();
                if (missing.Count > 0)
                {
                    float now = Time.realtimeSinceStartup;
                    if (_cameraWaitStart < 0f)
                    {
                        _cameraWaitStart = now;
                        Debug.Log($"[Kerbcast][CMDCAM] waiting for KSP camera(s) {string.Join(", ", missing)} before building the {Label}");
                    }
                    if (now - _cameraWaitStart < CameraWaitSeconds)
                    {
                        Retry(now);
                        return;
                    }
                    Debug.LogWarning($"[Kerbcast][CMDCAM] KSP camera(s) {string.Join(", ", missing)} still missing after {CameraWaitSeconds:F0}s; building anyway");
                }
                else if (_cameraWaitStart >= 0f)
                {
                    Debug.Log($"[Kerbcast][CMDCAM] KSP cameras present after {Time.realtimeSinceStartup - _cameraWaitStart:F1}s");
                }
                RetryPending = false;

                string name = Created
                    ? CommandCameraNaming.FeedName(Name, Vantage.Kind, _site.NodeName, _site.DisplayName, _site.Body?.bodyName)
                    : null;
                CommandCameraMount mount = null;
                try
                {
                    mount = new CommandCameraMount(_site, flightId, (float)S.CommandCameraFovMax, name);
                    var cam = new KerbcastCamera(
                        mount,
                        flightId,
                        _p._ringDir,
                        _p._ringSlots,
                        S.Width,
                        S.Height,
                        S.Width,
                        S.Height,
                        CameraLayers.All,
                        enableAtmosphericFx: false,
                        fxLayers: AtmoFxLayers.None,
                        manifestKind: "command",
                        manifestExtraFields: ManifestFields(_site, Created, Token));
                    try
                    {
                        var rig = new CommandCameraRig(cam, mount, _target, _p._authority,
                            S.CommandCameraFrameFraction,
                            _p.LensFovMin(_site),
                            S.CommandCameraFovMax,
                            S.CommandCameraTargetMarker,
                            S.CommandCameraTargetModel,
                            S.CommandCameraDebugUtStripe);
                        cam.Rig = rig;
                        if (LookAt != null) rig.SetLookAt(LookAt, LookAtOrbit, LookAtMarginM);
                    }
                    catch
                    {
                        cam.Dispose();
                        throw;
                    }
                    result.Add(cam);
                    Built = true;
                    Debug.Log(Created
                        ? $"[Kerbcast][CMDCAM] command camera cam={flightId} created at {Vantage} as '{name}' ({_site})"
                        : $"[Kerbcast][CMDCAM] command camera cam={flightId} created for {CameraId.CommandCentreId(_site.NodeName)}");
                }
                catch (Exception ex)
                {
                    mount?.DestroyAnchor();
                    Debug.LogWarning($"[Kerbcast][CMDCAM] command camera cam={flightId} not created: {ex}");
                }
            }

            /* The settings-driven site, falling back to the home station. */
            private bool ResolveDefault(Vessel vessel)
            {
                if (S.CommandCameraAnchor == "vessel")
                {
                    float now = Time.realtimeSinceStartup;
                    if (_siteAttempts == 0) _siteFirstAttemptAt = now;
                    _siteAttempts++;
                    _site = CommandCameraSite.ResolveVessel(S.CommandCameraVantageName);
                    if (_site != null)
                    {
                        RetryPending = false;
                        Debug.Log($"[Kerbcast][CMDCAM] vantage vessel resolved: {_site}");
                        if (_site.Vantage.loaded)
                            Debug.LogWarning("[Kerbcast][CMDCAM] vantage vessel is loaded, so the camera sits inside its parts; keep it unloaded (over 2.5 km from the active vessel)");
                    }
                    else if (now - _siteFirstAttemptAt < SiteRetrySeconds)
                    {
                        if (_siteAttempts == 1)
                            Debug.LogWarning($"[Kerbcast][CMDCAM] vantage vessel '{S.CommandCameraVantageName}' not found; retrying every {SiteRetryInterval:F0}s for up to {SiteRetrySeconds:F0}s");
                        Retry(now);
                        return false;
                    }
                    else
                    {
                        RetryPending = false;
                        Debug.LogWarning($"[Kerbcast][CMDCAM] vantage vessel '{S.CommandCameraVantageName}' still not found; using the home station");
                    }
                }
                if (_site == null)
                {
                    if (S.CommandCameraAnchor == "launchsite")
                    {
                        string launchedFrom = _p._spaceCenter
                            ? _target.LaunchedFrom
                            : (FlightGlobals.ActiveVessel ?? vessel)?.launchedFrom;
                        float now = Time.realtimeSinceStartup;
                        if (_siteAttempts == 0) _siteFirstAttemptAt = now;
                        _siteAttempts++;
                        _site = CommandCameraSite.ResolveLaunchSite(launchedFrom,
                            S.CommandCameraPadDistance, S.CommandCameraPadBearing,
                            S.CommandCameraPadHeight, out string pending);
                        if (_site != null)
                        {
                            RetryPending = false;
                            Debug.Log($"[Kerbcast][CMDCAM] launch site resolved: {_site} distance={S.CommandCameraPadDistance:F0} bearing={S.CommandCameraPadBearing:F0} height={S.CommandCameraPadHeight:F1}"
                                + (_siteAttempts > 1 ? $" after {_siteAttempts} attempts ({now - _siteFirstAttemptAt:F1}s)" : ""));
                        }
                        else if (pending != null && now - _siteFirstAttemptAt < SiteRetrySeconds)
                        {
                            /* Never build on a (0,0) anchor: come back on the retry
                               cadence until stock fills the spawn point in. */
                            if (_siteAttempts == 1)
                                Debug.LogWarning($"[Kerbcast][CMDCAM] launch site unresolved: {pending}; retrying every {SiteRetryInterval:F0}s for up to {SiteRetrySeconds:F0}s");
                            Retry(now);
                            return false;
                        }
                        else
                        {
                            RetryPending = false;
                            Debug.LogWarning(pending != null
                                ? $"[Kerbcast][CMDCAM] launch site still unresolved after {_siteAttempts} attempts ({now - _siteFirstAttemptAt:F1}s): {pending}; using the home station"
                                : "[Kerbcast][CMDCAM] no launch site found; using the home station");
                        }
                    }
                    if (_site == null)
                    {
                        _site = CommandCameraSite.ResolveHome(S.CommandCameraAltitudeOffset);
                        if (_site == null)
                        {
                            if (!_resolveFailedLogged)
                                Debug.LogWarning("[Kerbcast][CMDCAM] no home station or home body found; command camera not created");
                            _resolveFailedLogged = true;
                            return false;
                        }
                        Debug.Log($"[Kerbcast][CMDCAM] home station resolved: {_site}");
                    }
                }
                return true;
            }

            /* A created camera's site, exactly where asked or nowhere: it never
               falls back to another place. Retried while the place may not be
               loaded yet, then dropped. */
            private bool ResolveCreated(Vessel vessel)
            {
                float now = Time.realtimeSinceStartup;
                if (_siteAttempts == 0) _siteFirstAttemptAt = now;
                _siteAttempts++;
                var v = Vantage;
                string pending = null;
                switch (v.Kind)
                {
                    case CommandVantageKind.LaunchSite:
                    {
                        bool explicitSite = v.Name.Length > 0;
                        string from = explicitSite ? v.Name
                            : _p._spaceCenter ? _target.LaunchedFrom
                            : (FlightGlobals.ActiveVessel ?? vessel)?.launchedFrom;
                        _site = CommandCameraSite.ResolveLaunchSite(from, v.Distance, v.Bearing, v.Height,
                            out pending, fallbackToPad: !explicitSite);
                        if (_site == null && pending == null)
                            return Drop(explicitSite ? $"no launch site named '{v.Name}'" : "no launch site found");
                        break;
                    }
                    case CommandVantageKind.Station:
                        _site = CommandCameraSite.ResolveStation(v.Name, v.Height);
                        if (_site == null) pending = $"no ground station '{v.Name}' placed yet";
                        break;
                    case CommandVantageKind.Surface:
                    {
                        var body = FlightGlobals.GetBodyByName(v.Body);
                        if (body == null) return Drop($"no body named '{v.Body}'");
                        _site = CommandCameraSite.ResolveSurface(body, v.Latitude, v.Longitude, v.Height);
                        break;
                    }
                    case CommandVantageKind.Vessel:
                        _site = CommandCameraSite.ResolveVessel(v.PersistentId);
                        if (_site == null) pending = $"no craft with persistentId={v.PersistentId}";
                        break;
                }
                if (_site != null)
                {
                    RetryPending = false;
                    Debug.Log($"[Kerbcast][CMDCAM] {Label} site resolved: {_site}"
                        + (_siteAttempts > 1 ? $" after {_siteAttempts} attempts ({now - _siteFirstAttemptAt:F1}s)" : ""));
                    if (_site.Vantage != null && _site.Vantage.loaded)
                        Debug.LogWarning($"[Kerbcast][CMDCAM] {Label}: vantage vessel is loaded, so the camera sits inside its parts; keep it unloaded (over 2.5 km from the active vessel)");
                    return true;
                }
                if (now - _siteFirstAttemptAt >= SiteRetrySeconds)
                    return Drop($"{pending} after {SiteRetrySeconds:F0}s");
                if (_siteAttempts == 1)
                    Debug.LogWarning($"[Kerbcast][CMDCAM] {Label} at {v}: {pending}; retrying every {SiteRetryInterval:F0}s for up to {SiteRetrySeconds:F0}s");
                Retry(now);
                return false;
            }

            private bool Drop(string why)
            {
                RetryPending = false;
                Dropped = true;
                Debug.LogWarning($"[Kerbcast][CMDCAM] {Label} at {Vantage} not created: {why}");
                return false;
            }
        }

        /* Tightest zoom for the lens at `site`. A launch-site camera is a
           tracking camera with level 1's lens at every level. A station's lens
           follows the Tracking Station level in career with the tracking rules
           on. A vessel vantage or a surface point, sandbox and science get the
           full zoom. */
        private Func<double> LensFovMin(CommandCameraSite site)
        {
            double top = _settings.CommandCameraFovMin;
            double middle = Math.Max(_settings.CommandCameraFovMinLevel2, top);
            double lowest = Math.Max(_settings.CommandCameraFovMinLevel1, middle);
            if (site.Vantage != null || site.IsSurfacePoint) return () => top;
            if (site.IsLaunchSite) return () => lowest;
            return () => CommandCameraTargets.Gated()
                ? CommandCameraMath.LensFovMin(CommandCameraTargets.NormLevel(), lowest, middle, top)
                : top;
        }

        /* Station identity for /cameras. station_id is gonogo's centre id, so a
           feed lines up with its command centre with no mapping table. A
           created camera also carries the token its creator sent. */
        private static string ManifestFields(CommandCameraSite s, bool created, string token)
        {
            var inv = CultureInfo.InvariantCulture;
            return $"  \"station_id\": \"{Esc(CameraId.CommandCentreId(s.NodeName))}\",\n"
                + $"  \"station_node_name\": \"{Esc(s.NodeName)}\",\n"
                + $"  \"station_name\": \"{Esc(string.IsNullOrEmpty(s.DisplayName) ? s.NodeName : s.DisplayName)}\",\n"
                + $"  \"station_body\": \"{Esc(s.Body != null ? s.Body.bodyName : "")}\",\n"
                + $"  \"station_latitude\": {s.Latitude.ToString("R", inv)},\n"
                + $"  \"station_longitude\": {s.Longitude.ToString("R", inv)},\n"
                + $"  \"station_altitude\": {s.Altitude.ToString("R", inv)}"
                + (created ? ",\n  \"command_created\": true" : "")
                + (created && !string.IsNullOrEmpty(token) ? $",\n  \"command_token\": \"{Esc(token)}\"" : "");
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
