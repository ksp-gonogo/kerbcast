using System;
using System.Collections.Generic;

namespace Kerbcast.Api.Testing
{
    /**
     * <summary>
     * The command lock, aims and world model as a test sees them. It applies the API's documented
     * rules (one lock owner, owner-only aims and light speed, a publisher only for the holder, ordered
     * sequences, epochs per lock tenure, fail-closed dispose) and nothing of Kerbcast's rendering.
     * Real-time silence is not simulated.
     * </summary>
     */
    public sealed class RecordingCommand : ICommand
    {
        readonly RecordingKerbcast kc;
        readonly List<CommandFeed> feeds = new List<CommandFeed>();
        readonly Dictionary<uint, List<CommandTarget>> targets = new Dictionary<uint, List<CommandTarget>>();
        readonly Dictionary<uint, Aim> aims = new Dictionary<uint, Aim>();
        readonly Dictionary<uint, OpticsSettings> optics = new Dictionary<uint, OpticsSettings>();
        readonly Dictionary<uint, List<NetworkVantage>> networks = new Dictionary<uint, List<NetworkVantage>>();
        readonly Dictionary<uint, NetworkOptions> networkOptions = new Dictionary<uint, NetworkOptions>();
        readonly Dictionary<uint, List<NetworkVantageStatus>> networkStatus = new Dictionary<uint, List<NetworkVantageStatus>>();
        readonly List<GroundStationInfo> stations = new List<GroundStationInfo>();
        LineOfSightResult sight = new LineOfSightResult(false, false, null, false, double.NaN);
        uint nextNetworkId = 0x4E000001;
        readonly List<PublishedSnapshot> published = new List<PublishedSnapshot>();
        string lockOwner;
        int scene;
        /* Bumped whenever a lock tenure ends or starts, so a publisher from an earlier one is refused. */
        int tenure;
        RecordingPublisher publisher;
        long lastEpoch;
        bool hasEpoch;
        IReadOnlyDictionary<string, string> bindings;

        internal RecordingCommand(RecordingKerbcast kerbcast)
        {
            kc = kerbcast;
        }

        /** <summary>Who holds the lock, null when nobody does.</summary> */
        public string LockOwner
        {
            get { lock (kc.Gate) return lockOwner; }
        }

        public LockOptions LockOptions { get; private set; }
        public string SilentText { get; private set; }
        public string ReservedOwner { get; private set; }
        public string ReservedAssembly { get; private set; }

        /** <summary>The holder's light speed; null (no delay) until set, and again after the lock is released or the scene changes.</summary> */
        public double? LightSpeed { get; private set; }

        /** <summary>Owner calls since the lock was taken, each one a heartbeat.</summary> */
        public int Heartbeats { get; private set; }

        /** <summary>True after the holder's live publisher detached with returnToTruth, until a publisher attaches again, the lock is taken again or the scene changes.</summary> */
        public bool ReturnedToTruth { get; private set; }

        /** <summary>Decides SetAim once the lock and feed checks pass; Ok when unset.</summary> */
        public Func<uint, Aim, AimResult> AimJudge { get; set; }

        /** <summary>The aim on each feed, as set and not cleared.</summary> */
        public IReadOnlyDictionary<uint, Aim> Aims => aims;

        /** <summary>The optics override on each feed, as set and not cleared.</summary> */
        public IReadOnlyDictionary<uint, OpticsSettings> Optics => optics;

        /**
         * <summary>
         * Body names by flightGlobalsIndex, for a surface vantage's site key ("surface:Kerbin:..."), as Kerbcast names
         * them. Stock's bodies to start with; a test of a modded system replaces them.
         * </summary>
         */
        public IDictionary<int, string> BodyNames { get; } = new Dictionary<int, string>
        {
            [0] = "Sun", [1] = "Kerbin", [2] = "Mun", [3] = "Minmus", [4] = "Moho", [5] = "Eve", [6] = "Duna", [7] = "Ike",
            [8] = "Jool", [9] = "Laythe", [10] = "Vall", [11] = "Bop", [12] = "Tylo", [13] = "Gilly", [14] = "Pol", [15] = "Dres", [16] = "Eeloo",
        };

        /** <summary>Each network camera's vantages, as created or last replaced.</summary> */
        public IReadOnlyDictionary<uint, List<NetworkVantage>> Networks => networks;

        /** <summary>Each network camera's options, as created or last replaced.</summary> */
        public IReadOnlyDictionary<uint, NetworkOptions> NetworkOptionsSet => networkOptions;

        /**
         * <summary>
         * What NetworkStatus returns for a network camera. Unset, it lists the camera's vantages in order with their
         * kind and craft id, the first active and every one "ok" with a NaN score: a test of cuts and states sets its own here.
         * </summary>
         */
        public void SetNetworkStatus(uint flightId, IEnumerable<NetworkVantageStatus> status) =>
            networkStatus[flightId] = new List<NetworkVantageStatus>(status ?? new NetworkVantageStatus[0]);

        /** <summary>What every LineOfSight call answers: not Known until a test sets it.</summary> */
        public void SetLineOfSight(LineOfSightResult answer) => sight = answer;

        /** <summary>What GroundStations returns: none until a test sets them.</summary> */
        public void SetGroundStations(IEnumerable<GroundStationInfo> list)
        {
            stations.Clear();
            if (list != null) stations.AddRange(list);
        }

        /** <summary>Every accepted snapshot, oldest first, from any thread.</summary> */
        public IReadOnlyList<PublishedSnapshot> Published
        {
            get { lock (kc.Gate) return published.ToArray(); }
        }

        /** <summary>The publisher AttachWorldModel handed out last, null before then.</summary> */
        public RecordingPublisher Publisher
        {
            get { lock (kc.Gate) return publisher; }
        }

        /**
         * <summary>
         * Adds or replaces a command feed by FlightId. Feeds reports each feed's Scope as Kerbcast does:
         * from the bindings of the last snapshot accepted in this lock tenure, null when its SiteKey is
         * unbound, before any snapshot, and after the lock is released. FollowsActiveVessel is likewise
         * Kerbcast's: true while the feed has no aim in Aims. The Scope and FollowsActiveVessel given here are ignored.
         * </summary>
         */
        public void AddFeed(CommandFeed feed)
        {
            if (feed == null) throw new ArgumentNullException(nameof(feed));
            feeds.RemoveAll(f => f.FlightId == feed.FlightId);
            feeds.Add(Copy(feed));
        }

        /** <summary>What Targets returns for a feed.</summary> */
        public void SetTargets(uint flightId, IEnumerable<CommandTarget> list) =>
            targets[flightId] = new List<CommandTarget>(list ?? new CommandTarget[0]);

        /**
         * <summary>
         * A scene change: the lock, its tenure's state and the publisher reset, raising KerbcastApi.LockLost
         * (SceneEnded) for a holder, during which TakeLock returns NotRunning; then a reservation whose
         * assembly is loaded takes the lock again, with WorldModelRequired.
         * </summary>
         */
        public void ChangeScene()
        {
            string held;
            lock (kc.Gate)
            {
                held = lockOwner;
                lockOwner = null;
                tenure++;
                hasEpoch = false;
                bindings = null;
            }
            EndTenure();
            /* As in Kerbcast, the dying scene refuses a take from the handler. */
            sceneEnding = true;
            try
            {
                if (held != null) KerbcastApi.RaiseLockLost(new LockLostEvent(held, LockLossReason.SceneEnded), null);
            }
            finally
            {
                sceneEnding = false;
            }
            if (!startupChecked)
            {
                /* Kerbcast drops a reservation once, at its first scene, when its assembly is missing. */
                startupChecked = true;
                if (ReservedOwner != null && (ReservedAssemblies.IsReserved(ReservedAssembly) || !AssemblyLoaded(ReservedAssembly)))
                {
                    ReservedOwner = null;
                    ReservedAssembly = null;
                }
            }
            bool reserved = ReservedOwner != null && AssemblyLoaded(ReservedAssembly);
            lock (kc.Gate)
            {
                scene++;
                tenure++;
                lockOwner = reserved ? ReservedOwner : null;
                hasEpoch = false;
                bindings = null;
            }
            EndTenure();
            /* A reserved scene starts locked with every craft unknown until the owner attaches and publishes. */
            if (reserved) LockOptions = LockOptions.WorldModelRequired;
            kc.Record("(scene change)");
        }

        /**
         * <summary>
         * The player's in-game release: the lock goes, raising KerbcastApi.LockLost (ReleasedByPlayer) for a
         * holder, and any reservation is cleared, whoever owns it.
         * </summary>
         */
        public void ReleaseByPlayer()
        {
            kc.Record("(player release)");
            ReservedOwner = null;
            ReservedAssembly = null;
            LoseLock(LockLossReason.ReleasedByPlayer);
        }

        void LoseLock(LockLossReason reason)
        {
            string held;
            lock (kc.Gate)
            {
                held = lockOwner;
                if (held == null) return;
                lockOwner = null;
                tenure++;
                hasEpoch = false;
                bindings = null;
            }
            EndTenure();
            KerbcastApi.RaiseLockLost(new LockLostEvent(held, reason), null);
        }

        /**
         * <summary>
         * Whether a reservation's assembly is loaded, for ChangeScene; true for any name when unset, and never
         * for an empty one. The first ChangeScene stands in for Kerbcast's startup, dropping a reservation whose
         * assembly is missing, once.
         * </summary>
         */
        public Func<string, bool> AssemblyLoadedCheck { get; set; }

        bool startupChecked;
        bool sceneEnding;

        bool AssemblyLoaded(string assembly) =>
            !string.IsNullOrEmpty(assembly) && (AssemblyLoadedCheck == null || AssemblyLoadedCheck(assembly));

        public LockResult TakeLock(string owner, LockOptions options, string silentText = null)
        {
            kc.Enter("TakeLock", ApiFeature.CommandLock);
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("an owner is required", nameof(owner));
            ReservedAssemblies.CheckText(owner, nameof(owner));
            ReservedAssemblies.CheckText(silentText, nameof(silentText));
            kc.Record($"TakeLock {owner} {options}");
            if (sceneEnding) return LockResult.NotRunning;
            lock (kc.Gate)
            {
                if (lockOwner != null && lockOwner != owner) return LockResult.HeldByOther;
                bool renewed = lockOwner == owner;
                bool gainsRequired = (options & LockOptions.WorldModelRequired) != 0 && (LockOptions & LockOptions.WorldModelRequired) == 0;
                lockOwner = owner;
                LockOptions = options;
                SilentText = silentText;
                Heartbeats++;
                if (renewed)
                {
                    /* A renewal keeps a return to truth unless it newly requires a world model. */
                    if (gainsRequired) ReturnedToTruth = false;
                    return LockResult.Renewed;
                }
                tenure++;
                hasEpoch = false;
                bindings = null;
            }
            aims.Clear();
            optics.Clear();
            ReturnedToTruth = false;
            return LockResult.Taken;
        }

        public void ReleaseLock(string owner)
        {
            kc.Enter("ReleaseLock", ApiFeature.CommandLock);
            kc.Record($"ReleaseLock {owner}");
            lock (kc.Gate)
            {
                if (lockOwner == null || lockOwner != owner) return;
                lockOwner = null;
                tenure++;
                hasEpoch = false;
                bindings = null;
            }
            EndTenure();
        }

        /**
         * <summary>
         * As Kerbcast's: the named assembly must be the caller's own, so a test passes its own assembly's
         * name (for example <c>Assembly.GetExecutingAssembly().GetName().Name</c>).
         * </summary>
         */
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Reserve(string owner, string ownerAssembly)
        {
            var caller = System.Reflection.Assembly.GetCallingAssembly().GetName().Name;
            kc.Enter("Reserve", ApiFeature.CommandLock);
            ReservedAssemblies.Check(owner, ownerAssembly, ReservedOwner);
            if (!string.Equals(ReservedAssemblies.Bare(ownerAssembly), caller, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{ownerAssembly}' is not the calling assembly ('{caller}')", nameof(ownerAssembly));
            kc.Record($"Reserve {owner} {ownerAssembly}");
            ReservedOwner = owner;
            ReservedAssembly = ownerAssembly;
        }

        public void ClearReservation(string owner)
        {
            kc.Enter("ClearReservation", ApiFeature.CommandLock);
            kc.Record($"ClearReservation {owner}");
            if (owner == null || ReservedOwner != owner) return;
            ReservedOwner = null;
            ReservedAssembly = null;
        }

        public bool SetLightSpeed(string owner, double? metresPerSecond)
        {
            kc.Enter("SetLightSpeed", ApiFeature.LightSpeed);
            if (metresPerSecond.HasValue && !(Vec3d.Finite(metresPerSecond.Value) && metresPerSecond.Value > 0))
                throw new ArgumentOutOfRangeException(nameof(metresPerSecond), metresPerSecond, "must be finite and positive, or null");
            kc.Record($"SetLightSpeed {owner} {metresPerSecond?.ToString() ?? "null"}");
            if (owner == null || LockOwner != owner) return false;
            Heartbeats++;
            LightSpeed = metresPerSecond;
            return true;
        }

        public AimResult SetAim(uint flightId, Aim aim, string owner)
        {
            kc.Enter("SetAim", ApiFeature.CommandAim);
            kc.Record($"SetAim {flightId} {aim?.ToString() ?? "null"} {owner}");
            string holder = LockOwner;
            if (holder != null)
            {
                if (holder != owner) return AimResult.NotLockHolder;
                Heartbeats++;
            }
            if (!feeds.Exists(f => f.FlightId == flightId)) return AimResult.NotCommandFeed;
            if (aim == null)
            {
                aims.Remove(flightId);
                return AimResult.Ok;
            }
            var result = AimJudge?.Invoke(flightId, aim) ?? AimResult.Ok;
            if (result == AimResult.Ok) aims[flightId] = aim;
            return result;
        }

        public OpticsResult SetOptics(uint flightId, OpticsSettings settings, string owner)
        {
            kc.Enter("SetOptics", ApiFeature.Optics);
            kc.Record($"SetOptics {flightId} {settings?.ToString() ?? "null"} {owner}");
            string holder = LockOwner;
            if (holder != null)
            {
                if (holder != owner) return OpticsResult.NotLockHolder;
                Heartbeats++;
            }
            if (!feeds.Exists(f => f.FlightId == flightId)) return OpticsResult.NotCommandFeed;
            if (settings != null && (!AtLeastZero(settings.ResolutionArcsec) || !AtLeastZero(settings.SeeingArcsec) || !AtLeastZero(settings.SeeingAirmassPower)
                || !AtLeastZero(settings.DaySkyStrength) || !AtLeastZero(settings.BrightnessHold) || settings.BrightnessHold > 1))
                return OpticsResult.BadNumber;
            /* As in Kerbcast, settings with every value NaN are no override at all. */
            if (settings == null || (double.IsNaN(settings.ResolutionArcsec) && double.IsNaN(settings.SeeingArcsec)
                && double.IsNaN(settings.SeeingAirmassPower) && double.IsNaN(settings.BrightnessHold) && double.IsNaN(settings.DaySkyStrength)))
            {
                optics.Remove(flightId);
                return OpticsResult.Ok;
            }
            optics[flightId] = new OpticsSettings
            {
                ResolutionArcsec = settings.ResolutionArcsec,
                SeeingArcsec = settings.SeeingArcsec,
                SeeingAirmassPower = settings.SeeingAirmassPower,
                BrightnessHold = settings.BrightnessHold,
                DaySkyStrength = settings.DaySkyStrength,
            };
            return OpticsResult.Ok;
        }

        /* NaN (Kerbcast's own value) or finite and not negative. */
        static bool AtLeastZero(double v) => double.IsNaN(v) || (!double.IsInfinity(v) && v >= 0);

        /**
         * <summary>
         * As Kerbcast's, without the game: the lock and the list are checked, and a created network camera is listed by
         * Feeds at once, its SiteKey the first vantage's (a station's or launch site's name, "vessel:" and the craft's
         * id, or "surface:", the body's name from BodyNames and its place) and its DisplayName the first vantage's label
         * or that key. A surface vantage on a body BodyNames lacks is a BadVantage, as an unknown body is to Kerbcast. The aim is
         * recorded in Aims unchecked.
         * </summary>
         */
        public NetworkResult CreateNetworkCamera(IReadOnlyList<NetworkVantage> vantages, string name, Aim aim, NetworkOptions options,
            string owner, out uint flightId)
        {
            kc.Enter("CreateNetworkCamera", ApiFeature.NetworkCameras);
            flightId = 0;
            kc.Record($"CreateNetworkCamera {(vantages == null ? "null" : string.Join(", ", vantages))} {name ?? "null"} {aim?.ToString() ?? "null"} {owner}");
            var refused = NetworkLock(owner);
            if (refused != NetworkResult.Unknown) return refused;
            var bad = Judge(vantages, options);
            if (bad != NetworkResult.Ok) return bad;
            flightId = nextNetworkId++;
            networks[flightId] = new List<NetworkVantage>(vantages);
            networkOptions[flightId] = Copy(options ?? new NetworkOptions());
            if (aim != null) aims[flightId] = aim;
            string key = Key(vantages[0]);
            feeds.Add(new CommandFeed
            {
                FlightId = flightId,
                DisplayName = vantages[0].Label ?? key,
                SiteKey = key,
                SiteKind = vantages[0].Kind,
                VantagePersistentId = vantages[0].Kind == SiteKind.Vessel ? vantages[0].PersistentId : 0,
                Created = true,
                Network = true,
            });
            return NetworkResult.Ok;
        }

        public NetworkResult SetNetworkVantages(uint flightId, IReadOnlyList<NetworkVantage> vantages, NetworkOptions options, string owner)
        {
            kc.Enter("SetNetworkVantages", ApiFeature.NetworkCameras);
            kc.Record($"SetNetworkVantages {flightId} {(vantages == null ? "null" : string.Join(", ", vantages))} {owner}");
            var refused = NetworkLock(owner);
            if (refused != NetworkResult.Unknown) return refused;
            if (!networks.ContainsKey(flightId)) return NetworkResult.NotCommandFeed;
            var bad = Judge(vantages, options);
            if (bad != NetworkResult.Ok) return bad;
            networks[flightId] = new List<NetworkVantage>(vantages);
            if (options != null) networkOptions[flightId] = Copy(options);
            return NetworkResult.Ok;
        }

        public NetworkResult DestroyNetworkCamera(uint flightId, string owner)
        {
            kc.Enter("DestroyNetworkCamera", ApiFeature.NetworkCameras);
            kc.Record($"DestroyNetworkCamera {flightId} {owner}");
            var refused = NetworkLock(owner);
            if (refused != NetworkResult.Unknown) return refused;
            if (!networks.Remove(flightId)) return NetworkResult.NotCommandFeed;
            networkOptions.Remove(flightId);
            networkStatus.Remove(flightId);
            aims.Remove(flightId);
            feeds.RemoveAll(f => f.FlightId == flightId);
            return NetworkResult.Ok;
        }

        public IReadOnlyList<NetworkVantageStatus> NetworkStatus(uint flightId)
        {
            kc.Enter("NetworkStatus", ApiFeature.NetworkCameras);
            if (networkStatus.TryGetValue(flightId, out var set)) return set.ConvertAll(Copy);
            var result = new List<NetworkVantageStatus>();
            if (!networks.TryGetValue(flightId, out var list)) return result;
            for (int i = 0; i < list.Count; i++)
                result.Add(new NetworkVantageStatus
                {
                    SiteKey = Key(list[i]), Label = list[i].Label ?? Key(list[i]), SiteKind = list[i].Kind,
                    VantagePersistentId = list[i].Kind == SiteKind.Vessel ? list[i].PersistentId : 0,
                    Active = i == 0, Fallback = list[i].Fallback, State = "ok", Score = 0,
                });
            return result;
        }

        /* NotLockHolder for anyone but a held lock's owner, else Unknown, counting the holder's heartbeat. */
        NetworkResult NetworkLock(string owner)
        {
            string holder = LockOwner;
            if (holder == null) return NetworkResult.Unknown;
            if (holder != owner) return NetworkResult.NotLockHolder;
            Heartbeats++;
            return NetworkResult.Unknown;
        }

        /* The list and options as Kerbcast checks them before the game: whether a named station or craft exists is not known here. */
        NetworkResult Judge(IReadOnlyList<NetworkVantage> vantages, NetworkOptions options)
        {
            if (vantages == null || vantages.Count == 0) return NetworkResult.NoVantages;
            if (vantages.Count > 64) return NetworkResult.BadVantage;
            if (BodyNames == null) return NetworkResult.BadVantage;
            foreach (var v in vantages)
            {
                if (v == null) return NetworkResult.BadVantage;
                bool ok;
                switch (v.Kind)
                {
                    case SiteKind.Station: ok = !string.IsNullOrWhiteSpace(v.Name) && Vec3d.Finite(v.Height); break;
                    case SiteKind.LaunchSite: ok = Vec3d.Finite(v.Distance) && v.Distance >= 0 && Vec3d.Finite(v.Bearing) && Vec3d.Finite(v.Height); break;
                    case SiteKind.Surface: ok = BodyNames.ContainsKey(v.BodyIndex) && Vec3d.Finite(v.Latitude) && v.Latitude >= -90 && v.Latitude <= 90 && Vec3d.Finite(v.Longitude) && Vec3d.Finite(v.Height); break;
                    default: ok = v.PersistentId != 0; break;
                }
                if (!ok) return NetworkResult.BadVantage;
            }
            if (options != null && !(NotNegative(options.MinHoldUt) && NotNegative(options.MinHoldRealSeconds) && NotNegative(options.MarginStops)))
                return NetworkResult.BadNumber;
            return NetworkResult.Ok;
        }

        static bool NotNegative(double v) => Vec3d.Finite(v) && v >= 0;

        string Key(NetworkVantage v)
        {
            switch (v.Kind)
            {
                case SiteKind.Vessel: return "vessel:" + v.PersistentId;
                case SiteKind.Surface:
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "surface:{0}:{1:F5}:{2:F5}", BodyNames[v.BodyIndex], v.Latitude, v.Longitude);
                default: return v.Name;
            }
        }

        static NetworkOptions Copy(NetworkOptions o) => new NetworkOptions
        {
            MinHoldUt = o.MinHoldUt, MinHoldRealSeconds = o.MinHoldRealSeconds, MarginStops = o.MarginStops,
        };

        static NetworkVantageStatus Copy(NetworkVantageStatus s) => new NetworkVantageStatus
        {
            SiteKey = s.SiteKey, Label = s.Label, SiteKind = s.SiteKind, VantagePersistentId = s.VantagePersistentId,
            Active = s.Active, Fallback = s.Fallback, State = s.State, Score = s.Score,
        };

        public IReadOnlyList<CommandFeed> Feeds()
        {
            kc.Enter("Feeds", ApiFeature.CommandFeeds);
            IReadOnlyDictionary<string, string> bound;
            lock (kc.Gate) bound = bindings;
            return feeds.ConvertAll(f =>
            {
                var c = Copy(f);
                c.Scope = bound != null && c.SiteKey != null && bound.TryGetValue(c.SiteKey, out var scope) ? scope : null;
                c.FollowsActiveVessel = !aims.ContainsKey(c.FlightId);
                return c;
            });
        }

        public IReadOnlyList<GroundStationInfo> GroundStations()
        {
            kc.Enter("GroundStations", ApiFeature.GroundStations);
            return stations.ConvertAll(s => new GroundStationInfo
            {
                NodeName = s.NodeName, DisplayName = s.DisplayName, BodyIndex = s.BodyIndex, Latitude = s.Latitude,
                Longitude = s.Longitude, Altitude = s.Altitude, Enabled = s.Enabled,
            });
        }

        public LineOfSightResult LineOfSight(NetworkVantage vantage, double ut, int bodyIndex, Vec3d position, double radiusM = double.NaN)
        {
            kc.Enter("LineOfSight", ApiFeature.LineOfSight);
            kc.Record($"LineOfSight {vantage?.ToString() ?? "null"} {ut} {bodyIndex} {position}");
            return vantage == null ? new LineOfSightResult(false, false, null, false, double.NaN) : sight;
        }

        public IReadOnlyList<CommandTarget> Targets(uint flightId)
        {
            kc.Enter("Targets", ApiFeature.CommandFeeds);
            return targets.TryGetValue(flightId, out var list) ? list.ConvertAll(Copy) : new List<CommandTarget>();
        }

        public IWorldModelPublisher AttachWorldModel(string owner)
        {
            kc.Enter("AttachWorldModel", ApiFeature.WorldModel);
            kc.Record($"AttachWorldModel {owner}");
            lock (kc.Gate)
            {
                if (lockOwner == null || lockOwner != owner) return null;
                publisher?.Retire();
                publisher = new RecordingPublisher(kc, this, owner, scene, tenure);
                Heartbeats++;
                ReturnedToTruth = false;
                return publisher;
            }
        }

        /* The state a lock tenure carries ends with it. */
        void EndTenure()
        {
            aims.Clear();
            optics.Clear();
            Heartbeats = 0;
            LightSpeed = null;
            LockOptions = LockOptions.None;
            SilentText = null;
            ReturnedToTruth = false;
        }

        /* Called under the gate. */
        internal PublishResult Admit(RecordingPublisher from, WorldModelBuilder builder, out string error)
        {
            error = null;
            if (scene != from.Scene)
            {
                error = "the scene changed since the publisher attached";
                return PublishResult.SceneChanged;
            }
            if (!from.Live || lockOwner != from.Owner || tenure != from.Tenure)
            {
                error = $"{from.Owner} does not hold the lock";
                return PublishResult.NotLockHolder;
            }
            if (builder.Sequence <= from.LastAcceptedSequence)
            {
                error = $"sequence {builder.Sequence} is not after {from.LastAcceptedSequence}";
                return PublishResult.OutOfOrder;
            }
            if (hasEpoch && builder.Epoch < lastEpoch)
            {
                error = $"epoch {builder.Epoch} is older than {lastEpoch}";
                return PublishResult.OlderEpoch;
            }
            if (builder.FirstError != null)
            {
                error = builder.FirstError;
                return PublishResult.Invalid;
            }
            var snapshot = new PublishedSnapshot(builder);
            published.Add(snapshot);
            bindings = snapshot.SiteBindings;
            lastEpoch = builder.Epoch;
            hasEpoch = true;
            Heartbeats++;
            return PublishResult.Accepted;
        }

        /* Called under the gate. */
        internal bool Holds(RecordingPublisher from) => scene == from.Scene && tenure == from.Tenure && lockOwner == from.Owner;

        /* Called under the gate, from the holder's live publisher only. */
        internal void MarkReturnedToTruth() => ReturnedToTruth = true;

        static CommandFeed Copy(CommandFeed f) => new CommandFeed
        {
            FlightId = f.FlightId, DisplayName = f.DisplayName, SiteKey = f.SiteKey, SiteKind = f.SiteKind,
            VantagePersistentId = f.VantagePersistentId, Scope = f.Scope, SubjectPersistentId = f.SubjectPersistentId,
            Created = f.Created, Network = f.Network, FollowsActiveVessel = f.FollowsActiveVessel,
        };

        static CommandTarget Copy(CommandTarget t) => new CommandTarget
        {
            PersistentId = t.PersistentId, Name = t.Name, Kind = t.Kind, RangeM = t.RangeM, Eligibility = t.Eligibility,
            EligibilityText = t.EligibilityText, RefusalCode = t.RefusalCode, RefusalText = t.RefusalText, Ut = t.Ut,
        };
    }

    /** <summary>The recorder's publisher. Safe on any thread, as the real one is.</summary> */
    public sealed class RecordingPublisher : IWorldModelPublisher
    {
        readonly RecordingKerbcast kerbcast;
        readonly RecordingCommand command;
        bool disposed;
        bool detached;
        bool retired;

        internal readonly string Owner;
        internal readonly int Scene;
        internal readonly int Tenure;

        internal RecordingPublisher(RecordingKerbcast kerbcast, RecordingCommand command, string owner, int scene, int tenure)
        {
            Tenure = tenure;
            this.kerbcast = kerbcast;
            this.command = command;
            Owner = owner;
            Scene = scene;
            LastAcceptedSequence = long.MinValue;
        }

        /** <summary>The last accepted sequence, long.MinValue before the first.</summary> */
        public long LastAcceptedSequence { get; private set; }

        public bool Disposed
        {
            get { lock (kerbcast.Gate) return disposed; }
        }

        public bool Attached
        {
            get { lock (kerbcast.Gate) return Live && command.Holds(this); }
        }

        internal bool Live => !disposed && !detached && !retired;

        public WorldModelBuilder Begin(long sequence, long epoch, double knowledgeUt)
        {
            kerbcast.Record($"Begin {sequence} {epoch} {knowledgeUt}");
            return new WorldModelBuilder(this, sequence, epoch, knowledgeUt);
        }

        public PublishResult Publish(WorldModelBuilder builder, out string error)
        {
            Own(builder);
            if (!builder.TryConsume()) throw new InvalidOperationException("WorldModelBuilder: already published or abandoned");
            kerbcast.Record($"Publish {builder.Sequence}");
            lock (kerbcast.Gate)
            {
                var result = command.Admit(this, builder, out error);
                if (result == PublishResult.Accepted) LastAcceptedSequence = builder.Sequence;
                return result;
            }
        }

        public void Abandon(WorldModelBuilder builder)
        {
            Own(builder);
            if (!builder.TryConsume()) throw new InvalidOperationException("WorldModelBuilder: already published or abandoned");
            kerbcast.Record($"Abandon {builder.Sequence}");
        }

        /** <summary>Only the holder's live publisher in this scene changes anything; a retired, disposed or earlier-scene one is ignored.</summary> */
        public void Detach(bool returnToTruth)
        {
            kerbcast.Record($"Detach {returnToTruth}");
            lock (kerbcast.Gate)
            {
                if (!Live || !command.Holds(this)) return;
                detached = true;
                if (returnToTruth) command.MarkReturnedToTruth();
            }
        }

        public void Dispose()
        {
            lock (kerbcast.Gate)
            {
                if (disposed) return;
                disposed = true;
            }
            kerbcast.Record("Dispose publisher");
        }

        internal void Retire() => retired = true;

        void Own(WorldModelBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (!ReferenceEquals(builder.Publisher, this)) throw new ArgumentException("the builder came from another publisher", nameof(builder));
            if (!builder.IsLive) throw new InvalidOperationException("WorldModelBuilder: already published or abandoned");
        }
    }
}
