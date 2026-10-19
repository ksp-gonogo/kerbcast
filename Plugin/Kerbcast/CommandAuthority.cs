using System;
using System.Collections.Generic;

namespace Kerbcast
{
    public enum CommandAimKind
    {
        Vessel,
        Orbit,
        Surface,
        PositionAt,
    }

    /* Where an authority tells a command feed to point. Build one with the
       factory methods; the kind says which fields are meaningful. */
    public sealed class CommandAim
    {
        public CommandAimKind Kind { get; }

        /* Vessel: the craft's Vessel.persistentId. */
        public uint PersistentId { get; }

        /* Orbit, Surface and PositionAt: the reference body's bodyName. */
        public string Body { get; }

        /* Orbit: the elements as KSP's Orbit constructor takes them, angles in
           degrees except the mean anomaly at epoch, which is in radians. */
        public double Inclination { get; }
        public double Eccentricity { get; }
        public double SemiMajorAxis { get; }
        public double Lan { get; }
        public double ArgumentOfPeriapsis { get; }
        public double MeanAnomalyAtEpoch { get; }
        public double Epoch { get; }

        /* Surface: degrees and metres above sea level. */
        public double Latitude { get; }
        public double Longitude { get; }
        public double Altitude { get; }

        /* PositionAt: UT of the sample and the body-relative position and
           velocity at it, in the body's non-rotating frame (the frame of
           Orbit.getRelativePositionAtUT, z up). */
        public double Ut { get; }
        public Vector3d BodyRelativePosition { get; }
        public Vector3d BodyRelativeVelocity { get; }

        private CommandAim(CommandAimKind kind, uint pid = 0, string body = null,
            double inc = 0, double e = 0, double sma = 0, double lan = 0, double argPe = 0, double mEp = 0, double epoch = 0,
            double lat = 0, double lon = 0, double alt = 0,
            double ut = 0, Vector3d pos = default, Vector3d vel = default)
        {
            Kind = kind;
            PersistentId = pid;
            Body = body ?? "";
            Inclination = inc; Eccentricity = e; SemiMajorAxis = sma; Lan = lan;
            ArgumentOfPeriapsis = argPe; MeanAnomalyAtEpoch = mEp; Epoch = epoch;
            Latitude = lat; Longitude = lon; Altitude = alt;
            Ut = ut; BodyRelativePosition = pos; BodyRelativeVelocity = vel;
        }

        /* The true track of a craft, for when the network has a live track of it. */
        public static CommandAim Vessel(uint persistentId) =>
            new CommandAim(CommandAimKind.Vessel, pid: persistentId);

        /* A conic Kerbcast propagates itself, on the same physics tick as the frame. */
        public static CommandAim Orbit(string body, double inc, double e, double sma, double lan, double argPe,
            double meanAnomalyAtEpoch, double epoch) =>
            new CommandAim(CommandAimKind.Orbit, body: body, inc: inc, e: e, sma: sma, lan: lan, argPe: argPe,
                mEp: meanAnomalyAtEpoch, epoch: epoch);

        /* A fixed point on a body, carried round by its rotation. */
        public static CommandAim Surface(string body, double lat, double lon, double alt) =>
            new CommandAim(CommandAimKind.Surface, body: body, lat: lat, lon: lon, alt: alt);

        /* A position the caller propagated itself (Principia-safe), extrapolated
           linearly from `ut` by the velocity. */
        public static CommandAim PositionAt(string body, double ut, Vector3d bodyRelativePos, Vector3d bodyRelativeVel) =>
            new CommandAim(CommandAimKind.PositionAt, body: body, ut: ut, pos: bodyRelativePos, vel: bodyRelativeVel);

        public override string ToString()
        {
            switch (Kind)
            {
                case CommandAimKind.Vessel: return $"Vessel({PersistentId})";
                case CommandAimKind.Orbit: return $"Orbit({Body} sma={SemiMajorAxis:F0} e={Eccentricity:F4} inc={Inclination:F2})";
                case CommandAimKind.Surface: return $"Surface({Body} {Latitude:F4},{Longitude:F4} alt={Altitude:F0})";
                default: return $"PositionAt({Body} ut={Ut:F2})";
            }
        }
    }

    /* A command feed an authority can govern. */
    public sealed class CommandFeedView
    {
        public uint FlightId;
        /* The CommNet home node the station stands at (or beside). */
        public string StationNodeName;
        /* gonogo's command-centre id, "ground:<node>". */
        public string StationId;
        /* The station's in-game name, or StationNodeName when it has none. */
        public string DisplayName;
        /* Vessel.persistentId of the craft the feed watches; 0 for none. */
        public uint TargetPersistentId;
        /* Station to that craft in metres; NaN when either cannot be placed. */
        public double RangeM;
        /* The Tracking Station's level, 1-based (stock 1 to 3); the highest
           level outside career. */
        public int TrackingLevel;
        /* Whether the craft plus the framing margin fits the widest view. */
        public bool FramingFits;
    }

    /* One feed's instructions from the authority. */
    internal sealed class CommandFeedOrders
    {
        public CommandAim Aim;
        /* Built once per Orbit aim. */
        public Orbit AimOrbit;
        /* Metres beyond the craft's own radius that framing and the bracket hold. */
        public double FramingMarginM;
        public string Blank;
    }

    /* The in-game authority (gonogo) over command feeds, for one scene. While an
       owner is registered every command feed is governed and fails closed; the
       KerbcastControl setters are its only way in, and each call is a
       heartbeat. Main thread only. */
    internal sealed class CommandAuthority
    {
        public const float TimeoutSeconds = 5f;

        private readonly Dictionary<uint, CommandFeedOrders> _feeds = new Dictionary<uint, CommandFeedOrders>();
        private float _lastCall;

        public string Owner { get; private set; }
        public bool Registered => Owner != null;

        /* Real time, so it keeps counting while the game is paused or warping. */
        public bool TimedOut(float realNow) => Registered && realNow - _lastCall > TimeoutSeconds;

        /* Idempotent for the same owner; a second owner is refused. */
        public bool Register(string owner, float realNow)
        {
            if (string.IsNullOrEmpty(owner)) return false;
            if (Owner != null && !string.Equals(Owner, owner, StringComparison.Ordinal)) return false;
            if (Owner == null) UnityEngine.Debug.Log($"[Kerbcast][CMDCAM] command authority registered: {owner}");
            Owner = owner;
            _lastCall = realNow;
            return true;
        }

        public void Clear(string owner)
        {
            if (Owner == null || !string.Equals(Owner, owner, StringComparison.Ordinal)) return;
            UnityEngine.Debug.Log($"[Kerbcast][CMDCAM] command authority cleared: {owner}");
            Reset();
        }

        public void Reset()
        {
            Owner = null;
            _feeds.Clear();
        }

        public void Heartbeat(float realNow) => _lastCall = realNow;

        public CommandFeedOrders Orders(uint flightId) =>
            _feeds.TryGetValue(flightId, out var o) ? o : null;

        public CommandFeedOrders OrdersFor(uint flightId)
        {
            if (!_feeds.TryGetValue(flightId, out var o))
            {
                o = new CommandFeedOrders();
                _feeds[flightId] = o;
            }
            return o;
        }
    }
}
