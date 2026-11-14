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
     * the station built but never drawn. A body's ocean is a child sphere
     * with quads of its own, built and drawn by the same rule against its own
     * radius, so a centre is held on every child sphere too, in that sphere's
     * frame; without it a shore station's detailed land would stand beside
     * coarse water stock hides, and the seabed would show.
     *
     * The main view draws those quads as well, where its far clip (750 km)
     * reaches them. Stock leaves undrawn only the quads past the craft's
     * visible radius, which lies past its horizon, so quads drawn only for a
     * centre sit behind the ground stock draws nearer the craft and fail the
     * depth test, unless they rise above that horizon, where they are the
     * real terrain. They cost the main view draw calls but change nothing it
     * shows; detailDrawnOnly in the terrain line counts them.
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

        /* A centre on one sphere: the station's direction in that sphere's frame and the lens's height above the ground. */
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
            public int MaxLevel;
        }

        /* Each camera's centres, one for the body's sphere first, then one for each of its child spheres. */
        private static readonly Dictionary<uint, Centre[]> s_centres = new Dictionary<uint, Centre[]>();
        private static readonly DetailCentreHold<uint> s_hold = new DetailCentreHold<uint>(HoldSeconds);
        private static readonly List<uint> s_released = new List<uint>();
        /* By sphere and its finest level then, so a terrain detail change mid-session is read afresh. */
        private static readonly Dictionary<(PQS, int), int> s_colliderFrom = new Dictionary<(PQS, int), int>();
        /* Read by the postfix on every quad update; replaced whole, never changed in place. */
        private static Group[] s_groups = new Group[0];
        private static Harmony s_harmony;
        private static bool s_patchFailed;

        /* Postfix calls and quads given a centre's distance since the last line, to show the patch runs and how much it does. */
        private static long s_calls;
        private static long s_lowered;

        /* Updates of unsubdivided quads stock leaves undrawn but a centre draws, about one per such quad, this frame: stock updates quads in PQS.Update, before cameras render. */
        private static int s_drawnOnlyFrame = -1;
        private static int s_drawnOnly;

        public static int Count => s_centres.Count;

        /*
         * Holds a centre for camera `cam`, seen at `now`, at `dir` (a unit
         * vector in `pqs`'s frame, the station at `world`), `heightM` above
         * the ground, and at the same place on each of `pqs`'s child spheres.
         */
        public static void Touch(uint cam, PQS pqs, Vector3d dir, Vector3d world, double heightM, double now)
        {
            if (pqs == null || !EnsurePatched()) return;
            bool fresh = s_hold.Touch(cam, now);
            if (!fresh && s_centres.TryGetValue(cam, out var held) && held.Length > 0 && ReferenceEquals(held[0].Sphere, pqs)
                && (held[0].Dir - dir).sqrMagnitude == 0 && held[0].Height == heightM) return;
            var children = pqs.ChildSpheres;
            int n = 1;
            if (children != null)
                for (int i = 0; i < children.Length; i++)
                    if (children[i] != null) n++;
            var centres = new Centre[n];
            centres[0] = new Centre { Sphere = pqs, Dir = dir, Height = heightM };
            var sb = new System.Text.StringBuilder();
            sb.Append(pqs.name).Append(":cap").Append(CapFor(pqs));
            n = 1;
            if (children != null)
                for (int i = 0; i < children.Length; i++)
                {
                    var child = children[i];
                    if (child == null) continue;
                    var childDir = ((Vector3d)child.transform.InverseTransformPoint(world)).normalized;
                    centres[n++] = new Centre { Sphere = child, Dir = childDir, Height = heightM };
                    sb.Append(',').Append(child.name).Append(":cap").Append(CapFor(child));
                }
            s_centres[cam] = centres;
            Rebuild();
            Debug.Log($"{Tag} event=station-detail state={(fresh ? "on" : "moved")} cam={cam} body={pqs.name} cap={CapFor(pqs)} maxLevel={pqs.maxLevel} "
                + $"collidersFrom={ColliderFrom(pqs)} levelsShort={KerbcastSettings.StationDetailLevelsShort} spheres={sb.ToString().Replace(' ', '_')} "
                + $"centres={s_centres.Count}");
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
            s_released.Clear();
            var groups = s_groups;
            for (int i = 0; i < groups.Length; i++)
                if (groups[i].Sphere != null && groups[i].Sphere.maxLevel != groups[i].MaxLevel)
                {
                    Rebuild();
                    break;
                }
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
                + $"detailCalls={s_calls} detailLowered={s_lowered} detailDrawnOnly={(s_drawnOnlyFrame == Time.frameCount ? s_drawnOnly : 0)}";
            s_calls = 0;
            s_lowered = 0;
            return s;
        }

        private static int CapFor(PQS pqs) =>
            StationDetailMath.DetailCap(pqs.maxLevel, ColliderFrom(pqs), KerbcastSettings.StationDetailLevelsShort);

        /* The first level `pqs` gives a mesh collider, or past its finest when it gives none. */
        private static int ColliderFrom(PQS pqs)
        {
            var key = (pqs, pqs.maxLevel);
            if (s_colliderFrom.TryGetValue(key, out int level)) return level;
            level = pqs.maxLevel + 1;
            var mods = pqs.GetComponentsInChildren<PQSMod_QuadMeshColliders>(true);
            for (int i = 0; i < mods.Length; i++)
                if (mods[i] != null && mods[i].sphere == pqs)
                    level = Math.Min(level, pqs.maxLevel - Math.Abs(mods[i].maxLevelOffset));
            s_colliderFrom[key] = level;
            return level;
        }

        private static void Rebuild()
        {
            var bySphere = new Dictionary<PQS, Group>();
            foreach (var centres in s_centres.Values)
                for (int i = 0; i < centres.Length; i++)
                {
                    var sphere = centres[i].Sphere;
                    if (sphere == null) continue;
                    if (!bySphere.TryGetValue(sphere, out var g))
                        bySphere[sphere] = g = new Group { Sphere = sphere, Cap = CapFor(sphere), MaxLevel = sphere.maxLevel };
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
            foreach (var centres in s_centres.Values)
                for (int i = 0; i < centres.Length; i++)
                {
                    var c = centres[i];
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
                if (arc < __instance.gcd1)
                {
                    if (!__instance.isSubdivided && arc < sphere.visibleRadius && __instance.gcd1 >= sphere.visibleRadius) CountDrawnOnly();
                    __instance.gcd1 = arc;
                }
                return;
            }
        }

        private static void CountDrawnOnly()
        {
            int frame = Time.frameCount;
            if (frame != s_drawnOnlyFrame)
            {
                s_drawnOnly = 0;
                s_drawnOnlyFrame = frame;
            }
            s_drawnOnly++;
        }
    }

    /* Ticks StationDetail once a frame in the scenes command cameras run in, and drops every centre when the scene ends. */
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, once: false)]
    internal sealed class StationDetailHost : MonoBehaviour
    {
        private void Update()
        {
            if (StationDetail.Count > 0) StationDetail.Tick(Time.unscaledTime, StationTerrain.DetailedGround);
        }

        private void OnDestroy() => StationDetail.ReleaseAll("scene-end");
    }
}
