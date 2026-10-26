using System;
using System.Collections.Generic;

namespace Kerbcast
{
    internal enum BodyMotionKind { Orbit, Surface, Linear }

    /* How a point moves through time: on a conic, fixed to its body's
       surface, or extrapolated linearly from a body-relative state. */
    internal struct BodyMotion
    {
        public BodyMotionKind Kind;
        public Orbit Orbit;
        public CelestialBody Body;
        /* Surface: offset from the body centre now, world axes. */
        public DVec3 RelNow;
        /* Linear: body-relative z-up position at RefUt and velocity. */
        public Vector3d Rel, Vel;
        public double RefUt;

        public static BodyMotion OnOrbit(Orbit o) =>
            new BodyMotion { Kind = BodyMotionKind.Orbit, Orbit = o, Body = o.referenceBody };

        public static BodyMotion OnSurface(CelestialBody body, Vector3d worldNow) =>
            new BodyMotion
            {
                Kind = BodyMotionKind.Surface, Body = body,
                RelNow = new DVec3(worldNow.x - body.position.x, worldNow.y - body.position.y, worldNow.z - body.position.z),
            };

        public static BodyMotion Linear(CelestialBody body, Vector3d rel, Vector3d vel, double refUt) =>
            new BodyMotion { Kind = BodyMotionKind.Linear, Body = body, Rel = rel, Vel = vel, RefUt = refUt };
    }

    /*
     * Forecasts a command feed's camera-axis edges (occlusion and sunlight at
     * the aimed point) a few seconds of real time ahead, so a client reading
     * the status file late still switches on the right frame.
     *
     * Every position at a future UT is taken in the current world frame:
     * body centres from getTruePositionAtUT, conics from
     * getRelativePositionAtUT, and surface points carried round by their
     * body's spin. Occlusion goes through CommandCameraOcclusion.Blocks,
     * the same test the rig records with.
     *
     * Begin drops every body that cannot reach either sight line (station
     * to aim, aim to Sun) within the horizon, from a speed bound on each
     * point, so a sample only positions the bodies that matter. Speeds are
     * taken relative to the station's body: moving every point by the same
     * offset leaves each distance unchanged.
     */
    internal sealed class CommandCameraForecast
    {
        /* Margin on the periapsis speed bounds, for frames and rounding. */
        private const double SpeedMargin = 1.1;

        private readonly CommandCameraOcclusion _occlusion;
        private readonly List<CelestialBody> _bodies = new List<CelestialBody>();
        private readonly List<int> _occluders = new List<int>();
        private readonly List<int> _shadowers = new List<int>();
        private DVec3[] _centres = new DVec3[0];
        private double[] _centresAt = new double[0];
        private DVec3[] _centresNow = new DVec3[0];
        private DVec3[] _spins = new DVec3[0];
        private int _sun = -1;
        private double _now;
        private BodyMotion _station, _aim;
        private int _stationBody, _aimBody;

        /* StateAt calls since the last Begin. */
        public int LastSamples { get; private set; }

        private double _aimRadius;

        public CommandCameraForecast(CommandCameraOcclusion occlusion)
        {
            _occlusion = occlusion;
        }

        public bool Begin(double nowUt, double horizon, BodyMotion station, BodyMotion aim, double aimRadius)
        {
            LastSamples = 0;
            _aimRadius = aimRadius;
            var bodies = FlightGlobals.Bodies;
            if (bodies == null || bodies.Count == 0) return false;
            _bodies.Clear();
            _bodies.AddRange(bodies);
            int n = _bodies.Count;
            if (_centres.Length != n)
            {
                _centres = new DVec3[n];
                _centresAt = new double[n];
                _centresNow = new DVec3[n];
                _spins = new DVec3[n];
            }
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            _sun = -1;
            for (int i = 0; i < n; i++)
            {
                _centresAt[i] = double.NaN;
                var b = _bodies[i];
                if (b == null) continue;
                if (b == sun) _sun = i;
                _centresNow[i] = ToD(b.position);
                _spins[i] = CommandCameraOcclusion.Spin(b);
            }
            _now = nowUt;
            _station = station;
            _aim = aim;
            _stationBody = _bodies.IndexOf(station.Body);
            _aimBody = _bodies.IndexOf(aim.Body);
            if (_stationBody < 0 || _aimBody < 0) return false;
            PickBodies(horizon);
            return true;
        }

        private void PickBodies(double horizon)
        {
            _occluders.Clear();
            _shadowers.Clear();
            double t1 = _now + horizon;
            var s0 = PositionAt(_station, _stationBody, _now);
            var a0 = PositionAt(_aim, _aimBody, _now);
            var s1 = PositionAt(_station, _stationBody, t1);
            var a1 = PositionAt(_aim, _aimBody, t1);
            var frame = _bodies[_stationBody];
            double vs = SpeedBound(_station, frame), va = SpeedBound(_aim, frame);
            bool lightKnown = _sun >= 0;
            DVec3 sun0 = default, sun1 = default;
            double vSun = 0;
            if (lightKnown)
            {
                sun0 = CentreAt(_sun, _now);
                sun1 = CentreAt(_sun, t1);
                vSun = BodySpeedBound(_bodies[_sun], frame);
            }
            var mask = _occlusion.MaskBody;
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                if (b == null) continue;
                var c0 = CentreAt(i, _now);
                var c1 = CentreAt(i, t1);
                double vb = BodySpeedBound(b, frame);
                if (b == mask
                    || CommandCameraMath.MayReach(CommandCameraMath.DistanceToSegment(c0, s0, a0),
                        CommandCameraMath.DistanceToSegment(c1, s1, a1), vb + Math.Max(vs, va), horizon,
                        CommandCameraOcclusion.BoundRadius(b)))
                    _occluders.Add(i);
                if (lightKnown && i != _sun
                    && CommandCameraMath.MayReach(CommandCameraMath.DistanceToSegment(c0, a0, sun0),
                        CommandCameraMath.DistanceToSegment(c1, a1, sun1), vb + Math.Max(va, vSun), horizon, b.Radius))
                    _shadowers.Add(i);
            }
        }

        /* Occlusion and sunlight at `t`; blank is left unset. */
        public CommandTrackEvent StateAt(double t)
        {
            LastSamples++;
            var station = PositionAt(_station, _stationBody, t);
            var aim = PositionAt(_aim, _aimBody, t);
            double dt = t - _now;
            string occluder = null;
            for (int k = 0; k < _occluders.Count; k++)
            {
                int i = _occluders[k];
                if (_occlusion.Blocks(_bodies[i], station, aim, CentreAt(i, t), _centresNow[i], _spins[i], dt, out _, _aimRadius))
                {
                    occluder = _bodies[i].bodyName;
                    break;
                }
            }
            return new CommandTrackEvent(t, occluder != null, occluder, Sunlit(aim, t), null, true);
        }

        private DVec3 PositionAt(BodyMotion m, int body, double t)
        {
            var c = CentreAt(body, t);
            switch (m.Kind)
            {
                case BodyMotionKind.Orbit:
                    return c + ToD(m.Orbit.getRelativePositionAtUT(t).xzy);
                case BodyMotionKind.Surface:
                    return CommandCameraMath.CarriedByBody(m.RelNow, c, _spins[body], t - _now);
                default:
                    return c + ToD((m.Rel + m.Vel * (t - m.RefUt)).xzy);
            }
        }

        private DVec3 CentreAt(int i, double t)
        {
            if (_centresAt[i] != t)
            {
                _centresAt[i] = t;
                _centres[i] = ToD(_bodies[i].getTruePositionAtUT(t));
            }
            return _centres[i];
        }

        /* Fastest a point on this motion can move relative to `frame`: its
           own speed at periapsis (or spin, or drift) plus its body's. */
        private static double SpeedBound(BodyMotion m, CelestialBody frame)
        {
            double own;
            switch (m.Kind)
            {
                case BodyMotionKind.Orbit:
                    own = PeriapsisSpeed(m.Orbit);
                    break;
                case BodyMotionKind.Surface:
                    own = CommandCameraOcclusion.Spin(m.Body).Magnitude * m.RelNow.Magnitude;
                    break;
                default:
                    own = m.Vel.magnitude;
                    break;
            }
            return own * SpeedMargin + BodySpeedBound(m.Body, frame);
        }

        /* A body's speed relative to `frame`: the periapsis speeds up its
           chain of parents to `frame`, or to the root plus frame's own when
           `frame` is not among them. */
        private static double BodySpeedBound(CelestialBody b, CelestialBody frame)
        {
            double v = 0;
            for (int guard = 0; b != null && guard < 16; guard++)
            {
                if (b == frame) return v;
                if (b.orbit == null) break;
                v += PeriapsisSpeed(b.orbit) * SpeedMargin;
                b = b.orbit.referenceBody;
            }
            return frame != null ? v + BodySpeedBound(frame, null) : v;
        }

        /* Vis-viva at periapsis, the fastest point of any conic. */
        private static double PeriapsisSpeed(Orbit o)
        {
            var parent = o != null ? o.referenceBody : null;
            if (parent == null) return 0;
            double pe = o.PeR;
            if (!(pe > 0)) return 0;
            double v2 = parent.gravParameter * (2.0 / pe - 1.0 / o.semiMajorAxis);
            return v2 > 0 ? Math.Sqrt(v2) : 0;
        }

        /* IsSunlit in CommandCameraRig, with the bodies that can shade the
           aim where they will be. */
        private bool Sunlit(DVec3 point, double t)
        {
            if (_sun < 0) return false;
            var sunPos = CentreAt(_sun, t);
            for (int k = 0; k < _shadowers.Count; k++)
            {
                int i = _shadowers[k];
                if (!CommandCameraMath.SegmentClearOfSphere(point, sunPos, CentreAt(i, t), _bodies[i].Radius)) return false;
            }
            return true;
        }

        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
    }
}
