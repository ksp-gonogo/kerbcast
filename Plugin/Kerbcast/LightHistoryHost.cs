using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Kerbcast
{
    /* Feeds the light-time history from the game: samples every craft each
       physics tick through the gate and turns KSP's identity events into ring
       events. Lives for the whole game session on a DontDestroyOnLoad object,
       so scene changes keep the ring. Does nothing while the ring is inactive
       (no reservation and no signal speed), so a player with no lock holder
       pays nothing.

       The ring's frame is the world's axes with the planetarium's rotation
       taken out: KSP turns the world with the body below about 100 km, and
       Inverse(Planetarium.Rotation) undoes that for positions, velocities and
       attitudes alike. Planetarium.Rotation maps it back. */
    [KSPAddon(KSPAddon.Startup.Instantly, once: true)]
    internal sealed class LightHistoryHost : MonoBehaviour
    {
        private const string Tag = "[Kerbcast][HISTORY]";
        private const float TrimIntervalS = 5f;
        private const float LogIntervalS = 60f;
        private const float WarnIntervalS = 60f;
        /* A craft on rails is checked against its last entry this often, in real
           time and in game time, to see orbit changes no event reports
           (Persistent Thrust, HyperEdit, cheats). */
        private const float PackedCheckIntervalS = 1f;
        private const double PackedCheckIntervalUt = 60;
        private const string PrincipiaType = "principia.ksp_plugin_adapter.ExternalInterface";

        internal static LightHistory History { get; } = new LightHistory();

        /* The save holds at most this much history, as base64 text in slices a ConfigNode value carries easily. */
        private const int SaveCapChars = 4 * 1024 * 1024;
        private const int SaveSliceChars = 16 * 1024;
        /* Game time that may pass after a load before the history turns on and still keep the save's history. */
        private const double RestoredGraceUt = 1;
        /* This session's timeline of the ring: written into every save, and a new one after every load. A
           save carrying the current one was written from this ring since its last load, which already holds
           everything in it, so a scene change does not read it back. */
        private static string s_timeline = NewTimeline();

        private readonly Dictionary<uint, Vessel> _lastSeen = new Dictionary<uint, Vessel>();
        private readonly Dictionary<uint, PackedDue> _nextPackedCheck = new Dictionary<uint, PackedDue>();
        private readonly Dictionary<int, BodyTicks> _bodyTicks = new Dictionary<int, BodyTicks>();
        private readonly List<KeyValuePair<uint, double>> _pendingRecover = new List<KeyValuePair<uint, double>>();
        private readonly System.Random _stagger = new System.Random();
        private readonly HashSet<uint> _dirtyComposition = new HashSet<uint>();
        private readonly List<PendingDestroy> _pendingDestroy = new List<PendingDestroy>();
        private readonly Dictionary<string, float> _partRadius = new Dictionary<string, float>();
        private readonly HashSet<uint> _present = new HashSet<uint>();
        private int _bodyCount = -1;
        private bool _principiaChecked;
        private bool _sceneLoading;
        private bool _reconcilePending;
        private static string s_gameIdentity;
        private float _trimCooldown;
        private float _logCooldown = LogIntervalS;
        private float _nextWarn;
        private int _suppressedWarnings;

        private struct PendingDestroy
        {
            public uint Pid;
            public double Ut;
            public int Frame;
            public long Generation;
        }

        private struct PackedDue
        {
            public float Real;
            public double Ut;
        }

        /* A body's last two positions, for a centred velocity at the middle one. */
        private struct BodyTicks
        {
            public int Count;
            public double Ut0, Ut1;
            public DVec3 P0, P1;
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            History.Rewound += OnRewound;
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
            GameEvents.onGameStatePostLoad.Add(OnGameStatePostLoad);
            GameEvents.onVesselGoOnRails.Add(OnGoOnRails);
            GameEvents.onVesselGoOffRails.Add(OnGoOffRails);
            GameEvents.onVesselLoaded.Add(OnLoaded);
            GameEvents.onVesselUnloaded.Add(OnUnloaded);
            GameEvents.onVesselWasModified.Add(OnModified);
            GameEvents.onVesselPersistentIdChanged.Add(OnPersistentIdChanged);
            GameEvents.onCrewBoardVessel.Add(OnBoard);
            GameEvents.onCrewOnEva.Add(OnEva);
            GameEvents.onVesselsUndocking.Add(OnUndocking);
            GameEvents.onPartDeCoupleNewVesselComplete.Add(OnDecoupled);
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onVesselWillDestroy.Add(OnWillDestroy);
            GameEvents.onVesselRecovered.Add(OnRecovered);
            GameEvents.onVesselTerminated.Add(OnTerminated);
            ApplyDevSignalSpeed();
        }

        /* settings.cfg's devSignalSpeedMps, honoured only beside the dev marker
           file, never shipped in a release; without the marker the key is not even read. */
        private static void ApplyDevSignalSpeed()
        {
            var marker = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "Kerbcast", "PluginData", "dev");
            if (!File.Exists(marker)) return;
            double mps = KerbcastSettings.Load().DevSignalSpeedMps;
            if (!(mps > 0) || double.IsInfinity(mps))
            {
                Debug.Log($"{Tag} dev marker {marker} present, devSignalSpeedMps not set: no signal speed");
                return;
            }
            History.SetSignalSpeed(mps);
            Debug.LogWarning($"{Tag} DEV SIGNAL SPEED ON: {mps} m/s from devSignalSpeedMps, because {marker} exists. "
                + "Command cameras draw distant unloaded craft where their arriving light left them. Delete the marker for normal play.");
        }

        private void OnDestroy()
        {
            History.Rewound -= OnRewound;
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
            GameEvents.onGameStatePostLoad.Remove(OnGameStatePostLoad);
            GameEvents.onVesselGoOnRails.Remove(OnGoOnRails);
            GameEvents.onVesselGoOffRails.Remove(OnGoOffRails);
            GameEvents.onVesselLoaded.Remove(OnLoaded);
            GameEvents.onVesselUnloaded.Remove(OnUnloaded);
            GameEvents.onVesselWasModified.Remove(OnModified);
            GameEvents.onVesselPersistentIdChanged.Remove(OnPersistentIdChanged);
            GameEvents.onCrewBoardVessel.Remove(OnBoard);
            GameEvents.onCrewOnEva.Remove(OnEva);
            GameEvents.onVesselsUndocking.Remove(OnUndocking);
            GameEvents.onPartDeCoupleNewVesselComplete.Remove(OnDecoupled);
            GameEvents.onPartDie.Remove(OnPartDie);
            GameEvents.onVesselWillDestroy.Remove(OnWillDestroy);
            GameEvents.onVesselRecovered.Remove(OnRecovered);
            GameEvents.onVesselTerminated.Remove(OnTerminated);
        }

        /* In a scene with vessels and bodies, and the ring is on. */
        private bool Ready()
        {
            if (!History.Active || _sceneLoading || Planetarium.fetch == null || FlightGlobals.fetch == null) return false;
            var scene = HighLogic.LoadedScene;
            if (scene == GameScenes.FLIGHT) return FlightGlobals.ready;
            return (scene == GameScenes.SPACECENTER || scene == GameScenes.TRACKSTATION)
                && FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0;
        }

        private static double Ut => Planetarium.GetUniversalTime();

        private static DQuat DeRotation()
        {
            var r = Planetarium.Rotation;
            return new DQuat(r.x, r.y, r.z, r.w).Conjugate;
        }

        private static DVec3 D(Vector3d v) => new DVec3(v.x, v.y, v.z);
        private static DQuat Q(Quaternion q) => new DQuat(q.x, q.y, q.z, q.w);

        private void FixedUpdate()
        {
            if (History.RestoredUt.HasValue && !History.Active && Planetarium.fetch != null && Ut > History.RestoredUt.Value + RestoredGraceUt)
            {
                Debug.Log($"{Tag} game time passed with the history off: the save's history dropped");
                History.DropRestored();
            }
            if (!Ready()) return;
            if (!KerbcastSettings.EnableTelemetry)
            {
                Tick();
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Tick();
            Telemetry.RingTick.Record(Telemetry.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t0));
        }

        /* One physics tick's sampling: every craft, any body off its Kepler path, and the compositions that changed. */
        private void Tick()
        {
            double ut = Ut;
            CatchUp(ut);
            EnsureBodies();
            if (_pendingRecover.Count > 0)
            {
                foreach (var kv in _pendingRecover) History.Recovered(kv.Key, kv.Value);
                _pendingRecover.Clear();
            }
            if (_reconcilePending) Reconcile(ut);
            var deRot = DeRotation();
            float now = Time.realtimeSinceStartup;

            /* Each body is stamped a tick back. */
            if (History.SampleEverything) SampleBodies(ut, deRot);

            var vessels = FlightGlobals.Vessels;
            for (int i = 0; i < vessels.Count; i++)
            {
                var v = vessels[i];
                if (v == null || v.state == Vessel.State.DEAD || v.mainBody == null) continue;
                try { SampleVessel(v, ut, deRot, now); }
                catch (Exception ex) { Warn($"sample of '{v.vesselName}' failed: {ex.Message}"); }
            }

            if (_dirtyComposition.Count > 0)
            {
                foreach (var pid in _dirtyComposition)
                    if (FlightGlobals.PersistentVesselIds.TryGetValue(pid, out var v) && v != null) TakeComposition(v, ut);
                _dirtyComposition.Clear();
            }
        }

        private void Update()
        {
            /* A destroy waits for the next frame, so a recovery or a merge fired
               with it, or from the GUI later in the same frame, names it instead. */
            if (_pendingDestroy.Count > 0 && Ready())
            {
                /* After a load Update can come before any FixedUpdate: rewind
                   first, and the ring drops destroys from the old timeline. */
                CatchUp(Ut);
                int frame = Time.frameCount;
                for (int i = _pendingDestroy.Count - 1; i >= 0; i--)
                {
                    var d = _pendingDestroy[i];
                    if (d.Frame >= frame) continue;
                    History.Destroyed(d.Pid, d.Ut, d.Generation);
                    _pendingDestroy.RemoveAt(i);
                }
            }

            _trimCooldown -= Time.unscaledDeltaTime;
            if (_trimCooldown <= 0 && Ready())
            {
                _trimCooldown = TrimIntervalS;
                History.Trim(Ut);
            }
            _logCooldown -= Time.unscaledDeltaTime;
            if (_logCooldown <= 0 && History.Active && History.TrackCount > 0)
            {
                _logCooldown = LogIntervalS;
                Debug.Log($"{Tag} tracks={History.TrackCount} entries={History.EntryTotal} bytes={History.Bytes} "
                    + $"horizon={History.HorizonS:F0}s floor={History.FloorUt:F1} principia={History.SampleEverything}");
            }
        }

        private static void CatchUp(double ut)
        {
            if (ut >= History.LatestUt - 1e-6) return;
            Debug.Log($"{Tag} time went back from {History.LatestUt:F2} to {ut:F2}: history after it dropped");
            History.Rewind(ut);
        }

        private void Warn(string message)
        {
            if (Time.realtimeSinceStartup < _nextWarn) { _suppressedWarnings++; return; }
            _nextWarn = Time.realtimeSinceStartup + WarnIntervalS;
            string more = _suppressedWarnings > 0 ? $" ({_suppressedWarnings} more since the last)" : "";
            _suppressedWarnings = 0;
            Debug.LogWarning($"{Tag} {message}{more}");
        }

        /* A vessel object already seen under a track that has ended is the
           ended craft lingering for a tick; any other object under that id is
           a new craft (a relaunch from the same .craft file) and gets a fresh track. */
        private bool Lingering(Vessel v)
        {
            var t = History.Track(LightHistory.CraftKey(v.persistentId));
            return t != null && t.Ended && _lastSeen.TryGetValue(v.persistentId, out var seen) && ReferenceEquals(seen, v);
        }

        private void SampleVessel(Vessel v, double ut, DQuat deRot, float now)
        {
            if (Lingering(v)) return;
            uint pid = v.persistentId;
            long key = LightHistory.CraftKey(pid);
            _lastSeen[pid] = v;

            bool known = History.LastBody(key, out int lastBody);
            int body = v.mainBody.flightGlobalsIndex;
            if (known && lastBody != body && v.packed)
            {
                /* Under warp the tick that sees the change can come long after it. */
                double at = Math.Min(Math.Max(v.orbit.StartUT, History.Track(key).Last.Ut), ut);
                History.BodyChanged(key, StateOf(v, at, deRot), ut);
                return;
            }

            bool gated = !v.packed || History.SampleEverything;
            if (!gated && known)
            {
                if (!_nextPackedCheck.TryGetValue(pid, out var due))
                {
                    /* Staggered, so a save's craft do not all fall due on one tick. */
                    float offset = (float)_stagger.NextDouble() * PackedCheckIntervalS;
                    _nextPackedCheck[pid] = new PackedDue { Real = now + offset, Ut = ut + offset / PackedCheckIntervalS * PackedCheckIntervalUt };
                    return;
                }
                if (now < due.Real && ut < due.Ut) return;
                _nextPackedCheck[pid] = new PackedDue { Real = now + PackedCheckIntervalS, Ut = ut + PackedCheckIntervalUt };
                gated = true;
            }

            bool added = History.Sample(key, StateOf(v, ut, deRot), gated, checkAttitude: !v.packed);
            /* Fuel moves the centre of mass without any part event; checked only when the gate takes an entry. */
            if (added && v.loaded && !v.packed)
            {
                var com = v.localCoM;
                History.MoveCom(pid, ut, com.x, com.y, com.z);
            }
            var track = History.Track(key);
            if (track != null && track.Compositions.Count == 0) TakeComposition(v, ut);
        }

        /* The centre of mass about the root part of a craft not in the scene, as its save holds it. */
        private static Vector3 PackedCoM(Vessel v) => v.protoVessel != null ? v.protoVessel.CoM : v.localCoM;

        private static HistoryState StateOf(Vessel v, double ut, DQuat deRot)
        {
            var body = v.mainBody;
            var s = new HistoryState { Ut = ut, Body = body.flightGlobalsIndex };
            Quaternion rotation;
            if (v.LandedOrSplashed)
            {
                rotation = v.loaded ? v.transform.rotation : body.bodyTransform.rotation * v.srfRelRotation;
                /* A packed craft's coordinates are its root part's; the ring follows the centre of mass, as a loaded craft's does. */
                Vector3d rel = v.loaded
                    ? v.CoMD - body.position
                    : body.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude) + (Vector3d)(rotation * PackedCoM(v)) - body.position;
                s.Kind = HistoryKind.Surface;
                s.Position = deRot.Rotate(D(rel));
                s.AngularVelocity = deRot.Rotate(D(body.angularVelocity));
                s.Velocity = DVec3.Cross(s.AngularVelocity, s.Position);
            }
            else
            {
                var o = v.orbit;
                s.Kind = HistoryKind.Conic;
                s.Position = deRot.Rotate(D(o.getRelativePositionAtUT(ut).xzy));
                s.Velocity = deRot.Rotate(D(o.getOrbitalVelocityAtUT(ut).xzy));
                rotation = v.transform.rotation;
                if (!v.packed && v.ReferenceTransform != null)
                {
                    /* The rigid body's rate is relative to the world, which turns
                       with the main body while inverse rotation is on. */
                    Vector3d world = (Vector3d)(v.ReferenceTransform.rotation * (Vector3)v.angularVelocityD);
                    var main = FlightGlobals.currentMainBody;
                    if (main != null && main.inverseRotation) world += main.angularVelocity;
                    s.AngularVelocity = deRot.Rotate(D(world));
                }
            }
            s.Attitude = (deRot * Q(rotation)).Normalized;
            return s;
        }

        /* Under Principia bodies leave their Kepler orbits, and body.orbit is
           not known to follow them, so a body's state comes from where the game
           has put it: its position about its parent, with a centred velocity.
           That needs the tick after, so each body is sampled one tick back; under
           warp a tick is a long chord, and the gate then adds entries often. */
        private void SampleBodies(double ut, DQuat deRot)
        {
            foreach (var b in FlightGlobals.Bodies)
            {
                if (b == null || b.referenceBody == null || b.referenceBody == b) continue;
                var p = deRot.Rotate(D(b.position - b.referenceBody.position));
                int index = b.flightGlobalsIndex;
                _bodyTicks.TryGetValue(index, out var k);
                if (k.Count > 0 && !(ut > k.Ut1)) continue;
                if (k.Count >= 2)
                {
                    var s = new HistoryState
                    {
                        Ut = k.Ut1,
                        Body = b.referenceBody.flightGlobalsIndex,
                        Kind = HistoryKind.Conic,
                        Position = k.P1,
                        Velocity = (p - k.P0) * (1.0 / (ut - k.Ut0)),
                        Attitude = DQuat.Identity,
                    };
                    History.SampleBody(index, s);
                }
                _bodyTicks[index] = new BodyTicks { Count = Math.Min(k.Count + 1, 2), Ut0 = k.Ut1, P0 = k.P1, Ut1 = ut, P1 = p };
            }
        }

        private void EnsureBodies()
        {
            var list = FlightGlobals.Bodies;
            if (list.Count == _bodyCount) return;
            _bodyCount = list.Count;
            int max = 0;
            foreach (var b in list) if (b != null) max = Math.Max(max, b.flightGlobalsIndex + 1);
            var bodies = new HistoryBody[max];
            for (int i = 0; i < max; i++) bodies[i].Index = -1;
            double reach = 0;
            foreach (var b in list)
            {
                if (b == null) continue;
                bool root = b.orbit == null || b.referenceBody == null || b.referenceBody == b;
                var hb = new HistoryBody
                {
                    Index = b.flightGlobalsIndex,
                    Parent = root ? -1 : b.referenceBody.flightGlobalsIndex,
                    Mu = b.gravParameter,
                    Radius = b.Radius,
                    SoiRadius = root || double.IsNaN(b.sphereOfInfluence) ? double.PositiveInfinity : b.sphereOfInfluence,
                };
                if (!root)
                {
                    var o = b.orbit;
                    hb.MaxOrbitalSpeed = o.PeR > 0
                        ? Math.Sqrt(b.referenceBody.gravParameter * (1 + o.eccentricity) / o.PeR)
                        : o.orbitalSpeed;
                    /* A planet's reach covers its moons, which stay inside its SOI. */
                    if (b.referenceBody.orbit == null && o.eccentricity < 1)
                        reach = Math.Max(reach, o.ApR + hb.SoiRadius);
                }
                bodies[b.flightGlobalsIndex] = hb;
            }
            History.SetBodies(bodies);
            History.SystemDiameterM = 2 * reach;

            if (!_principiaChecked)
            {
                _principiaChecked = true;
                History.SampleEverything = PrincipiaLoaded();
            }
            Debug.Log($"{Tag} {list.Count} bodies, system diameter {2 * reach / 1e9:F1} Gm, horizon {History.HorizonS:F0} s, "
                + $"memory cap {History.MemoryCapBytes / (1024 * 1024)} MB, principia={History.SampleEverything}");
        }

        /* By its interface type, or failing that by its install folder. */
        private static bool PrincipiaLoaded()
        {
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                if (a == null) continue;
                try { if (a.assembly?.GetType(PrincipiaType, false) != null) return true; }
                catch (Exception) { /* an assembly that cannot list its types is not Principia */ }
                if (a.url != null && a.url.StartsWith("Principia", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string NewTimeline() => Guid.NewGuid().ToString("N");

        /* The ring into a save's scenario node: the slowest speed always, the
           history while the ring has any. A failure saves no history and
           never stops the other scenario modules saving. */
        internal static void SaveTo(ConfigNode node)
        {
            try
            {
                /* A quickload not yet seen by a tick: nothing after now may be written. */
                if (Planetarium.fetch != null) CatchUp(Ut);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                node.AddValue("format", LightHistory.SaveFormat);
                node.AddValue("timeline", s_timeline);
                var slowest = History.SlowestSignalSpeed;
                if (slowest.HasValue) node.AddValue("slowestSignalSpeed", slowest.Value.ToString("R", inv));
                node.AddValue("generation", History.Generation);
                if ((!History.Active && !History.RestoredUt.HasValue) || Planetarium.fetch == null) return;
                double ut = Ut;
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var bytes = History.Save(ut, SaveCapChars / 4 * 3, out double floor);
                if (bytes == null)
                {
                    Debug.LogWarning($"{Tag} history not saved: even its newest instant is over {SaveCapChars / 1024} KB");
                    return;
                }
                string text = LightHistory.ToSaveText(bytes);
                node.AddValue("ut", ut.ToString("R", inv));
                node.AddValue("floor", floor.ToString("R", inv));
                node.AddValue("bytes", bytes.Length);
                node.AddValue("crc", LightHistory.Crc32(bytes));
                for (int i = 0; i < text.Length; i += SaveSliceChars)
                    node.AddValue("d", text.Substring(i, Math.Min(SaveSliceChars, text.Length - i)));
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Debug.Log($"{Tag} saved {History.TrackCount} tracks, {text.Length / 1024} KB of text, from UT {floor:F1} to {ut:F1} in {ms:F1} ms");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} history save threw, no history saved: {ex}");
            }
        }

        /*
         * The ring from a save's scenario node: the save's history as of the
         * load, or none for a save from before the history was saved, so a
         * distant craft is then not drawn until its past instant passes the
         * load. A node this ring wrote is skipped while the ring still holds
         * all of it. Never throws: the other scenario modules still load.
         */
        internal static void LoadFrom(ConfigNode node)
        {
            var game = HighLogic.CurrentGame;
            double loadUt = game?.flightState != null ? game.flightState.universalTime : double.NaN;
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var floats = System.Globalization.NumberStyles.Float;
                if (double.IsNaN(loadUt) && !double.TryParse(node.GetValue("ut"), floats, inv, out loadUt)) loadUt = double.NaN;
                string timeline = node.GetValue("timeline");
                bool sameTimeline = timeline != null && timeline == s_timeline;
                s_timeline = NewTimeline();
                /* A save with no history has no floor: the ring must merely not have changed since. */
                if (!double.TryParse(node.GetValue("floor"), floats, inv, out double floor)) floor = double.PositiveInfinity;
                if (sameTimeline && long.TryParse(node.GetValue("generation"), System.Globalization.NumberStyles.Integer, inv, out long generation)
                    && History.HoldsSave(generation, floor))
                    return;
                if (game != null && GameIdentity(game) != s_gameIdentity)
                {
                    History.Clear();
                    s_gameIdentity = GameIdentity(game);
                }
                if (double.TryParse(node.GetValue("slowestSignalSpeed"), floats, inv, out double slowest)) History.RestoreSlowest(slowest);

                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                byte[] bytes = null;
                string error = null;
                var slices = node.GetValues("d");
                if (slices.Length > 0)
                {
                    bytes = LightHistory.FromSaveText(string.Concat(slices));
                    if (!int.TryParse(node.GetValue("bytes"), System.Globalization.NumberStyles.Integer, inv, out int length) || length != bytes.Length)
                        error = $"{bytes.Length} bytes where the save says {node.GetValue("bytes")}";
                    else if (!uint.TryParse(node.GetValue("crc"), System.Globalization.NumberStyles.Integer, inv, out uint crc) || crc != LightHistory.Crc32(bytes))
                        error = "checksum does not match";
                }
                if (error == null) History.Load(bytes, loadUt, out error);
                else History.Load(null, loadUt, out _);
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (error != null) Debug.LogWarning($"{Tag} the save's history could not be read ({error}): starting empty at UT {loadUt:F1}");
                else if (bytes == null) Debug.Log($"{Tag} the save has no history: craft show from their past instants after UT {loadUt:F1}");
                else Debug.Log($"{Tag} loaded {History.TrackCount} tracks, {bytes.Length / 1024} KB, as of UT {loadUt:F1} in {ms:F1} ms");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} history load threw, starting empty: {ex}");
                History.Load(null, loadUt, out _);
            }
            if (game != null) s_gameIdentity = GameIdentity(game);
        }

        /* Another save's craft, and the slowest speed it set, mean nothing here. */
        private static void CheckGameIdentity()
        {
            var game = HighLogic.CurrentGame;
            if (game == null) return;
            string identity = GameIdentity(game);
            if (s_gameIdentity != null && identity != s_gameIdentity)
            {
                Debug.Log($"{Tag} a different game: history cleared");
                History.Clear();
            }
            s_gameIdentity = identity;
        }

        private static string GameIdentity(Game game) => HighLogic.SaveFolder + "/" + game.Seed;

        /* After a load the ring may hold craft the loaded game never had. */
        private void Reconcile(double ut)
        {
            _reconcilePending = false;
            CheckGameIdentity();
            var game = HighLogic.CurrentGame;
            if (game == null) return;
            _present.Clear();
            foreach (var v in FlightGlobals.Vessels) if (v != null) _present.Add(v.persistentId);
            if (game.flightState?.protoVessels != null)
                foreach (var pv in game.flightState.protoVessels) if (pv != null) _present.Add(pv.persistentId);
            int ended = History.EndMissing(_present, ut);
            if (ended > 0) Debug.Log($"{Tag} {ended} craft not in the loaded game ended at UT {ut:F1}");
        }

        private void TakeComposition(Vessel v, double ut)
        {
            PartRecord[] parts;
            Vector3 com;
            if (v.loaded && v.parts != null && v.parts.Count > 0)
            {
                parts = new PartRecord[v.parts.Count];
                for (int i = 0; i < parts.Length; i++)
                {
                    var p = v.parts[i];
                    parts[i] = Record(p.persistentId, p.partInfo, p.orgPos, p.orgRot);
                }
                com = v.localCoM;
            }
            else if (v.protoVessel?.protoPartSnapshots != null)
            {
                var pps = v.protoVessel.protoPartSnapshots;
                parts = new PartRecord[pps.Count];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = Record(pps[i].persistentId, pps[i].partInfo, pps[i].position, pps[i].rotation);
                com = v.protoVessel.CoM;
            }
            else return;
            History.SetComposition(v.persistentId, ut, parts, com.x, com.y, com.z);
        }

        private PartRecord Record(uint pid, AvailablePart info, Vector3 pos, Quaternion rot) => new PartRecord
        {
            Pid = pid,
            Name = info?.name,
            Px = pos.x, Py = pos.y, Pz = pos.z,
            Qx = rot.x, Qy = rot.y, Qz = rot.z, Qw = rot.w,
            RadiusM = PartRadius(info),
        };

        /* From the prefab's mesh bounds, once per part type. */
        private float PartRadius(AvailablePart info)
        {
            if (info?.partPrefab == null) return 1f;
            if (_partRadius.TryGetValue(info.name, out float cached)) return cached;
            var root = info.partPrefab.transform;
            float rootScale = Mathf.Max(root.lossyScale.x, Mathf.Max(root.lossyScale.y, root.lossyScale.z));
            double r = 0;
            foreach (var mf in info.partPrefab.GetComponentsInChildren<MeshFilter>(true))
                if (mf != null) r = Math.Max(r, MeshReach(root, rootScale, mf.transform, mf.sharedMesh));
            foreach (var sm in info.partPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (sm != null) r = Math.Max(r, MeshReach(root, rootScale, sm.transform, sm.sharedMesh));
            float radius = (float)Math.Max(r, 0.5);
            _partRadius[info.name] = radius;
            return radius;
        }

        private static double MeshReach(Transform root, float rootScale, Transform t, Mesh mesh)
        {
            if (mesh == null) return 0;
            var s = t.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))) / Mathf.Max(rootScale, 1e-6f);
            return root.InverseTransformPoint(t.TransformPoint(mesh.bounds.center)).magnitude + mesh.bounds.extents.magnitude * scale;
        }

        private void RecordEvent(Vessel v, HistoryReason reason)
        {
            if (v == null || v.mainBody == null || !Ready() || Lingering(v)) return;
            try
            {
                double ut = Ut;
                long key = LightHistory.CraftKey(v.persistentId);
                _lastSeen[v.persistentId] = v;
                History.Record(key, StateOf(v, ut, DeRotation()), reason);
                if (History.Track(key).Compositions.Count == 0) TakeComposition(v, ut);
            }
            catch (Exception ex) { Warn($"{reason} entry for '{v.vesselName}' failed: {ex.Message}"); }
        }

        private void OnGoOnRails(Vessel v) => RecordEvent(v, HistoryReason.OnRails);
        private void OnGoOffRails(Vessel v) => RecordEvent(v, HistoryReason.OffRails);
        private void OnLoaded(Vessel v) => RecordEvent(v, HistoryReason.Load);
        private void OnUnloaded(Vessel v) => RecordEvent(v, HistoryReason.Unload);

        private void OnModified(Vessel v)
        {
            if (v != null && History.Active) _dirtyComposition.Add(v.persistentId);
        }

        /* Fired for a dock or claw merge (absorbed, survivor), and when KSP gives
           a newly added craft a fresh id because its own clashed. Only a survivor
           with history makes it a merge; a fresh id starts its own history. */
        private void OnPersistentIdChanged(uint from, uint to)
        {
            /* A craft arriving under a taken id: the craft that had it keeps its id, its sights and its history. */
            if (VesselIdentity.IsClash(from, to)) return;
            PicturePass.CraftMerged(from, to);
            if (!Ready()) return;
            var survivor = History.Track(LightHistory.CraftKey(to));
            if (survivor == null || survivor.Ended) return;
            History.Merge(from, to, Ut);
            _dirtyComposition.Add(to);
        }

        private void OnBoard(GameEvents.FromToAction<Part, Part> a)
        {
            var kerbal = a.from?.vessel;
            var craft = a.to?.vessel;
            if (kerbal == null || craft == null || !Ready()) return;
            History.Merge(kerbal.persistentId, craft.persistentId, Ut);
            _dirtyComposition.Add(craft.persistentId);
        }

        private void OnEva(GameEvents.FromToAction<Part, Part> a) => Split(a.from?.vessel, a.to?.vessel);
        private void OnUndocking(Vessel from, Vessel to) => Split(from, to);
        private void OnDecoupled(Vessel from, Vessel to) => Split(from, to);

        private void Split(Vessel parent, Vessel child)
        {
            if (parent == null || child == null || !Ready()) return;
            History.Split(parent.persistentId, child.persistentId, Ut);
            _lastSeen[child.persistentId] = child;
            _dirtyComposition.Add(parent.persistentId);
            _dirtyComposition.Add(child.persistentId);
        }

        private void OnPartDie(Part p)
        {
            var v = p?.vessel;
            if (v == null || !Ready()) return;
            History.PartLost(v.persistentId, p.persistentId, Ut);
            _dirtyComposition.Add(v.persistentId);
        }

        private void OnWillDestroy(Vessel v)
        {
            if (v == null || !Ready()) return;
            double ut = Ut;
            CatchUp(ut);
            _pendingDestroy.Add(new PendingDestroy { Pid = v.persistentId, Ut = ut, Frame = Time.frameCount, Generation = History.Generation });
        }

        /* A recovery while a scene loads is kept for the first ready tick, so it
           is not later reconciled as merely removed. */
        private void OnRecovered(ProtoVessel pv, bool quick)
        {
            if (pv == null || !History.Active) return;
            if (Ready()) History.Recovered(pv.persistentId, Ut);
            else if (Planetarium.fetch != null) _pendingRecover.Add(new KeyValuePair<uint, double>(pv.persistentId, Ut));
        }

        private void OnTerminated(ProtoVessel pv)
        {
            if (pv == null || !Ready()) return;
            CatchUp(Ut);
            History.Destroyed(pv.persistentId, Ut, History.Generation);
        }

        private void OnRewound(double ut)
        {
            _bodyTicks.Clear();
            _nextPackedCheck.Clear();
        }

        private void OnGameStatePostLoad(ConfigNode node) => _reconcilePending = true;

        private void OnSceneLoadRequested(GameScenes scene)
        {
            _sceneLoading = true;
            _pendingDestroy.Clear();
            _dirtyComposition.Clear();
            _lastSeen.Clear();
            _nextPackedCheck.Clear();
            _bodyTicks.Clear();
        }

        private void OnLevelReady(GameScenes scene)
        {
            _sceneLoading = false;
            _bodyCount = -1;
            _reconcilePending = true;
            CheckGameIdentity();
        }
    }

    /* The holder's signal speed for code that needs a light time but must not
       read the ring's truth. Null while delay is off. */
    internal static class LightTime
    {
        public static double? SignalSpeed => LightHistoryHost.History.SignalSpeed;
    }
}
