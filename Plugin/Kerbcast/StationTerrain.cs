using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Kerbcast
{
    /*
     * What stock has built of a sphere's terrain around a ground station, read
     * from the sphere's quad tree, and the once a second [Kerbcast][TERRAIN]
     * line that records it while a station camera renders. Stock subdivides
     * only around its one target, the active craft (StationTerrainMath), so
     * the line puts the level built at the station beside the craft's range,
     * the level stock's rule predicts there, and the quad pool's use.
     */
    internal static class StationTerrain
    {
        private const string Tag = "[Kerbcast][TERRAIN]";
        private const float LogSeconds = 1f;
        /* A camera not heard from for this long no longer takes a turn at the line. */
        private const float SeenSeconds = 2f;
        /* The neighbourhood sampled about the station: a ring this far out, this many points round it. */
        private const double RingM = 20000.0;
        private const int RingPoints = 8;

        public struct Leaf
        {
            public bool Found;
            public int Level;
            public bool Visible;
            public double GcDist;
        }

        private static readonly Dictionary<uint, float> s_seen = new Dictionary<uint, float>();
        private static readonly Dictionary<uint, float> s_logged = new Dictionary<uint, float>();
        private static readonly List<uint> s_gone = new List<uint>();
        private static float s_nextLog;
        private static int s_frame = -1;
        private static int s_frames;
        private static double s_frameSum;
        private static double s_frameMax;
        private static readonly StringBuilder s_sb = new StringBuilder(512);
        private static int[] s_built = new int[0];

        /*
         * The finest quad of `pqs` built over `dir`, a unit vector in the
         * sphere's frame: from the cube face nearest it, down through the
         * child whose centre is nearest at each level.
         */
        public static Leaf LeafAt(PQS pqs, Vector3d dir)
        {
            var roots = pqs != null ? pqs.quads : null;
            if (roots == null) return default;
            PQ q = Nearest(roots, dir);
            if (q == null) return default;
            while (q.isSubdivided && q.subNodes != null)
            {
                var child = Nearest(q.subNodes, dir);
                if (child == null) break;
                q = child;
            }
            return new Leaf { Found = true, Level = q.subdivision, Visible = q.isVisible, GcDist = q.gcDist };
        }

        private static PQ Nearest(PQ[] quads, Vector3d dir)
        {
            PQ best = null;
            double bestDot = double.NegativeInfinity;
            for (int i = 0; i < quads.Length; i++)
            {
                var q = quads[i];
                if (q == null) continue;
                double d = Vector3d.Dot(q.positionPlanetRelative, dir);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = q;
                }
            }
            return best;
        }

        /* The station's direction in `pqs`'s frame, and the lens's height above the ground drawn under it (the sea, over an ocean). */
        public static bool StationInSphere(PQS pqs, CelestialBody body, Vector3d world, out Vector3d dir, out double eyeHeightM)
        {
            dir = default;
            eyeHeightM = 0;
            if (pqs == null || body == null) return false;
            dir = ((Vector3d)pqs.transform.InverseTransformPoint(world)).normalized;
            double ground = pqs.GetSurfaceHeight(dir);
            if (body.ocean) ground = Math.Max(ground, body.Radius);
            eyeHeightM = (world - body.position).magnitude - ground;
            return true;
        }

        /* Counts one rendered frame towards the line's frame time, once per frame however many cameras render. */
        public static void NoteFrame()
        {
            if (Time.frameCount == s_frame) return;
            s_frame = Time.frameCount;
            double ms = Time.unscaledDeltaTime * 1000.0;
            s_frames++;
            s_frameSum += ms;
            if (ms > s_frameMax) s_frameMax = ms;
        }

        /*
         * Whether camera `cam` writes the line now: at most once a second in
         * all, each camera rendering a station taking its turn, the one
         * longest since its last line first.
         */
        public static bool LogDue(uint cam, float now)
        {
            s_seen[cam] = now;
            if (now < s_nextLog) return false;
            uint pick = cam;
            float oldest = s_logged.TryGetValue(cam, out float mine) ? mine : float.NegativeInfinity;
            s_gone.Clear();
            foreach (var kv in s_seen)
            {
                if (now - kv.Value > SeenSeconds)
                {
                    s_gone.Add(kv.Key);
                    continue;
                }
                float at = s_logged.TryGetValue(kv.Key, out float t) ? t : float.NegativeInfinity;
                if (at < oldest)
                {
                    oldest = at;
                    pick = kv.Key;
                }
            }
            for (int i = 0; i < s_gone.Count; i++)
            {
                s_seen.Remove(s_gone[i]);
                s_logged.Remove(s_gone[i]);
            }
            if (pick != cam) return false;
            s_logged[cam] = now;
            s_nextLog = now + LogSeconds;
            return true;
        }

        public static void Forget(uint cam)
        {
            s_seen.Remove(cam);
            s_logged.Remove(cam);
        }

        /*
         * The line itself, key=value pairs with no spaces in a value:
         *  level, visible, gcDistKm: the quad built over the station
         *  ringLevels: the coarsest and finest quads 20 km round it
         *  predicted: the level stock's rule gives the station for the craft where it is
         *  lensLevel: the level whose vertices are no farther apart than the lens's horizon
         *  craftKm, craftAngleDeg, craftHeightKm: stock's target (the craft) from the station, round the sphere, and above its ground
         *  craftLevel: the quad built under the craft, to see the craft's own terrain starve
         *  speedCap: stock's level cap for the craft's speed
         *  quadCounts: PQS.quadCounts by level; built: quads in the tree by level, walked here
         *  cacheAssigned, cacheFree, cacheTotal: PQSCache's pool
         *  frameMs, frameMsMax: unscaled frame time over the second
         * `extra` is appended as given.
         */
        public static void Log(uint cam, string site, CelestialBody body, PQS pqs, Vector3d world, Vector3d dir, double eyeHeightM,
            Leaf leaf, string extra)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = s_sb;
            sb.Length = 0;
            sb.Append(Tag).Append(" event=station-terrain cam=").Append(cam)
                .Append(" site=").Append(string.IsNullOrEmpty(site) ? "-" : site.Replace(' ', '_'))
                .Append(" body=").Append(body.bodyName);
            sb.Append(" level=").Append(leaf.Found ? leaf.Level.ToString(inv) : "-")
                .Append(" visible=").Append(leaf.Found && leaf.Visible ? "true" : "false")
                .Append(" gcDistKm=").Append(leaf.Found ? (leaf.GcDist / 1000.0).ToString("F1", inv) : "-");

            int ringMin = int.MaxValue, ringMax = int.MinValue;
            double ringAngle = RingM / body.Radius;
            for (int k = 0; k < RingPoints; k++)
            {
                StationTerrainMath.RingPoint(dir.x, dir.y, dir.z, ringAngle, k, RingPoints, out double rx, out double ry, out double rz);
                var at = LeafAt(pqs, new Vector3d(rx, ry, rz));
                if (!at.Found) continue;
                ringMin = Math.Min(ringMin, at.Level);
                ringMax = Math.Max(ringMax, at.Level);
            }
            sb.Append(" ringLevels=").Append(ringMin <= ringMax ? ringMin + ".." + ringMax : "-");

            var targetDir = pqs.relativeTargetPositionNormalized;
            double angle = StationTerrainMath.AngleRad(dir.x, dir.y, dir.z, targetDir.x, targetDir.y, targetDir.z);
            int predicted = StationTerrainMath.StockLevel(angle, pqs.radius, pqs.targetHeight, pqs.minDetailDistance,
                pqs.subdivisionThreshold, pqs.minLevel, pqs.maxLevelAtCurrentTgtSpeed);
            int lensLevel = StationTerrainMath.LevelForLens(pqs.radius, PQS.cacheSideVertCount, eyeHeightM, pqs.maxLevel);
            sb.Append(" predicted=").Append(predicted).Append(" lensLevel=").Append(lensLevel)
                .Append(" eyeHeightM=").Append(eyeHeightM.ToString("F1", inv));

            var target = pqs.target;
            sb.Append(" craftKm=").Append(target != null ? (((Vector3d)target.position - world).magnitude / 1000.0).ToString("F1", inv) : "-")
                .Append(" craftAngleDeg=").Append((angle * 180.0 / Math.PI).ToString("F2", inv))
                .Append(" craftHeightKm=").Append((pqs.targetHeight / 1000.0).ToString("F1", inv));
            var craftLeaf = LeafAt(pqs, targetDir);
            sb.Append(" craftLevel=").Append(craftLeaf.Found ? craftLeaf.Level.ToString(inv) : "-")
                .Append(" speedCap=").Append(pqs.maxLevelAtCurrentTgtSpeed)
                .Append(" minLevel=").Append(pqs.minLevel).Append(" maxLevel=").Append(pqs.maxLevel)
                .Append(" minDetail=").Append(pqs.minDetailDistance.ToString("G4", inv));

            sb.Append(" quadCount=").Append(pqs.quadCount).Append(" quadCounts=");
            AppendLevels(sb, pqs.quadCounts, pqs.quadCounts != null ? pqs.quadCounts.Length : 0);
            int levels = pqs.maxLevel + 2;
            if (s_built.Length < levels) s_built = new int[levels];
            Array.Clear(s_built, 0, s_built.Length);
            int leaves = 0, shown = 0;
            if (pqs.quads != null)
                for (int i = 0; i < pqs.quads.Length; i++) Walk(pqs.quads[i], ref leaves, ref shown);
            sb.Append(" built=");
            AppendLevels(sb, s_built, levels);
            sb.Append(" leaves=").Append(leaves).Append(" leavesVisible=").Append(shown);

            var cache = PQSCache.Instance;
            if (cache != null)
                sb.Append(" cacheAssigned=").Append(cache.cachePQAssignedCount)
                    .Append(" cacheFree=").Append(cache.cachePQUnassignedCount)
                    .Append(" cacheTotal=").Append(cache.cachePQTotalCount);
            double mean = s_frames > 0 ? s_frameSum / s_frames : 0;
            sb.Append(" frameMs=").Append(mean.ToString("F2", inv)).Append(" frameMsMax=").Append(s_frameMax.ToString("F2", inv));
            s_frames = 0;
            s_frameSum = 0;
            s_frameMax = 0;
            if (!string.IsNullOrEmpty(extra)) sb.Append(' ').Append(extra);
            Debug.Log(sb.ToString());
        }

        private static void Walk(PQ q, ref int leaves, ref int shown)
        {
            if (q == null) return;
            int level = q.subdivision;
            if (level >= 0 && level < s_built.Length) s_built[level]++;
            if (!q.isSubdivided || q.subNodes == null)
            {
                leaves++;
                if (q.isVisible) shown++;
                return;
            }
            for (int i = 0; i < q.subNodes.Length; i++) Walk(q.subNodes[i], ref leaves, ref shown);
        }

        /* Levels as a comma list, "-" for none. */
        private static void AppendLevels(StringBuilder sb, int[] counts, int n)
        {
            if (counts == null || n <= 0)
            {
                sb.Append('-');
                return;
            }
            for (int i = 0; i < n && i < counts.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(counts[i]);
            }
        }
    }
}
