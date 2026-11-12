using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Kerbcast
{
    /*
     * Extra terrain subdivision centres at the ground stations being watched,
     * for the save's "detailed ground" switch. Stock subdivides a sphere only
     * around its one target, the active craft; a postfix on
     * PQ.UpdateTargetRelativity gives every quad below a level cap the
     * smaller of stock's distance and its distance to each centre
     * (StationDetailMath), so the ground round a watched station is built as
     * if a craft stood there, and stock's own collapse rule, reading the same
     * distance, keeps it. Stock draws a quad only while its own arc to the
     * craft (gcd1) is inside the craft's visible radius, so a centre lowers
     * that arc too: otherwise a craft landed far away leaves the ground round
     * the station built but never drawn.
     *
     * A centre is held while its camera renders, which it does only while
     * someone watches it, and released DetailHoldSeconds after the last
     * frame. The cap keeps detail quads below the level stock gives mesh
     * colliders (PQSMod_QuadMeshColliders), so they add no physics.
     */
    internal static class StationDetail
    {
        private const string Tag = "[Kerbcast][TERRAIN]";
        private const string HarmonyId = "kerbcast.stationdetail";
        public const double HoldSeconds = 5.0;

        private struct Centre
        {
            public PQS Sphere;
            public Vector3d Dir;
            public double Height;
        }

        /* The centres on one sphere, flat for the postfix. */
        private sealed class Group
        {
            public PQS Sphere;
            public double[] Dirs = new double[0];
            public double[] Heights = new double[0];
            public int Count;
            public int Cap;
        }

        private static readonly Dictionary<uint, Centre> s_centres = new Dictionary<uint, Centre>();
        private static readonly DetailCentreHold<uint> s_hold = new DetailCentreHold<uint>(HoldSeconds);
        private static readonly List<uint> s_released = new List<uint>();
        private static readonly Dictionary<PQS, int> s_colliderFrom = new Dictionary<PQS, int>();
        /* Read by the postfix on every quad update; replaced whole, never changed in place. */
        private static Group[] s_groups = new Group[0];
        private static Harmony s_harmony;
        private static bool s_patchFailed;

        /* Postfix calls and quads given a centre's distance since the last line, to show the patch runs and how much it does. */
        private static long s_calls;
        private static long s_lowered;

        public static int Count => s_centres.Count;

        /* Holds a centre at `dir` (a unit vector in `pqs`'s frame), `heightM` above the ground, for camera `cam`, seen at `now`. */
        public static void Touch(uint cam, PQS pqs, Vector3d dir, double heightM, double now)
        {
            if (pqs == null || !EnsurePatched()) return;
            bool fresh = s_hold.Touch(cam, now);
            if (!fresh && s_centres.TryGetValue(cam, out var held) && ReferenceEquals(held.Sphere, pqs)
                && (held.Dir - dir).sqrMagnitude == 0 && held.Height == heightM) return;
            s_centres[cam] = new Centre { Sphere = pqs, Dir = dir, Height = heightM };
            Rebuild();
            int cap = CapFor(pqs);
            Debug.Log($"{Tag} event=station-detail state={(fresh ? "on" : "moved")} cam={cam} body={pqs.name} cap={cap} maxLevel={pqs.maxLevel} "
                + $"collidersFrom={ColliderFrom(pqs)} levelsShort={KerbcastSettings.StationDetailLevelsShort} centres={s_centres.Count}");
        }

        /* Drops camera `cam`'s centre at once. */
        public static void Release(uint cam, string reason)
        {
            s_hold.Release(cam);
            if (!s_centres.Remove(cam)) return;
            Rebuild();
            Debug.Log($"{Tag} event=station-detail state=off cam={cam} reason={reason} centres={s_centres.Count}");
        }

        /* Once a frame: drops the centres whose cameras stopped rendering a hold ago, or every centre with the switch off. */
        public static void Tick(double now, bool on)
        {
            if (s_centres.Count == 0) return;
            s_released.Clear();
            if (on) s_hold.Expire(now, s_released);
            else s_hold.Clear(s_released);
            for (int i = 0; i < s_released.Count; i++) Release(s_released[i], on ? "no-viewer" : "setting-off");
        }

        public static void ReleaseAll(string reason)
        {
            s_released.Clear();
            s_hold.Clear(s_released);
            for (int i = 0; i < s_released.Count; i++) Release(s_released[i], reason);
        }

        /* The detail fields for StationTerrain's line, counters reset. */
        public static string LogFields(PQS pqs)
        {
            string s = $"centres={s_centres.Count} detailCap={(pqs != null ? CapFor(pqs).ToString() : "-")} "
                + $"detailCalls={s_calls} detailLowered={s_lowered}";
            s_calls = 0;
            s_lowered = 0;
            return s;
        }

        private static int CapFor(PQS pqs) =>
            StationDetailMath.DetailCap(pqs.maxLevel, ColliderFrom(pqs), KerbcastSettings.StationDetailLevelsShort);

        /* The first level `pqs` gives a mesh collider, or past its finest when it gives none. */
        private static int ColliderFrom(PQS pqs)
        {
            if (s_colliderFrom.TryGetValue(pqs, out int level)) return level;
            level = pqs.maxLevel + 1;
            var mods = pqs.GetComponentsInChildren<PQSMod_QuadMeshColliders>(true);
            for (int i = 0; i < mods.Length; i++)
                if (mods[i] != null && mods[i].sphere == pqs)
                    level = Math.Min(level, pqs.maxLevel - Math.Abs(mods[i].maxLevelOffset));
            s_colliderFrom[pqs] = level;
            return level;
        }

        private static void Rebuild()
        {
            var bySphere = new Dictionary<PQS, Group>();
            foreach (var c in s_centres.Values)
            {
                if (c.Sphere == null) continue;
                if (!bySphere.TryGetValue(c.Sphere, out var g)) bySphere[c.Sphere] = g = new Group { Sphere = c.Sphere, Cap = CapFor(c.Sphere) };
                g.Count++;
            }
            var groups = new Group[bySphere.Count];
            int n = 0;
            foreach (var g in bySphere.Values)
            {
                g.Dirs = new double[3 * g.Count];
                g.Heights = new double[g.Count];
                g.Count = 0;
                groups[n++] = g;
            }
            foreach (var c in s_centres.Values)
            {
                if (c.Sphere == null) continue;
                var g = bySphere[c.Sphere];
                g.Dirs[3 * g.Count] = c.Dir.x;
                g.Dirs[3 * g.Count + 1] = c.Dir.y;
                g.Dirs[3 * g.Count + 2] = c.Dir.z;
                g.Heights[g.Count] = c.Height;
                g.Count++;
            }
            s_groups = groups;
        }

        private static bool EnsurePatched()
        {
            if (s_harmony != null) return true;
            if (s_patchFailed) return false;
            try
            {
                var method = AccessTools.Method(typeof(PQ), nameof(PQ.UpdateTargetRelativity));
                if (method == null) throw new MissingMethodException("PQ.UpdateTargetRelativity");
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(StationDetail), nameof(UpdateTargetRelativityPostfix)));
                s_harmony = harmony;
                Debug.Log($"{Tag} event=station-detail patched PQ.UpdateTargetRelativity");
                return true;
            }
            catch (Exception ex)
            {
                s_patchFailed = true;
                Debug.LogError($"{Tag} event=station-detail patch failed, detailed ground stays off: {ex}");
                return false;
            }
        }

        private static void UpdateTargetRelativityPostfix(PQ __instance, double ___angularinterval)
        {
            var groups = s_groups;
            if (groups.Length == 0) return;
            s_calls++;
            var sphere = __instance.sphereRoot;
            for (int i = 0; i < groups.Length; i++)
            {
                var g = groups[i];
                if (!ReferenceEquals(g.Sphere, sphere)) continue;
                var p = __instance.positionPlanetRelative;
                double stock = __instance.gcDist;
                double gc = StationDetailMath.DetailGcDistM(stock, __instance.subdivision, p.x, p.y, p.z, ___angularinterval,
                    sphere.radius, g.Dirs, g.Heights, g.Count, g.Cap, out double arc);
                if (gc < stock)
                {
                    __instance.gcDist = gc;
                    s_lowered++;
                }
                if (arc < __instance.gcd1) __instance.gcd1 = arc;
                return;
            }
        }
    }

    /* Ticks StationDetail once a frame in the scenes command cameras run in, and drops every centre when the scene ends. */
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, once: false)]
    internal sealed class StationDetailHost : MonoBehaviour
    {
        private void Update() => StationDetail.Tick(Time.unscaledTime, StationTerrain.DetailedGround);

        private void OnDestroy() => StationDetail.ReleaseAll("scene-end");
    }
}
