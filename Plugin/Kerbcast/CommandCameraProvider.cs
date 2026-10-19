using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Debug = UnityEngine.Debug;
using Time = UnityEngine.Time;

namespace Kerbcast
{
    /* One command camera beside a launch site (or at the home CommNet station,
       per CommandCameraAnchor). In flight it watches whichever craft is active;
       at the Space Center it watches the craft CommandCameraTargetName picks.
       KerbcastControl.SetCommandTarget (or a client) can choose another.
       Not tied to any vessel: it returns the same camera whatever vessel is
       scanned, and KerbcastCore keeps it across vessel switches. Only
       registered when EnableCommandCamera is on. The site is resolved
       once per scene, retried for a short while if stock has not filled the
       launch site's coordinates in yet. */
    internal sealed class CommandCameraProvider : ICameraSourceProvider
    {
        private readonly KerbcastSettings _settings;
        private readonly string _ringDir;
        private readonly int _ringSlots;
        private readonly bool _spaceCenter;
        private readonly CommandAuthority _authority;
        private CommandCameraTarget _target;
        private CommandCameraSite _site;
        private bool _resolveFailedLogged;

        private const float SiteRetryInterval = 1f;
        private const float SiteRetrySeconds = 30f;
        private int _siteAttempts;
        private float _siteFirstAttemptAt;
        private float _nextSiteAttemptAt;

        /* The camera copies KSP's own cameras, and at the Space Center the
           scaled one appears a moment after the scene wakes. Built without it,
           the scaled layer falls back to Unity defaults for the whole scene. */
        private static readonly string[] SourceCameras = { "Camera 00", "Camera ScaledSpace", "GalaxyCamera" };
        private const float CameraWaitSeconds = 15f;
        private float _cameraWaitStart = -1f;

        /* The launch site exists but its coordinates are not filled in yet;
           KerbcastCore enumerates again once RetryDue. */
        public bool RetryPending { get; private set; }
        public bool RetryDue => RetryPending && Time.realtimeSinceStartup >= _nextSiteAttemptAt;

        public CommandCameraProvider(KerbcastSettings settings, string ringDir, int ringSlots, bool spaceCenter,
            CommandAuthority authority)
        {
            _authority = authority;
            _settings = settings;
            _ringDir = ringDir;
            _ringSlots = ringSlots;
            _spaceCenter = spaceCenter;
        }

        public IEnumerable<ICamera> Enumerate(Vessel vessel, IReadOnlyCollection<uint> existingFlightIds)
        {
            var result = new List<ICamera>();
            if (_target == null)
            {
                /* A vessel vantage in flight can watch a named craft other than the active one. */
                bool named = _spaceCenter
                    || (_settings.CommandCameraAnchor == "vessel" && _settings.CommandCameraTargetName.Length > 0);
                _target = named
                    ? CommandCameraTarget.Chosen(_settings.CommandCameraTargetName)
                    : CommandCameraTarget.ActiveVessel();
            }
            if (_site == null && _settings.CommandCameraAnchor == "vessel")
            {
                float now = Time.realtimeSinceStartup;
                if (_siteAttempts == 0) _siteFirstAttemptAt = now;
                _siteAttempts++;
                _site = CommandCameraSite.ResolveVessel(_settings.CommandCameraVantageName);
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
                        Debug.LogWarning($"[Kerbcast][CMDCAM] vantage vessel '{_settings.CommandCameraVantageName}' not found; retrying every {SiteRetryInterval:F0}s for up to {SiteRetrySeconds:F0}s");
                    RetryPending = true;
                    _nextSiteAttemptAt = now + SiteRetryInterval;
                    return result;
                }
                else
                {
                    RetryPending = false;
                    Debug.LogWarning($"[Kerbcast][CMDCAM] vantage vessel '{_settings.CommandCameraVantageName}' still not found; using the home station");
                }
            }
            if (_site == null)
            {
                if (_settings.CommandCameraAnchor == "launchsite")
                {
                    string launchedFrom = _spaceCenter
                        ? _target.LaunchedFrom
                        : (FlightGlobals.ActiveVessel ?? vessel)?.launchedFrom;
                    float now = Time.realtimeSinceStartup;
                    if (_siteAttempts == 0) _siteFirstAttemptAt = now;
                    _siteAttempts++;
                    _site = CommandCameraSite.ResolveLaunchSite(launchedFrom,
                        _settings.CommandCameraPadDistance, _settings.CommandCameraPadBearing,
                        _settings.CommandCameraPadHeight, out string pending);
                    if (_site != null)
                    {
                        RetryPending = false;
                        Debug.Log($"[Kerbcast][CMDCAM] launch site resolved: {_site} distance={_settings.CommandCameraPadDistance:F0} bearing={_settings.CommandCameraPadBearing:F0} height={_settings.CommandCameraPadHeight:F1}"
                            + (_siteAttempts > 1 ? $" after {_siteAttempts} attempts ({now - _siteFirstAttemptAt:F1}s)" : ""));
                    }
                    else if (pending != null && now - _siteFirstAttemptAt < SiteRetrySeconds)
                    {
                        /* Never build on a (0,0) anchor: come back on the retry
                           cadence until stock fills the spawn point in. */
                        if (_siteAttempts == 1)
                            Debug.LogWarning($"[Kerbcast][CMDCAM] launch site unresolved: {pending}; retrying every {SiteRetryInterval:F0}s for up to {SiteRetrySeconds:F0}s");
                        RetryPending = true;
                        _nextSiteAttemptAt = now + SiteRetryInterval;
                        return result;
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
                    _site = CommandCameraSite.ResolveHome(_settings.CommandCameraAltitudeOffset);
                    if (_site == null)
                    {
                        if (!_resolveFailedLogged)
                            Debug.LogWarning("[Kerbcast][CMDCAM] no home station or home body found; command camera not created");
                        _resolveFailedLogged = true;
                        return result;
                    }
                    Debug.Log($"[Kerbcast][CMDCAM] home station resolved: {_site}");
                }
            }

            uint flightId = CameraId.CommandCameraWireId(_site.NodeName);
            if (existingFlightIds.Contains(flightId)) return result;

            var missing = SourceCameras.Where(n => KerbcastCamera.FindKspCamerasByName(n).Count == 0).ToList();
            if (missing.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                if (_cameraWaitStart < 0f)
                {
                    _cameraWaitStart = now;
                    Debug.Log($"[Kerbcast][CMDCAM] waiting for KSP camera(s) {string.Join(", ", missing)} before building the command camera");
                }
                if (now - _cameraWaitStart < CameraWaitSeconds)
                {
                    RetryPending = true;
                    _nextSiteAttemptAt = now + SiteRetryInterval;
                    return result;
                }
                Debug.LogWarning($"[Kerbcast][CMDCAM] KSP camera(s) {string.Join(", ", missing)} still missing after {CameraWaitSeconds:F0}s; building anyway");
            }
            else if (_cameraWaitStart >= 0f)
            {
                Debug.Log($"[Kerbcast][CMDCAM] KSP cameras present after {Time.realtimeSinceStartup - _cameraWaitStart:F1}s");
            }
            RetryPending = false;

            CommandCameraMount mount = null;
            try
            {
                mount = new CommandCameraMount(_site, flightId, (float)_settings.CommandCameraFovMax);
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
                    manifestKind: "command",
                    manifestExtraFields: ManifestFields(_site));
                try
                {
                    cam.Rig = new CommandCameraRig(cam, mount, _target, _authority,
                        _settings.CommandCameraFrameFraction,
                        LensFovMin(_site),
                        _settings.CommandCameraFovMax,
                        _settings.CommandCameraTargetMarker,
                        _settings.CommandCameraTargetModel,
                        _settings.CommandCameraDebugUtStripe);
                }
                catch
                {
                    cam.Dispose();
                    throw;
                }
                result.Add(cam);
                Debug.Log($"[Kerbcast][CMDCAM] command camera cam={flightId} created for {CameraId.CommandCentreId(_site.NodeName)}");
            }
            catch (Exception ex)
            {
                mount?.DestroyAnchor();
                Debug.LogWarning($"[Kerbcast][CMDCAM] command camera cam={flightId} not created: {ex}");
            }
            return result;
        }

        /* Tightest zoom for the lens at `site`. A launch-site camera is a
           tracking camera with level 1's lens at every level. The home
           station's lens follows the Tracking Station level in career with
           the tracking rules on. A vessel vantage, sandbox and science get
           the full zoom. */
        private Func<double> LensFovMin(CommandCameraSite site)
        {
            double top = _settings.CommandCameraFovMin;
            double middle = Math.Max(_settings.CommandCameraFovMinLevel2, top);
            double lowest = Math.Max(_settings.CommandCameraFovMinLevel1, middle);
            if (site.Vantage != null) return () => top;
            if (site.IsLaunchSite) return () => lowest;
            return () => CommandCameraTargets.Gated()
                ? CommandCameraMath.LensFovMin(CommandCameraTargets.NormLevel(), lowest, middle, top)
                : top;
        }

        /* Station identity for /cameras. station_id is gonogo's centre id, so a
           feed lines up with its command centre with no mapping table. */
        private static string ManifestFields(CommandCameraSite s)
        {
            var inv = CultureInfo.InvariantCulture;
            return $"  \"station_id\": \"{Esc(CameraId.CommandCentreId(s.NodeName))}\",\n"
                + $"  \"station_node_name\": \"{Esc(s.NodeName)}\",\n"
                + $"  \"station_name\": \"{Esc(string.IsNullOrEmpty(s.DisplayName) ? s.NodeName : s.DisplayName)}\",\n"
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
