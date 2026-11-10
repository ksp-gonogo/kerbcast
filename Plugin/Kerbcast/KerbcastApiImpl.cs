using System;
using System.Collections.Generic;
using Kerbcast.Api;
using UnityEngine;

namespace Kerbcast
{
    /*
     * Kerbcast.API's implementation, KerbcastApi.Instance while KerbcastCore
     * runs. The obsolete KerbcastControl facade calls the static cores
     * directly, keeping its old behaviour: no thread check, and command feeds
     * accepted. Identity events go through KerbcastApi.Identity (VesselIdentity).
     */
    internal sealed class KerbcastApiImpl : IKerbcast, ICameras, ICommand
    {
        static KerbcastApiImpl shared;

        readonly MainThread main = new MainThread();
        readonly ThrottleSettings throttle;

        KerbcastApiImpl()
        {
            throttle = new SaveThrottle(main);
        }

        /* Called from KerbcastCore.Awake on the main thread. */
        public static void Start()
        {
            if (shared == null) shared = new KerbcastApiImpl();
            KerbcastApi.Register(shared);
        }

        public static void Stop()
        {
            if (shared != null) KerbcastApi.Unregister(shared);
        }

        public ICameras Cameras => this;
        public ICommand Command => this;
        public bool SidecarAlive => SidecarAliveNow;
        public string BindAddress => BindAddressNow;
        public int Port => PortNow;
        public bool BindIsLoopback => KerbcastSettings.IsLoopback(BindAddressNow);

        internal static bool SidecarAliveNow => KerbcastSidecarHost.Instance?.SidecarRunning ?? false;
        internal static string BindAddressNow => BindSettings.BindAddress;
        internal static int PortNow => BindSettings.Port;

        static KerbcastSettings configured;

        /* What the running sidecar was launched with, or what settings.cfg
           configures before the first flight starts it. */
        static KerbcastSettings BindSettings =>
            KerbcastSidecarHost.Instance?.LaunchSettings ?? (configured ?? (configured = KerbcastSettings.Load()));

        public IReadOnlyList<CameraView> CamerasFor(uint vesselPersistentId)
        {
            main.Check(nameof(CamerasFor));
            var result = new List<CameraView>();
            var cams = KerbcastCore.Instance?.Cameras;
            if (cams == null || vesselPersistentId == 0) return result;
            for (int i = 0; i < cams.Count; i++)
                if (IsPartCamera(cams[i], out var c) && c.Vessel != null && c.Vessel.persistentId == vesselPersistentId)
                    result.Add(ToView(c));
            return result;
        }

        public IReadOnlyList<CameraView> CamerasFor(Guid vesselId)
        {
            main.Check(nameof(CamerasFor));
            var result = new List<CameraView>();
            var cams = KerbcastCore.Instance?.Cameras;
            if (cams == null || vesselId == Guid.Empty) return result;
            for (int i = 0; i < cams.Count; i++)
                if (IsPartCamera(cams[i], out var c) && c.Vessel != null && c.Vessel.id == vesselId)
                    result.Add(ToView(c));
            return result;
        }

        public CameraView View(uint flightId)
        {
            main.Check(nameof(View));
            var c = Find(flightId);
            return c == null ? null : ToView(c);
        }

        public bool SetFov(uint flightId, double fov)
        {
            main.Check(nameof(SetFov));
            return Finite(fov) && IsPartCamera(flightId) && SetFovCore(flightId, (float)fov);
        }

        public bool SetPan(uint flightId, double yaw, double pitch)
        {
            main.Check(nameof(SetPan));
            return Finite(yaw) && Finite(pitch) && IsPartCamera(flightId) && SetPanCore(flightId, (float)yaw, (float)pitch);
        }

        public bool AimAt(uint flightId, Vec3d target)
        {
            main.Check(nameof(AimAt));
            return target.IsFinite && IsPartCamera(flightId) && AimAtCore(flightId, new Vector3((float)target.X, (float)target.Y, (float)target.Z));
        }

        public bool SetTrackMode(uint flightId, TrackMode mode)
        {
            main.Check(nameof(SetTrackMode));
            return mode >= TrackMode.None && mode <= TrackMode.Target && IsPartCamera(flightId) && SetTrackModeCore(flightId, (int)mode);
        }

        public TrackMode GetTrackMode(uint flightId)
        {
            main.Check(nameof(GetTrackMode));
            return (TrackMode)GetTrackModeCore(flightId);
        }

        public bool TryVessel(uint persistentId, out Guid vesselId)
        {
            main.Check(nameof(TryVessel));
            return VesselIdentity.TryGet(persistentId, out vesselId);
        }

        public bool TryPersistentId(Guid vesselId, out uint persistentId)
        {
            main.Check(nameof(TryPersistentId));
            return VesselIdentity.TryGetPersistentId(vesselId, out persistentId);
        }

        public ThrottleSettings Throttle
        {
            get
            {
                main.Check(nameof(Throttle));
                return throttle;
            }
        }

        /* The same lock as the obsolete KerbcastControl.SetCommandAuthority. */
        public LockResult TakeLock(string owner, LockOptions options, string silentText = null)
        {
            Enter(nameof(TakeLock), ApiFeature.CommandLock);
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("an owner is required", nameof(owner));
            return KerbcastControl.TakeLockCore(owner, options, silentText);
        }

        public void ReleaseLock(string owner)
        {
            Enter(nameof(ReleaseLock), ApiFeature.CommandLock);
            KerbcastControl.ReleaseLockCore(owner);
        }

        /* The named assembly must be the caller's own, so a reservation always
           lapses with the mod that made it. Not inlined, so the calling
           assembly is the consumer's. */
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Reserve(string owner, string ownerAssembly)
        {
            var caller = System.Reflection.Assembly.GetCallingAssembly().GetName().Name;
            Enter(nameof(Reserve), ApiFeature.CommandLock);
            var reservation = LockReservationStore.Reservation;
            ReservedAssemblies.Check(owner, ownerAssembly, reservation.Owner);
            if (!string.Equals(ReservedAssemblies.Bare(ownerAssembly), caller, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{ownerAssembly}' is not the calling assembly ('{caller}')", nameof(ownerAssembly));
            reservation.Reserve(owner, ownerAssembly);
        }

        public void ClearReservation(string owner)
        {
            Enter(nameof(ClearReservation), ApiFeature.CommandLock);
            LockReservationStore.Reservation.Clear(owner);
        }

        public bool SetLightSpeed(string owner, double? metresPerSecond)
        {
            Enter(nameof(SetLightSpeed), ApiFeature.LightSpeed);
            return KerbcastControl.SetLightSpeedCore(owner, metresPerSecond);
        }

        /* Through the same path as the obsolete SetCommandAim, so the lock and
           every check apply alike: the lock (a heartbeat for the holder), then
           the feed, then the aim, in the recorder's order. */
        public AimResult SetAim(uint flightId, Aim aim, string owner)
        {
            Enter(nameof(SetAim), ApiFeature.CommandAim);
            var core = KerbcastCore.Instance;
            if (core == null) return AimResult.NotRunning;
            var auth = core.CommandAuthority;
            if (auth.Registered && !auth.Lock.Holds(owner)) return AimResult.NotLockHolder;
            if (!KerbcastControl.IsCommandFeed(flightId))
            {
                if (auth.Registered) auth.Heartbeat(Time.realtimeSinceStartup);
                return AimResult.NotCommandFeed;
            }
            CommandAim command = null;
            if (aim != null)
            {
                var converted = ToCommandAim(aim, out command);
                if (converted != AimResult.Ok)
                {
                    if (auth.Registered) auth.Heartbeat(Time.realtimeSinceStartup);
                    return converted;
                }
            }
            return KerbcastControl.SetAimCore(flightId, command, owner);
        }

        public OpticsResult SetOptics(uint flightId, OpticsSettings optics, string owner)
        {
            Enter(nameof(SetOptics), ApiFeature.Optics);
            var knobs = optics == null ? OpticsKnobs.Unset : new OpticsKnobs
            {
                ResolutionArcsec = optics.ResolutionArcsec,
                SeeingArcsec = optics.SeeingArcsec,
                SeeingAirmassPower = optics.SeeingAirmassPower,
                BrightnessHold = optics.BrightnessHold,
                DaySkyStrength = optics.DaySkyStrength,
            };
            return KerbcastControl.SetOpticsCore(flightId, knobs, owner);
        }

        public NetworkResult CreateNetworkCamera(IReadOnlyList<NetworkVantage> vantages, string name, Aim aim, NetworkOptions options,
            string owner, out uint flightId)
        {
            Enter(nameof(CreateNetworkCamera), ApiFeature.NetworkCameras);
            flightId = 0;
            if (KerbcastCore.Instance == null) return NetworkResult.NotRunning;
            var specs = ToSpecs(vantages, out var bad);
            if (bad != NetworkResult.Ok) return Refuse(bad, owner);
            CommandAim command = null;
            if (aim != null && ToCommandAim(aim, out command) != AimResult.Ok) return Refuse(NetworkResult.BadAim, owner);
            return KerbcastControl.CreateNetworkCore(specs, name, ToTuning(options), command, owner, out flightId);
        }

        public NetworkResult SetNetworkVantages(uint flightId, IReadOnlyList<NetworkVantage> vantages, NetworkOptions options, string owner)
        {
            Enter(nameof(SetNetworkVantages), ApiFeature.NetworkCameras);
            if (KerbcastCore.Instance == null) return NetworkResult.NotRunning;
            var specs = ToSpecs(vantages, out var bad);
            if (bad != NetworkResult.Ok) return Refuse(bad, owner);
            return KerbcastControl.SetNetworkCore(flightId, specs, options == null ? (NetworkTuning?)null : ToTuning(options), owner);
        }

        public NetworkResult DestroyNetworkCamera(uint flightId, string owner)
        {
            Enter(nameof(DestroyNetworkCamera), ApiFeature.NetworkCameras);
            return KerbcastControl.DestroyNetworkCore(flightId, owner);
        }

        public IReadOnlyList<NetworkVantageStatus> NetworkStatus(uint flightId)
        {
            main.Check(nameof(NetworkStatus));
            var result = new List<NetworkVantageStatus>();
            if (!KerbcastApi.Supports(ApiFeature.NetworkCameras)) throw MainThread.NotYet(nameof(NetworkStatus));
            var network = (Find(flightId)?.Rig as CommandCameraRig)?.Network;
            if (network == null) return result;
            var readings = new List<NetworkVantageReading>();
            network.Readings(readings);
            foreach (var r in readings)
                result.Add(new NetworkVantageStatus
                {
                    SiteKey = r.SiteKey,
                    Label = r.Label,
                    SiteKind = r.Kind,
                    VantagePersistentId = r.VantagePersistentId,
                    Active = r.Active,
                    Fallback = r.Fallback,
                    State = NetworkMath.Code(r.State),
                    Score = r.Score,
                    Occluder = r.Occluder,
                });
            return result;
        }

        readonly CommandLineOfSight sight = new CommandLineOfSight();

        public LineOfSightResult LineOfSight(NetworkVantage vantage, double ut, int bodyIndex, Vec3d position, double radiusM = double.NaN)
        {
            Enter(nameof(LineOfSight), ApiFeature.LineOfSight);
            var a = sight.Check(vantage, ut, bodyIndex, new DVec3(position.X, position.Y, position.Z), radiusM);
            return new LineOfSightResult(a.Known, a.Visible, a.Occluder, a.BelowHorizon, a.RangeM, a.Disabled,
                KerbcastApi.Supports(ApiFeature.VantageResolution) ? a.ResolutionRad : double.NaN);
        }

        public VantageResolution VantageResolution(NetworkVantage vantage)
        {
            Enter(nameof(VantageResolution), ApiFeature.VantageResolution);
            var lens = sight.Lens(vantage);
            if (!lens.Known) return new VantageResolution();
            return new VantageResolution
            {
                Known = true,
                FovMinDeg = lens.FovMinDeg,
                FrameWidthPx = lens.WidthPx,
                FrameHeightPx = lens.HeightPx,
                PixelRad = lens.PixelArcsec / OpticsModel.ArcsecPerRadian,
                OpticsOn = lens.OpticsOn,
                LensArcsec = lens.LensArcsec,
                BestResolutionRad = lens.ResolutionArcsec(0, 90) / OpticsModel.ArcsecPerRadian,
            };
        }

        public IReadOnlyList<GroundStationInfo> GroundStations()
        {
            Enter(nameof(GroundStations), ApiFeature.GroundStations);
            var stations = new List<GroundStation>();
            CommandCameraSite.GroundStations(stations);
            var result = new List<GroundStationInfo>(stations.Count);
            foreach (var s in stations)
                result.Add(new GroundStationInfo
                {
                    NodeName = s.NodeName,
                    DisplayName = s.DisplayName,
                    BodyIndex = s.BodyIndex,
                    Latitude = s.Latitude,
                    Longitude = s.Longitude,
                    Altitude = s.Altitude,
                    Enabled = s.Enabled,
                    DisabledReason = s.DisabledReason,
                });
            return result;
        }

        /* A refusal found before the lock is checked still answers the lock first, as SetAim does: the holder's call is a heartbeat, anyone else's is NotLockHolder. */
        static NetworkResult Refuse(NetworkResult why, string owner)
        {
            var auth = KerbcastCore.Instance?.CommandAuthority;
            if (auth == null || !auth.Registered) return why;
            if (!auth.Lock.Holds(owner)) return NetworkResult.NotLockHolder;
            auth.Heartbeat(Time.realtimeSinceStartup);
            return why;
        }

        static NetworkTuning ToTuning(NetworkOptions o) => o == null ? NetworkTuning.Default : new NetworkTuning
        {
            MinHoldUt = o.MinHoldUt,
            MinHoldRealSeconds = o.MinHoldRealSeconds,
            MarginStops = o.MarginStops,
        };

        /* The API's vantages as the plugin's. NoVantages for none, BadVantage for a null entry, too many, or a body index nobody has. */
        static List<NetworkVantageSpec> ToSpecs(IReadOnlyList<NetworkVantage> vantages, out NetworkResult bad)
        {
            bad = NetworkResult.Ok;
            if (vantages == null || vantages.Count == 0)
            {
                bad = NetworkResult.NoVantages;
                return null;
            }
            if (vantages.Count > NetworkMath.MaxVantages)
            {
                bad = NetworkResult.BadVantage;
                return null;
            }
            var specs = new List<NetworkVantageSpec>(vantages.Count);
            foreach (var v in vantages)
            {
                var cv = ToCommandVantage(v);
                if (cv == null)
                {
                    bad = NetworkResult.BadVantage;
                    return null;
                }
                specs.Add(new NetworkVantageSpec { Vantage = cv, Label = v.Label, Fallback = v.Fallback });
            }
            return specs;
        }

        /* The plugin's vantage for an API one; null for a null vantage or a surface on an unknown body. */
        internal static CommandVantage ToCommandVantage(NetworkVantage v)
        {
            if (v == null) return null;
            switch (v.Kind)
            {
                case SiteKind.Station: return CommandVantage.Station(v.Name, v.Height);
                case SiteKind.LaunchSite: return CommandVantage.LaunchSite(v.Name, v.Distance, v.Bearing, v.Height);
                case SiteKind.Vessel: return CommandVantage.Vessel(v.PersistentId);
                case SiteKind.Surface:
                    var body = BodyByIndex(v.BodyIndex);
                    return body != null ? CommandVantage.Surface(body.bodyName, v.Latitude, v.Longitude, v.Height) : null;
            }
            return null;
        }

        static CelestialBody BodyByIndex(int index)
        {
            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return null;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i] != null && bodies[i].flightGlobalsIndex == index) return bodies[i];
            return null;
        }

        /* Kerbcast.API names bodies by flightGlobalsIndex; the rig by name. */
        static AimResult ToCommandAim(Aim a, out CommandAim command)
        {
            command = null;
            string body = null;
            if (a.Kind == AimKind.Orbit || a.Kind == AimKind.Surface || a.Kind == AimKind.PositionAt)
            {
                CelestialBody b = null;
                var bodies = FlightGlobals.Bodies;
                if (bodies != null)
                    for (int i = 0; i < bodies.Count && b == null; i++)
                        if (bodies[i] != null && bodies[i].flightGlobalsIndex == a.BodyIndex) b = bodies[i];
                if (b == null) return AimResult.UnknownBody;
                body = b.bodyName;
            }
            switch (a.Kind)
            {
                case AimKind.Vessel:
                    command = CommandAim.Vessel(a.SubjectPersistentId, a.FramingMarginM, a.AllowUncertain);
                    break;
                case AimKind.Orbit:
                    command = CommandAim.Orbit(body, a.Inclination, a.Eccentricity, a.SemiMajorAxis, a.Lan, a.ArgumentOfPeriapsis,
                        a.MeanAnomalyAtEpoch, a.Epoch, a.SubjectPersistentId, a.FramingMarginM);
                    break;
                case AimKind.Surface:
                    command = CommandAim.Surface(body, a.Latitude, a.Longitude, a.Altitude, a.FramingMarginM);
                    break;
                case AimKind.PositionAt:
                    command = CommandAim.PositionAt(body, a.Ut, new Vector3d(a.Position.X, a.Position.Y, a.Position.Z),
                        new Vector3d(a.Velocity.X, a.Velocity.Y, a.Velocity.Z), a.SubjectPersistentId, a.FramingMarginM);
                    break;
                case AimKind.Direction:
                    command = CommandAim.Direction(a.Azimuth, a.Elevation, a.FramingMarginM);
                    break;
                default:
                    return AimResult.BadNumber;
            }
            return AimResult.Ok;
        }

        /* The thread check, then the feature check, as every API member does. */
        void Enter(string member, ApiFeature feature)
        {
            main.Check(member);
            if (!KerbcastApi.Supports(feature)) throw MainThread.NotYet(member);
        }

        /* Built feeds only: a created camera appears once its site resolves. */
        public IReadOnlyList<CommandFeed> Feeds()
        {
            main.Check(nameof(Feeds));
            var result = new List<CommandFeed>();
            var core = KerbcastCore.Instance;
            var cams = core?.Cameras;
            if (cams == null) return result;
            var sites = core.CommandAuthority.Sites;
            for (int i = 0; i < cams.Count; i++)
            {
                if (!(cams[i] is KerbcastCamera c) || !(c.Rig is CommandCameraRig gr)) continue;
                var site = gr.Site;
                result.Add(new CommandFeed
                {
                    FlightId = c.FlightId,
                    DisplayName = gr.SiteLabel,
                    SiteKey = site.NodeName,
                    SiteKind = site.Kind,
                    VantagePersistentId = site.VantagePersistentId,
                    Scope = sites.ScopeFor(site.NodeName),
                    SubjectPersistentId = gr.SubjectPersistentId,
                    Created = core.CommandProvider?.IsCreated(c.FlightId) ?? false,
                    Network = gr.Network != null,
                    Saved = core.IsSavedNetwork(c.FlightId),
                    FollowsActiveVessel = gr.Aim == null,
                });
            }
            return result;
        }

        public IReadOnlyList<CommandTarget> Targets(uint flightId)
        {
            main.Check(nameof(Targets));
            var result = new List<CommandTarget>();
            if (!(Find(flightId)?.Rig is CommandCameraRig gr)) return result;
            double ut = Planetarium.GetUniversalTime();
            foreach (var t in CommandCameraTargets.List(gr.Site, ut, gr.FovMax))
            {
                result.Add(new CommandTarget
                {
                    PersistentId = t.PersistentId,
                    Name = t.VesselName,
                    Kind = t.VesselType,
                    RangeM = t.RangeM,
                    Eligibility = t.Eligibility,
                    EligibilityText = t.EligibilityText,
                    RefusalCode = t.RefusalCode,
                    RefusalText = t.RefusalText,
                    Ut = ut,
                });
            }
            return result;
        }

        /* The lock today is the one KerbcastControl.SetCommandAuthority takes.
           Unsupported, so throwing, until Kerbcast.API itself can take the lock. */
        public IWorldModelPublisher AttachWorldModel(string owner)
        {
            Enter(nameof(AttachWorldModel), ApiFeature.WorldModel);
            var auth = KerbcastCore.Instance?.CommandAuthority;
            if (auth == null || !auth.Lock.Holds(owner)) return null;
            auth.Heartbeat(Time.realtimeSinceStartup);
            return auth.WorldModel.Attach(owner);
        }

        Exception Unsupported(string member)
        {
            main.Check(member);
            return MainThread.NotYet(member);
        }

        internal static bool SetFovCore(uint flightId, float fov)
        {
            var c = Find(flightId); if (c == null) return false; c.SetFov(fov); return true;
        }

        internal static bool SetPanCore(uint flightId, float yaw, float pitch)
        {
            var c = Find(flightId); if (c == null) return false; c.SetPanTarget(yaw, pitch); return true;
        }

        /* `target` is relative to the camera's craft's centre of mass (kOS's
           TARGET:POSITION frame); the camera aims in world space. */
        internal static bool AimAtCore(uint flightId, Vector3 target)
        {
            var c = Find(flightId); if (c == null) return false;
            var vessel = c.Mount.Vessel;
            Vector3 worldPoint = vessel != null ? (Vector3)vessel.CoM + target : target;
            c.AimAt(worldPoint);
            return true;
        }

        /* Only pan and zoom cameras honour a set, as in the browser. */
        internal static bool SetTrackModeCore(uint flightId, int mode)
        {
            var c = Find(flightId);
            if (c == null || !(c.SupportsPan && c.SupportsZoom)) return false;
            c.RequestTrackMode(mode);
            return true;
        }

        internal static int GetTrackModeCore(uint flightId)
        {
            var c = Find(flightId);
            return c != null ? c.GetTrackMode() : 0;
        }

        /* Command feeds are steered through ICommand, never these controls. */
        static bool IsPartCamera(uint flightId) => IsPartCamera(Find(flightId), out _);

        /* Kerbal cameras are not part cameras, nor are command cameras, whose
           Vessel is the craft they watch. */
        internal static bool IsPartCamera(ICamera cam, out KerbcastCamera c)
        {
            c = cam as KerbcastCamera;
            return c != null && c.Rig == null;
        }

        internal static KerbcastCamera Find(uint flightId)
        {
            var cams = KerbcastCore.Instance?.Cameras;
            if (cams == null) return null;
            for (int i = 0; i < cams.Count; i++)
                if (cams[i] is KerbcastCamera kc && kc.FlightId == flightId) return kc;
            return null;
        }

        static CameraView ToView(KerbcastCamera c)
        {
            bool command = c.Rig is CommandCameraRig;
            var vessel = command ? null : c.Mount.Vessel;
            var part = c.Mount.OwningPart;
            Vector3 fwd = c.BoresightWorld;
            Vector3 pos = c.PositionWorld;
            if (vessel != null) pos -= (Vector3)vessel.CoM;
            var view = new CameraView
            {
                FlightId = c.FlightId,
                Kind = command ? CameraKind.Command : CameraKind.Part,
                Name = c.CameraName,
                VesselPersistentId = vessel != null ? vessel.persistentId : 0u,
                VesselId = vessel != null ? vessel.id : Guid.Empty,
                PartFlightId = part != null ? part.flightID : 0u,
                PartPersistentId = part != null ? part.persistentId : 0u,
                PartName = c.PartName,
                PartTitle = c.PartTitle,
                SupportsZoom = c.SupportsZoom,
                SupportsPan = c.SupportsPan,
                Fov = c.Fov, FovMin = c.FovMin, FovMax = c.FovMax,
                PanYaw = c.PanYaw, PanPitch = c.PanPitch,
                PanYawMin = c.PanYawMin, PanYawMax = c.PanYawMax,
                PanPitchMin = c.PanPitchMin, PanPitchMax = c.PanPitchMax,
                Boresight = new Vec3d(fwd.x, fwd.y, fwd.z),
                Position = new Vec3d(pos.x, pos.y, pos.z),
                TrackMode = (TrackMode)c.GetTrackMode(),
            };
            if (c.Rig is CommandCameraRig gr && gr.Occluded)
            {
                view.Occluded = true;
                view.OccludedSinceUt = gr.OccludedSinceUt;
                view.Occluder = string.IsNullOrEmpty(gr.Blocker) ? null : gr.Blocker;
            }
            ReadDocking(part, view);
            return view;
        }

        static void ReadDocking(Part part, CameraView view)
        {
            if (part == null) return;
            try
            {
                var node = part.FindModuleImplementing<ModuleDockingNode>();
                view.HasDockingNode = node != null;
                if (node == null) return;
                view.DockingNodeType = string.IsNullOrEmpty(node.nodeType) ? null : node.nodeType;
                view.DockingNodeState = string.IsNullOrEmpty(node.state) ? null : node.state;
            }
            catch (Exception ex)
            {
                view.HasDockingNode = null;
                Debug.LogWarning($"[Kerbcast][API] docking facts for cam={view.FlightId} unreadable: {ex.Message}");
            }
        }

        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        sealed class SaveThrottle : ThrottleSettings
        {
            readonly MainThread main;

            public SaveThrottle(MainThread main)
            {
                this.main = main;
            }

            public override bool? MainScreen
            {
                get
                {
                    main.Check("Throttle.MainScreen");
                    return Node()?.ThrottleMainScreen;
                }
            }

            /* KerbcastCore reconciles the live render with the save's value. */
            public override bool SetMainScreen(bool on)
            {
                main.Check("Throttle.SetMainScreen");
                var node = Node();
                if (node == null) return false;
                node.ThrottleMainScreen = on;
                return true;
            }

            static KerbcastGameParameters Node() => HighLogic.CurrentGame?.Parameters?.CustomParams<KerbcastGameParameters>();
        }
    }
}
