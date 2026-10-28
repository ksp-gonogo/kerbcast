using System;

namespace Kerbcast
{
    /* Double-precision rotation with the same Hamilton algebra as Unity's and
       KSP's quaternions, so a rotation read from a transform composes and
       rotates vectors here exactly as it does in the game. */
    internal readonly struct DQuat
    {
        public readonly double X, Y, Z, W;

        public DQuat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }

        public static DQuat Identity => new DQuat(0, 0, 0, 1);

        public static DQuat operator *(DQuat a, DQuat b) => new DQuat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public DQuat Conjugate => new DQuat(-X, -Y, -Z, W);

        public DQuat Normalized
        {
            get
            {
                double m = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
                return m > 0 ? new DQuat(X / m, Y / m, Z / m, W / m) : Identity;
            }
        }

        public DVec3 Rotate(DVec3 v)
        {
            var u = new DVec3(X, Y, Z);
            var t = DVec3.Cross(u, v) * 2;
            return v + t * W + DVec3.Cross(u, t);
        }

        /* The rotation by |w| radians about w, right-handed in the same algebra
           as DVec3.Cross: a point p moves at w x p. */
        public static DQuat FromRotationVector(DVec3 w)
        {
            double angle = w.Magnitude;
            if (angle < 1e-15) return new DQuat(w.X * 0.5, w.Y * 0.5, w.Z * 0.5, 1).Normalized;
            double s = Math.Sin(angle * 0.5) / angle;
            return new DQuat(w.X * s, w.Y * s, w.Z * s, Math.Cos(angle * 0.5));
        }

        /* The rotation vector taking `from` to `to` (to = R * from), shortest way round. */
        public static DVec3 RotationVector(DQuat from, DQuat to)
        {
            var d = (to * from.Conjugate).Normalized;
            if (d.W < 0) d = new DQuat(-d.X, -d.Y, -d.Z, -d.W);
            double s = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            if (s < 1e-15) return new DVec3(d.X * 2, d.Y * 2, d.Z * 2);
            double angle = 2 * Math.Atan2(s, d.W);
            return new DVec3(d.X, d.Y, d.Z) * (angle / s);
        }

        public static double AngleBetween(DQuat a, DQuat b)
        {
            double dot = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
            return dot >= 1 ? 0 : 2 * Math.Acos(dot);
        }

        public static DQuat Slerp(DQuat a, DQuat b, double s)
        {
            double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            if (dot < 0) { b = new DQuat(-b.X, -b.Y, -b.Z, -b.W); dot = -dot; }
            if (dot > 0.9999995)
                return new DQuat(a.X + (b.X - a.X) * s, a.Y + (b.Y - a.Y) * s, a.Z + (b.Z - a.Z) * s, a.W + (b.W - a.W) * s).Normalized;
            double th = Math.Acos(dot), sin = Math.Sin(th);
            double wa = Math.Sin((1 - s) * th) / sin, wb = Math.Sin(s * th) / sin;
            return new DQuat(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb);
        }
    }

    /* How an entry's state carries forward to later instants. */
    internal enum HistoryKind : byte
    {
        /* A free conic about the body: Kepler from the stored position and velocity. */
        Conic,
        /* At rest on the body's surface: turns with the body's spin. The spin is
           kept in the velocity slots, in double precision, since a float rate
           drifts tens of metres over a long horizon; the velocity is spin x position. */
        Surface,
    }

    /* Why an entry was added. Reads use it to pick Kepler or Hermite for the
       span that ends at the entry. */
    internal enum HistoryReason : byte
    {
        Start,
        /* The state left the ballistic prediction of the entry before. */
        Departure,
        /* The last tick still on the coast, added when a departure follows a long coast,
           so the Hermite span after it is one tick long rather than the whole coast. */
        CoastEnd,
        SoiChange,
        /* The old body's last state, at the same UT as the SoiChange entry after it. */
        SoiExit,
        Load,
        Unload,
        OnRails,
        OffRails,
        Split,
    }

    /* One sampled state, kept compact: 96 bytes. Positions and velocities are
       relative to the body, in the ring's inertial frame; attitude and angular
       velocity are in the same frame and need only float precision. */
    internal struct HistoryEntry
    {
        public double Ut;
        public double Rx, Ry, Rz;
        public double Vx, Vy, Vz;
        public float Qx, Qy, Qz, Qw;
        public float Wx, Wy, Wz;
        public int Body;
        public HistoryKind Kind;
        public HistoryReason Reason;

        public DVec3 Position => new DVec3(Rx, Ry, Rz);
        public DVec3 Velocity => Kind == HistoryKind.Surface
            ? DVec3.Cross(new DVec3(Vx, Vy, Vz), Position)
            : new DVec3(Vx, Vy, Vz);
        public DQuat Attitude => new DQuat(Qx, Qy, Qz, Qw);
        public DVec3 AngularVelocity => Kind == HistoryKind.Surface
            ? new DVec3(Vx, Vy, Vz)
            : new DVec3(Wx, Wy, Wz);
    }

    /* A state read from the ring, in full precision. */
    internal struct HistoryState
    {
        public double Ut;
        public int Body;
        public HistoryKind Kind;
        public DVec3 Position;
        public DVec3 Velocity;
        public DQuat Attitude;
        public DVec3 AngularVelocity;

        public HistoryEntry ToEntry(HistoryReason reason)
        {
            var q = Attitude.Normalized;
            var stored = Kind == HistoryKind.Surface ? AngularVelocity : Velocity;
            return new HistoryEntry
            {
                Ut = Ut, Body = Body, Kind = Kind, Reason = reason,
                Rx = Position.X, Ry = Position.Y, Rz = Position.Z,
                Vx = stored.X, Vy = stored.Y, Vz = stored.Z,
                Qx = (float)q.X, Qy = (float)q.Y, Qz = (float)q.Z, Qw = (float)q.W,
                Wx = (float)AngularVelocity.X, Wy = (float)AngularVelocity.Y, Wz = (float)AngularVelocity.Z,
            };
        }
    }

    /* What the ring needs to know of a body. MaxOrbitalSpeed bounds its speed
       about its parent; zero for the root. PeriapsisM is the nearest its orbit
       comes to its parent, zero where that is not known. */
    internal struct HistoryBody
    {
        public int Index;
        public int Parent;
        public double Mu;
        public double Radius;
        public double SoiRadius;
        public double MaxOrbitalSpeed;
        public double PeriapsisM;
    }

    internal static class LightHistoryMath
    {
        public const double PositionToleranceM = 1.0;
        public const double AngleToleranceRad = 0.1 * Math.PI / 180.0;
        /* The 10 Hz cap on gated entries. */
        public const double MinIntervalS = 0.1;
        /* A departure this long after the last entry first records the coast's last tick. */
        public const double CoastWindowS = 1.0;
        /* Hermite spans no longer than this; anything longer reads by prediction. */
        public const double HermiteMaxGapS = 2.0;

        /* Universal-variable Kepler propagation, valid for every eccentricity. */
        public static void Kepler(DVec3 r0, DVec3 v0, double mu, double dt, out DVec3 r, out DVec3 v)
        {
            double r0n = r0.Magnitude;
            if (mu <= 0 || r0n <= 0 || dt == 0)
            {
                r = r0 + v0 * dt;
                v = v0;
                return;
            }
            double sqrtMu = Math.Sqrt(mu);
            double vr0 = DVec3.Dot(r0, v0) / r0n;
            double alpha = 2.0 / r0n - DVec3.Dot(v0, v0) / mu;

            /* Whole periods change nothing on a closed orbit and only cost precision. */
            if (alpha > 1e-14)
            {
                double period = 2 * Math.PI / (sqrtMu * alpha * Math.Sqrt(alpha));
                if (Math.Abs(dt) > period) dt = Math.IEEERemainder(dt, period);
            }

            double chi;
            if (alpha > 1e-14)
                chi = sqrtMu * alpha * dt;
            else if (alpha < -1e-14)
            {
                double a = 1 / alpha;
                double sign = Math.Sign(dt);
                double denom = DVec3.Dot(r0, v0) + sign * Math.Sqrt(-mu * a) * (1 - r0n * alpha);
                double arg = -2 * mu * alpha * dt / denom;
                chi = arg > 0 ? sign * Math.Sqrt(-a) * Math.Log(arg) : sqrtMu * dt / r0n;
            }
            else
                chi = sqrtMu * dt / r0n;

            double c = 0, s = 0;
            for (int i = 0; i < 60; i++)
            {
                double chi2 = chi * chi;
                Stumpff(alpha * chi2, out c, out s);
                double f = r0n * vr0 / sqrtMu * chi2 * c + (1 - alpha * r0n) * chi2 * chi * s + r0n * chi - sqrtMu * dt;
                double df = r0n * vr0 / sqrtMu * chi * (1 - alpha * chi2 * s) + (1 - alpha * r0n) * chi2 * c + r0n;
                double step = f / df;
                /* Damped so a poor first guess on a hyperbola cannot throw chi past the root. */
                if (Math.Abs(step) > Math.Abs(chi) + 1) step = Math.Sign(step) * (Math.Abs(chi) + 1);
                chi -= step;
                if (Math.Abs(step) <= 1e-12 * Math.Max(1, Math.Abs(chi))) break;
            }
            {
                double chi2 = chi * chi;
                Stumpff(alpha * chi2, out c, out s);
                double fl = 1 - chi2 / r0n * c;
                double gl = dt - chi2 * chi / sqrtMu * s;
                r = r0 * fl + v0 * gl;
                double rn = r.Magnitude;
                double fdot = sqrtMu / (rn * r0n) * (alpha * chi2 * chi * s - chi);
                double gdot = 1 - chi2 / rn * c;
                v = r0 * fdot + v0 * gdot;
            }
        }

        private static void Stumpff(double z, out double c, out double s)
        {
            if (z > 1e-6)
            {
                double sz = Math.Sqrt(z);
                c = (1 - Math.Cos(sz)) / z;
                s = (sz - Math.Sin(sz)) / (sz * z);
            }
            else if (z < -1e-6)
            {
                double sz = Math.Sqrt(-z);
                c = (Math.Cosh(sz) - 1) / -z;
                s = (Math.Sinh(sz) - sz) / (sz * -z);
            }
            else
            {
                c = 0.5 - z / 24 + z * z / 720;
                s = 1.0 / 6 - z / 120 + z * z / 5040;
            }
        }

        /* Cubic Hermite between two states, with its derivative. */
        public static void Hermite(DVec3 p0, DVec3 v0, DVec3 p1, DVec3 v1, double h, double t, out DVec3 p, out DVec3 v)
        {
            double s = h > 0 ? t / h : 0, s2 = s * s, s3 = s2 * s;
            p = p0 * (2 * s3 - 3 * s2 + 1) + v0 * (h * (s3 - 2 * s2 + s)) + p1 * (-2 * s3 + 3 * s2) + v1 * (h * (s3 - s2));
            if (h <= 0) { v = v0; return; }
            v = p0 * ((6 * s2 - 6 * s) / h) + v0 * (3 * s2 - 4 * s + 1) + p1 * ((-6 * s2 + 6 * s) / h) + v1 * (3 * s2 - 2 * s);
        }

        /* An entry carried forward to `ut` with nothing acting on it but gravity
           (or the body's spin, on the surface) and a constant turn rate. */
        public static HistoryState Predict(in HistoryEntry e, double mu, double ut)
        {
            double dt = ut - e.Ut;
            var w = e.AngularVelocity;
            var turn = DQuat.FromRotationVector(w * dt);
            var s = new HistoryState { Ut = ut, Body = e.Body, Kind = e.Kind, AngularVelocity = w, Attitude = (turn * e.Attitude).Normalized };
            if (e.Kind == HistoryKind.Surface)
            {
                s.Position = turn.Rotate(e.Position);
                s.Velocity = DVec3.Cross(w, s.Position);
            }
            else
            {
                Kepler(e.Position, e.Velocity, mu, dt, out s.Position, out s.Velocity);
            }
            return s;
        }

        /* Hermite between two entries on the same body, attitude by slerp. */
        public static HistoryState Interpolate(in HistoryEntry a, in HistoryEntry b, double ut)
        {
            double h = b.Ut - a.Ut;
            Hermite(a.Position, a.Velocity, b.Position, b.Velocity, h, ut - a.Ut, out var p, out var v);
            double s = h > 0 ? (ut - a.Ut) / h : 0;
            return new HistoryState
            {
                Ut = ut, Body = a.Body, Kind = b.Kind, Position = p, Velocity = v,
                Attitude = DQuat.Slerp(a.Attitude, b.Attitude, s),
                AngularVelocity = a.AngularVelocity + (b.AngularVelocity - a.AngularVelocity) * s,
            };
        }

        /* Whether the span from a to b reads by Hermite: b was added because the
           state left a's prediction, on the same body, and soon after a. */
        public static bool Hermite(in HistoryEntry a, in HistoryEntry b) =>
            b.Reason == HistoryReason.Departure && a.Body == b.Body && b.Ut - a.Ut <= HermiteMaxGapS;

        /* The gate test: has the state left the entry's prediction? */
        public static bool Departs(in HistoryEntry last, double mu, in HistoryState now, bool checkAttitude)
        {
            var p = Predict(last, mu, now.Ut);
            if ((p.Position - now.Position).Magnitude > PositionToleranceM) return true;
            return checkAttitude && DQuat.AngleBetween(p.Attitude, now.Attitude) > AngleToleranceRad;
        }

        /* Largest radius and speed a conic reaches about its body: apoapsis on a
           closed orbit, the SOI radius for an open one or one that leaves the SOI;
           speed at periapsis, since a Kepler read does not stop at the surface. */
        public static void ConicBound(DVec3 r0, DVec3 v0, double mu, double soiRadius,
            out double maxRadius, out double maxSpeed)
        {
            double rn = r0.Magnitude, v2 = DVec3.Dot(v0, v0);
            if (mu <= 0 || rn <= 0)
            {
                maxRadius = double.PositiveInfinity;
                maxSpeed = Math.Sqrt(v2);
                return;
            }
            double energy = v2 / 2 - mu / rn;
            double h = DVec3.Cross(r0, v0).Magnitude;
            double e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h * h / (mu * mu)));
            double rp = h * h / (mu * (1 + e));
            if (energy >= 0 || e >= 1)
                maxRadius = soiRadius;
            else
            {
                double apo = -mu / (2 * energy) * (1 + e);
                maxRadius = apo > soiRadius ? soiRadius : apo;
            }
            maxRadius = Math.Max(maxRadius, rn);
            maxSpeed = rp > 0 ? Math.Max(Math.Sqrt(Math.Max(0, 2 * (energy + mu / rp))), Math.Sqrt(v2)) : double.PositiveInfinity;
        }

        /* Exact bounds of a Hermite span from its Bezier hull. */
        public static void HermiteBound(in HistoryEntry a, in HistoryEntry b, out double maxRadius, out double maxSpeed)
        {
            double h = b.Ut - a.Ut;
            var b0 = a.Position;
            var b1 = a.Position + a.Velocity * (h / 3);
            var b2 = b.Position - b.Velocity * (h / 3);
            var b3 = b.Position;
            maxRadius = Math.Max(Math.Max(b0.Magnitude, b1.Magnitude), Math.Max(b2.Magnitude, b3.Magnitude));
            double mid = h > 0 ? ((b2 - b1) * (3 / h)).Magnitude : 0;
            maxSpeed = Math.Max(Math.Max(a.Velocity.Magnitude, b.Velocity.Magnitude), mid);
        }

        /* Bounds of an entry's own read forward: its conic, or its spot on the surface. */
        public static void EntryBound(in HistoryEntry e, in HistoryBody body, out double maxRadius, out double maxSpeed)
        {
            if (e.Kind == HistoryKind.Surface)
            {
                maxRadius = e.Position.Magnitude;
                maxSpeed = DVec3.Cross(e.AngularVelocity, e.Position).Magnitude;
                return;
            }
            ConicBound(e.Position, e.Velocity, body.Mu, body.SoiRadius, out maxRadius, out maxSpeed);
        }

        /* Seconds of history a lens can need: the longest distance between two
           points in the system over the light speed. */
        public static double HorizonS(double systemDiameterM, double lightSpeed) =>
            lightSpeed > 0 ? systemDiameterM / lightSpeed : double.PositiveInfinity;
    }
}
