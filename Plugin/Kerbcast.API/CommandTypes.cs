namespace Kerbcast.Api
{
    /** <summary>The command lock went from its holder without the holder releasing it. Immutable.</summary> */
    public sealed class LockLostEvent
    {
        public LockLostEvent(string owner, LockLossReason reason)
        {
            Owner = owner;
            Reason = reason;
        }

        /** <summary>The owner that held the lock.</summary> */
        public string Owner { get; }

        public LockLossReason Reason { get; }

        public override string ToString() => $"{Owner} lost the command lock: {Reason}";
    }

    /** <summary>One command feed as it was when the list was taken.</summary> */
    public sealed class CommandFeed
    {
        public uint FlightId { get; set; }
        public string DisplayName { get; set; }

        /** <summary>Stable name of the place the lens stands, unchanged when its craft docks.</summary> */
        public string SiteKey { get; set; }
        public SiteKind SiteKind { get; set; }

        /** <summary>For a vessel site, the craft the lens rides now; zero otherwise.</summary> */
        public uint VantagePersistentId { get; set; }

        /** <summary>The world-model scope the site is bound to, null when unbound.</summary> */
        public string Scope { get; set; }

        /** <summary>The craft the feed is aimed at, zero for none.</summary> */
        public uint SubjectPersistentId { get; set; }

        /** <summary>Whether a client or a mod created the feed, rather than settings.</summary> */
        public bool Created { get; set; }
    }

    /** <summary>One craft a command feed could watch.</summary> */
    public sealed class CommandTarget
    {
        public uint PersistentId { get; set; }
        public string Name { get; set; }

        /** <summary>KSP's VesselType name ("Ship", "Probe", "Debris"), or the world model's kind for a craft KSP no longer has.</summary> */
        public string Kind { get; set; }

        /** <summary>Metres from the site, NaN when the craft is not positioned.</summary> */
        public double RangeM { get; set; }

        /** <summary>"ok", or why the feed may not watch it ("tooFar", "notPositioned", "refused", "stale", "tooUncertain", ...). New codes may appear.</summary> */
        public string Eligibility { get; set; }
        public string EligibilityText { get; set; }

        /** <summary>The world model's own refusal, when Eligibility is "refused".</summary> */
        public string RefusalCode { get; set; }
        public string RefusalText { get; set; }

        /** <summary>The game time the entry describes.</summary> */
        public double Ut { get; set; }
    }

    /** <summary>Where a command feed points. Bodies are KSP's flightGlobalsIndex and vectors are body-relative, z-up, in KSP's fixed inertial frame, as <see cref="WorldModelBuilder.StateVector"/> takes them.</summary> */
    public sealed class Aim
    {
        public AimKind Kind { get; }
        public uint SubjectPersistentId { get; }
        public int BodyIndex { get; }
        public double FramingMarginM { get; }

        /** <summary>Accept a subject whose uncertainty is wider than the view could frame: a deliberate search of a region.</summary> */
        public bool AllowUncertain { get; }

        public double Inclination { get; }
        public double Eccentricity { get; }
        public double SemiMajorAxis { get; }
        public double Lan { get; }
        public double ArgumentOfPeriapsis { get; }
        public double MeanAnomalyAtEpoch { get; }
        public double Epoch { get; }

        public double Latitude { get; }
        public double Longitude { get; }
        public double Altitude { get; }

        public double Ut { get; }
        public Vec3d Position { get; }
        public Vec3d Velocity { get; }

        /** <summary>Degrees from north and above the horizon at the lens.</summary> */
        public double Azimuth { get; }
        public double Elevation { get; }

        Aim(AimKind kind, uint subject, int body, double margin, bool allowUncertain,
            double inc = 0, double e = 0, double sma = 0, double lan = 0, double argPe = 0, double mna = 0, double epoch = 0,
            double lat = 0, double lon = 0, double alt = 0,
            double ut = 0, Vec3d position = default, Vec3d velocity = default,
            double azimuth = 0, double elevation = 0)
        {
            Kind = kind;
            SubjectPersistentId = subject;
            BodyIndex = body;
            FramingMarginM = margin;
            AllowUncertain = allowUncertain;
            Inclination = inc;
            Eccentricity = e;
            SemiMajorAxis = sma;
            Lan = lan;
            ArgumentOfPeriapsis = argPe;
            MeanAnomalyAtEpoch = mna;
            Epoch = epoch;
            Latitude = lat;
            Longitude = lon;
            Altitude = alt;
            Ut = ut;
            Position = position;
            Velocity = velocity;
            Azimuth = azimuth;
            Elevation = elevation;
        }

        public static Aim Vessel(uint persistentId, double framingMarginM = 0, bool allowUncertain = false) =>
            new Aim(AimKind.Vessel, persistentId, -1, framingMarginM, allowUncertain);

        /**
         * <summary>
         * KSP's Orbit elements at <paramref name="epoch"/>: inclination, longitude of the ascending node and
         * argument of periapsis in degrees, semi-major axis in metres, mean anomaly at epoch in radians,
         * epoch in seconds, as KSP's Orbit constructor takes them.
         * </summary>
         */
        public static Aim Orbit(int bodyIndex, double inclination, double eccentricity, double semiMajorAxis, double lan,
            double argumentOfPeriapsis, double meanAnomalyAtEpoch, double epoch, uint subjectPersistentId = 0,
            double framingMarginM = 0, bool allowUncertain = false) =>
            new Aim(AimKind.Orbit, subjectPersistentId, bodyIndex, framingMarginM, allowUncertain,
                inc: inclination, e: eccentricity, sma: semiMajorAxis, lan: lan, argPe: argumentOfPeriapsis,
                mna: meanAnomalyAtEpoch, epoch: epoch);

        public static Aim Surface(int bodyIndex, double latitude, double longitude, double altitude, double framingMarginM = 0) =>
            new Aim(AimKind.Surface, 0, bodyIndex, framingMarginM, false, lat: latitude, lon: longitude, alt: altitude);

        /** <summary>A ballistic state at <paramref name="ut"/>, propagated under the body's gravity with no thrust.</summary> */
        public static Aim PositionAt(int bodyIndex, double ut, Vec3d position, Vec3d velocity, uint subjectPersistentId = 0,
            double framingMarginM = 0, bool allowUncertain = false) =>
            new Aim(AimKind.PositionAt, subjectPersistentId, bodyIndex, framingMarginM, allowUncertain,
                ut: ut, position: position, velocity: velocity);

        public static Aim Direction(double azimuth, double elevation, double framingMarginM = 0) =>
            new Aim(AimKind.Direction, 0, -1, framingMarginM, false, azimuth: azimuth, elevation: elevation);

        public override string ToString()
        {
            switch (Kind)
            {
                case AimKind.Vessel: return $"Vessel({SubjectPersistentId})";
                case AimKind.Orbit: return $"Orbit(body {BodyIndex}, sma {SemiMajorAxis}, e {Eccentricity})";
                case AimKind.Surface: return $"Surface(body {BodyIndex}, {Latitude}, {Longitude}, {Altitude})";
                case AimKind.PositionAt: return $"PositionAt(body {BodyIndex}, ut {Ut})";
                default: return $"Direction({Azimuth}, {Elevation})";
            }
        }
    }
}
