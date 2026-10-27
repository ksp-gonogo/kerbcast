using System;
using System.Collections.Generic;
using Kerbcast.Api;

namespace Kerbcast
{
    /* One craft's record as Kerbcast keeps it, copied out of the builder. */
    internal struct ModelRecord
    {
        public uint PersistentId;
        public CraftFixState State;
        public string Name;
        public string Kind;
        public string RefusalCode;
        public string RefusalText;

        public KinematicsKind Kinematics;
        public int BodyIndex;
        public double ValidFromUt;
        public double ValidUntilUt;

        public double Inclination;
        public double Eccentricity;
        public double SemiMajorAxis;
        public double Lan;
        public double ArgumentOfPeriapsis;
        public double MeanAnomalyAtEpoch;
        public double Epoch;

        public double StateUt;
        public Vec3d Position;
        public Vec3d Velocity;

        public double Latitude;
        public double Longitude;
        public double Altitude;

        public bool HasUncertainty;
        public double UncertaintyRadiusM;
        public double RateMps;
        public double AccelMps2;
        public double SpeedCapMps;

        public bool HasAttitude;
        public double AttitudeUt;
        public Quatd Rotation;
        public Vec3d AngularVelocity;

        /* NaN when the provider gave no size. */
        public double RadiusM;

        /* Where the record sits in its snapshot's array, for the snapshot's per-record caches. */
        public int Index;
    }

    /*
     * A published world model in Kerbcast's own arrays. Each scope's records
     * sit in one range of the array, sorted by persistentId, so a lookup is a
     * binary search. Its records are immutable once the store swaps it in;
     * only the per-record path cache is written after, lazily, on the thread
     * that captures it (the main thread). The store refills a recycled one
     * only after no frame can still be reading it.
     */
    internal sealed class WorldModelSnapshot
    {
        /* Alias chains longer than this are treated as a loop and stop. */
        private const int MaxAliasHops = 16;

        private ModelRecord[] _records = new ModelRecord[16];
        private int _count;
        /* Each conic or state-vector record's path, built on first read on the
           main thread and kept for the snapshot's life. */
        private KeplerPath[] _paths = new KeplerPath[16];
        private PathState[] _pathStates = new PathState[16];

        private enum PathState : byte { NotBuilt, Built, Failed }
        private readonly Dictionary<string, int> _scopeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<int> _scopeStart = new List<int>();
        private readonly List<int> _scopeCount = new List<int>();
        private readonly Dictionary<uint, uint> _aliases = new Dictionary<uint, uint>();
        private readonly Dictionary<string, string> _bindings = new Dictionary<string, string>(StringComparer.Ordinal);

        public long Sequence { get; private set; }
        /* Unique to this fill across every publisher in the store's life, so memos key on it. */
        public long Generation { get; private set; }
        public long Epoch { get; private set; }
        public double KnowledgeUt { get; private set; }

        /* The store's frame counter when this snapshot stopped being current;
           long.MaxValue while it is current. */
        public long RetiredAt;

        /* The thread the store captured this snapshot on, the only one that may fill its path cache. */
        public int ReaderThread = -1;

        public IReadOnlyDictionary<string, string> Bindings => _bindings;
        public int ScopeCount => _scopeStart.Count;
        public int RecordCount => _count;

        /* The store stamps a generation once the copy is accepted. */
        public void Stamp(long generation) => Generation = generation;

        /* Copies a builder with no error in it. */
        public void Fill(WorldModelBuilder builder)
        {
            Generation = 0;
            Sequence = builder.Sequence;
            Epoch = builder.Epoch;
            KnowledgeUt = builder.KnowledgeUt;
            RetiredAt = long.MaxValue;
            _scopeIndex.Clear();
            _scopeStart.Clear();
            _scopeCount.Clear();
            _aliases.Clear();
            _bindings.Clear();

            var records = builder.Records;
            if (_records.Length < records.Count) _records = new ModelRecord[Math.Max(records.Count, _records.Length * 2)];
            _count = records.Count;
            /* Scope may be called only once per key, so each scope's records are contiguous. */
            string scope = null;
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (scope == null || !string.Equals(scope, r.Scope, StringComparison.Ordinal))
                {
                    scope = r.Scope;
                    _scopeIndex[scope] = _scopeStart.Count;
                    _scopeStart.Add(i);
                    _scopeCount.Add(0);
                }
                _scopeCount[_scopeCount.Count - 1]++;
                _records[i] = Copy(r);
            }
            for (int s = 0; s < _scopeStart.Count; s++)
                Array.Sort(_records, _scopeStart[s], _scopeCount[s], ByPersistentId.Instance);
            if (_paths.Length < _records.Length)
            {
                _paths = new KeplerPath[_records.Length];
                _pathStates = new PathState[_records.Length];
            }
            for (int i = 0; i < _count; i++)
            {
                _records[i].Index = i;
                _pathStates[i] = PathState.NotBuilt;
            }
            foreach (var kv in builder.Bindings) _bindings[kv.Key] = kv.Value;
            foreach (var a in builder.Aliases) _aliases[a.Key] = a.Value;
        }

        /* The scope's index, -1 when the snapshot has no records for it. A
           bound scope with no records is still a scope that knows nothing. */
        public int ScopeOf(string key) => key != null && _scopeIndex.TryGetValue(key, out int i) ? i : -1;

        public int CountIn(int scope) => scope >= 0 && scope < _scopeCount.Count ? _scopeCount[scope] : 0;

        public ModelRecord RecordAt(int scope, int i) => _records[_scopeStart[scope] + i];

        public bool TryFind(int scope, uint persistentId, out ModelRecord record)
        {
            record = default;
            if (scope < 0 || scope >= _scopeStart.Count) return false;
            int lo = _scopeStart[scope], hi = lo + _scopeCount[scope] - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                uint pid = _records[mid].PersistentId;
                if (pid == persistentId)
                {
                    record = _records[mid];
                    return true;
                }
                if (pid < persistentId) lo = mid + 1;
                else hi = mid - 1;
            }
            return false;
        }

        /* The persistentId the model now knows `persistentId` as, following its aliases. */
        public uint Resolve(uint persistentId)
        {
            uint pid = persistentId;
            for (int hop = 0; hop < MaxAliasHops && _aliases.TryGetValue(pid, out uint next); hop++) pid = next;
            return pid;
        }

        /*
         * A positioned record's state at `ut` about its body's centre, in KSP's
         * fixed frame (see ZupFrame): a conic or a ballistic state vector on
         * its path, a surface point carried by `body`'s rotation. False when
         * it cannot be placed. Main thread only, since the first read of a
         * record builds its path.
         */
        public bool TryStateAt(in ModelRecord r, in KinematicsBody body, double ut, out DVec3 position, out DVec3 velocity)
        {
            position = velocity = default;
            if (!body.Known) return false;
            if (r.Kinematics == KinematicsKind.Surface)
            {
                position = body.SurfaceAt(r.Latitude, r.Longitude, r.Altitude, ut, out velocity);
                return Kinematics.Finite(position.X) && Kinematics.Finite(position.Y) && Kinematics.Finite(position.Z);
            }
            return TryPath(r, body, out var path) && path.TryAt(ut, out position, out velocity);
        }

        /* The record's conic, built once per fill. */
        public bool TryPath(in ModelRecord r, in KinematicsBody body, out KeplerPath path)
        {
            path = default;
            if (Environment.CurrentManagedThreadId != ReaderThread)
                throw new InvalidOperationException("WorldModelSnapshot.TryPath: only the thread that captured the snapshot may read paths");
            int i = r.Index;
            if (i < 0 || i >= _count || _records[i].PersistentId != r.PersistentId) return false;
            if (_pathStates[i] == PathState.NotBuilt)
            {
                bool built;
                if (r.Kinematics == KinematicsKind.Conic)
                    built = KeplerPath.TryFromElements(r.Inclination, r.Eccentricity, r.SemiMajorAxis, r.Lan,
                        r.ArgumentOfPeriapsis, r.MeanAnomalyAtEpoch, r.Epoch, body.Mu, out _paths[i]);
                else if (r.Kinematics == KinematicsKind.StateVector)
                    built = KeplerPath.TryFromState(new DVec3(r.Position.X, r.Position.Y, r.Position.Z),
                        new DVec3(r.Velocity.X, r.Velocity.Y, r.Velocity.Z), r.StateUt, body.Mu, out _paths[i], body.Radius);
                else built = false;
                _pathStates[i] = built ? PathState.Built : PathState.Failed;
            }
            if (_pathStates[i] != PathState.Built) return false;
            path = _paths[i];
            return true;
        }

        public string ScopeForSite(string siteKey) =>
            siteKey != null && _bindings.TryGetValue(siteKey, out var scope) ? scope : null;

        private static ModelRecord Copy(WorldModelRecord r) => new ModelRecord
        {
            PersistentId = r.PersistentId,
            State = ToState(r.State),
            Name = r.Name,
            Kind = r.Kind,
            RefusalCode = r.RefusalCode,
            RefusalText = r.RefusalText,
            Kinematics = r.Kinematics,
            BodyIndex = r.BodyIndex,
            ValidFromUt = r.ValidFromUt,
            ValidUntilUt = r.ValidUntilUt,
            Inclination = r.Inclination,
            Eccentricity = r.Eccentricity,
            SemiMajorAxis = r.SemiMajorAxis,
            Lan = r.Lan,
            ArgumentOfPeriapsis = r.ArgumentOfPeriapsis,
            MeanAnomalyAtEpoch = r.MeanAnomalyAtEpoch,
            Epoch = r.Epoch,
            StateUt = r.StateUt,
            Position = r.Position,
            Velocity = r.Velocity,
            Latitude = r.Latitude,
            Longitude = r.Longitude,
            Altitude = r.Altitude,
            HasUncertainty = r.HasUncertainty,
            UncertaintyRadiusM = r.UncertaintyRadiusM,
            RateMps = r.RateMps,
            AccelMps2 = r.AccelMps2,
            SpeedCapMps = r.SpeedCapMps,
            HasAttitude = r.HasAttitude,
            AttitudeUt = r.AttitudeUt,
            Rotation = r.Rotation,
            AngularVelocity = r.AngularVelocity,
            RadiusM = r.RadiusM,
        };

        private static CraftFixState ToState(Api.CraftFixState s)
        {
            switch (s)
            {
                case Api.CraftFixState.Refused: return CraftFixState.Refused;
                case Api.CraftFixState.Positioned: return CraftFixState.Positioned;
                case Api.CraftFixState.Listed: return CraftFixState.Listed;
                default: return CraftFixState.Unknown;
            }
        }

        private sealed class ByPersistentId : IComparer<ModelRecord>
        {
            public static readonly ByPersistentId Instance = new ByPersistentId();
            public int Compare(ModelRecord a, ModelRecord b) => a.PersistentId.CompareTo(b.PersistentId);
        }
    }

    internal static class ModelRules
    {
        /* The record's uncertainty radius at `ut` in metres; 0 for a record that gave none. */
        public static double UncertaintyAt(in ModelRecord r, double ut) =>
            r.HasUncertainty ? Kinematics.UncertaintyAt(r.UncertaintyRadiusM, r.RateMps, r.AccelMps2, r.SpeedCapMps, ut - r.ValidFromUt) : 0.0;

        /*
         * A record's state at `ut`, before any position is computed. A
         * positioned record outside its window, or on a body index this game
         * does not have (`bodyKnown`), reads refused with the stale code; refused and
         * listed records read as given.
         */
        public static CraftFixState StateAt(in ModelRecord r, double ut, Func<int, bool> bodyKnown, out string refusalCode)
        {
            refusalCode = r.RefusalCode;
            if (r.State != CraftFixState.Positioned) return r.State;
            if (!(ut >= r.ValidFromUt && ut <= r.ValidUntilUt) || !bodyKnown(r.BodyIndex))
            {
                refusalCode = CommandCameraMath.RefusalStale;
                return CraftFixState.Refused;
            }
            return CraftFixState.Positioned;
        }
    }

    /* What a source holds about one craft before any position is computed. */
    internal struct ModelFix
    {
        public CraftFixState State;
        public uint PersistentId;
        public string Name;
        public string Kind;
        public double RadiusM;
        public string RefusalCode;
        public string RefusalText;
        /* The record and its scope, when there is one. */
        public ModelRecord Record;
        public int Scope;
    }

    /*
     * Reading a frame's view from one site: the bound scope, the model's
     * aliases, then the record's state at a UT. No record, no binding or no
     * snapshot is unknown. A stale view refuses every record it has.
     */
    internal static class ModelLookup
    {
        /* The scope `siteKey` reads in this view, -1 for none: nothing published, an unbound site, or a scope with no records. */
        public static int ScopeFor(in WorldModelView view, string siteKey, out bool bound)
        {
            bound = false;
            if (view.Snapshot == null) return -1;
            string scope = view.Snapshot.ScopeForSite(siteKey);
            bound = scope != null;
            return bound ? view.Snapshot.ScopeOf(scope) : -1;
        }

        /* `ut` NaN asks only whether the craft is known, not where. */
        public static ModelFix Find(in WorldModelView view, int scope, uint persistentId, double ut, Func<int, bool> bodyKnown)
        {
            if (persistentId == 0 || scope < 0 || view.Snapshot == null
                || !view.Snapshot.TryFind(scope, view.Snapshot.Resolve(persistentId), out var r))
                return new ModelFix { State = CraftFixState.Unknown, PersistentId = persistentId, Name = "", Kind = "", RadiusM = double.NaN, Scope = -1 };
            return Of(view, scope, r, ut, bodyKnown);
        }

        public static ModelFix Of(in WorldModelView view, int scope, ModelRecord r, double ut, Func<int, bool> bodyKnown)
        {
            var fix = new ModelFix
            {
                PersistentId = r.PersistentId,
                Name = r.Name ?? "",
                Kind = r.Kind ?? "",
                RadiusM = r.RadiusM,
                RefusalText = r.RefusalText,
                Record = r,
                Scope = scope,
            };
            if (view.Mode == WorldModelMode.Stale)
            {
                fix.State = CraftFixState.Refused;
                fix.RefusalCode = CommandCameraMath.RefusalStale;
                fix.RefusalText = null;
                return fix;
            }
            fix.State = double.IsNaN(ut) && r.State == CraftFixState.Positioned
                ? CraftFixState.Positioned
                : ModelRules.StateAt(r, ut, bodyKnown, out fix.RefusalCode);
            if (fix.State != CraftFixState.Refused) fix.RefusalCode = null;
            return fix;
        }
    }
}
