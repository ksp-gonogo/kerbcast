using System.Collections.Generic;

namespace Kerbcast.Api.Testing
{
    /** <summary>A world-model snapshot the recorder accepted, as its builder captured it.</summary> */
    public sealed class PublishedSnapshot
    {
        public long Sequence { get; }
        public long Epoch { get; }
        public double KnowledgeUt { get; }

        /** <summary>Every record, in the order they were added.</summary> */
        public IReadOnlyList<PublishedRecord> Records { get; }

        /** <summary>Site key to scope.</summary> */
        public IReadOnlyDictionary<string, string> SiteBindings { get; }

        /** <summary>From persistentId to persistentId, in the order they were added.</summary> */
        public IReadOnlyList<KeyValuePair<uint, uint>> Aliases { get; }

        internal PublishedSnapshot(WorldModelBuilder builder)
        {
            Sequence = builder.Sequence;
            Epoch = builder.Epoch;
            KnowledgeUt = builder.KnowledgeUt;
            var records = new List<PublishedRecord>(builder.Records.Count);
            foreach (var r in builder.Records) records.Add(new PublishedRecord(r));
            Records = records;
            var bindings = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var kv in builder.Bindings) bindings[kv.Key] = kv.Value;
            SiteBindings = bindings;
            Aliases = new List<KeyValuePair<uint, uint>>(builder.Aliases);
        }

        /** <summary>The record for a craft in a scope, null when there is none.</summary> */
        public PublishedRecord Find(string scope, uint persistentId)
        {
            for (int i = 0; i < Records.Count; i++)
                if (Records[i].PersistentId == persistentId && string.Equals(Records[i].Scope, scope, System.StringComparison.Ordinal))
                    return Records[i];
            return null;
        }
    }

    /**
     * <summary>
     * One craft's record in one scope. Fields that do not apply to its state or kinematics are zero;
     * RadiusM is NaN when not given.
     * </summary>
     */
    public sealed class PublishedRecord
    {
        public string Scope { get; }
        public uint PersistentId { get; }
        public CraftFixState State { get; }

        public string Name { get; }
        public string Kind { get; }
        public string RefusalCode { get; }
        public string RefusalText { get; }

        public KinematicsKind Kinematics { get; }
        public int BodyIndex { get; }
        public double ValidFromUt { get; }
        public double ValidUntilUt { get; }

        public double Inclination { get; }
        public double Eccentricity { get; }
        public double SemiMajorAxis { get; }
        public double Lan { get; }
        public double ArgumentOfPeriapsis { get; }
        public double MeanAnomalyAtEpoch { get; }
        public double Epoch { get; }

        public double StateUt { get; }
        public Vec3d Position { get; }
        public Vec3d Velocity { get; }

        public double Latitude { get; }
        public double Longitude { get; }
        public double Altitude { get; }

        public bool HasUncertainty { get; }
        public double UncertaintyRadiusM { get; }
        public double RateMps { get; }
        public double AccelMps2 { get; }
        public double SpeedCapMps { get; }

        public bool HasAttitude { get; }
        public double AttitudeUt { get; }
        public Quatd Rotation { get; }
        public Vec3d AngularVelocity { get; }

        public double RadiusM { get; }

        internal PublishedRecord(WorldModelRecord r)
        {
            Scope = r.Scope;
            PersistentId = r.PersistentId;
            State = r.State;
            Name = r.Name;
            Kind = r.Kind;
            RefusalCode = r.RefusalCode;
            RefusalText = r.RefusalText;
            Kinematics = r.Kinematics;
            BodyIndex = r.BodyIndex;
            ValidFromUt = r.ValidFromUt;
            ValidUntilUt = r.ValidUntilUt;
            Inclination = r.Inclination;
            Eccentricity = r.Eccentricity;
            SemiMajorAxis = r.SemiMajorAxis;
            Lan = r.Lan;
            ArgumentOfPeriapsis = r.ArgumentOfPeriapsis;
            MeanAnomalyAtEpoch = r.MeanAnomalyAtEpoch;
            Epoch = r.Epoch;
            StateUt = r.StateUt;
            Position = r.Position;
            Velocity = r.Velocity;
            Latitude = r.Latitude;
            Longitude = r.Longitude;
            Altitude = r.Altitude;
            HasUncertainty = r.HasUncertainty;
            UncertaintyRadiusM = r.UncertaintyRadiusM;
            RateMps = r.RateMps;
            AccelMps2 = r.AccelMps2;
            SpeedCapMps = r.SpeedCapMps;
            HasAttitude = r.HasAttitude;
            AttitudeUt = r.AttitudeUt;
            Rotation = r.Rotation;
            AngularVelocity = r.AngularVelocity;
            RadiusM = r.RadiusM;
        }
    }
}
