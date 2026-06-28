// KSP-free FX math, shared SOURCE-LEVEL with the CI render harness — the
// Unity project at ci/kerbcast-shaders symlinks this directory in as
// Assets/Shared, so everything that decides where FX meshes sit, how big
// they are, and how intensity ramps is EXERCISED BY THE CI RENDERS, not
// mirrored by hand. The May-2026 "FX inside the hull" regression shipped
// precisely because the harness and the runtime each had their own copy of
// this logic and only the harness's was rendered. KSP-typed adapters
// (Vessel/Part traversal, caching) stay in the plugin; renderer-bounds
// corner collection stays in the harness — both feed relative corner
// points into FxSilhouette.FromCorners.
//
// Anything added here must use only UnityEngine types available in both
// Unity 2019.4 (C# 7.3) and the net48 plugin build.

using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    // Windward silhouette of a vessel relative to the wind axis.
    // ForwardStandoff/AftStandoff are the extents along ±windDir (where the
    // bowshock base and the trail/embers anchor sit). The perpendicular
    // cross-section is an ELLIPSE — MajorAxis is the direction (⊥ wind) of
    // the longest perpendicular extent, RadiusMajor/RadiusMinor the extents
    // along it and along wind × MajorAxis. A broadside vessel presents a
    // long flat silhouette; FX sized as circles read as if it were flying
    // nose-first. End-on the radii converge to the old circle.
    internal struct FxSilhouette
    {
        public float WindwardRadius;
        public float ForwardStandoff;
        public float AftStandoff;
        public Vector3 MajorAxis;
        public float RadiusMajor;
        public float RadiusMinor;

        public Vector3 MinorAxis(Vector3 windDir)
        {
            Vector3 minor = Vector3.Cross(windDir, MajorAxis);
            return minor.sqrMagnitude > 1e-6f ? minor.normalized : Vector3.up;
        }

        // Build from corner points RELATIVE to the anchor (vessel CoM /
        // proxy root). Two passes: extents + major direction, then the
        // minor extent once the major axis is known. Floors keep small
        // probes from producing degenerate (zero-sized) FX meshes.
        public static FxSilhouette FromCorners(List<Vector3> relCorners, Vector3 windDir)
        {
            var s = new FxSilhouette
            {
                WindwardRadius = 1f, ForwardStandoff = 1f, AftStandoff = 1f,
                MajorAxis = Vector3.right, RadiusMajor = 1f, RadiusMinor = 1f,
            };
            if (relCorners == null || relCorners.Count == 0) return s;

            float fwd = 0f, aft = 0f, perpMax = 0f;
            Vector3 majorDir = Vector3.zero;
            for (int i = 0; i < relCorners.Count; i++)
            {
                Vector3 rel = relCorners[i];
                float along = Vector3.Dot(rel, windDir);
                if (along > fwd) fwd = along;
                if (-along > aft) aft = -along;
                Vector3 perp = rel - along * windDir;
                float perpDist = perp.magnitude;
                if (perpDist > perpMax)
                {
                    perpMax = perpDist;
                    majorDir = perp;
                }
            }

            float minorMax = perpMax;
            if (majorDir.sqrMagnitude > 1e-6f)
            {
                Vector3 majorAxis = majorDir.normalized;
                Vector3 minorAxis = Vector3.Cross(windDir, majorAxis);
                if (minorAxis.sqrMagnitude > 1e-6f)
                {
                    minorAxis.Normalize();
                    minorMax = 0f;
                    for (int i = 0; i < relCorners.Count; i++)
                    {
                        Vector3 rel = relCorners[i];
                        Vector3 perp = rel - Vector3.Dot(rel, windDir) * windDir;
                        float d = Mathf.Abs(Vector3.Dot(perp, minorAxis));
                        if (d > minorMax) minorMax = d;
                    }
                }
                s.MajorAxis = majorAxis;
            }

            s.WindwardRadius = Mathf.Max(perpMax, 0.5f);
            s.ForwardStandoff = Mathf.Max(fwd, 1f);
            s.AftStandoff = Mathf.Max(aft, 1f);
            s.RadiusMajor = Mathf.Max(perpMax, 0.5f);
            s.RadiusMinor = Mathf.Max(minorMax, 0.4f);
            return s;
        }
    }

    // Mesh placement: position/rotation/scale for the FX meshes, computed
    // from the silhouette. One function per effect so the preview and the
    // runtime cannot disagree on the numbers.
    internal static class FxPlacement
    {
        public struct MeshPose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        // Bowshock dome (unit mesh: base ring z=0 faces vessel, apex z=+1
        // faces airflow). 1.5× the elliptical silhouette — shock wider than
        // body; broadside gives a long "canoe" along the vessel's length.
        // Depth from the geometric mean (flat oblate, ~real blunt-body
        // shock). Base sits at the windward extreme; up = minor axis (⊥
        // wind, so also the degenerate-LookRotation guard).
        public static MeshPose Bowshock(in FxSilhouette s, Vector3 windDir, Vector3 anchor)
        {
            float radMajor = s.RadiusMajor * 1.5f;
            float radMinor = s.RadiusMinor * 1.5f;
            float depth = Mathf.Sqrt(radMajor * radMinor) * 0.55f;
            return new MeshPose
            {
                Position = anchor + windDir * s.ForwardStandoff,
                Rotation = Quaternion.LookRotation(windDir, s.MinorAxis(windDir)),
                Scale = new Vector3(radMajor, radMinor, depth),
            };
        }

        // Trail tube (built along local +Z, radius tapering from
        // naturalRadius at z=0). Head buried 0.5 m inside the vessel's aft
        // extreme so the hull occludes the start ring. Each perpendicular
        // axis scales to the vessel's elliptical silhouette in ABSOLUTE
        // metres (clamped 0.5–4 major / 0.4–4 minor), normalised by the
        // mesh's natural radius — so meshes of different natural sizes
        // (runtime 4 m, preview 0.6 m) produce the same world-space wake.
        public static MeshPose Trail(in FxSilhouette s, Vector3 windDir, Vector3 anchor, float naturalRadius)
        {
            float radMajor = Mathf.Clamp(s.RadiusMajor, 0.5f, 4f);
            float radMinor = Mathf.Clamp(s.RadiusMinor, 0.4f, 4f);
            return new MeshPose
            {
                Position = anchor - windDir * Mathf.Max(s.AftStandoff - 0.5f, 0f),
                Rotation = Quaternion.LookRotation(-windDir, s.MinorAxis(windDir)),
                Scale = new Vector3(radMajor / naturalRadius, radMinor / naturalRadius, 1f),
            };
        }
    }

    // Intensity ramps. Mach gates the physics (when a shock/wake exists);
    // q RAMPS the visibility — q must never be a binary gate, because on
    // reentry mach is far past saturation long before the atmosphere
    // thickens, and a gate pops the effect in at full strength the frame
    // q crosses it.
    internal static class FxRamps
    {
        private const float _qLow = 0.1f;   // kPa — onset
        private const float _qHigh = 1.5f;  // kPa — full strength

        private static float QRamp(float dynamicPressure)
        {
            return Mathf.SmoothStep(0f, 1f,
                Mathf.Clamp01(Mathf.InverseLerp(_qLow, _qHigh, dynamicPressure)));
        }

        // Transonic onset (mach 1.0), full by 2.5.
        public static float Bowshock(float mach, float dynamicPressure)
        {
            float machRamp = Mathf.Clamp01(Mathf.InverseLerp(1.0f, 2.5f, mach));
            return machRamp * QRamp(dynamicPressure);
        }

        // Slightly later onset (0.9), saturates earlier (3.0) — per spec,
        // the wake is more aggressive than the core sheath.
        public static float Trail(float mach, float dynamicPressure)
        {
            float machRamp = Mathf.Clamp01(Mathf.InverseLerp(0.9f, 3.0f, mach));
            return machRamp * QRamp(dynamicPressure);
        }
    }

    // Procedural FX meshes. One builder per shape so the preview renders
    // the exact geometry the plugin draws.
    internal static class FxMeshes
    {
        // Oblate dome (flattened hemisphere). Unit-sized: base ring at z=0
        // (faces vessel), apex at z=+1 (faces airflow); TRS scale produces
        // the real dimensions. Smooth-normal mesh — the shader derives a
        // spherical normal from the INTERPOLATED localPos, which kinks at
        // every latitude ring on a coarse mesh and quantises the fresnel
        // into visible concentric bands. 32×64 keeps it smooth.
        public static Mesh BuildDome()
        {
            const int latSeg = 32;
            const int lonSeg = 64;
            int ringVerts = lonSeg + 1; // seam-duplicated for UV continuity
            int totalVerts = latSeg * ringVerts + 1; // + apex pole
            var verts = new Vector3[totalVerts];
            var uvs = new Vector2[totalVerts];
            verts[0] = new Vector3(0f, 0f, 1f);
            uvs[0] = new Vector2(0.5f, 1f);
            for (int lat = 1; lat <= latSeg; lat++)
            {
                float phi = (lat / (float)latSeg) * Mathf.PI * 0.5f;
                float sp = Mathf.Sin(phi);
                float cp = Mathf.Cos(phi);
                for (int lon = 0; lon < ringVerts; lon++)
                {
                    float th = (lon / (float)lonSeg) * Mathf.PI * 2f;
                    int idx = 1 + (lat - 1) * ringVerts + lon;
                    verts[idx] = new Vector3(sp * Mathf.Cos(th), sp * Mathf.Sin(th), cp);
                    uvs[idx] = new Vector2(lon / (float)lonSeg, 1f - lat / (float)latSeg);
                }
            }
            var tris = new List<int>();
            for (int lon = 0; lon < lonSeg; lon++)
            {
                tris.Add(0); tris.Add(1 + lon); tris.Add(1 + lon + 1);
            }
            for (int lat = 1; lat < latSeg; lat++)
            {
                int rowA = 1 + (lat - 1) * ringVerts;
                int rowB = 1 + lat * ringVerts;
                for (int lon = 0; lon < lonSeg; lon++)
                {
                    tris.Add(rowA + lon);     tris.Add(rowB + lon);     tris.Add(rowA + lon + 1);
                    tris.Add(rowA + lon + 1); tris.Add(rowB + lon);     tris.Add(rowB + lon + 1);
                }
            }
            var mesh = new Mesh { name = "Kerbcast Bowshock Dome" };
            if (totalVerts > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Hollow tapered tube along local +Z: radius startRadius at z=0
        // (uv.y=0, the vessel end) tapering linearly to 0 at z=length
        // (uv.y=1). Radial outward normals (taper tilt ignored — close
        // enough for a thin radial gradient in the shader). No end caps.
        // Bounds are huge so the mesh never frustum-culls when the TRS
        // flings it far behind a fast vessel.
        public static Mesh BuildTaperedTube(float startRadius, float length, int lengthSeg, int radialSeg)
        {
            int rings = lengthSeg + 1;
            int vertsPerRing = radialSeg + 1; // seam duplicated
            int vertCount = rings * vertsPerRing;
            var verts = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var tris = new int[lengthSeg * radialSeg * 6];

            for (int r = 0; r < rings; r++)
            {
                float vy = (float)r / lengthSeg;
                float z = vy * length;
                float radius = Mathf.Lerp(startRadius, 0f, vy);
                for (int sIdx = 0; sIdx < vertsPerRing; sIdx++)
                {
                    float ux = (float)sIdx / radialSeg;
                    float angle = ux * Mathf.PI * 2f;
                    float cx = Mathf.Cos(angle);
                    float cy = Mathf.Sin(angle);
                    int i = r * vertsPerRing + sIdx;
                    verts[i] = new Vector3(cx * radius, cy * radius, z);
                    normals[i] = new Vector3(cx, cy, 0f);
                    uvs[i] = new Vector2(ux, vy);
                }
            }

            int t = 0;
            for (int r = 0; r < lengthSeg; r++)
            {
                int row0 = r * vertsPerRing;
                int row1 = (r + 1) * vertsPerRing;
                for (int sIdx = 0; sIdx < radialSeg; sIdx++)
                {
                    int a = row0 + sIdx;
                    int b = row0 + sIdx + 1;
                    int c = row1 + sIdx;
                    int d = row1 + sIdx + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }

            var mesh = new Mesh { name = "Kerbcast Trail Tube" };
            if (vertCount > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
            return mesh;
        }
    }
}
