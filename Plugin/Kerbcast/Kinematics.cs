using System;

namespace Kerbcast
{
    /*
     * KSP's fixed frame is the z-up inertial frame Orbit.OrbitFrame and
     * Orbit.UpdateFromFixedVectors work in. Planetarium.Zup turns it into the
     * frame Orbit hands positions out in (getRelativePositionAtUT, Orbit.pos),
     * which KSP turns while the planetarium inverse-rotates and leaves where
     * it stopped afterwards, so that frame is not the fixed one even with
     * inverse rotation off; Orbit.getPositionAtUT then swizzles xzy into
     * Unity's world axes. A world-model record and a PositionAt aim are in the
     * fixed frame, so they stay valid however the planetarium has turned.
     */
    internal readonly struct ZupFrame
    {
        public readonly DVec3 X, Y, Z;

        public ZupFrame(DVec3 x, DVec3 y, DVec3 z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static ZupFrame Identity => new ZupFrame(new DVec3(1, 0, 0), new DVec3(0, 1, 0), new DVec3(0, 0, 1));

        /* A fixed-frame offset in world axes: Zup.WorldToLocal, then the xzy swizzle. */
        public DVec3 ToWorld(DVec3 v) => new DVec3(DVec3.Dot(v, X), DVec3.Dot(v, Z), DVec3.Dot(v, Y));

        /* A world-axis offset in the fixed frame: the swizzle undone, then FromLocal. */
        public DVec3 FromWorld(DVec3 w) => FromLocal(new DVec3(w.X, w.Z, w.Y));

        /* Zup.LocalToWorld: a z-up vector in the frame Orbit hands out (BodyFrame's axes, getRelativePositionAtUT) in the fixed frame. */
        public DVec3 FromLocal(DVec3 v) => X * v.X + Y * v.Y + Z * v.Z;
    }

    /*
     * One body as the kinematics need it, read from the game once per frame.
     * Its axes are KSP's BodyFrame at AxesUt in the fixed frame; the body
     * turns about the fixed z axis by SpinDegPerS, as KSP advances
     * rotationAngle.
     */
    internal struct KinematicsBody
    {
        public bool Known;
        public double Mu;
        public double Radius;
        public DVec3 AxisX, AxisY, AxisZ;
        public double SpinDegPerS;
        public double AxesUt;

        /* A point fixed to the surface at `ut`, in the fixed frame about the body centre. */
        public DVec3 SurfaceAt(double latitudeDeg, double longitudeDeg, double altitudeM, double ut, out DVec3 velocity)
        {
            double lat = latitudeDeg * Kinematics.Deg2Rad, lon = longitudeDeg * Kinematics.Deg2Rad;
            double r = Radius + altitudeM;
            double cl = Math.Cos(lat);
            var p = AxisX * (cl * Math.Cos(lon) * r) + AxisY * (cl * Math.Sin(lon) * r) + AxisZ * (Math.Sin(lat) * r);
            double w = SpinDegPerS * Kinematics.Deg2Rad;
            double turn = w * (ut - AxesUt);
            double c = Math.Cos(turn), s = Math.Sin(turn);
            var at = new DVec3(p.X * c - p.Y * s, p.X * s + p.Y * c, p.Z);
            velocity = new DVec3(-w * at.Y, w * at.X, 0);
            return at;
        }
    }

    /*
     * A path on one conic about one body: a state at T0, propagated by
     * universal variables, so ellipses, hyperbolae and near-parabolic
     * ballistic arcs share one solver. A conic from elements starts at its
     * periapsis passage. Built once per record per snapshot; a read allocates
     * nothing.
     */
    internal struct KeplerPath
    {
        private const int MaxIterations = 50;
        /* A residual within this many rounding errors of the terms it sums is converged. */
        private const double ResidualUlps = 8.0 * 2.220446049250313e-16;
        /* After the cap, the best iterate is kept if its residual is this small against its terms. */
        private const double BestResidual = 1e-10;
        /* ...and its residual, as time, this close to the asked UT (plus 1e-9 of the span). */
        private const double BestResidualS = 1e-3;
        /* Below this |alpha| * r0 the conic is near enough a parabola that the conic guesses mislead. */
        private const double NearParabolic = 1e-8;

        public DVec3 R0, V0;
        public double T0;
        public double Mu;
        /* 1/a: positive for an ellipse, negative for a hyperbola. */
        public double Alpha;
        /* The fastest point of the conic, for the forecast's speed bounds. */
        public double PeriapsisSpeed;

        private double _r0, _sigma0, _sqrtMu, _period;

        /* Ballistic from a fixed-frame state at `ut` under `mu`, no thrust. `bodyRadius` (0 for unknown) bounds a fall that would reach the centre. */
        public static bool TryFromState(DVec3 position, DVec3 velocity, double ut, double mu, out KeplerPath path,
            double bodyRadius = 0)
        {
            path = default;
            double r0 = position.Magnitude;
            if (!(mu > 0) || !(r0 > 0) || !Finite(position) || !Finite(velocity) || !Kinematics.Finite(ut)) return false;
            double v2 = DVec3.Dot(velocity, velocity);
            double alpha = 2.0 / r0 - v2 / mu;
            var h = DVec3.Cross(position, velocity);
            double p = DVec3.Dot(h, h) / mu;
            var eVec = position * (v2 / mu - 1.0 / r0) - velocity * (DVec3.Dot(position, velocity) / mu);
            double e = eVec.Magnitude;
            /* The fastest it can go is at the lowest point it reaches: periapsis, or the surface when that is lower. */
            double rp = p / (1.0 + e);
            double lowest = Math.Max(rp, Math.Min(bodyRadius, r0));
            double fastest = lowest > 0 ? Math.Sqrt(Math.Max(v2, v2 + 2.0 * mu * (1.0 / lowest - 1.0 / r0))) : Math.Sqrt(v2);
            path = Make(position, velocity, ut, mu, alpha, fastest);
            return path.PeriapsisSpeed >= 0 && Kinematics.Finite(path.PeriapsisSpeed);
        }

        /*
         * KSP Orbit elements: inclination, longitude of the ascending node and
         * argument of periapsis in degrees, semi-major axis in metres (negative
         * for a hyperbola, as KSP keeps it), mean anomaly at `epoch` in
         * radians. The orbital frame is Planetarium.CelestialFrame.OrbitalFrame's.
         */
        public static bool TryFromElements(double incDeg, double e, double sma, double lanDeg, double argPeDeg,
            double meanAnomalyAtEpoch, double epoch, double mu, out KeplerPath path)
        {
            path = default;
            if (!(mu > 0) || !(e >= 0) || e == 1.0 || !Kinematics.Finite(sma) || !Kinematics.Finite(meanAnomalyAtEpoch)
                || !Kinematics.Finite(epoch))
                return false;
            if (e < 1.0 ? !(sma > 0) : !(sma < 0)) return false;
            Kinematics.OrbitalFrame(lanDeg * Kinematics.Deg2Rad, incDeg * Kinematics.Deg2Rad, argPeDeg * Kinematics.Deg2Rad,
                out var x, out var y);
            double rp = sma * (1.0 - e);
            double vp = Math.Sqrt(mu * (1.0 + e) / rp);
            double n = Math.Sqrt(mu / Math.Abs(sma * sma * sma));
            path = Make(x * rp, y * vp, epoch - meanAnomalyAtEpoch / n, mu, 1.0 / sma, vp);
            return Kinematics.Finite(path.T0) && Kinematics.Finite(vp);
        }

        private static KeplerPath Make(DVec3 r, DVec3 v, double t0, double mu, double alpha, double periapsisSpeed)
        {
            var path = new KeplerPath
            {
                R0 = r,
                V0 = v,
                T0 = t0,
                Mu = mu,
                Alpha = alpha,
                PeriapsisSpeed = periapsisSpeed,
                _r0 = r.Magnitude,
                _sqrtMu = Math.Sqrt(mu),
            };
            path._sigma0 = DVec3.Dot(r, v) / path._sqrtMu;
            path._period = alpha > 0 ? 2.0 * Math.PI / Math.Sqrt(mu * alpha * alpha * alpha) : double.PositiveInfinity;
            return path;
        }

        /* Position and velocity at `ut` in the fixed frame about the body centre. False when the solve does not converge or overflows. */
        public bool TryAt(double ut, out DVec3 position, out DVec3 velocity)
        {
            position = velocity = default;
            if (!(_r0 > 0) || !Kinematics.Finite(ut)) return false;
            double dt = ut - T0;
            if (Alpha > 0)
            {
                dt %= _period;
                if (dt > _period * 0.5) dt -= _period;
                else if (dt < -_period * 0.5) dt += _period;
            }
            if (dt == 0)
            {
                position = R0;
                velocity = V0;
                return true;
            }
            double a = Alpha, r0 = _r0, s0 = _sigma0, sqrtMu = _sqrtMu;
            double k = 1.0 - a * r0;
            double target = sqrtMu * dt;
            double chi = Guess(dt);
            double bestChi = chi, bestRel = double.PositiveInfinity, bestF = double.PositiveInfinity, lastStep = double.PositiveInfinity;
            bool done = false;
            for (int i = 0; i < MaxIterations; i++)
            {
                double chi2 = chi * chi;
                double z = a * chi2;
                Kinematics.Stumpff(z, out double cz, out double sz);
                double t1 = s0 * chi2 * cz, t2 = k * chi2 * chi * sz, t3 = r0 * chi;
                double f = t1 + t2 + t3 - target;
                double fp = s0 * chi * (1.0 - z * sz) + k * chi2 * cz + r0;
                double fpp = s0 * (1.0 - z * cz) + k * chi * (1.0 - z * sz);
                /*
                 * Converged once the residual is down to the rounding of the
                 * terms it sums: a relative step test alone can ask for more
                 * than double precision holds, and the iterate then bounces.
                 */
                double scale = Math.Abs(t1) + Math.Abs(t2) + Math.Abs(t3) + Math.Abs(target);
                double rel = scale > 0 ? Math.Abs(f) / scale : 0.0;
                if (!Kinematics.Finite(rel)) return false;
                if (rel < bestRel)
                {
                    bestRel = rel;
                    bestChi = chi;
                    bestF = Math.Abs(f);
                }
                if (rel <= ResidualUlps)
                {
                    done = true;
                    break;
                }
                /* Laguerre's step (n = 5): it converges from a poor guess on every conic. */
                const double N = 5.0;
                double disc = Math.Sqrt(Math.Abs((N - 1) * (N - 1) * fp * fp - N * (N - 1) * f * fpp));
                double den = fp + (fp >= 0 ? disc : -disc);
                if (den == 0 || !Kinematics.Finite(den)) break;
                double step = N * f / den;
                /* A step that stops shrinking is rounding, not progress. */
                if (Math.Abs(step) >= lastStep && bestRel <= BestResidual) break;
                lastStep = Math.Abs(step);
                chi -= step;
                if (!Kinematics.Finite(chi)) break;
            }
            if (!done)
            {
                /*
                 * The best iterate must also be close in time, not only small
                 * against its terms: on a very eccentric hyperbola far outside
                 * any sphere of influence the terms are huge, and a residual
                 * small beside them is still a wrong place.
                 */
                if (!(bestRel <= BestResidual) || !(bestF / sqrtMu <= BestResidualS + 1e-9 * Math.Abs(dt))) return false;
                chi = bestChi;
            }
            double cc = chi * chi;
            Kinematics.Stumpff(a * cc, out double c, out double s);
            double r = s0 * chi * (1.0 - a * cc * s) + k * cc * c + r0;
            if (!(r > 0)) return false;
            double x2 = chi * chi, x3 = x2 * chi;
            double fc = 1.0 - x2 * c / r0;
            double gc = dt - x3 * s / sqrtMu;
            double fdot = sqrtMu / (r * r0) * chi * (a * x2 * s - 1.0);
            double gdot = 1.0 - x2 * c / r;
            position = R0 * fc + V0 * gc;
            velocity = R0 * fdot + V0 * gdot;
            return Finite(position) && Finite(velocity);
        }

        private double Guess(double dt)
        {
            bool conic = Math.Abs(Alpha) * _r0 > NearParabolic;
            if (conic && Alpha > 0) return _sqrtMu * Alpha * dt;
            if (conic && Alpha < 0)
            {
                double aa = 1.0 / Alpha;
                double sign = dt >= 0 ? 1.0 : -1.0;
                double arg = -2.0 * Mu * Alpha * dt / (_sigma0 * _sqrtMu + sign * Math.Sqrt(-Mu * aa) * (1.0 - _r0 * Alpha));
                if (arg > 0 && Kinematics.Finite(arg)) return sign * Math.Sqrt(-aa) * Math.Log(arg);
            }
            return _sqrtMu * dt / _r0;
        }

        private static bool Finite(DVec3 v) => Kinematics.Finite(v.X) && Kinematics.Finite(v.Y) && Kinematics.Finite(v.Z);
    }

    internal static class Kinematics
    {
        public const double Deg2Rad = Math.PI / 180.0;

        public static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /* Planetarium.CelestialFrame.SetFrame(LAN, inc, argPe) in radians: X toward periapsis, Y a quarter turn on in the direction of motion. */
        public static void OrbitalFrame(double lan, double inc, double argPe, out DVec3 x, out DVec3 y)
        {
            double ca = Math.Cos(lan), sa = Math.Sin(lan);
            double cb = Math.Cos(inc), sb = Math.Sin(inc);
            double cc = Math.Cos(argPe), sc = Math.Sin(argPe);
            x = new DVec3(ca * cc - sa * cb * sc, sa * cc + ca * cb * sc, sb * sc);
            y = new DVec3(-ca * sc - sa * cb * cc, -sa * sc + ca * cb * cc, sb * cc);
        }

        /* The Stumpff functions C(z) and S(z), by series near zero where the closed forms cancel. */
        public static void Stumpff(double z, out double c, out double s)
        {
            if (z > 0.1)
            {
                double q = Math.Sqrt(z);
                c = (1.0 - Math.Cos(q)) / z;
                s = (q - Math.Sin(q)) / (q * z);
            }
            else if (z < -0.1)
            {
                double q = Math.Sqrt(-z);
                c = (Math.Cosh(q) - 1.0) / -z;
                s = (Math.Sinh(q) - q) / (q * -z);
            }
            else
            {
                double tc = 0.5, ts = 1.0 / 6.0;
                c = tc;
                s = ts;
                for (int k = 1; k < 8; k++)
                {
                    tc *= -z / ((2 * k + 1) * (2 * k + 2));
                    ts *= -z / ((2 * k + 2) * (2 * k + 3));
                    c += tc;
                    s += ts;
                }
            }
        }

        /*
         * The free-fall uncertainty radius `dt` seconds after a record's
         * ValidFromUt: r0 + rate*dt, plus half accel*dt^2 until the speed cap
         * is reached at dt = cap/accel and linear at the cap after, the shape
         * of a world model's usual position uncertainty bound. Never below r0.
         */
        public static double UncertaintyAt(double r0, double rateMps, double accelMps2, double speedCapMps, double dt)
        {
            if (!(dt > 0)) return r0;
            double free = accelMps2 > 0 && dt > speedCapMps / accelMps2
                ? speedCapMps * dt - speedCapMps * speedCapMps / (2.0 * accelMps2)
                : 0.5 * accelMps2 * dt * dt;
            return r0 + rateMps * dt + free;
        }

        /*
         * Whether a craft is too uncertain to aim at: the uncertainty reaches
         * the lens, or the craft's radius plus the uncertainty plus the
         * operator's margin does not fit the widest field at the nearest the
         * craft could be (range minus uncertainty). No uncertainty, or no
         * range, is never too uncertain.
         */
        public static bool TooUncertain(double uncertaintyM, double radiusM, double marginM, double rangeM, double fovMaxDeg)
        {
            if (!(uncertaintyM > 0) || double.IsNaN(rangeM)) return false;
            if (uncertaintyM >= rangeM) return true;
            return !CommandCameraMath.FramingFits(radiusM + uncertaintyM + marginM, rangeM - uncertaintyM, fovMaxDeg);
        }
    }
}
