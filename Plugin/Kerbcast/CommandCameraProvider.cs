using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Debug = UnityEngine.Debug;
using Time = UnityEngine.Time;

namespace Kerbcast
{
    /* A camera's aim and held zoom (NaN for none), carried across a rebuild made under a held command lock. */
    internal sealed class LiveAim
    {
        public CommandAim Aim;
        public Orbit Orbit;
        public double Fov = double.NaN;
    }

    /* The scene's command cameras: the default one from settings, when
       EnableCommandCamera is on, plus any created at runtime through
       KerbcastControl.CreateCommandCamera or a client.

       The default stands beside a launch site (or at the home CommNet station,
       or on a vessel, per CommandCameraAnchor). In flight it watches whichever
       craft is active; at the Space Center it watches the craft
       CommandCameraTargetName picks. A created camera stands at its own
       CommandVantage and watches the same default craft until told otherwise.
       KerbcastControl.SetCommandAim (or a client) points any of them
       elsewhere.

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
           set when the vantage is malformed or names an unknown body or a
           craft that no longer exists. `taken` is every flightId in use, so
           the new one collides with none. */
        public uint Create(CommandVantage vantage, string name, CommandAim aim, Orbit aimOrbit, string token,
            IReadOnlyCollection<uint> taken, out string error)
        {
            error = vantage == null ? "no vantage" : vantage.Invalid();
            if (error != null) return 0;
            if (vantage.Kind == CommandVantageKind.Surface && FlightGlobals.GetBodyByName(vantage.Body) == null)
            {
                error = $"no body named '{vantage.Body}'";
                return 0;
            }
            if (vantage.Kind == CommandVantageKind.Vessel && !CommandCameraSite.VantageExists(vantage.PersistentId))
            {
                error = $"no craft with persistentId={vantage.PersistentId}";
                return 0;
            }
            uint flightId = NextFlightId(taken);
            var feed = new Feed(this, vantage, name, token, flightId);
            if (aim != null) feed.SetAim(aim, aimOrbit);
            _feeds.Add(feed);
            Debug.Log($"[Kerbcast][CMDCAM] command camera cam={flightId} requested at {vantage}"
                + (string.IsNullOrEmpty(name) ? "" : $" name='{name}'")
                + (string.IsNullOrEmpty(token) ? "" : $" token={token}"));
            return flightId;
        }

        /* Queues a tracking network camera over `vantages` (see CommandCameraNetwork), built once one of them resolves. Returns its flightId, or 0 with `error` set. */
        public uint CreateNetwork(IList<NetworkVantageSpec> vantages, string name, NetworkTuning tuning, CommandAim aim, Orbit aimOrbit,
            IReadOnlyCollection<uint> taken, out string error, string owner = null)
        {
            error = NetworkInvalid(vantages, tuning);
            if (error != null) return 0;
            uint flightId = NextFlightId(taken);
            var feed = new Feed(this, null, name, "", flightId, new List<NetworkVantageSpec>(vantages), tuning) { Owner = owner ?? "" };
            if (aim != null) feed.SetAim(aim, aimOrbit);
            _feeds.Add(feed);
            Debug.Log($"[Kerbcast][CMDCAM] network camera cam={flightId} requested over {vantages.Count} vantage(s)"
                + (string.IsNullOrEmpty(name) ? "" : $" name='{name}'"));
            return flightId;
        }

        /* A saved network's camera, under the flightId its slot fixes (CameraId.NetworkWireId). 0 with `error` set when that id is in use or the list is refused. */
        public uint CreateSavedNetwork(uint flightId, IList<NetworkVantageSpec> vantages, string name, NetworkTuning tuning, CommandAim aim,
            IReadOnlyCollection<uint> taken, out string error, Orbit aimOrbit = null, double heldFov = double.NaN)
        {
            error = NetworkInvalid(vantages, tuning);
            if (error == null && (taken.Contains(flightId) || _feeds.Any(f => f.FlightId == flightId))) error = $"cam={flightId} is already in use";
            if (error != null) return 0;
            var feed = new Feed(this, null, name, "", flightId, new List<NetworkVantageSpec>(vantages), tuning);
            if (aim != null) feed.SetAim(aim, aimOrbit);
            feed.PendingFov = heldFov;
            _feeds.Add(feed);
            Debug.Log($"[Kerbcast][CMDCAM] saved network camera cam={flightId} requested over {vantages.Count} vantage(s) name='{name}'");
            return flightId;
        }

        /* A network a mod made through the API, as the networks file lists it. */
        internal struct HostNetwork
        {
            public uint FlightId;
            public string Name;
            public string Owner;
            public List<NetworkVantageSpec> Vantages;
            public NetworkTuning Tuning;
        }

        /* Every network camera a mod made, built or not; saved networks are listed by their store. */
        public void HostNetworks(List<HostNetwork> into)
        {
            into.Clear();
            foreach (var f in _feeds)
                if (f.IsNetwork && f.Owner != null)
                    into.Add(new HostNetwork { FlightId = f.FlightId, Name = f.Name, Owner = f.Owner, Vantages = f.NetworkSpecs, Tuning = f.Tuning });
        }

        /* Replaces a network camera's list, built or not. False when `flightId` is no network camera; `error` says why the list is refused. */
        public bool SetNetworkVantages(uint flightId, IList<NetworkVantageSpec> vantages, NetworkTuning? tuning, out string error)
        {
            var feed = _feeds.FirstOrDefault(f => f.IsNetwork && f.FlightId == flightId);
            error = feed == null ? "not a network camera" : NetworkInvalid(vantages, tuning ?? feed.Tuning);
            if (error != null) return false;
            feed.SetVantages(new List<NetworkVantageSpec>(vantages), tuning ?? feed.Tuning);
            return true;
        }

        public bool IsNetwork(uint flightId) => _feeds.Any(f => f.IsNetwork && f.FlightId == flightId);

        /* Every site key a network camera's vantages have or will have, null when `flightId` is no network camera. */
        public List<string> NetworkSiteKeys(uint flightId) => _feeds.FirstOrDefault(f => f.IsNetwork && f.FlightId == flightId)?.NetworkSiteKeys();

        /* Null when the list is usable, else why not: a network needs at least one vantage, at most NetworkMath.MaxVantages, each well formed. */
        internal static string NetworkInvalid(IList<NetworkVantageSpec> vantages, NetworkTuning tuning)
        {
            if (vantages == null || vantages.Count == 0) return "a network needs at least one vantage";
            if (vantages.Count > NetworkMath.MaxVantages) return $"a network takes at most {NetworkMath.MaxVantages} vantages";
            if (!tuning.Valid) return "hold times and margin must be finite, 0 or more";
            for (int i = 0; i < vantages.Count; i++)
            {
                var v = vantages[i]?.Vantage;
                string why = v == null ? "no vantage" : v.Invalid();
                if (why != null) return $"vantage {i}: {why}";
                if (v.Kind == CommandVantageKind.Surface && FlightGlobals.GetBodyByName(v.Body) == null) return $"vantage {i}: no body named '{v.Body}'";
            }
            return null;
        }

        private uint NextFlightId(IReadOnlyCollection<uint> taken)
        {
            uint flightId;
            do
            {
                flightId = CameraId.CommandCameraWireId(CameraId.CreatedCommandCameraKey(_nextSerial++));
            }
            while (taken.Contains(flightId) || _feeds.Any(f => f.FlightId == flightId));
            return flightId;
        }

        /* Forgets a created camera, built or not. False for the default camera
           or an unknown id. The caller disposes the built camera. */
        public bool Destroy(uint flightId)
        {
            int i = _feeds.FindIndex(f => f.Created && f.FlightId == flightId);
            if (i < 0) return false;
            _feeds[i].Forget();
            _feeds.RemoveAt(i);
            Debug.Log($"[Kerbcast][CMDCAM] command camera cam={flightId} destroyed");
            return true;
        }

        public bool IsCreated(uint flightId) => _feeds.Any(f => f.Created && f.FlightId == flightId);

        /* A network a mod made through the API, which a browser may never remove. */
        public bool IsHostNetwork(uint flightId) => _feeds.Any(f => f.IsNetwork && f.Owner != null && f.FlightId == flightId);

        /* A mod's network that `caller` is not the owner of (see NetworkMath.MayChangeHostNetwork). */
        public bool OwnedByOther(uint flightId, string caller) =>
            _feeds.Any(f => f.IsNetwork && f.Owner != null && f.FlightId == flightId && !NetworkMath.MayChangeHostNetwork(f.Owner, caller));

        /* The default camera lost the craft it rode: its site is resolved
           afresh from settings on the next enumeration, so it lands on the
           home station unless a craft with the configured name still exists,
           and `aim` carries over. The caller disposes the built camera. */
        public void ReresolveDefault(CommandAim aim, Orbit aimOrbit)
        {
            var feed = _feeds.FirstOrDefault(f => !f.Created);
            feed?.Reresolve(aim, aimOrbit);
        }

        /* A created camera that is not built yet. */
        public bool IsPending(uint flightId) => _feeds.Any(f => f.Created && f.FlightId == flightId && !f.Built);

        /* The site key a created camera that is not built yet will have: its
           resolved site's, or what its vantage names; null when neither is known. */
        public string PendingSiteKey(uint flightId)
        {
            var feed = _feeds.FirstOrDefault(f => f.Created && f.FlightId == flightId && !f.Built);
            return feed == null ? null : feed.SiteKey;
        }

        /* A token already in use by a created camera this scene. */
        public bool HasToken(string token) =>
            !string.IsNullOrEmpty(token) && _feeds.Any(f => f.Token == token);

        /* What every feed's request may resolve to changed (the lock was taken or released). */
        public void RefreshSubjects()
        {
            foreach (var f in _feeds) f.RefreshSubject();
        }

        /* Drops every aim waiting for a created camera to be built. */
        public void ClearPendingAims()
        {
            foreach (var f in _feeds)
                if (!f.Built) f.SetAim(null, null);
        }

        /* The aim waiting for a created camera that is not built yet; null when it is built or unknown. */
        public LiveAim PendingLiveAim(uint flightId)
        {
            var feed = _feeds.FirstOrDefault(f => f.Created && f.FlightId == flightId && !f.Built);
            return feed == null ? null : new LiveAim { Aim = feed.PendingAim, Orbit = feed.PendingOrbit, Fov = feed.PendingFov };
        }

        /* An aim set before a created camera is built, applied when it is. */
        public bool SetPendingAim(uint flightId, CommandAim aim, Orbit orbit)
        {
            var feed = _feeds.FirstOrDefault(f => f.Created && f.FlightId == flightId && !f.Built);
            if (feed == null) return false;
            feed.SetAim(aim, orbit);
            return true;
        }

        public IEnumerable<ICamera> Enumerate(Vessel vessel, IReadOnlyCollection<uint> existingFlightIds)
        {
            var result = new List<ICamera>();
            foreach (var feed in _feeds.ToList())
            {
                feed.Enumerate(vessel, existingFlightIds, result);
                if (feed.Dropped)
                {
                    feed.Forget();
                    _feeds.Remove(feed);
                }
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
            public bool Created => Vantage != null || IsNetwork;

            /* A tracking network's list as the host last gave it, and the network once the feed is first enumerated. */
            private List<NetworkVantageSpec> _networkSpecs;
            private CommandCameraNetwork _network;
            public bool IsNetwork { get; }
            public NetworkTuning Tuning { get; private set; }
            /* The mod that made this network through the API ("" when it named none); null for a saved network or any other camera. */
            public string Owner { get; set; }
            public List<NetworkVantageSpec> NetworkSpecs => _networkSpecs;

            public string SiteKey => _site != null ? _site.NodeName
                : IsNetwork ? (_networkSpecs != null && _networkSpecs.Count > 0 ? _networkSpecs[0].Key : null)
                : CommandCameraSite.KeyFor(Vantage);
            /* Fixed at creation for a created camera; the default's comes from its site. */
            public uint FlightId { get; private set; }
            public bool Built { get; private set; }
            /* A created camera whose site never resolved; removed after this enumeration. */
            public bool Dropped { get; private set; }

            private CommandAim _pendingAim;
            private Orbit _pendingOrbit;
            public CommandAim PendingAim => _pendingAim;
            public Orbit PendingOrbit => _pendingOrbit;
            /* A held zoom carried across a rebuild, applied after the aim; any new aim replaces it, as it does on a built rig. */
            public double PendingFov { get; set; } = double.NaN;

            /* Which craft the feed watches, resolved by _known and drawn by
               _drawn. Under truth both read KSP's handles for it in _track. */
            private SubjectSelection _selection;
            private SubjectTrack _track;
            private TruthFixSource _known;
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

            public Feed(CommandCameraProvider p, CommandVantage vantage, string name, string token, uint flightId,
                List<NetworkVantageSpec> network = null, NetworkTuning tuning = default)
            {
                _p = p;
                Vantage = vantage;
                Name = name;
                Token = token ?? "";
                FlightId = flightId;
                IsNetwork = network != null;
                _networkSpecs = network;
                Tuning = tuning;
                if (Created)
                {
                    RetryPending = true;
                    _nextSiteAttemptAt = Time.realtimeSinceStartup;
                }
            }

            /* The subject is chosen at once, so a launch site vantage resolves
               against the craft the camera will watch. */
            public void SetAim(CommandAim aim, Orbit orbit)
            {
                _pendingAim = aim;
                _pendingOrbit = orbit;
                PendingFov = double.NaN;
                uint subject = aim?.SubjectPersistentId ?? 0;
                if (_selection != null && subject != _selection.ChosenPersistentId) _selection.Choose(subject);
            }

            /* Under a world model, an aim set before the site was known was
               checked against the key the vantage named; now the site is
               known, its craft is checked again against what this site knows,
               and dropped if it fails. Truth keeps 1.11's behaviour. */
            private bool PendingAimStands(string siteKey)
            {
                uint subject = _pendingAim.SubjectPersistentId;
                if (subject == 0) return true;
                var result = CommandCameraTargets.AimSubject(siteKey, _site, subject, _pendingAim.Kind == CommandAimKind.Vessel,
                    _pendingAim.AllowUncertain, _pendingAim.FramingMarginM, S.CommandCameraFovMax);
                if (result == Api.AimResult.Ok) return true;
                Debug.LogWarning($"[Kerbcast][CMDCAM] {Label}: aim {_pendingAim} dropped once its site resolved ({result}); the camera starts at its default");
                return false;
            }

            public List<string> NetworkSiteKeys()
            {
                var keys = new List<string>();
                if (_network != null) _network.SiteKeys(keys);
                else if (_networkSpecs != null) foreach (var s in _networkSpecs) keys.Add(s.Key);
                return keys;
            }

            public void SetVantages(List<NetworkVantageSpec> specs, NetworkTuning tuning)
            {
                Tuning = tuning;
                _networkSpecs = specs;
                _network?.SetVantages(specs, tuning);
            }

            /* The feed is gone; a network not handed to a rig lets go of its game events here. */
            public void Forget()
            {
                if (!Built) _network?.Dispose();
            }

            /* The lock changed: the request may now resolve differently, and under the lock a default names no craft. */
            public void RefreshSubject()
            {
                if (_selection == null) return;
                _selection.Locked = _p._authority.Registered;
                _known.LockChanged();
                _selection.Refresh();
            }

            public void Reresolve(CommandAim aim, Orbit orbit)
            {
                _site = null;
                Built = false;
                _siteAttempts = 0;
                _resolveFailedLogged = false;
                _cameraWaitStart = -1f;
                _vantageLost = true;
                SetAim(aim, orbit);
                RetryPending = true;
                _nextSiteAttemptAt = Time.realtimeSinceStartup;
            }

            /* Mid-scene, a missing vantage craft will not turn up later the
               way one still loading at scene start does, so there is no wait. */
            private bool _vantageLost;

            private void Retry(float now)
            {
                RetryPending = true;
                _nextSiteAttemptAt = now + SiteRetryInterval;
            }

            public void Enumerate(Vessel vessel, IReadOnlyCollection<uint> existingFlightIds, List<ICamera> result)
            {
                if (_selection == null)
                {
                    /* A vessel vantage in flight can watch a named craft other than the active one. */
                    bool named = _p._spaceCenter
                        || (!Created && S.CommandCameraAnchor == "vessel" && S.CommandCameraTargetName.Length > 0);
                    _selection = named
                        ? SubjectSelection.Chosen(S.CommandCameraTargetName, Debug.Log)
                        : SubjectSelection.ActiveVessel(Debug.Log);
                    _track = new SubjectTrack();
                    var store = _p._authority.WorldModel;
                    _selection.Locked = _p._authority.Registered;
                    _known = new TruthFixSource(_selection, _track, () => !store.Governed);
                    uint subject = _pendingAim?.SubjectPersistentId ?? 0;
                    if (subject != 0) _selection.Choose(subject);
                }
                if (IsNetwork && _network == null)
                {
                    _network = _p.NewNetwork(FlightId, Name, _networkSpecs, Tuning, _selection, _known, NetworkLaunchedFrom);
                }
                if (_site == null && !(IsNetwork ? ResolveNetwork() : Created ? ResolveCreated(vessel) : ResolveDefault(vessel))) return;

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

                string name = IsNetwork ? _network.Name
                    : Created ? CommandCameraNaming.FeedName(Name, Vantage.Kind, _site.NodeName, _site.DisplayName, _site.Body?.bodyName)
                    : null;
                CommandCameraMount mount = null;
                try
                {
                    mount = new CommandCameraMount(_site, flightId, (float)S.CommandCameraFovMax, _p.LensFovMin(_site), name,
                        IsNetwork ? _network.Name : null);
                    var cam = new KerbcastCamera(
                        mount,
                        flightId,
                        _p._ringDir,
                        _p._ringSlots,
                        S.Width,
                        _p.FrameRows,
                        S.Width,
                        _p.FrameRows,
                        CameraLayers.All,
                        enableAtmosphericFx: false,
                        fxLayers: AtmoFxLayers.None,
                        manifestKind: "command",
                        manifestExtraFields: ManifestFields(_site, Created, Token));
                    try
                    {
                        /* Each rig measures its craft afresh, as it frames it. */
                        var drawn = new DrawnSubject(_selection, _track, cam.Picture);
                        _known.MeasureWith(drawn);
                        var store = _p._authority.WorldModel;
                        var known = new FeedFixSource(_known,
                            new ProviderFixSource(store, _p._authority.Sites, () => mount.Site, _selection, flightId), store);
                        var rig = new CommandCameraRig(cam, mount, _selection, known, drawn, _p._authority,
                            S.CommandCameraFrameFraction,
                            _p.LensFovMin(_site),
                            S.CommandCameraFovMax,
                            S.CommandCameraTargetMarker,
                            S.CommandCameraTargetModel,
                            S.CommandCameraDebugUtStripe,
                            _p.LensOptics(_site));
                        cam.Rig = rig;
                        if (IsNetwork) rig.AttachNetwork(_network);
                        /* A network's aim was checked against every vantage when it was set; one site's verdict never drops it. */
                        if (_pendingAim != null && store.Governed && !IsNetwork && !PendingAimStands(_site.NodeName)) SetAim(null, null);
                        if (_pendingAim != null) rig.SetAim(_pendingAim, _pendingOrbit);
                        if (!double.IsNaN(PendingFov)) rig.SetFov(PendingFov);
                        /* Applied once: a rebuilt camera must not bring back an aim from before a lock. */
                        _pendingAim = null;
                        _pendingOrbit = null;
                        PendingFov = double.NaN;
                    }
                    catch
                    {
                        cam.Dispose();
                        throw;
                    }
                    result.Add(cam);
                    Built = true;
                    Debug.Log(IsNetwork
                        ? $"[Kerbcast][CMDCAM] network camera cam={flightId} created as '{name}' over {_network.Count} vantage(s), first at {_site}"
                        : Created
                        ? $"[Kerbcast][CMDCAM] command camera cam={flightId} created at {Vantage} as '{name}' ({_site})"
                        : $"[Kerbcast][CMDCAM] command camera cam={flightId} created for site {_site.NodeName}");
                }
                catch (Exception ex)
                {
                    mount?.DestroyAnchor();
                    Debug.LogWarning($"[Kerbcast][CMDCAM] command camera cam={flightId} not created: {ex}");
                }
            }

            /* Stock fills a spawn point in only once it builds the site's surroundings, which it never
               does while the scene is far from them. */
            private static bool HomeSurroundingsUnbuilt()
            {
                var home = FlightGlobals.GetHomeBody();
                return home != null && home.pqsController != null && !home.pqsController.isActive;
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
                    }
                    else if (!_vantageLost && now - _siteFirstAttemptAt < SiteRetrySeconds)
                    {
                        if (_siteAttempts == 1)
                            Debug.LogWarning($"[Kerbcast][CMDCAM] vantage vessel '{S.CommandCameraVantageName}' not found; retrying every {SiteRetryInterval:F0}s for up to {SiteRetrySeconds:F0}s");
                        Retry(now);
                        return false;
                    }
                    else
                    {
                        RetryPending = false;
                        Debug.LogWarning($"[Kerbcast][CMDCAM] vantage vessel '{S.CommandCameraVantageName}' "
                            + (_vantageLost ? "gone" : "still not found") + "; using the home station");
                    }
                }
                if (_site == null)
                {
                    if (S.CommandCameraAnchor == "launchsite")
                    {
                        string launchedFrom = _p._spaceCenter
                            ? _track.LaunchedFrom
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
                            {
                                string retryText = $"[Kerbcast][CMDCAM] launch site unresolved: {pending}; retrying every {SiteRetryInterval:F0}s for up to {SiteRetrySeconds:F0}s";
                                if (HomeSurroundingsUnbuilt()) Debug.Log(retryText);
                                else Debug.LogWarning(retryText);
                            }
                            Retry(now);
                            return false;
                        }
                        else
                        {
                            RetryPending = false;
                            /* Expected when the scene is far from the site, not a fault. */
                            bool unbuilt = pending != null && HomeSurroundingsUnbuilt();
                            string text = pending != null
                                ? $"[Kerbcast][CMDCAM] launch site still unresolved after {_siteAttempts} attempts ({now - _siteFirstAttemptAt:F1}s): {pending}; using the home station"
                                : "[Kerbcast][CMDCAM] no launch site found; using the home station";
                            if (unbuilt) Debug.Log(text + " (the scene is far from the launch site, so stock has not placed it)");
                            else Debug.LogWarning(text);
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
                string from = v.Kind != CommandVantageKind.LaunchSite || v.Name.Length > 0 ? v.Name
                    : _p._spaceCenter ? _track.LaunchedFrom
                    : (FlightGlobals.ActiveVessel ?? vessel)?.launchedFrom;
                _site = ResolveVantage(v, from, out string pending, out string fatal);
                if (fatal != null) return Drop(fatal);
                if (_site != null)
                {
                    RetryPending = false;
                    Debug.Log($"[Kerbcast][CMDCAM] {Label} site resolved: {_site}"
                        + (_siteAttempts > 1 ? $" after {_siteAttempts} attempts ({now - _siteFirstAttemptAt:F1}s)" : ""));
                    return true;
                }
                if (now - _siteFirstAttemptAt >= SiteRetrySeconds)
                    return Drop($"{pending} after {SiteRetrySeconds:F0}s");
                if (_siteAttempts == 1)
                    Debug.LogWarning($"[Kerbcast][CMDCAM] {Label} at {v}: {pending}; retrying every {SiteRetryInterval:F0}s for up to {SiteRetrySeconds:F0}s");
                Retry(now);
                return false;
            }

            /* The launch site a network's LaunchSite vantage stands beside: the one it names, else the one a created lens would take. */
            private string NetworkLaunchedFrom(CommandVantage v)
            {
                if (v.Name.Length > 0) return v.Name;
                return _p._spaceCenter ? _track.LaunchedFrom : FlightGlobals.ActiveVessel?.launchedFrom;
            }

            /* A network camera stands at the first of its vantages that resolves; it is dropped only when none does. */
            private bool ResolveNetwork()
            {
                float now = Time.realtimeSinceStartup;
                _site = _network.FirstSite(now);
                if (_site != null)
                {
                    RetryPending = false;
                    return true;
                }
                if (_network.AllGaveUp) return Drop("no vantage resolved");
                Retry(now);
                return false;
            }

            private bool Drop(string why)
            {
                RetryPending = false;
                Dropped = true;
                Debug.LogWarning($"[Kerbcast][CMDCAM] {Label} at {(IsNetwork ? (object)"its vantages" : Vantage)} not created: {why}");
                return false;
            }
        }

        /*
         * The site `v` names, exactly where asked or nowhere. `launchedFrom`
         * names the launch site for a LaunchSite vantage. Null with `pending`
         * set while the place may not be loaded yet, or with `fatal` set when
         * it never will be.
         */
        internal static CommandCameraSite ResolveVantage(CommandVantage v, string launchedFrom, out string pending, out string fatal)
        {
            pending = null;
            fatal = null;
            CommandCameraSite site = null;
            switch (v.Kind)
            {
                case CommandVantageKind.LaunchSite:
                {
                    bool explicitSite = v.Name.Length > 0;
                    site = CommandCameraSite.ResolveLaunchSite(launchedFrom, v.Distance, v.Bearing, v.Height,
                        out pending, fallbackToPad: !explicitSite);
                    if (site == null && pending == null) fatal = explicitSite ? $"no launch site named '{v.Name}'" : "no launch site found";
                    break;
                }
                case CommandVantageKind.Station:
                    site = CommandCameraSite.ResolveStation(v.Name, v.Height);
                    if (site == null) pending = $"no ground station '{v.Name}' placed yet";
                    break;
                case CommandVantageKind.Surface:
                {
                    var body = FlightGlobals.GetBodyByName(v.Body);
                    if (body == null) fatal = $"no body named '{v.Body}'";
                    else site = CommandCameraSite.ResolveSurface(body, v.Latitude, v.Longitude, v.Height);
                    break;
                }
                case CommandVantageKind.Vessel:
                    site = CommandCameraSite.ResolveVessel(v.PersistentId);
                    if (site == null) pending = $"no craft with persistentId={v.PersistentId}";
                    break;
            }
            return site;
        }

        private CommandCameraNetwork NewNetwork(uint flightId, string name, List<NetworkVantageSpec> specs, NetworkTuning tuning,
            SubjectSelection selection, IFixSource truth, Func<CommandVantage, string> launchedFrom)
        {
            var wiring = new NetworkWiring
            {
                FlightId = flightId,
                Selection = selection,
                Truth = truth,
                Authority = _authority,
                LaunchedFrom = launchedFrom,
                FovMin = LensFovMin,
                Lens = LensOptics,
                ScoreKnobs = LensKnobs,
                DaylightStops = () => _settings.NetworkDaylightStops,
                TerrainSamples = CommandCameraRig.TerrainSampleCount,
            };
            return new CommandCameraNetwork(wiring, name, specs, tuning);
        }

        /* Tightest zoom for the lens at `site`. A launch-site camera is a
           tracking camera with level 1's lens at every level. A station's lens
           follows the Tracking Station level in career with the tracking rules
           on. A vessel vantage or a surface point, sandbox and science get the
           full zoom. */
        private Func<double> LensFovMin(CommandCameraSite site) => () => FovMinAt(site);

        /*
         * The frame height every command camera renders at full quality. The
         * rig's optics floor reads it back as the camera's MaxHeight, so the
         * lens limit and that floor count the same pixels.
         */
        internal int FrameRows => _settings.Height;

        /* `inVacuum` overrides whether a craft vantage is outside the air, for a question asked at another time than now. */
        private double FovMinAt(CommandCameraSite site, bool? inVacuum = null)
        {
            double top = _settings.CommandCameraFovMin;
            /* Under deep-space optics the blur, not the zoom, holds a low level back; without the shaders for it the level's zoom stays. */
            if (_settings.DeepSpaceOptics && CommandCameraOptics.Available())
            {
                /* A space lens finer than the top level's zooms past CommandCameraFovMin, to where its own blur spans two pixels. */
                var lens = LensAt(site, false, inVacuum);
                return lens.Kind == LensKind.SpaceLens ? SpaceLens.FovMin(top, lens.Arcsec, FrameRows) : top;
            }
            double middle = Math.Max(_settings.CommandCameraFovMinLevel2, top);
            double lowest = Math.Max(_settings.CommandCameraFovMinLevel1, middle);
            if (site.HasVantage || site.IsSurfacePoint) return top;
            if (site.IsLaunchSite) return lowest;
            return CommandCameraTargets.Gated()
                ? CommandCameraMath.LensFovMin(CommandCameraTargets.NormLevel(), lowest, middle, top)
                : top;
        }

        /*
         * The lens at `site` and where it comes from. The camera beside the
         * launch site has level 1's lens, as it has level 1's zoom without
         * optics. Every other site follows the Tracking Station in career with
         * the tracking rules on; sandbox, science and career without the rules
         * get the top lens. A craft outside any atmosphere takes its space lens
         * instead when a researched step makes that finer. `named` fills Name,
         * which looks up a tech title, so scoring skips it. `inVacuum`
         * overrides whether the craft is outside the air, for another time.
         */
        internal LensChoice LensAt(CommandCameraSite site, bool named, bool? inVacuum = null)
        {
            var s = _settings;
            double l1 = s.OpticsResolutionLevel1, l2 = s.OpticsResolutionLevel2, l3 = s.OpticsResolutionLevel3;
            if (site.IsLaunchSite)
                return new LensChoice(OpticsModel.ResolutionForLevel(0, l1, l2, l3), LensKind.LaunchSite, 1,
                    named ? "Tracking Station level 1" : null, "launch site, level 1 lens");
            bool gated = CommandCameraTargets.Gated();
            int level = gated ? CommandCameraTargets.TrackingLevel() : CommandCameraTargets.TopTrackingLevel();
            var ground = new LensChoice(OpticsModel.ResolutionForLevel(gated ? CommandCameraTargets.NormLevel() : 1, l1, l2, l3),
                LensKind.TrackingStation, level, named ? $"Tracking Station level {level}" : null,
                gated ? $"Tracking Station level {level}" : $"top lens ({TopLensWhy()})");
            if (!site.HasVantage || !(inVacuum ?? site.VantageInVacuum)) return ground;
            int step = SpaceLens.BestStep(s.SpaceLensSteps, SpaceLensTech.Researched);
            if (step < 0 || !SpaceLens.Beats(s.SpaceLensSteps[step].Arcsec, ground.Arcsec)) return ground;
            string tech = s.SpaceLensSteps[step].TechId;
            return new LensChoice(s.SpaceLensSteps[step].Arcsec, LensKind.SpaceLens, step + 1,
                named ? SpaceLensTech.Title(tech) : null, $"space lens step {step + 1} ({tech})");
        }

        private static string TopLensWhy()
        {
            var game = HighLogic.CurrentGame;
            return game == null ? "no game"
                : game.Mode == Game.Modes.CAREER ? "tracking rules off"
                : game.Mode.ToString().ToLowerInvariant();
        }

        /* The lens's optics at `site`, null with deep-space optics off. Re-read once a second by the rig, so a craft leaving the air or a node researched mid-flight takes effect. */
        private Func<OpticsLens> LensOptics(CommandCameraSite site)
        {
            var s = _settings;
            if (!s.DeepSpaceOptics) return null;
            return () =>
            {
                var lens = LensAt(site, false);
                return new OpticsLens(s.OpticsDefaults(lens.Arcsec), lens.Label, lens.Kind == LensKind.SpaceLens);
            };
        }

        /*
         * What a command camera at `site` renders with at its tightest zoom,
         * from the rules its rig is built with: the zoom limit, the frame at
         * full quality, and the optics when this GPU draws them. A feed's
         * SetOptics override is the feed's, not the site's, so it is not in it.
         * `inVacuum` overrides whether a craft vantage is outside the air, for
         * a line of sight asked at another time than now.
         */
        internal LensLimits LensLimitsAt(CommandCameraSite site, bool? inVacuum = null)
        {
            if (site == null) return LensLimits.Unknown;
            bool on = _settings.DeepSpaceOptics && CommandCameraOptics.Available();
            var lens = LensAt(site, true, inVacuum);
            var knobs = on ? _settings.OpticsDefaults(lens.Arcsec) : OpticsKnobs.Unset;
            double fovMin = FovMinAt(site, inVacuum);
            return new LensLimits
            {
                Known = true,
                FovMinDeg = on ? OpticsModel.OpticsFovFloor(fovMin, knobs.ResolutionArcsec, FrameRows) : fovMin,
                WidthPx = _settings.Width,
                HeightPx = FrameRows,
                OpticsOn = on,
                Knobs = knobs,
                Lens = lens,
            };
        }

        /*
         * The lens knobs LensOptics gives `site`, with deep-space optics on or
         * off and without its label, for scoring a network's vantages: with
         * the effect off the score still ranks them as the effect would.
         */
        private OpticsKnobs LensKnobs(CommandCameraSite site) => _settings.OpticsDefaults(LensAt(site, false).Arcsec);

        /* Site identity for /cameras: its stable key and kind, its vantage
           craft, and where it stands. station_id is what a 1.11 sidecar needs
           to list the camera at all. A created camera also carries the token
           its creator sent. */
        private static string ManifestFields(CommandCameraSite s, bool created, string token)
        {
            var inv = CultureInfo.InvariantCulture;
            return $"  \"site_key\": \"{Esc(s.NodeName)}\",\n"
                + $"  \"site_kind\": \"{SiteKindTag(s.Kind)}\",\n"
                + $"  \"vantage_persistent_id\": {s.VantagePersistentId},\n"
                + $"  \"station_id\": \"{Esc(CameraId.SiteHashKey(s.NodeName))}\",\n"
                + $"  \"station_node_name\": \"{Esc(s.NodeName)}\",\n"
                + $"  \"station_name\": \"{Esc(string.IsNullOrEmpty(s.DisplayName) ? s.NodeName : s.DisplayName)}\",\n"
                + $"  \"station_body\": \"{Esc(s.Body != null ? s.Body.bodyName : "")}\",\n"
                + $"  \"station_latitude\": {s.Latitude.ToString("R", inv)},\n"
                + $"  \"station_longitude\": {s.Longitude.ToString("R", inv)},\n"
                + $"  \"station_altitude\": {s.Altitude.ToString("R", inv)}"
                + (created ? ",\n  \"command_created\": true" : "")
                + (created && !string.IsNullOrEmpty(token) ? $",\n  \"command_token\": \"{Esc(token)}\"" : "");
        }

        /* The wire's camelCase names for SiteKind. */
        internal static string SiteKindTag(Api.SiteKind kind)
        {
            switch (kind)
            {
                case Api.SiteKind.LaunchSite: return "launchSite";
                case Api.SiteKind.Surface: return "surface";
                case Api.SiteKind.Vessel: return "vessel";
                default: return "station";
            }
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
