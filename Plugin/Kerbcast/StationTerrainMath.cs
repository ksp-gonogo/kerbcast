using System;
using System.Collections.Generic;

namespace Kerbcast
{
    /*
     * Stock's terrain subdivision rule, as PQ and PQS apply it, apart from
     * Unity so it can be tested. A quad at level L subdivides while its
     * distance measure gcDist is below radius * minDetailDistance / 2^L *
     * subdivisionThreshold, where gcDist is the great-circle distance from
     * the quad's centre to the point under the craft, stretched by 1.3, plus
     * the craft's height above the ground, less the quad's angular interval
     * (PQ.UpdateTargetRelativity). Stock has one such centre, the active
     * craft; a station camera far from it sees the coarse quads left there.
     */
    internal static class StationTerrainMath
    {
        /* PQ.UpdateTargetRelativity's stretch of the great-circle distance. */
        public const double GcStretch = 1.3;

        /* PQS.subdivisionThresholds[level]. */
        public static double SubdivideThresholdM(double radiusM, double minDetailDistance, double subdivisionThreshold, int level) =>
            radiusM * minDetailDistance / Math.Pow(2.0, level) * subdivisionThreshold;

        /* PQ.angularinterval for a quad at `level`: the root quad's plane scale is 1 and halves per level. */
        public static double AngularIntervalM(double radiusM, int level) =>
            2.0 * Math.PI * radiusM * Math.Atan(Math.Pow(0.5, level) / radiusM);

        /* PQ.gcDist for a quad whose centre is `angleRad` round the sphere from a centre `heightM` above the ground there. */
        public static double GcDistM(double angleRad, double radiusM, double heightM, double angularIntervalM) =>
            angleRad * radiusM * GcStretch + Math.Abs(heightM) - angularIntervalM;

        /* The angle between two unit vectors, safe against rounding just past +-1. */
        public static double AngleRad(double ax, double ay, double az, double bx, double by, double bz)
        {
            double d = ax * bx + ay * by + az * bz;
            if (d > 1) d = 1;
            if (d < -1) d = -1;
            return Math.Acos(d);
        }

        /*
         * The level stock builds the quad at a spot `angleRad` from the craft,
         * which stands `craftHeightM` above the ground under it: the first
         * level from `minLevel` whose threshold the spot's gcDist does not
         * beat, or `maxLevel`. The spot stands for the quad's centre, so this
         * is the level a fresh build settles at; a quad left finer by the
         * craft passing keeps its level until gcDist passes the collapse
         * threshold, a few times larger.
         */
        public static int StockLevel(double angleRad, double radiusM, double craftHeightM, double minDetailDistance,
            double subdivisionThreshold, int minLevel, int maxLevel)
        {
            int level = Math.Max(0, minLevel);
            while (level < maxLevel)
            {
                double gc = GcDistM(angleRad, radiusM, craftHeightM, AngularIntervalM(radiusM, level));
                if (!(gc < SubdivideThresholdM(radiusM, minDetailDistance, subdivisionThreshold, level))) break;
                level++;
            }
            return level;
        }

        /* Ground distance between neighbouring vertices of a quad at `level`: a cube face spans a quarter of the circumference, split 2^level times, each quad `sideVerts` vertices across. */
        public static double VertexSpacingM(double radiusM, int level, int sideVerts) =>
            Math.PI * 0.5 * radiusM / Math.Pow(2.0, level) / Math.Max(1, sideVerts - 1);

        /* Distance to the horizon of a smooth sphere from `heightM` above it. */
        public static double HorizonM(double radiusM, double heightM)
        {
            double h = Math.Max(0.0, heightM);
            return Math.Sqrt(2.0 * radiusM * h + h * h);
        }

        /*
         * The coarsest level whose vertices are no farther apart than the
         * lens's own horizon over smooth ground, at most `maxLevel`. Coarser
         * than this, all the ground the lens can see before its horizon lies
         * on one or two flat facets, and the shape drawn there, a ridge or a
         * peak, is the facet's and not the terrain's.
         */
        public static int LevelForLens(double radiusM, int sideVerts, double eyeHeightM, int maxLevel)
        {
            double horizon = HorizonM(radiusM, Math.Max(1.0, eyeHeightM));
            int level = 0;
            while (level < maxLevel && VertexSpacingM(radiusM, level, sideVerts) > horizon) level++;
            return level;
        }

        /* Whether terrain built at `level` under a lens, drawn or not, is too coarse for it (LevelForLens). */
        public static bool Coarse(int level, bool visible, int levelForLens) => !visible || level < levelForLens;

        /*
         * A unit vector `angleRad` from `dir` toward compass point `k` of
         * `of` evenly spaced round it, for sampling the ground in a ring
         * about a station. Any pair of axes at right angles to `dir` will do.
         */
        public static void RingPoint(double x, double y, double z, double angleRad, int k, int of,
            out double rx, out double ry, out double rz)
        {
            /* A first axis from whichever world axis is least along dir. */
            double ax = Math.Abs(x) < 0.9 ? 1 : 0, ay = ax == 1 ? 0 : 1, az = 0;
            double t1x = ay * z - az * y, t1y = az * x - ax * z, t1z = ax * y - ay * x;
            double n = Math.Sqrt(t1x * t1x + t1y * t1y + t1z * t1z);
            t1x /= n; t1y /= n; t1z /= n;
            double t2x = y * t1z - z * t1y, t2y = z * t1x - x * t1z, t2z = x * t1y - y * t1x;
            double phi = 2.0 * Math.PI * k / Math.Max(1, of);
            double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
            double cp = Math.Cos(phi), sp = Math.Sin(phi);
            rx = x * c + (t1x * cp + t2x * sp) * s;
            ry = y * c + (t1y * cp + t2y * sp) * s;
            rz = z * c + (t1z * cp + t2z * sp) * s;
            double m = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            rx /= m; ry /= m; rz /= m;
        }
    }
}
