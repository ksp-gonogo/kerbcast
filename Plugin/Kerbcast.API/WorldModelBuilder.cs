using System;
using System.Collections.Generic;
using System.Threading;

namespace Kerbcast.Api
{
    /**
     * <summary>
     * One world-model snapshot under construction, from <see cref="IWorldModelPublisher.Begin"/>.
     * Methods rather than public structs, so new optional fields never change a layout a caller
     * compiled against. One thread at a time.
     * </summary>
     * <remarks>
     * Records follow the last <see cref="Scope"/> call, at most one per craft per scope. Name, Kind,
     * Uncertainty, Attitude and Radius apply to the record added last. Calling a record method before
     * any Scope, a modifier before any record, or anything after the builder was published or
     * abandoned throws InvalidOperationException; a bad value instead makes Publish return Invalid
     * with the first problem in its error.
     * </remarks>
     */
    public sealed class WorldModelBuilder
    {
        internal readonly object Publisher;
        internal readonly long Sequence;
        internal readonly long Epoch;
        internal readonly double KnowledgeUt;

        BuilderStorage storage;
        string scope;
        int scopeOrdinal = -1;
        WorldModelRecord last;
        volatile bool live = true;
        int consumed;

        internal string FirstError { get; private set; }

        internal WorldModelBuilder(object publisher, long sequence, long epoch, double knowledgeUt)
            : this(publisher, sequence, epoch, knowledgeUt, new BuilderStorage())
        {
        }

        /* `storage` is the builder's alone until Release hands it back. */
        internal WorldModelBuilder(object publisher, long sequence, long epoch, double knowledgeUt, BuilderStorage storage)
        {
            Publisher = publisher;
            Sequence = sequence;
            Epoch = epoch;
            KnowledgeUt = knowledgeUt;
            this.storage = storage;
            storage.Reset();
            if (!Vec3d.Finite(knowledgeUt)) Fail($"knowledgeUt {knowledgeUt} is not finite");
        }

        /** <summary>Starts scope <paramref name="key"/>; records added after this belong to it.</summary> */
        public void Scope(string key)
        {
            Live();
            if (string.IsNullOrEmpty(key))
            {
                Fail("a scope key is empty");
                key = "";
            }
            scope = key;
            last = null;
            if (storage.Scopes.Add(key)) scopeOrdinal++;
            else Fail($"scope '{key}' appears twice");
        }

        /** <summary>Command feeds at <paramref name="siteKey"/> read <paramref name="scope"/>. An unbound site reads every craft as unknown.</summary> */
        public void BindSite(string siteKey, string scope)
        {
            Live();
            if (string.IsNullOrEmpty(siteKey) || string.IsNullOrEmpty(scope))
            {
                Fail("BindSite needs a site key and a scope");
                return;
            }
            if (storage.Bindings.ContainsKey(siteKey)) Fail($"site '{siteKey}' is bound twice");
            storage.Bindings[siteKey] = scope;
        }

        /**
         * <summary>
         * The model holds that one craft is now another, as when it hears of a dock. Each craft is
         * aliased at most once, and aliases may chain but not loop.
         * </summary>
         */
        public void Alias(uint fromPersistentId, uint toPersistentId)
        {
            Live();
            if (fromPersistentId == 0 || toPersistentId == 0 || fromPersistentId == toPersistentId)
            {
                Fail($"alias {fromPersistentId} -> {toPersistentId} is malformed");
                return;
            }
            var map = storage.AliasMap;
            if (map.ContainsKey(fromPersistentId))
            {
                Fail($"craft {fromPersistentId} is aliased twice");
                return;
            }
            for (uint at = toPersistentId; map.TryGetValue(at, out uint next); at = next)
            {
                if (next != fromPersistentId) continue;
                Fail($"alias {fromPersistentId} -> {toPersistentId} closes a loop");
                return;
            }
            map[fromPersistentId] = toPersistentId;
            storage.Aliases.Add(new KeyValuePair<uint, uint>(fromPersistentId, toPersistentId));
        }

        /** <summary>A craft known but not positioned.</summary> */
        public void Listed(uint persistentId) => Add(persistentId, CraftFixState.Listed);

        /** <summary>A craft the model refuses to position, with a stable opaque code and optional text.</summary> */
        public void Refused(uint persistentId, string code, string text = null)
        {
            var r = Add(persistentId, CraftFixState.Refused);
            if (string.IsNullOrEmpty(code)) Fail($"craft {persistentId} is refused without a code");
            r.RefusalCode = code;
            r.RefusalText = text;
        }

        /**
         * <summary>
         * A positioned craft on KSP Orbit elements at <paramref name="epoch"/>, valid between the two UTs:
         * inclination, longitude of the ascending node and argument of periapsis in degrees, semi-major
         * axis in metres (negative for a hyperbola, as KSP keeps it), mean anomaly at epoch in radians,
         * epoch and UTs in seconds, as KSP's Orbit constructor takes them. A parabola (e = 1) is not a
         * conic Kerbcast takes; publish a state vector instead.
         * </summary>
         */
        public void Conic(uint persistentId, int bodyIndex, double inc, double e, double sma, double lan,
            double argPe, double meanAnomalyAtEpoch, double epoch, double validFromUt, double validUntilUt)
        {
            var r = Positioned(persistentId, KinematicsKind.Conic, bodyIndex, validFromUt, validUntilUt);
            r.Inclination = inc;
            r.Eccentricity = e;
            r.SemiMajorAxis = sma;
            r.Lan = lan;
            r.ArgumentOfPeriapsis = argPe;
            r.MeanAnomalyAtEpoch = meanAnomalyAtEpoch;
            r.Epoch = epoch;
            Check(persistentId, "elements", inc, e, sma, lan, argPe, meanAnomalyAtEpoch, epoch);
            if (e < 0) Fail($"craft {persistentId} has a negative eccentricity");
            else if (e == 1) Fail($"craft {persistentId} is on a parabola (e = 1)");
            else if (e < 1 ? !(sma > 0) : !(sma < 0)) Fail($"craft {persistentId} has semi-major axis {sma} for eccentricity {e}");
        }

        /**
         * <summary>
         * A positioned craft by body-relative position and velocity at <paramref name="ut"/>, propagated
         * ballistically under the body's gravity with no thrust.
         * </summary>
         * <remarks>
         * Vectors are z-up in KSP's fixed inertial frame, the one Orbit.OrbitFrame and
         * Orbit.UpdateFromFixedVectors work in. From an Orbit that is always
         * Planetarium.Zup.LocalToWorld(orbit.getRelativePositionAtUT(ut)), and likewise for
         * getOrbitalVelocityAtUT, orbit.pos and orbit.vel. The raw values are not this frame in general:
         * KSP turns Zup while the planetarium inverse-rotates and leaves it turned afterwards. A vector
         * in the fixed frame stays right however the planetarium turns after it is read.
         * </remarks>
         */
        public void StateVector(uint persistentId, int bodyIndex, double ut, Vec3d position, Vec3d velocity,
            double validFromUt, double validUntilUt)
        {
            var r = Positioned(persistentId, KinematicsKind.StateVector, bodyIndex, validFromUt, validUntilUt);
            r.StateUt = ut;
            r.Position = position;
            r.Velocity = velocity;
            Check(persistentId, "state", ut);
            if (!position.IsFinite || !velocity.IsFinite) Fail($"craft {persistentId} has a non-finite state vector");
            else if (position.Magnitude == 0) Fail($"craft {persistentId} has its state vector at the body's centre");
        }

        /** <summary>A positioned craft on the surface (degrees, metres above sea level), carried by the body's rotation.</summary> */
        public void Surface(uint persistentId, int bodyIndex, double latitude, double longitude, double altitude,
            double validFromUt, double validUntilUt)
        {
            var r = Positioned(persistentId, KinematicsKind.Surface, bodyIndex, validFromUt, validUntilUt);
            r.Latitude = latitude;
            r.Longitude = longitude;
            r.Altitude = altitude;
            Check(persistentId, "surface position", latitude, longitude, altitude);
            if (latitude < -90 || latitude > 90) Fail($"craft {persistentId} has latitude {latitude}");
        }

        /** <summary>A display name, so a craft KSP no longer has still lists.</summary> */
        public void Name(string name) => Last().Name = name;

        /** <summary>A kind ("Ship", "Probe"), so a craft KSP no longer has still lists.</summary> */
        public void Kind(string kind) => Last().Kind = kind;

        /**
         * <summary>
         * How uncertain the position is. At ut, with d = ut - ValidFromUt, the radius is
         * radiusM + rateMps*d plus a free-fall term: accel*d*d/2 until the speed cap is reached, then
         * linear at the cap.
         * </summary>
         */
        public void Uncertainty(double radiusM, double rateMps, double accelMps2, double speedCapMps)
        {
            var r = Last();
            r.HasUncertainty = true;
            r.UncertaintyRadiusM = radiusM;
            r.RateMps = rateMps;
            r.AccelMps2 = accelMps2;
            r.SpeedCapMps = speedCapMps;
            if (!(NonNegative(radiusM) && NonNegative(rateMps) && NonNegative(accelMps2) && NonNegative(speedCapMps)))
                Fail($"craft {r.PersistentId} has a negative or non-finite uncertainty");
        }

        /** <summary>Body-inertial rotation at <paramref name="ut"/>, and angular velocity in radians per second.</summary> */
        public void Attitude(double ut, Quatd bodyInertialRotation, Vec3d angularVelocity)
        {
            var r = Last();
            r.HasAttitude = true;
            r.AttitudeUt = ut;
            r.Rotation = bodyInertialRotation;
            r.AngularVelocity = angularVelocity;
            if (!Vec3d.Finite(ut) || !bodyInertialRotation.IsFinite || !angularVelocity.IsFinite)
                Fail($"craft {r.PersistentId} has a non-finite attitude");
        }

        /** <summary>The craft's size for framing, in metres. Without it, a nominal radius.</summary> */
        public void Radius(double metres)
        {
            var r = Last();
            r.RadiusM = metres;
            if (!(Vec3d.Finite(metres) && metres > 0)) Fail($"craft {r.PersistentId} has radius {metres}");
        }

        internal bool IsLive => live;

        /* Ends the builder; any later call throws. Its contents stay readable until Release. */
        internal void Invalidate()
        {
            Interlocked.Exchange(ref consumed, 1);
            live = false;
        }

        /* Ends the builder for exactly one publish or abandon, even when two threads race on it; false for the loser. */
        internal bool TryConsume()
        {
            if (Interlocked.Exchange(ref consumed, 1) != 0) return false;
            live = false;
            return true;
        }

        /* Ends the builder and hands its storage back for the next one. A kept reference only ever throws. */
        internal BuilderStorage Release()
        {
            live = false;
            var s = storage;
            storage = null;
            last = null;
            return s;
        }

        internal IReadOnlyList<WorldModelRecord> Records => storage.Records;
        internal IReadOnlyDictionary<string, string> Bindings => storage.Bindings;
        internal IReadOnlyList<KeyValuePair<uint, uint>> Aliases => storage.Aliases;

        WorldModelRecord Add(uint persistentId, CraftFixState state)
        {
            Live();
            if (scope == null) throw new InvalidOperationException("WorldModelBuilder: call Scope before adding a record");
            if (persistentId == 0) Fail("a record has persistentId 0");
            if (!storage.Seen.Add(((ulong)(uint)scopeOrdinal << 32) | persistentId))
                Fail($"craft {persistentId} appears twice in scope '{scope}'");
            last = storage.Next(scope, persistentId, state);
            return last;
        }

        WorldModelRecord Positioned(uint persistentId, KinematicsKind kind, int bodyIndex, double validFromUt, double validUntilUt)
        {
            var r = Add(persistentId, CraftFixState.Positioned);
            r.Kinematics = kind;
            r.BodyIndex = bodyIndex;
            r.ValidFromUt = validFromUt;
            r.ValidUntilUt = validUntilUt;
            if (bodyIndex < 0) Fail($"craft {persistentId} has body index {bodyIndex}");
            if (!(Vec3d.Finite(validFromUt) && Vec3d.Finite(validUntilUt) && validFromUt <= validUntilUt))
                Fail($"craft {persistentId} has window {validFromUt}..{validUntilUt}");
            return r;
        }

        WorldModelRecord Last()
        {
            Live();
            if (last == null) throw new InvalidOperationException("WorldModelBuilder: add a record before setting its fields");
            return last;
        }

        void Live()
        {
            if (!live) throw new InvalidOperationException("WorldModelBuilder: already published or abandoned; call Begin for a new one");
        }

        void Check(uint persistentId, string what, params double[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (Vec3d.Finite(values[i])) continue;
                Fail($"craft {persistentId} has non-finite {what}");
                return;
            }
        }

        void Fail(string error)
        {
            if (FirstError == null) FirstError = error;
        }

        static bool NonNegative(double v) => Vec3d.Finite(v) && v >= 0;
    }

    /* One craft's record in one scope, as the builder captured it. Fields that
       do not apply to its state or kinematics stay zero; RadiusM is NaN when
       not given. */
    internal sealed class WorldModelRecord
    {
        public string Scope;
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

        public double RadiusM = double.NaN;

        public WorldModelRecord(string scope, uint persistentId, CraftFixState state)
        {
            Reset(scope, persistentId, state);
        }

        /* Every field back to its new value, for reuse. */
        public void Reset(string scope, uint persistentId, CraftFixState state)
        {
            Scope = scope;
            PersistentId = persistentId;
            State = state;
            Name = Kind = RefusalCode = RefusalText = null;
            Kinematics = KinematicsKind.None;
            BodyIndex = 0;
            ValidFromUt = ValidUntilUt = 0;
            Inclination = Eccentricity = SemiMajorAxis = Lan = ArgumentOfPeriapsis = MeanAnomalyAtEpoch = Epoch = 0;
            StateUt = 0;
            Position = Velocity = default(Vec3d);
            Latitude = Longitude = Altitude = 0;
            HasUncertainty = false;
            UncertaintyRadiusM = RateMps = AccelMps2 = SpeedCapMps = 0;
            HasAttitude = false;
            AttitudeUt = 0;
            Rotation = default(Quatd);
            AngularVelocity = default(Vec3d);
            RadiusM = double.NaN;
        }
    }

    /*
     * What one builder fills: its records and the sets that check them. A
     * publisher keeps a few and hands one to each builder, so a steady
     * publisher reuses its record objects instead of allocating them anew.
     */
    internal sealed class BuilderStorage
    {
        public readonly List<WorldModelRecord> Records = new List<WorldModelRecord>();
        public readonly Dictionary<string, string> Bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<KeyValuePair<uint, uint>> Aliases = new List<KeyValuePair<uint, uint>>();
        public readonly Dictionary<uint, uint> AliasMap = new Dictionary<uint, uint>();
        public readonly HashSet<string> Scopes = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<ulong> Seen = new HashSet<ulong>();
        readonly List<WorldModelRecord> spare = new List<WorldModelRecord>();

        public void Reset()
        {
            spare.AddRange(Records);
            Records.Clear();
            Bindings.Clear();
            Aliases.Clear();
            AliasMap.Clear();
            Scopes.Clear();
            Seen.Clear();
        }

        public WorldModelRecord Next(string scope, uint persistentId, CraftFixState state)
        {
            WorldModelRecord r;
            if (spare.Count > 0)
            {
                r = spare[spare.Count - 1];
                spare.RemoveAt(spare.Count - 1);
                r.Reset(scope, persistentId, state);
            }
            else r = new WorldModelRecord(scope, persistentId, state);
            Records.Add(r);
            return r;
        }
    }
}
