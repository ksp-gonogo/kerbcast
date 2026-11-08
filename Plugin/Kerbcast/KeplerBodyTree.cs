using System.Collections.Generic;

namespace Kerbcast
{
    /*
     * The game's bodies on their Kepler orbits, as a tree for the light-time
     * solve. Offsets are each body's position about its parent at a UT, in
     * world axes turned by `Turn`: the identity for the world frame, the
     * inverse of the planetarium's rotation for the history ring's frame.
     * Ephemerides only, never a craft.
     */
    internal struct KeplerBodyTree : IBodyTree
    {
        public List<CelestialBody> Bodies;
        public DQuat Turn;

        public KeplerBodyTree(List<CelestialBody> bodies, DQuat turn)
        {
            Bodies = bodies;
            Turn = turn;
        }

        public static KeplerBodyTree World => new KeplerBodyTree(FlightGlobals.Bodies, DQuat.Identity);

        public CelestialBody Body(int index) =>
            Bodies != null && index >= 0 && index < Bodies.Count ? Bodies[index] : null;

        public bool TryParent(int body, out int parent)
        {
            parent = -1;
            var b = Body(body);
            var up = b != null ? b.referenceBody : null;
            if (up == null || up == b) return false;
            parent = up.flightGlobalsIndex;
            return true;
        }

        public bool TryOffset(int body, double ut, out DVec3 offset)
        {
            offset = default;
            var b = Body(body);
            if (b == null || b.orbit == null) return false;
            var r = b.orbit.getRelativePositionAtUT(ut).xzy;
            offset = Turn.Rotate(new DVec3(r.x, r.y, r.z));
            return true;
        }
    }
}
