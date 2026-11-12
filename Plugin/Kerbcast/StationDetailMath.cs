using System;
using System.Collections.Generic;

namespace Kerbcast
{
    /* The extra subdivision centres a watched station adds to stock's one (StationTerrainMath), apart from Unity so it can be tested. */
    internal static class StationDetailMath
    {
        /*
         * The finest level a detail centre lets stock build: `levelsShort`
         * below the sphere's own finest, and below `colliderFromLevel`, the
         * first level stock gives a mesh collider (PQSMod_QuadMeshColliders),
         * so detail quads never add physics. Children are built one level
         * finer than the quad that subdivides, so a quad below this level
         * takes the centre's distance and one at it does not.
         */
        public static int DetailCap(int maxLevel, int colliderFromLevel, int levelsShort) =>
            Math.Min(maxLevel - Math.Max(0, levelsShort), colliderFromLevel - 1);

        /*
         * A quad's gcDist with the detail centres taken into account: the
         * smaller of stock's (the craft's) and each centre's, for a quad
         * below `capLevel`. Centres are unit directions in the sphere's
         * frame, three to a centre in `dirs`, with their heights above the
         * ground in `heights`. `arcM` is the nearest centre's stretched arc
         * (stock's gcd1, which decides whether a quad is drawn), at any
         * level, or infinity with no centres.
         */
        public static double DetailGcDistM(double stockGcDist, int quadLevel, double qx, double qy, double qz, double angularIntervalM,
            double radiusM, double[] dirs, double[] heights, int count, int capLevel, out double arcM)
        {
            arcM = double.PositiveInfinity;
            double best = stockGcDist;
            for (int i = 0; i < count; i++)
            {
                double arc = StationTerrainMath.AngleRad(qx, qy, qz, dirs[3 * i], dirs[3 * i + 1], dirs[3 * i + 2]) * radiusM * StationTerrainMath.GcStretch;
                if (arc < arcM) arcM = arc;
                if (quadLevel >= capLevel) continue;
                double gc = arc + Math.Abs(heights[i]) - angularIntervalM;
                if (gc < best) best = gc;
            }
            return best;
        }
    }

    /*
     * Which stations hold a detail centre: each is touched every frame its
     * camera renders and released `holdSeconds` after the last touch, so a
     * viewer flicking between feeds does not build and collapse the same
     * ground over and over.
     */
    internal sealed class DetailCentreHold<TKey>
    {
        private readonly Dictionary<TKey, double> _touched = new Dictionary<TKey, double>();
        private readonly List<TKey> _stale = new List<TKey>();
        private readonly double _holdSeconds;

        public DetailCentreHold(double holdSeconds)
        {
            _holdSeconds = holdSeconds;
        }

        public int Count => _touched.Count;

        public bool Holds(TKey key) => _touched.ContainsKey(key);

        /* Marks `key` seen at `now`. True when it was not held before. */
        public bool Touch(TKey key, double now)
        {
            bool fresh = !_touched.ContainsKey(key);
            _touched[key] = now;
            return fresh;
        }

        /* Drops `key` at once, as when its camera goes away or the setting is switched off. */
        public bool Release(TKey key) => _touched.Remove(key);

        /* Drops every key not touched within the hold, adding each to `released`. */
        public void Expire(double now, List<TKey> released)
        {
            _stale.Clear();
            foreach (var kv in _touched)
                if (now - kv.Value > _holdSeconds) _stale.Add(kv.Key);
            for (int i = 0; i < _stale.Count; i++)
            {
                _touched.Remove(_stale[i]);
                released.Add(_stale[i]);
            }
        }

        public void Clear(List<TKey> released)
        {
            foreach (var key in _touched.Keys) released.Add(key);
            _touched.Clear();
        }
    }
}
