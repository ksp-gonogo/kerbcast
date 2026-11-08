using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * The game's bodies and planetarium frame as the kinematics read them,
     * taken once per frame on the main thread: each body's gravity, radius,
     * axes in KSP's fixed frame and spin, and Planetarium.Zup. Ephemerides
     * only, never a craft, so knowledge code may use it.
     */
    internal sealed class KinematicsWorld
    {
        private static readonly KinematicsWorld Shared = new KinematicsWorld();

        private readonly List<CelestialBody> _bodies = new List<CelestialBody>();
        private KinematicsBody[] _table = new KinematicsBody[0];
        private int _frame = -1;
        private double _ut = double.NaN;

        public ZupFrame Zup { get; private set; } = ZupFrame.Identity;

        /* This frame's reading; read again when the frame or the UT moves. */
        public static KinematicsWorld Now()
        {
            double ut = Planetarium.GetUniversalTime();
            if (Shared._frame != Time.frameCount || Shared._ut != ut) Shared.Read(ut);
            return Shared;
        }

        /* The body with this flightGlobalsIndex, or null. */
        public CelestialBody Body(int index)
        {
            if (index < 0) return null;
            if (index < _bodies.Count && _bodies[index] != null && _bodies[index].flightGlobalsIndex == index) return _bodies[index];
            for (int i = 0; i < _bodies.Count; i++)
                if (_bodies[i] != null && _bodies[i].flightGlobalsIndex == index) return _bodies[i];
            return null;
        }

        public KinematicsBody Of(CelestialBody body)
        {
            int i = body != null ? _bodies.IndexOf(body) : -1;
            return i >= 0 ? _table[i] : default;
        }

        public KinematicsBody Of(int index) => Of(Body(index));

        /* A fixed-frame offset about `body` as a world position this frame. */
        public DVec3 ToWorld(CelestialBody body, DVec3 offset)
        {
            var c = body.position;
            return new DVec3(c.x, c.y, c.z) + Zup.ToWorld(offset);
        }

        private void Read(double ut)
        {
            _frame = Time.frameCount;
            _ut = ut;
            _bodies.Clear();
            var bodies = FlightGlobals.Bodies;
            if (bodies != null) _bodies.AddRange(bodies);
            if (_table.Length != _bodies.Count) _table = new KinematicsBody[_bodies.Count];
            var z = Planetarium.Zup;
            var zup = new ZupFrame(D(z.X), D(z.Y), D(z.Z));
            Zup = zup;
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                if (b == null)
                {
                    _table[i] = default;
                    continue;
                }
                var f = b.BodyFrame;
                _table[i] = new KinematicsBody
                {
                    Known = true,
                    Mu = b.gravParameter,
                    Radius = b.Radius,
                    AxisX = zup.FromLocal(D(f.X)),
                    AxisY = zup.FromLocal(D(f.Y)),
                    AxisZ = zup.FromLocal(D(f.Z)),
                    SpinDegPerS = b.rotates ? 360.0 * b.rotPeriodRecip : 0.0,
                    AxesUt = ut,
                };
            }
        }

        private static DVec3 D(Vector3d v) => new DVec3(v.x, v.y, v.z);
    }
}
