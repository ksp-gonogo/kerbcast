using System;
using System.Collections.Generic;
using System.IO;
using Kerbcast.Api;
using UnityEngine;

namespace Kerbcast.DevProvider
{
    /*
     * The development world-model provider: KSP's own truth, delayed by the
     * time its light takes to reach the home world, published through
     * Kerbcast.API exactly as another mod would publish its own. It is the
     * test rig for the world model and an example for other mods.
     *
     * It runs only when GameData/Kerbcast/PluginData/dev exists and Kerbcast's
     * settings set devLightSpeedMps (read as Kerbcast reads them: the shipped
     * settings.cfg, then PluginData's on top), and this assembly ships in no
     * release. devDelaySeconds fixes the delay instead of deriving it from
     * distance; devPublishHz sets how often it publishes (2 by default).
     *
     * It does not set the light speed: Kerbcast's own dev switch applies the
     * same devLightSpeedMps for the whole session, and a speed set through
     * the lock would end, and clear the light-time history, every time the
     * lock is released at a scene change.
     *
     * Under its lock Kerbcast's default aim names no craft, so it aims every
     * command feed itself at the craft the default would watch (DefaultAims).
     * Once the lock goes it stops publishing and aiming (LockTenure) and takes
     * the lock again only when the next scene starts.
     *
     * The game going back (a quickload or revert) shows only as UT going back,
     * since KSP announces a load before it sets the loaded UT; a different
     * save shows as a different save folder or seed. Either rewinds or
     * replaces the history and moves to a new epoch.
     */
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class DevProviderAddon : MonoBehaviour
    {
        private const string Owner = "kerbcast-dev-provider";
        private const string Tag = "[Kerbcast][DEV]";
        private const string SilentText = "Development provider silent";
        /* A craft whose delta-v KSP has not reckoned yet is bounded generously, so its uncertainty never understates. */
        private const double UnknownDeltaV = 20000;

        /* Kept across scenes, so a craft's history outlives a scene change. */
        private static DelayedTruth _truth;
        private static long _epoch;
        private static bool _identityHooked;
        private static bool _lockLostHooked;
        /* The addon of this scene, for the process-long LockLost subscription. */
        private static DevProviderAddon _current;
        private static string _gameIdentity;

        private double _lightSpeed;
        private double? _fixedDelay;
        private float _publishInterval = 0.5f;
        private float _nextPublish;
        private float _nextFailureLog;
        private long _sequence;
        private IWorldModelPublisher _publisher;
        private bool _running;
        private readonly LockTenure _tenure = new LockTenure(Owner);
        private readonly List<CraftSample> _scratch = new List<CraftSample>();
        private readonly List<string> _sites = new List<string>();
        private readonly DefaultAims _aims = new DefaultAims();
        private readonly List<(uint Feed, uint Craft)> _due = new List<(uint, uint)>();
        private readonly Dictionary<uint, (uint Craft, AimResult Result)> _aimLogged = new Dictionary<uint, (uint, AimResult)>();
        private string _targetName = "";
        private bool _vesselAnchor;

        private void Start()
        {
            if (!ReadSwitch()) return;
            var api = KerbcastApi.Instance;
            if (api == null || !KerbcastApi.Supports(ApiFeature.CommandLock) || !KerbcastApi.Supports(ApiFeature.WorldModel))
            {
                Debug.LogWarning($"{Tag} Kerbcast.API without the lock and world model here; not starting");
                return;
            }
            if (_truth == null) _truth = new DelayedTruth(DelayOf);
            CheckTimeline(Planetarium.GetUniversalTime());
            if (!_identityHooked)
            {
                _identityHooked = true;
                KerbcastApi.Identity += e => _truth?.Identity(e);
            }
            if (!_lockLostHooked)
            {
                _lockLostHooked = true;
                KerbcastApi.LockLost += e => _current?.OnLockLost(e);
            }
            var result = api.Command.TakeLock(Owner, LockOptions.WorldModelRequired, SilentText);
            if (result != LockResult.Taken && result != LockResult.Renewed)
            {
                Debug.LogWarning($"{Tag} command lock not taken ({result}); not starting");
                return;
            }
            _publisher = api.Command.AttachWorldModel(Owner);
            if (_publisher == null)
            {
                /* Holding the lock with nothing publishing would blank every feed. */
                api.Command.ReleaseLock(Owner);
                Debug.LogWarning($"{Tag} no publisher for {Owner}; lock released, not starting");
                return;
            }
            _running = true;
            _current = this;
            _tenure.Took();
            Debug.Log($"{Tag} publishing truth delayed at {_lightSpeed} m/s"
                + (_fixedDelay.HasValue ? $" (fixed {_fixedDelay.Value} s)" : "") + $", every {_publishInterval:F2} s");
        }

        private void Update()
        {
            if (!_running || Time.realtimeSinceStartup < _nextPublish) return;
            _nextPublish = Time.realtimeSinceStartup + _publishInterval;
            var api = KerbcastApi.Instance;
            if (api == null) return;
            WorldModelBuilder b = null;
            try
            {
                double now = Planetarium.GetUniversalTime();
                CheckTimeline(now);
                _scratch.Clear();
                ReadTruth(_scratch);
                if (_truth.Sample(now, _scratch)) _epoch++;
                _sites.Clear();
                foreach (var f in api.Command.Feeds()) _sites.Add(f.SiteKey);
                b = _publisher.Begin(++_sequence, _epoch, now);
                _truth.Fill(b, now, _sites);
                var builder = b;
                b = null;
                var published = _publisher.Publish(builder, out string error);
                if (_tenure.Refused(published))
                {
                    /* A no-op unless a stale tenure still holds the lock; release it so feeds return to truth. */
                    api.Command.ReleaseLock(Owner);
                    Stop($"publish {_sequence} refused: {published} {error}");
                    return;
                }
                if (published != PublishResult.Accepted) Failed($"publish {_sequence} refused: {published} {error}");
                _truth.Prune(now);
                AimFeeds(api);
            }
            catch (Exception ex)
            {
                if (b != null)
                {
                    try { _publisher.Abandon(b); }
                    catch (InvalidOperationException) { /* already published or abandoned */ }
                }
                Failed($"publish failed: {ex}");
            }
        }

        private void OnDestroy()
        {
            if (_current == this) _current = null;
            if (!_running) return;
            _publisher?.Dispose();
            KerbcastApi.Instance?.Command.ReleaseLock(Owner);
        }

        private void OnLockLost(LockLostEvent lost)
        {
            if (_running && _tenure.Lost(lost)) Stop($"command lock lost ({lost.Reason})");
        }

        /* Without the lock any client may aim, so aiming on would undo theirs. */
        private void Stop(string why)
        {
            _running = false;
            _publisher?.Dispose();
            _publisher = null;
            Debug.Log($"{Tag} {why}; publishing and aiming stop until the next scene takes the lock");
        }

        /* Each feed not watching its default craft is aimed at it (DefaultAims). */
        private void AimFeeds(IKerbcast api)
        {
            uint active = 0, named = 0;
            bool flight = HighLogic.LoadedSceneIsFlight;
            if (flight && FlightGlobals.ActiveVessel != null) active = FlightGlobals.ActiveVessel.persistentId;
            if (!flight || NamedInFlight) named = LatestLaunch(_targetName);
            _aims.Due(api.Command.Feeds(), f => DefaultFor(f, flight, active, named), Time.realtimeSinceStartup, _due);
            foreach (var (flightId, craft) in _due)
            {
                var result = api.Command.SetAim(flightId, Aim.Vessel(craft, allowUncertain: true), Owner);
                if (_aimLogged.TryGetValue(flightId, out var was) && was.Craft == craft && was.Result == result) continue;
                _aimLogged[flightId] = (craft, result);
                Debug.Log($"{Tag} feed {flightId} aimed at craft {craft}: {result}");
            }
        }

        /*
         * Kerbcast's default for a feed: in flight the active craft, except
         * the settings feed on a vessel anchor with CommandCameraTargetName
         * set, which watches that craft; elsewhere the named or latest
         * launched craft.
         */
        private uint DefaultFor(CommandFeed f, bool flight, uint active, uint named) =>
            !flight || (!f.Created && NamedInFlight) ? named : active;

        private bool NamedInFlight => _vesselAnchor && _targetName.Length > 0;

        /* The latest launched craft Kerbcast's default may pick, preferring one named `name`. */
        private static uint LatestLaunch(string name)
        {
            var craft = new List<(uint, string, double)>();
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
                foreach (var v in vessels)
                    if (v != null && Watchable(v.vesselType)) craft.Add((v.persistentId, v.vesselName, v.launchTime));
            if (craft.Count == 0)
            {
                /* As Kerbcast counts the save's records: not in flight once the scene has built its craft, nor one destroyed. */
                bool authoritative = HighLogic.LoadedSceneIsFlight && vessels != null && vessels.Count > 0;
                var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
                if (protos != null && !authoritative)
                    foreach (var pv in protos)
                        if (pv != null && Watchable(pv.vesselType) && !(!ReferenceEquals(pv.vesselRef, null) && pv.vesselRef == null))
                            craft.Add((pv.persistentId, pv.vesselName, pv.launchTime));
            }
            return DefaultAims.Latest(craft, name);
        }

        /* At most one warning every few seconds, so a lasting fault does not flood the log. */
        private void Failed(string message)
        {
            if (Time.realtimeSinceStartup < _nextFailureLog) return;
            _nextFailureLog = Time.realtimeSinceStartup + 10f;
            Debug.LogWarning($"{Tag} {message}");
        }

        /* A different save replaces the history; the game going back rewinds it. Both are a new epoch. */
        private static void CheckTimeline(double now)
        {
            var game = HighLogic.CurrentGame;
            string identity = game != null ? HighLogic.SaveFolder + "/" + game.Seed : null;
            if (identity != null && _gameIdentity != null && identity != _gameIdentity)
            {
                _truth = new DelayedTruth(_truth.DelayFunction);
                _epoch++;
                Debug.Log($"{Tag} a different game: history replaced");
            }
            if (identity != null) _gameIdentity = identity;
            if (!double.IsNaN(_truth.LatestUt) && now < _truth.LatestUt)
            {
                _truth.Rewind(now);
                _epoch++;
                Debug.Log($"{Tag} the game went back to {now:F1}: history after it forgotten");
            }
        }

        /* Light time from the home world, or the fixed delay. */
        private double DelayOf(CraftSample s) => _fixedDelay ?? s.DistanceM / _lightSpeed;

        /* From the home world's centre plus its radius to `at`: no site on the home world is farther, so no home lens hears it sooner. */
        private static double FromHome(Vector3d at)
        {
            var home = FlightGlobals.GetHomeBody();
            return home == null ? 0 : (at - home.position).magnitude + home.Radius;
        }

        /* Every craft as KSP has it now: live vessels, then saved records for any a scene has not built. */
        private static void ReadTruth(List<CraftSample> into)
        {
            var seen = new HashSet<uint>();
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                foreach (var v in vessels)
                {
                    if (v == null || v.state == Vessel.State.DEAD || !Listed(v.vesselType) || v.mainBody == null) continue;
                    if (!seen.Add(v.persistentId)) continue;
                    var s = new CraftSample
                    {
                        PersistentId = v.persistentId,
                        Name = v.vesselName,
                        Kind = v.vesselType.ToString(),
                        BodyIndex = v.mainBody.flightGlobalsIndex,
                        MayManoeuvre = v.loaded && !v.packed,
                        DistanceM = FromHome(v.GetWorldPos3D()),
                    };
                    if (s.MayManoeuvre)
                    {
                        /* The most it could burn: every engine at full thrust over its mass, and its delta-v left. */
                        double thrustKn = 0;
                        foreach (var p in v.parts)
                            if (p != null)
                                foreach (var m in p.Modules)
                                    if (m is ModuleEngines e) thrustKn += e.maxThrust;
                        s.AccelMps2 = v.totalMass > 0 ? thrustKn / v.totalMass : 0;
                        var dv = v.VesselDeltaV;
                        s.DeltaVMps = dv != null && dv.IsReady ? Math.Max(0, (double)dv.TotalDeltaVActual) : UnknownDeltaV;
                    }
                    if (v.LandedOrSplashed)
                    {
                        s.OnSurface = true;
                        s.Latitude = v.latitude;
                        s.Longitude = v.longitude;
                        s.Altitude = v.altitude;
                    }
                    else if (v.orbit != null) Elements(v.orbit, ref s);
                    else continue;
                    into.Add(s);
                }
            }
            var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protos == null) return;
            foreach (var pv in protos)
            {
                if (pv == null || !Listed(pv.vesselType) || pv.orbitSnapShot == null || !seen.Add(pv.persistentId)) continue;
                var s = new CraftSample
                {
                    PersistentId = pv.persistentId,
                    Name = pv.vesselName,
                    Kind = pv.vesselType.ToString(),
                    BodyIndex = pv.orbitSnapShot.ReferenceBodyIndex,
                };
                try { s.DistanceM = FromHome(pv.orbitSnapShot.Load().getPositionAtUT(Planetarium.GetUniversalTime())); }
                catch (Exception) { continue; }
                if (pv.situation == Vessel.Situations.LANDED || pv.situation == Vessel.Situations.SPLASHED
                    || pv.situation == Vessel.Situations.PRELAUNCH)
                {
                    s.OnSurface = true;
                    s.Latitude = pv.latitude;
                    s.Longitude = pv.longitude;
                    s.Altitude = pv.altitude;
                }
                else
                {
                    var o = pv.orbitSnapShot;
                    s.Inclination = o.inclination;
                    s.Eccentricity = o.eccentricity;
                    s.SemiMajorAxis = o.semiMajorAxis;
                    s.Lan = o.LAN;
                    s.ArgumentOfPeriapsis = o.argOfPeriapsis;
                    s.MeanAnomalyAtEpoch = o.meanAnomalyAtEpoch;
                    s.Epoch = o.epoch;
                }
                into.Add(s);
            }
        }

        private static void Elements(Orbit o, ref CraftSample s)
        {
            s.Inclination = o.inclination;
            s.Eccentricity = o.eccentricity;
            s.SemiMajorAxis = o.semiMajorAxis;
            s.Lan = o.LAN;
            s.ArgumentOfPeriapsis = o.argumentOfPeriapsis;
            s.MeanAnomalyAtEpoch = o.meanAnomalyAtEpoch;
            s.Epoch = o.epoch;
        }

        /* The craft Kerbcast's default may pick: no debris, asteroids, flags, kerbals or ground parts. */
        private static bool Watchable(VesselType t) =>
            Listed(t) && t != VesselType.Debris && t != VesselType.SpaceObject && t != VesselType.EVA
            && t != VesselType.DeployedScienceController;

        private static bool Listed(VesselType t) =>
            t != VesselType.Flag && t != VesselType.Unknown && t != VesselType.DroppedPart
            && t != VesselType.DeployedSciencePart && t != VesselType.DeployedGroundPart;

        /* The dev file, and devLightSpeedMps in Kerbcast's settings. */
        private bool ReadSwitch()
        {
            string root = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "Kerbcast");
            if (!File.Exists(Path.Combine(root, "PluginData", "dev"))) return false;
            var shipped = ConfigNode.Load(Path.Combine(root, "settings.cfg"))?.GetNode("Settings");
            var user = ConfigNode.Load(Path.Combine(root, "PluginData", "settings.cfg"))?.GetNode("Settings");
            if (!Setting(shipped, user, "devLightSpeedMps", out double c) || !(c > 0) || double.IsInfinity(c))
            {
                Debug.LogWarning($"{Tag} the dev file is there but Kerbcast's settings have no positive devLightSpeedMps; not starting");
                return false;
            }
            _lightSpeed = c;
            if (Setting(shipped, user, "devDelaySeconds", out double d) && d >= 0 && !double.IsInfinity(d)) _fixedDelay = d;
            _targetName = (user?.GetValue("CommandCameraTargetName") ?? shipped?.GetValue("CommandCameraTargetName") ?? "").Trim();
            string anchor = user?.GetValue("CommandCameraAnchor") ?? shipped?.GetValue("CommandCameraAnchor") ?? "";
            _vesselAnchor = anchor.Trim().ToLowerInvariant() == "vessel";
            if (Setting(shipped, user, "devPublishHz", out double hz) && hz > 0 && hz <= 50) _publishInterval = (float)(1.0 / hz);
            return true;
        }

        /* A key as Kerbcast's layered settings read it: PluginData's value over the shipped one. */
        private static bool Setting(ConfigNode shipped, ConfigNode user, string key, out double value)
        {
            string raw = user?.GetValue(key) ?? shipped?.GetValue(key);
            return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }
}
