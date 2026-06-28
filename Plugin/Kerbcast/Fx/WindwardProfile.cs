// KSP adapter for FxSilhouette: walks the vessel's parts and feeds their
// bounds corners (relative to CoM) into the shared silhouette math in
// Fx/Core/FxCore.cs — the SAME code the CI render harness compiles, so the
// previews exercise the real sizing/placement. This file owns only the
// KSP-typed plumbing: per-part bounds caching and the per-frame memo.
//
// Measures part RENDERER BOUNDS, not part transform positions — a part's
// origin sits near its centre, so position-only measurement underestimated
// every extent by half a part and the FX meshes sat inside the hull. Per
// part a local-space box is computed once (lazily, from merged renderer
// bounds) and cached; per frame only its 8 corners are projected. A
// same-frame memo collapses the bowshock+trail double call.

using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    internal static class WindwardProfile
    {
        private struct PartBox
        {
            public Vector3 Centre;   // part-local
            public Vector3 Extents;  // part-local, axis-aligned
        }

        private static readonly Dictionary<Part, PartBox> _boxCache = new Dictionary<Part, PartBox>();
        private static float _lastCacheFlush;

        // Corner scratch (relative to CoM) handed to FxSilhouette.FromCorners
        // — reused so the per-frame path allocates nothing after warmup.
        private static readonly List<Vector3> _cornerScratch = new List<Vector3>(512);

        // Same-frame memo — bowshock and trail both compute the silhouette
        // for the same vessel/wind every frame.
        private static Vessel _memoVessel;
        private static Vector3 _memoWind;
        private static int _memoFrame = -1;
        private static FxSilhouette _memo;

        public static FxSilhouette Compute(Vessel vessel, Vector3 windDir)
        {
            if (vessel == null || vessel.parts == null || vessel.parts.Count == 0)
            {
                return FxSilhouette.FromCorners(null, windDir);
            }

            if (Time.frameCount == _memoFrame && ReferenceEquals(vessel, _memoVessel)
                && (windDir - _memoWind).sqrMagnitude < 1e-6f)
            {
                return _memo;
            }

            // Destroyed parts would otherwise pin the cache forever; a
            // periodic flush is cheaper than per-part liveness checks.
            if (Time.time - _lastCacheFlush > 60f)
            {
                _boxCache.Clear();
                _lastCacheFlush = Time.time;
            }

            Vector3 com = vessel.CoM;
            _cornerScratch.Clear();
            foreach (var part in vessel.parts)
            {
                if (part == null || part.transform == null) continue;
                if (!_boxCache.TryGetValue(part, out var box))
                {
                    box = ComputeLocalBox(part);
                    _boxCache[part] = box;
                }

                Transform tr = part.transform;
                Vector3 c = tr.TransformPoint(box.Centre) - com;
                Vector3 ax = tr.right * box.Extents.x;
                Vector3 ay = tr.up * box.Extents.y;
                Vector3 az = tr.forward * box.Extents.z;
                for (int i = 0; i < 8; i++)
                {
                    _cornerScratch.Add(c
                        + ((i & 1) == 0 ? -ax : ax)
                        + ((i & 2) == 0 ? -ay : ay)
                        + ((i & 4) == 0 ? -az : az));
                }
            }

            var s = FxSilhouette.FromCorners(_cornerScratch, windDir);
            _memoVessel = vessel;
            _memoWind = windDir;
            _memoFrame = Time.frameCount;
            _memo = s;
            return s;
        }

        // Merged renderer bounds expressed in the part's local frame. The
        // world AABB → local AABB conversion is conservative (box of a box),
        // which errs toward FX sitting slightly outside the hull — the
        // right direction; inside means clipping.
        private static PartBox ComputeLocalBox(Part part)
        {
            Transform tr = part.transform;
            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            var renderers = part.GetComponentsInChildren<Renderer>(includeInactive: false);
            for (int i = 0; i < renderers.Length; i++)
            {
                var rend = renderers[i];
                if (rend == null || !rend.enabled) continue;
                if (!(rend is MeshRenderer || rend is SkinnedMeshRenderer)) continue;

                Bounds b = rend.bounds;
                Vector3 cLocal = tr.InverseTransformPoint(b.center);
                Vector3 ex = tr.InverseTransformDirection(Vector3.right) * b.extents.x;
                Vector3 ey = tr.InverseTransformDirection(Vector3.up) * b.extents.y;
                Vector3 ez = tr.InverseTransformDirection(Vector3.forward) * b.extents.z;
                Vector3 eLocal = new Vector3(
                    Mathf.Abs(ex.x) + Mathf.Abs(ey.x) + Mathf.Abs(ez.x),
                    Mathf.Abs(ex.y) + Mathf.Abs(ey.y) + Mathf.Abs(ez.y),
                    Mathf.Abs(ex.z) + Mathf.Abs(ey.z) + Mathf.Abs(ez.z));

                Vector3 lo = cLocal - eLocal;
                Vector3 hi = cLocal + eLocal;
                if (!any) { min = lo; max = hi; any = true; }
                else
                {
                    min = Vector3.Min(min, lo);
                    max = Vector3.Max(max, hi);
                }
            }
            if (!any) return new PartBox { Centre = Vector3.zero, Extents = Vector3.zero };
            return new PartBox { Centre = (min + max) * 0.5f, Extents = (max - min) * 0.5f };
        }
    }
}
