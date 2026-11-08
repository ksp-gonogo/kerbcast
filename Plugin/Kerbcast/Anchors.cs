using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kerbcast
{
    /* What an anchor marks; the wire's AnchorKind. */
    public enum AnchorKind
    {
        Part,
        Vessel,
        Surface,
        Direction,
        Kinematic,
    }

    /* How a kinematic anchor moves; the wire's AnchorKinematicsKind, and a
       world model record's kinematics. */
    public enum AnchorMotion
    {
        None,
        Conic,
        StateVector,
        Surface,
    }

    /* A named direction of a craft, taken from its orbit; the wire's
       AnchorDirection. None for a fixed vector. */
    public enum AnchorDirection
    {
        None,
        Prograde,
        Retrograde,
        Normal,
        Antinormal,
        RadialIn,
        RadialOut,
        Maneuver,
        Sun,
        Target,
        TargetRelativeVelocity,
    }

    /* How much of a browser client's anchor results a camera sends while a
       command authority holds the lock. Full sends everything; ObservedOnly
       sends only what the picture shows (see AnchorResult.Observed). */
    public enum AnchorPolicy
    {
        ObservedOnly,
        Full,
    }

    /* Which results are limited to what the picture shows. */
    internal static class AnchorRules
    {
        /* Kerbcast's own anchors always are; a browser client's are while a
           lock is held and the camera's policy says so; anchors set in game
           never are. A kinematic anchor never is: it marks knowledge its
           owner supplied, not anything read from the game. */
        public static bool Observed(string owner, bool lockHeld, AnchorPolicy policy, AnchorKind kind)
        {
            if (kind == AnchorKind.Kinematic) return false;
            return Observed(owner, lockHeld, policy);
        }

        /*
         * A kinematic anchor that the owner's rule would limit (a browser's
         * under ObservedOnly, or Kerbcast's own) keeps its position but must
         * not reveal the lens's pose, which the picture does not show: its
         * position and size go out only while in frame, and never its range,
         * whether it is behind the lens or its angle off axis.
         */
        public static bool PoseWithheld(string owner, bool lockHeld, AnchorPolicy policy, AnchorKind kind) =>
            kind == AnchorKind.Kinematic && Observed(owner, lockHeld, policy);

        /*
         * Whether a kinematic anchor is placed at the instant its light left
         * it. A surface point is not: it stays on the body as drawn, at its
         * present rotation, as a landed craft's stand-in does; turning it back
         * by the light time would move it across the drawn ground.
         */
        public static bool PlacedAtLightInstant(AnchorMotion motion) => motion != AnchorMotion.Surface;

        public static bool Observed(string owner, bool lockHeld, AnchorPolicy policy)
        {
            if (string.Equals(owner, AnchorRegistry.KerbcastOwner, StringComparison.Ordinal)) return true;
            if (owner != null && owner.StartsWith(AnchorRegistry.PeerPrefix, StringComparison.Ordinal))
                return lockHeld && policy == AnchorPolicy.ObservedOnly;
            return false;
        }
    }

    /* Something a client wants marked on a camera's picture. Kind says which
       fields apply; see the wire's Anchor for each. Absent numbers are NaN so
       validation can tell them from zero. */
    public sealed class AnchorSpec
    {
        public const int MaxIdLength = 64;
        /* The sidecar's MAX_BODY_LEN. */
        public const int MaxBodyLength = 128;

        public string Id = "";
        public AnchorKind Kind;
        /* Part and Vessel: the craft. Direction: the craft followed, 0 for the active one. */
        public uint Vessel;
        /* Part: the part's persistentId. */
        public uint Part;
        /* Part: metres from the part's origin in its own axes. */
        public double OffsetX, OffsetY, OffsetZ;
        public string Body = "";
        public double Latitude = double.NaN, Longitude = double.NaN, Altitude;
        public AnchorDirection Direction;
        /* Direction: a fixed vector in the inertial frame of Orbit.getRelativePositionAtUT (z up). */
        public bool HasVector;
        public double VectorX, VectorY, VectorZ;
        public bool Orientation;
        public bool Occlusion;
        public double MarginM;

        /* Kerbcast's own aim anchor only: it carries the aim, which the operator holds, so its result is never limited to what the picture shows. */
        internal bool NeverObserved;

        /* Kinematic: how the point moves about Body, propagated as a world
           model's records are. Conic takes KSP Orbit elements (degrees,
           metres, mean anomaly at Epoch in radians); StateVector a position
           and velocity at Ut, z up in KSP's fixed inertial frame; Surface
           takes Latitude, Longitude and Altitude. */
        public AnchorMotion Motion;
        public double Inclination, Eccentricity, SemiMajorAxis, Lan, ArgumentOfPeriapsis, MeanAnomalyAtEpoch, Epoch;
        public double Ut;
        public DVec3 Position, Velocity;

        /* The projector's path for a Conic or StateVector, built on first use. */
        internal bool PathTried, PathOk;
        internal KeplerPath Path;

        /* This spec with no path built yet. */
        internal AnchorSpec Copy()
        {
            var c = (AnchorSpec)MemberwiseClone();
            c.PathTried = c.PathOk = false;
            c.Path = default;
            return c;
        }

        public static AnchorSpec ForPart(string id, uint vessel, uint part) =>
            new AnchorSpec { Id = id, Kind = AnchorKind.Part, Vessel = vessel, Part = part };

        public static AnchorSpec ForVessel(string id, uint vessel) =>
            new AnchorSpec { Id = id, Kind = AnchorKind.Vessel, Vessel = vessel };

        public static AnchorSpec ForSurface(string id, string body, double latitude, double longitude, double altitude = 0) =>
            new AnchorSpec { Id = id, Kind = AnchorKind.Surface, Body = body ?? "", Latitude = latitude, Longitude = longitude, Altitude = altitude };

        public static AnchorSpec ForDirection(string id, AnchorDirection direction, uint vessel = 0) =>
            new AnchorSpec { Id = id, Kind = AnchorKind.Direction, Direction = direction, Vessel = vessel };

        public static AnchorSpec ForVector(string id, double x, double y, double z) =>
            new AnchorSpec { Id = id, Kind = AnchorKind.Direction, HasVector = true, VectorX = x, VectorY = y, VectorZ = z };

        /* A point on a conic: KSP Orbit elements about `body` at `epoch`. A hyperbola's semi-major axis is negative. */
        public static AnchorSpec ForConic(string id, string body, double inclination, double eccentricity, double semiMajorAxis,
            double lan, double argumentOfPeriapsis, double meanAnomalyAtEpoch, double epoch) =>
            new AnchorSpec
            {
                Id = id, Kind = AnchorKind.Kinematic, Motion = AnchorMotion.Conic, Body = body ?? "",
                Inclination = inclination, Eccentricity = eccentricity, SemiMajorAxis = semiMajorAxis, Lan = lan,
                ArgumentOfPeriapsis = argumentOfPeriapsis, MeanAnomalyAtEpoch = meanAnomalyAtEpoch, Epoch = epoch,
            };

        /* A point coasting ballistically from a state at `ut` about `body`, z up in KSP's fixed inertial frame. */
        public static AnchorSpec ForStateVector(string id, string body, double ut, DVec3 position, DVec3 velocity) =>
            new AnchorSpec
            {
                Id = id, Kind = AnchorKind.Kinematic, Motion = AnchorMotion.StateVector, Body = body ?? "",
                Ut = ut, Position = position, Velocity = velocity,
            };

        /* A point fixed to `body`'s surface, as a kinematic anchor: never limited to what the picture shows. */
        public static AnchorSpec ForSurfaceMotion(string id, string body, double latitude, double longitude, double altitude = 0) =>
            new AnchorSpec
            {
                Id = id, Kind = AnchorKind.Kinematic, Motion = AnchorMotion.Surface, Body = body ?? "",
                Latitude = latitude, Longitude = longitude, Altitude = altitude,
            };

        /* Why this anchor can not be used, or null. Mirrors the sidecar's
           validate_anchor: shape only, whether the craft or body exists is
           decided when it is located. */
        public string Invalid()
        {
            if (string.IsNullOrEmpty(Id) || Id.Length > MaxIdLength) return $"anchor id must be 1 to {MaxIdLength} characters";
            if (!Finite(MarginM) || MarginM < 0) return $"anchor {Id}: marginM must be a finite number, 0 or more";
            switch (Kind)
            {
                case AnchorKind.Part:
                    if (Part == 0) return $"anchor {Id}: a part anchor needs the part's persistentId";
                    return Finite(OffsetX) && Finite(OffsetY) && Finite(OffsetZ) ? null : $"anchor {Id}: offset must be finite";
                case AnchorKind.Vessel:
                    return Vessel == 0 ? $"anchor {Id}: a vessel anchor needs the craft's persistentId" : null;
                case AnchorKind.Surface:
                    if (string.IsNullOrEmpty(Body) || Body.Trim().Length == 0) return $"anchor {Id}: a surface anchor needs a body name";
                    if (!Finite(Latitude) || Latitude < -90 || Latitude > 90) return $"anchor {Id}: latitude must be within -90..90";
                    return Finite(Longitude) && Finite(Altitude) ? null : $"anchor {Id}: longitude and altitude must be finite";
                case AnchorKind.Direction:
                    if (HasVector == (Direction != AnchorDirection.None))
                        return $"anchor {Id}: a direction anchor needs exactly one of a named direction and a vector";
                    if (HasVector && (!Finite(VectorX) || !Finite(VectorY) || !Finite(VectorZ) || (VectorX == 0 && VectorY == 0 && VectorZ == 0)))
                        return $"anchor {Id}: the vector must be finite and not zero";
                    return null;
                case AnchorKind.Kinematic:
                    return InvalidMotion();
            }
            return $"anchor {Id}: unknown kind";
        }

        private string InvalidMotion()
        {
            if (string.IsNullOrEmpty(Body) || Body.Trim().Length == 0 || Body.Trim().Length > MaxBodyLength)
                return $"anchor {Id}: a kinematic anchor needs a body name of 1 to {MaxBodyLength} characters";
            switch (Motion)
            {
                case AnchorMotion.Conic:
                    if (!(Finite(Inclination) && Finite(Eccentricity) && Finite(SemiMajorAxis) && Finite(Lan)
                          && Finite(ArgumentOfPeriapsis) && Finite(MeanAnomalyAtEpoch) && Finite(Epoch)))
                        return $"anchor {Id}: the elements must be finite";
                    if (Eccentricity < 0 || Eccentricity == 1) return $"anchor {Id}: eccentricity must be 0 or more and not 1";
                    return (Eccentricity < 1 ? SemiMajorAxis > 0 : SemiMajorAxis < 0)
                        ? null : $"anchor {Id}: the semi-major axis must be positive for an ellipse and negative for a hyperbola";
                case AnchorMotion.StateVector:
                    if (!(Finite(Ut) && Finite(Position.X) && Finite(Position.Y) && Finite(Position.Z)
                          && Finite(Velocity.X) && Finite(Velocity.Y) && Finite(Velocity.Z)))
                        return $"anchor {Id}: the state must be finite";
                    return Position.X == 0 && Position.Y == 0 && Position.Z == 0
                        ? $"anchor {Id}: the position must not be the body's centre" : null;
                case AnchorMotion.Surface:
                    if (!Finite(Latitude) || Latitude < -90 || Latitude > 90) return $"anchor {Id}: latitude must be within -90..90";
                    return Finite(Longitude) && Finite(Altitude) ? null : $"anchor {Id}: longitude and altitude must be finite";
            }
            return $"anchor {Id}: a kinematic anchor needs its kinematics";
        }

        /* One anchor from the commands file, or null with a reason. */
        internal static AnchorSpec FromJson(Dictionary<string, object> o, out string why)
        {
            why = null;
            if (o == null) { why = "anchor is not an object"; return null; }
            var a = new AnchorSpec { Id = Str(o, "id") };
            switch (Str(o, "kind"))
            {
                case "part": a.Kind = AnchorKind.Part; break;
                case "vessel": a.Kind = AnchorKind.Vessel; break;
                case "surface": a.Kind = AnchorKind.Surface; break;
                case "direction": a.Kind = AnchorKind.Direction; break;
                case "kinematic": a.Kind = AnchorKind.Kinematic; break;
                default: why = $"anchor {a.Id}: unknown kind '{Str(o, "kind")}'"; return null;
            }
            a.Vessel = U32(o, "vessel");
            a.Part = U32(o, "part");
            if (o.TryGetValue("offset", out var off) && off != null)
            {
                if (!Vec3(off, out a.OffsetX, out a.OffsetY, out a.OffsetZ)) { why = $"anchor {a.Id}: offset must be 3 numbers"; return null; }
            }
            a.Body = Str(o, "body");
            a.Latitude = Num(o, "latitude", double.NaN);
            a.Longitude = Num(o, "longitude", double.NaN);
            a.Altitude = Num(o, "altitude", 0);
            string dir = Str(o, "direction");
            if (dir.Length > 0)
            {
                a.Direction = DirectionFromWire(dir);
                if (a.Direction == AnchorDirection.None) { why = $"anchor {a.Id}: unknown direction '{dir}'"; return null; }
            }
            if (o.TryGetValue("vector", out var vec) && vec != null)
            {
                if (!Vec3(vec, out a.VectorX, out a.VectorY, out a.VectorZ)) { why = $"anchor {a.Id}: vector must be 3 numbers"; return null; }
                a.HasVector = true;
            }
            a.Orientation = Bool(o, "orientation");
            a.Occlusion = Bool(o, "occlusion");
            a.MarginM = Num(o, "marginM", 0);
            if (o.TryGetValue("kinematics", out var k) && k != null)
            {
                if (a.Kind != AnchorKind.Kinematic) { why = $"anchor {a.Id}: kinematics belong on a kinematic anchor only"; return null; }
                if (!(k is Dictionary<string, object> m)) { why = $"anchor {a.Id}: kinematics must be an object"; return null; }
                if (!MotionFromJson(a, m, out why)) return null;
            }
            why = a.Invalid();
            return why == null ? a : null;
        }

        /* The wire's AnchorKinematics into `a`; a kinematic anchor's body and surface point use the surface anchor's fields. */
        private static bool MotionFromJson(AnchorSpec a, Dictionary<string, object> m, out string why)
        {
            why = null;
            switch (Str(m, "kind"))
            {
                case "conic": a.Motion = AnchorMotion.Conic; break;
                case "stateVector": a.Motion = AnchorMotion.StateVector; break;
                case "surface": a.Motion = AnchorMotion.Surface; break;
                default: why = $"anchor {a.Id}: unknown kinematics '{Str(m, "kind")}'"; return false;
            }
            a.Body = Str(m, "body");
            a.Inclination = Num(m, "inclination", double.NaN);
            a.Eccentricity = Num(m, "eccentricity", double.NaN);
            a.SemiMajorAxis = Num(m, "semiMajorAxis", double.NaN);
            a.Lan = Num(m, "lan", double.NaN);
            a.ArgumentOfPeriapsis = Num(m, "argumentOfPeriapsis", double.NaN);
            a.MeanAnomalyAtEpoch = Num(m, "meanAnomalyAtEpoch", double.NaN);
            a.Epoch = Num(m, "epoch", double.NaN);
            a.Ut = Num(m, "ut", double.NaN);
            a.Latitude = Num(m, "latitude", double.NaN);
            a.Longitude = Num(m, "longitude", double.NaN);
            a.Altitude = Num(m, "altitude", 0);
            if (a.Motion == AnchorMotion.StateVector)
            {
                if (!(m.TryGetValue("position", out var p) && Vec3(p, out double px, out double py, out double pz)
                      && m.TryGetValue("velocity", out var v) && Vec3(v, out double vx, out double vy, out double vz)))
                {
                    why = $"anchor {a.Id}: a state vector needs position and velocity, 3 numbers each";
                    return false;
                }
                a.Position = new DVec3(px, py, pz);
                a.Velocity = new DVec3(vx, vy, vz);
            }
            return true;
        }

        internal static AnchorDirection DirectionFromWire(string s)
        {
            switch (s)
            {
                case "prograde": return AnchorDirection.Prograde;
                case "retrograde": return AnchorDirection.Retrograde;
                case "normal": return AnchorDirection.Normal;
                case "antinormal": return AnchorDirection.Antinormal;
                case "radialIn": return AnchorDirection.RadialIn;
                case "radialOut": return AnchorDirection.RadialOut;
                case "maneuver": return AnchorDirection.Maneuver;
                case "sun": return AnchorDirection.Sun;
                case "target": return AnchorDirection.Target;
                case "targetRelativeVelocity": return AnchorDirection.TargetRelativeVelocity;
            }
            return AnchorDirection.None;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static bool Vec3(object v, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (!(v is List<object> l) || l.Count != 3 || !(l[0] is double a) || !(l[1] is double b) || !(l[2] is double c)) return false;
            x = a; y = b; z = c;
            return true;
        }

        private static string Str(Dictionary<string, object> o, string key) =>
            o.TryGetValue(key, out var v) && v is string s ? s : "";

        private static double Num(Dictionary<string, object> o, string key, double fallback) =>
            o.TryGetValue(key, out var v) && v is double d ? d : fallback;

        private static bool Bool(Dictionary<string, object> o, string key) =>
            o.TryGetValue(key, out var v) && v is bool b && b;

        private static uint U32(Dictionary<string, object> o, string key)
        {
            double d = Num(o, key, 0);
            return d >= 0 && d <= uint.MaxValue && d == Math.Floor(d) ? (uint)d : 0u;
        }
    }

    /* Where one anchor landed in one frame; the wire's FrameAnchor plus its owner. */
    internal struct AnchorResult
    {
        public string Id;
        public string Owner;
        public double X, Y;
        public bool InFrame, Behind;
        /* NaN for a direction. */
        public double DistanceM;
        public double ApparentRadiusPx;
        public double AngleOffAxisDeg;
        public bool HasOrientation;
        public DVec3 Forward, Up;
        public bool HasOcclusion;
        public bool Occluded;
        /* Limited to what the picture shows: the position, size and axes only
           while in frame and not occluded, and never the range, the angle off
           axis or whether it is behind the lens. Needs HasOcclusion. */
        public bool Observed;
        /* A kinematic anchor's knowledge kept, but not the lens's pose: see AnchorRules.PoseWithheld. */
        public bool PoseWithheld;

        /* Whether the position goes out: always, unless observed and out of view, or the pose is withheld and it is out of frame. */
        public bool ShowsPosition => PoseWithheld ? InFrame : !Observed || (InFrame && HasOcclusion && !Occluded);

        /* This result as one JSON object, appended to `sb`. */
        public void AppendJson(StringBuilder sb)
        {
            bool pos = ShowsPosition;
            sb.Append("{\"id\":");
            AnchorJson.AppendString(sb, Id);
            sb.Append(",\"owner\":");
            AnchorJson.AppendString(sb, Owner);
            if (Observed) sb.Append(",\"observed\":true");
            if (pos)
            {
                sb.Append(",\"x\":").Append(AnchorJson.Num(X));
                sb.Append(",\"y\":").Append(AnchorJson.Num(Y));
            }
            sb.Append(",\"inFrame\":").Append(InFrame ? "true" : "false");
            bool pose = !Observed && !PoseWithheld;
            if (pose)
            {
                sb.Append(",\"behind\":").Append(Behind ? "true" : "false");
                if (!double.IsNaN(DistanceM)) sb.Append(",\"distanceM\":").Append(AnchorJson.Num(DistanceM));
            }
            if (pos) sb.Append(",\"apparentRadiusPx\":").Append(AnchorJson.Num(ApparentRadiusPx));
            if (pose) sb.Append(",\"angleOffAxis\":").Append(AnchorJson.Num(AngleOffAxisDeg));
            if (HasOrientation && pos)
            {
                sb.Append(",\"forward\":");
                AnchorJson.AppendVec(sb, Forward);
                sb.Append(",\"up\":");
                AnchorJson.AppendVec(sb, Up);
            }
            if (HasOcclusion) sb.Append(",\"occluded\":").Append(Occluded ? "true" : "false");
            sb.Append('}');
        }
    }

    internal static class AnchorJson
    {
        /* Finite numbers to 4 decimals; anything else as 0, since JSON has no NaN. */
        public static string Num(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "0" : Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

        public static void AppendVec(StringBuilder sb, DVec3 v)
        {
            sb.Append('[').Append(Num(v.X)).Append(',').Append(Num(v.Y)).Append(',').Append(Num(v.Z)).Append(']');
        }

        public static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /* Every anchor in the scene, keyed by owner then camera. Browser clients'
       anchors (owners "peer:...") arrive as a whole table from the sidecar and
       replace the previous one on each read; anchors set in game come through
       KerbcastControl.SetAnchors. Also remembers dock merges, so an anchor
       naming a craft that was absorbed follows it into the survivor. */
    internal sealed class AnchorRegistry
    {
        public const int MaxPerCamera = 16;
        public const string PeerPrefix = "peer:";
        /* Kerbcast's own anchors, such as a command camera's subject. */
        public const string KerbcastOwner = "kerbcast";

        private readonly Dictionary<string, Dictionary<uint, List<AnchorSpec>>> _byOwner =
            new Dictionary<string, Dictionary<uint, List<AnchorSpec>>>(StringComparer.Ordinal);
        private readonly Dictionary<uint, uint> _merged = new Dictionary<uint, uint>();

        /* Replace (or with null or an empty list, clear) a game-side owner's
           anchors on one camera. False with a reason for a reserved owner, too
           many anchors, a repeated id or an invalid anchor; nothing changes then. */
        public bool Set(string owner, uint flightId, IList<AnchorSpec> anchors, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(owner) || owner.StartsWith(PeerPrefix, StringComparison.Ordinal)
                || string.Equals(owner, KerbcastOwner, StringComparison.Ordinal))
            {
                error = $"owner '{owner}' is reserved";
                return false;
            }
            if (!Valid(anchors, out error)) return false;
            Put(owner, flightId, anchors);
            return true;
        }

        /* Forget every anchor `owner` set in game, on every camera. */
        public void Clear(string owner)
        {
            if (owner != null) _byOwner.Remove(owner);
        }

        /* Swap in the browser clients' table from the commands file. A set that
           fails validation is skipped with its reason in `skipped`. */
        public void ReplaceRemote(List<RemoteAnchorSet> sets, List<string> skipped)
        {
            var stale = new List<string>();
            foreach (var owner in _byOwner.Keys)
                if (owner.StartsWith(PeerPrefix, StringComparison.Ordinal)) stale.Add(owner);
            foreach (var owner in stale) _byOwner.Remove(owner);
            foreach (var s in sets)
            {
                if (s.Owner == null || !s.Owner.StartsWith(PeerPrefix, StringComparison.Ordinal))
                {
                    skipped?.Add($"anchors for cam={s.FlightId}: owner '{s.Owner}' is not a browser client");
                    continue;
                }
                if (!Valid(s.Anchors, out string why))
                {
                    skipped?.Add($"anchors of {s.Owner} for cam={s.FlightId}: {why}");
                    continue;
                }
                Put(s.Owner, s.FlightId, s.Anchors);
            }
        }

        /* Every anchor on camera `flightId`, as (owner, anchor), into `into`. */
        public void ForCamera(uint flightId, List<KeyValuePair<string, AnchorSpec>> into)
        {
            into.Clear();
            AddForCamera(flightId, into);
        }

        /* ForCamera after whatever `into` already holds, so anchors put first keep their place under a frame's cap. */
        public void AddForCamera(uint flightId, List<KeyValuePair<string, AnchorSpec>> into)
        {
            foreach (var kv in _byOwner)
            {
                if (!kv.Value.TryGetValue(flightId, out var list)) continue;
                foreach (var a in list) into.Add(new KeyValuePair<string, AnchorSpec>(kv.Key, a));
            }
        }

        public int Count
        {
            get
            {
                int n = 0;
                foreach (var cams in _byOwner.Values)
                    foreach (var list in cams.Values) n += list.Count;
                return n;
            }
        }

        /* A craft absorbed in a dock or claw grab lives on as the survivor. */
        public void Merged(uint oldPid, uint newPid)
        {
            if (oldPid == 0 || newPid == 0 || oldPid == newPid) return;
            _merged[oldPid] = newPid;
            /* A merge back into an absorbed id would loop; the newest merge wins. */
            _merged.Remove(newPid);
        }

        /* The craft `pid` became through any merges since this scene began. */
        public uint Resolve(uint pid)
        {
            for (int i = 0; i < 16 && pid != 0 && _merged.TryGetValue(pid, out uint next); i++) pid = next;
            return pid;
        }

        private void Put(string owner, uint flightId, IList<AnchorSpec> anchors)
        {
            if (!_byOwner.TryGetValue(owner, out var cams))
            {
                if (anchors == null || anchors.Count == 0) return;
                cams = new Dictionary<uint, List<AnchorSpec>>();
                _byOwner[owner] = cams;
            }
            if (anchors == null || anchors.Count == 0)
            {
                cams.Remove(flightId);
                if (cams.Count == 0) _byOwner.Remove(owner);
                return;
            }
            /* Copies, so a caller changing its spec later can not leave a path built from the old one. */
            var copies = new List<AnchorSpec>(anchors.Count);
            foreach (var a in anchors) copies.Add(a.Copy());
            cams[flightId] = copies;
        }

        private static bool Valid(IList<AnchorSpec> anchors, out string error)
        {
            error = null;
            if (anchors == null) return true;
            if (anchors.Count > MaxPerCamera)
            {
                error = $"at most {MaxPerCamera} anchors per camera, got {anchors.Count}";
                return false;
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in anchors)
            {
                error = a == null ? "null anchor" : a.Invalid();
                if (error != null) return false;
                if (!ids.Add(a.Id))
                {
                    error = $"anchor id {a.Id} is used twice";
                    return false;
                }
            }
            return true;
        }

        /* The browser clients' table from the commands file's "anchors". An
           unreadable file, or one without the field (an older sidecar), is an
           empty table. */
        public static List<RemoteAnchorSet> ParseRemote(string json, List<string> skipped)
        {
            var result = new List<RemoteAnchorSet>();
            object root;
            try { root = MiniJson.Parse(json); }
            catch (FormatException ex)
            {
                skipped?.Add("unreadable: " + ex.Message);
                return result;
            }
            if (!(root is Dictionary<string, object> obj) || !obj.TryGetValue("anchors", out var sets) || !(sets is List<object> list))
                return result;
            foreach (var item in list)
            {
                if (!(item is Dictionary<string, object> o)) continue;
                string owner = o.TryGetValue("owner", out var ow) && ow is string s ? s : "";
                double fid = o.TryGetValue("flightId", out var f) && f is double d ? d : -1;
                if (fid < 0 || fid > uint.MaxValue || fid != Math.Floor(fid))
                {
                    skipped?.Add($"anchors of {owner}: missing flightId");
                    continue;
                }
                var set = new RemoteAnchorSet { Owner = owner, FlightId = (uint)fid, Anchors = new List<AnchorSpec>() };
                bool ok = true;
                if (o.TryGetValue("anchors", out var arr) && arr is List<object> anchors)
                {
                    foreach (var a in anchors)
                    {
                        var spec = AnchorSpec.FromJson(a as Dictionary<string, object>, out string why);
                        if (spec == null)
                        {
                            skipped?.Add($"anchors of {owner} for cam={set.FlightId}: {why}");
                            ok = false;
                            break;
                        }
                        set.Anchors.Add(spec);
                    }
                }
                if (ok) result.Add(set);
            }
            return result;
        }
    }

    internal sealed class RemoteAnchorSet
    {
        public string Owner;
        public uint FlightId;
        public List<AnchorSpec> Anchors;
    }
}
