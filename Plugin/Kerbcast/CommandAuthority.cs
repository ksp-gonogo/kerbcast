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
        Direction,
    }

    /* Where a command feed points, for KerbcastControl.SetCommandAim and
       CreateCommandCamera. Build one with the factory methods; the kind says
       which fields are meaningful.

       Vessel follows a craft, which is also the subject: the real craft the
       feed draws (model or marker), brackets, sizes its framing by and reads
       out. Orbit and PositionAt follow a predicted path and take an optional
       subject. Surface holds a ground point and Direction holds a direction
       from the camera's local horizon; neither has a subject of its own, so
       the camera's default craft stays the subject. Every kind takes a
       framing margin in metres, added to the subject's own radius for the
       field of view and the bracket. */
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

        /* Direction: compass bearing from north, clockwise, and angle above
           the horizon, both in degrees. */
        public double Azimuth { get; }
        public double Elevation { get; }

        /* Orbit and PositionAt: the craft the path belongs to; 0 for none. */
        public uint Subject { get; }

        public double FramingMarginM { get; }

        /* The craft this aim makes the subject, or 0 to keep the default. */
        public uint SubjectPersistentId => Kind == CommandAimKind.Vessel ? PersistentId : Subject;

        private CommandAim(CommandAimKind kind, double margin, uint pid = 0, string body = null,
            double inc = 0, double e = 0, double sma = 0, double lan = 0, double argPe = 0, double mEp = 0, double epoch = 0,
            double lat = 0, double lon = 0, double alt = 0,
            double ut = 0, Vector3d pos = default, Vector3d vel = default,
            double az = 0, double el = 0, uint subject = 0)
        {
            Kind = kind;
            FramingMarginM = margin;
            PersistentId = pid;
            Body = body ?? "";
            Inclination = inc; Eccentricity = e; SemiMajorAxis = sma; Lan = lan;
            ArgumentOfPeriapsis = argPe; MeanAnomalyAtEpoch = mEp; Epoch = epoch;
            Latitude = lat; Longitude = lon; Altitude = alt;
            Ut = ut; BodyRelativePosition = pos; BodyRelativeVelocity = vel;
            Azimuth = az; Elevation = el;
            Subject = subject;
        }

        /* Follows a craft wherever it truly is. */
        public static CommandAim Vessel(uint persistentId, double framingMarginM = 0) =>
            new CommandAim(CommandAimKind.Vessel, framingMarginM, pid: persistentId);

        /* Follows a conic Kerbcast propagates itself, on the same physics tick as the frame. */
        public static CommandAim Orbit(string body, double inc, double e, double sma, double lan, double argPe,
            double meanAnomalyAtEpoch, double epoch, uint subject = 0, double framingMarginM = 0) =>
            new CommandAim(CommandAimKind.Orbit, framingMarginM, body: body, inc: inc, e: e, sma: sma, lan: lan,
                argPe: argPe, mEp: meanAnomalyAtEpoch, epoch: epoch, subject: subject);

        /* Holds a fixed point on a body, carried round by its rotation. */
        public static CommandAim Surface(string body, double lat, double lon, double alt, double framingMarginM = 0) =>
            new CommandAim(CommandAimKind.Surface, framingMarginM, body: body, lat: lat, lon: lon, alt: alt);

        /* Follows a position the caller propagated itself (Principia-safe),
           extrapolated linearly from `ut` by the velocity. */
        public static CommandAim PositionAt(string body, double ut, Vector3d bodyRelativePos, Vector3d bodyRelativeVel,
            uint subject = 0, double framingMarginM = 0) =>
            new CommandAim(CommandAimKind.PositionAt, framingMarginM, body: body, ut: ut, pos: bodyRelativePos,
                vel: bodyRelativeVel, subject: subject);

        /* Holds a direction from the camera's local horizon: `azimuth` degrees
           clockwise from north, `elevation` degrees above the horizon (-90..90).
           The view is zoomed for the subject's range, so the subject is framed
           when it passes through. */
        public static CommandAim Direction(double azimuth, double elevation, double framingMarginM = 0) =>
            new CommandAim(CommandAimKind.Direction, framingMarginM, az: azimuth, el: elevation);

        public override string ToString()
        {
            string tail = (Subject != 0 ? $" subject={Subject}" : "") + (FramingMarginM != 0 ? $" margin={FramingMarginM:F0}m" : "");
            switch (Kind)
            {
                case CommandAimKind.Vessel: return $"Vessel({PersistentId}{tail})";
                case CommandAimKind.Orbit: return $"Orbit({Body} sma={SemiMajorAxis:F0} e={Eccentricity:F4} inc={Inclination:F2}{tail})";
                case CommandAimKind.Surface: return $"Surface({Body} {Latitude:F4},{Longitude:F4} alt={Altitude:F0}{tail})";
                case CommandAimKind.Direction: return $"Direction(az={Azimuth:F1} el={Elevation:F1}{tail})";
                default: return $"PositionAt({Body} ut={Ut:F2}{tail})";
            }
        }
    }

    /* A command feed in this scene. */
    public sealed class CommandFeedView
    {
        public uint FlightId;
        /* The CommNet home node the station stands at (or beside). */
        public string StationNodeName;
        /* gonogo's command-centre id, "ground:<node>". */
        public string StationId;
        /* The station's in-game name, or StationNodeName when it has none. */
        public string DisplayName;
        /* The aim SetCommandAim last applied; null for the default. */
        public CommandAim Aim;
        /* Vessel.persistentId of the feed's subject; 0 for none. */
        public uint SubjectPersistentId;
        /* Station to that craft in metres; NaN when either cannot be placed. */
        public double RangeM;
        /* The Tracking Station's level, 1-based (stock 1 to 3); the highest
           level outside career. */
        public int TrackingLevel;
        /* Whether the craft plus the framing margin fits the widest view. */
        public bool FramingFits;
        /* Made by KerbcastControl.CreateCommandCamera or a client, rather than from settings. */
        public bool Created;
    }

    /* One feed's instructions from the authority. */
    internal sealed class CommandFeedOrders
    {
        public string Blank;
    }

    /* The in-game authority (gonogo) over command feeds, for one scene: a lock
       on every command feed's aim, held by one owner. Only the owner's
       SetCommandAim calls are accepted while it is held. The lock also fails
       closed: a holder that stops calling blanks every feed, and it can blank
       one itself. Each owner call is a heartbeat. Main thread only. */
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
            SubjectAnchorVisible = true;
            _defaultAnchorPolicy = AnchorPolicy.ObservedOnly;
            _anchorPolicies.Clear();
        }

        /* Whether command feeds send Kerbcast's subject anchor while the lock
           is held. The owner can switch it off; it returns with the lock. */
        public bool SubjectAnchorVisible { get; set; } = true;

        private AnchorPolicy _defaultAnchorPolicy = AnchorPolicy.ObservedOnly;
        private readonly Dictionary<uint, AnchorPolicy> _anchorPolicies = new Dictionary<uint, AnchorPolicy>();

        /* Browser clients' anchors on camera `flightId`: observed-only while
           the lock is held unless the owner relaxed it, everything otherwise. */
        public AnchorPolicy BrowserAnchorPolicy(uint flightId)
        {
            if (!Registered) return AnchorPolicy.Full;
            return _anchorPolicies.TryGetValue(flightId, out var p) ? p : _defaultAnchorPolicy;
        }

        /* The policy for every camera, replacing any per-camera ones. */
        public void SetAnchorPolicy(AnchorPolicy policy)
        {
            _defaultAnchorPolicy = policy;
            _anchorPolicies.Clear();
        }

        public void SetAnchorPolicy(uint flightId, AnchorPolicy policy) => _anchorPolicies[flightId] = policy;

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
