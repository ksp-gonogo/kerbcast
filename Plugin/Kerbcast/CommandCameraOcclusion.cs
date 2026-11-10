using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * Whether a body hides a point from a CommNet station, tested against
     * what the command camera actually draws of that body:
     *
     * - The station's own body, for a surface station: its terrain skyline
     *   (HorizonMask), built from TerrainAltitude once per azimuth bin,
     *   then any building or other scenery collider close to the lens.
     * - Every other body: its scaled-space mesh, which KerbcastCamera draws
     *   for every body but the station's. Where that body's PQS is active
     *   its terrain also draws in the local layers, over the mesh, so the
     *   terrain is sampled as well and either one blocks.
     * - A vessel vantage's own body: terrain sampled along the line, as
     *   before.
     * - A body with no readable scaled mesh: the sphere at its radius.
     *
     * The rig's per-frame test and the forecast share Blocks, so recorded
     * and predicted edges come from the same geometry.
     */
    internal sealed class CommandCameraOcclusion
    {
        private const string Tag = "[Kerbcast][CMDCAM]";
        private const double MaskBinDeg = 0.25;
        /* Skyline bins one call may build; the rest fall back to the
           sea-level skyline for that call only. */
        public const int MaskBudgetPerCall = 4;
        /* Bound on a body's scaled mesh before it is built, over its
           highest terrain. */
        private const double UnbuiltMeshPad = 1.05;

        public const string SourceScaled = "scaled";
        public const string SourceTerrain = "terrain";
        public const string SourceSphere = "sphere";
        public const string SourceScenery = "scenery";

        /* Scenery farther from the lens than this is left to the skyline. */
        private const double SceneryReachM = 5000.0;
        /* Above the tallest KSC building, measured from a lens standing on the ground. */
        private const double SceneryCeilingM = 300.0;
        /* Local Scenery: buildings, launch pads and terrain quads. */
        private const int SceneryLayers = 1 << 15;
        private static readonly RaycastHit[] SceneryHits = new RaycastHit[8];

        private sealed class MeshEntry
        {
            public Mesh Mesh;
            public ScaledMeshOccluder Occluder;
        }

        /* Bodies and their scaled meshes outlive scenes, so one build serves
           every rig. A null occluder marks a mesh that cannot be used. */
        private static readonly Dictionary<CelestialBody, MeshEntry> Meshes = new Dictionary<CelestialBody, MeshEntry>();

        private readonly int _terrainSamples;
        private readonly uint _flightId;
        private HorizonMask _mask;
        private CelestialBody _maskBody;
        private double _maskLat, _maskLon, _maskAlt;
        private bool _maskWanted;
        /* A vessel vantage's body: terrain sampled along the line. */
        private CelestialBody _vantageBody;

        public CommandCameraOcclusion(int terrainSamples, uint flightId)
        {
            _terrainSamples = terrainSamples;
            _flightId = flightId;
        }

        /* The station body tested by skyline, or null. */
        public CelestialBody MaskBody => _maskWanted ? _maskBody : null;

        /* Points the skyline at the site, rebuilding it when the site has
           moved, and refills the per-call bin budget. */
        public void UseSite(CommandCameraSite site)
        {
            var body = site.Body;
            _vantageBody = site.HasVantage ? body : null;
            _maskWanted = !site.HasVantage && body != null && body.pqsController != null;
            if (!_maskWanted) return;
            if (_mask == null || body != _maskBody || site.Latitude != _maskLat || site.Longitude != _maskLon
                || site.Altitude != _maskAlt)
            {
                _maskBody = body;
                _maskLat = site.Latitude;
                _maskLon = site.Longitude;
                _maskAlt = site.Altitude;
                var b = body;
                double seaR = b.Radius;
                bool allowNegative = !b.ocean;
                _mask = new HorizonMask(seaR + site.Altitude, seaR, b.pqsController.radiusMax, MaskBinDeg, dir =>
                {
                    var world = b.position + new Vector3d(dir.X, dir.Y, dir.Z) * seaR;
                    return seaR + b.TerrainAltitude(b.GetLatitude(world), b.GetLongitude(world), allowNegative);
                });
                Debug.Log($"{Tag} cam={_flightId} event=horizon-mask body={b.bodyName} lat={site.Latitude:F5} lon={site.Longitude:F5} "
                    + $"alt={site.Altitude:F1} bins={360.0 / MaskBinDeg:F0} samplesPerBin={_mask.SampleCount} "
                    + $"reach={_mask.ArcLimitRad * seaR / 1000.0:F1}km (built per bin on first need)");
            }
            _mask.Budget = MaskBudgetPerCall;
        }

        public int MaskBinsBuilt => _mask?.BinsBuilt ?? 0;
        public int MaskFallbacks => _mask?.Fallbacks ?? 0;

        /* The first body hiding `target` from `station` now, or null. A
           target radius counts against terrain only: the station's own
           skyline and terrain sampled along the line. */
        public string FindOccluder(DVec3 station, DVec3 target, out string source, double targetRadius = 0)
        {
            source = null;
            var bodies = FlightGlobals.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b == null) continue;
                var c = ToD(b.position);
                if (Blocks(b, station, target, c, c, Spin(b), 0, out source, targetRadius)) return b.bodyName;
            }
            return null;
        }

        /* The picture's FindOccluder: the first body hiding `target` from
           `station` with each body where `picture` draws it, at its past
           instant and turned back by its spin, and none the picture leaves
           out. For what a frame shows, never for what the camera knows. */
        public string FindDrawnOccluder(PicturePass picture, DVec3 station, DVec3 target, out string source, double targetRadius = 0)
        {
            source = null;
            var bodies = FlightGlobals.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b == null || picture.HidesBody(b)) continue;
                var c = ToD(b.position);
                /* `dt` is the spin back too for a body drawn where it is. */
                var at = picture.TryDrawnBody(b, out var drawn, out double dt) ? drawn : c;
                if (Blocks(b, station, target, at, c, Spin(b), dt, out source, targetRadius)) return b.bodyName;
            }
            return null;
        }

        public static DVec3 Spin(CelestialBody b) => b.rotates ? ToD(b.angularVelocity) : default;

        /* Farthest any drawn surface of the body reaches from its centre. */
        public static double BoundRadius(CelestialBody b)
        {
            var pqs = b.pqsController;
            double r = Math.Max(b.Radius, pqs != null ? pqs.radiusMax : 0);
            if (Meshes.TryGetValue(b, out var e) && e.Occluder != null) return Math.Max(r, e.Occluder.RMax);
            return r * UnbuiltMeshPad;
        }

        /*
         * Whether body `b` hides `target` from `station`, both in the world
         * of now at time now + dt, with the body's centre then at centreAtT
         * and now at centreNow, spinning at `spin`. A target on the body's
         * own surface is not hidden by the ground it stands on, only by the
         * body between it and the station (SurfaceSightEnd).
         */
        public bool Blocks(CelestialBody b, DVec3 station, DVec3 target, DVec3 centreAtT, DVec3 centreNow, DVec3 spin,
            double dt, out string source, double targetRadius = 0)
        {
            source = null;
            if (_maskWanted && b == _maskBody && _mask != null)
            {
                var z = b.BodyFrame.Z;
                if (LineOfSightMath.SkylineBlocks(_mask, station, target, centreAtT, centreNow, spin, dt, new DVec3(z.x, z.z, z.y), targetRadius))
                {
                    source = SourceTerrain;
                    return true;
                }
                if (!SceneryBlocks(station, target, centreAtT, centreNow, spin, dt)) return false;
                source = SourceScenery;
                return true;
            }

            if (CommandCameraMath.SegmentClearOfSphere(station, target, centreAtT, BoundRadius(b))) return false;
            var pqs = b.pqsController;
            if (b != _vantageBody)
            {
                var occ = MeshFor(b);
                if (occ != null)
                {
                    MeshAxes(b, out var ax, out var ay, out var az);
                    var a = CommandCameraMath.ToBodyLocal(station, centreAtT, spin, dt, ax, ay, az);
                    var t = CommandCameraMath.ToBodyLocal(target, centreAtT, spin, dt, ax, ay, az);
                    double slack = t.Magnitude < occ.RMax ? 2.0 * (occ.RMax - occ.RMin) : 0.0;
                    if (occ.Blocks(a, t, slack))
                    {
                        source = SourceScaled;
                        return true;
                    }
                }
                else if (!CommandCameraMath.SegmentClearOfSphere(station,
                    CommandCameraMath.SurfaceSightEnd(station, target, centreAtT, b.Radius, b.Radius), centreAtT, b.Radius))
                {
                    source = SourceSphere;
                    return true;
                }
                if (pqs == null || !pqs.isActive) return false;
            }
            if (pqs == null)
            {
                var end = CommandCameraMath.SurfaceSightEnd(station, target, centreAtT, b.Radius, b.Radius);
                if (CommandCameraMath.SegmentClearOfSphere(station, end, centreAtT, b.Radius)) return false;
                source = SourceSphere;
                return true;
            }
            if (TerrainClear(b, station, target, centreAtT, centreNow, spin, dt, targetRadius)) return false;
            source = SourceTerrain;
            return true;
        }

        /*
         * Whether a building or other scenery collider stands on the sight
         * line close to a surface station's lens. The skyline knows only the
         * terrain, so a lens beside the VAB would otherwise see straight
         * through it. Tested in the world as it is now, where the colliders
         * are, with terrain quads skipped since the skyline already covers
         * them, and craft too, which are not scenery.
         */
        private static bool SceneryBlocks(DVec3 station, DVec3 target, DVec3 centreAtT, DVec3 centreNow, DVec3 spin, double dt)
        {
            var from = CommandCameraMath.ToBodyFrameNow(station, centreAtT, centreNow, spin, dt);
            var to = CommandCameraMath.ToBodyFrameNow(target, centreAtT, centreNow, spin, dt);
            var d = to - from;
            double dist = d.Magnitude;
            if (!(dist > 0)) return false;
            var dir = d * (1.0 / dist);
            double rise = DVec3.Dot(dir, (from - centreNow).Normalized);
            double len = CommandCameraMath.SceneryRayM(dist, rise, SceneryReachM, SceneryCeilingM);
            if (len <= 0) return false;
            int n = Physics.RaycastNonAlloc(new Vector3((float)from.X, (float)from.Y, (float)from.Z),
                new Vector3((float)dir.X, (float)dir.Y, (float)dir.Z), SceneryHits, (float)len, SceneryLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = SceneryHits[i].collider;
                if (c == null || c.GetComponent<PQ>() != null || c.GetComponentInParent<Part>() != null) continue;
                return true;
            }
            return false;
        }

        private bool TerrainClear(CelestialBody b, DVec3 station, DVec3 target, DVec3 centreAtT, DVec3 centreNow,
            DVec3 spin, double dt, double targetRadius)
        {
            Func<DVec3, double> surface = p =>
            {
                var now = CommandCameraMath.ToBodyFrameNow(p, centreAtT, centreNow, spin, dt);
                var world = new Vector3d(now.X, now.Y, now.Z);
                return b.Radius + b.TerrainAltitude(b.GetLatitude(world), b.GetLongitude(world));
            };
            /* A craft is kept clear by its own radius; an aim point with no size skips its own shell (SurfaceSightEnd), so a landed craft behind a nearby ridge still reads hidden. */
            double top = b.pqsController.radiusMax;
            var end = targetRadius > 0 ? target : CommandCameraMath.SurfaceSightEnd(station, target, centreAtT, b.Radius, top);
            /* A lens riding a craft landed on this body rests on its ground as a landed target does; its size is not read, so the nominal one stands in. */
            double stationRadius = b == _vantageBody ? CommandCameraMath.NominalCraftRadiusM : 0;
            return CommandCameraMath.SegmentClearOfTerrain(station, end, centreAtT, b.Radius, top, surface, _terrainSamples,
                targetRadius, stationRadius);
        }

        /* The scaled mesh's axes in world space now. ScaledMovement sets its
           local rotation to the body transform's rotation each LateUpdate;
           taking the body's own rotation keeps this tick's spin. */
        private static void MeshAxes(CelestialBody b, out DVec3 ax, out DVec3 ay, out DVec3 az)
        {
            var parent = b.scaledBody.transform.parent;
            var rot = (parent != null ? parent.rotation : Quaternion.identity) * b.bodyTransform.rotation;
            ax = ToD(rot * Vector3.right);
            ay = ToD(rot * Vector3.up);
            az = ToD(rot * Vector3.forward);
        }

        /*
         * The body's scaled mesh in metres in its own axes: mesh vertices
         * times the scaled transform's lossy scale times ScaledSpace's scale
         * factor, the same mapping that places the drawn mesh in the scaled
         * scene (ScaledSpace.LocalToScaledSpace divides world positions by
         * that factor). Built once, logged with its radii against the body's.
         */
        private ScaledMeshOccluder MeshFor(CelestialBody b)
        {
            var go = b.scaledBody;
            var mf = go != null ? go.GetComponent<MeshFilter>() : null;
            var mesh = mf != null ? mf.sharedMesh : null;
            if (Meshes.TryGetValue(b, out var e) && e.Mesh == mesh) return e.Occluder;
            e = new MeshEntry { Mesh = mesh };
            Meshes[b] = e;
            if (mesh == null || !mesh.isReadable)
            {
                Debug.Log($"{Tag} cam={_flightId} event=scaled-occluder body={b.bodyName} unavailable "
                    + $"({(mesh == null ? "no mesh" : "mesh not readable")}), sphere at {b.Radius:F0}m stands in");
                return null;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var v = mesh.vertices;
                var tris = mesh.triangles;
                var scale = go.transform.lossyScale;
                double k = ScaledSpace.ScaleFactor;
                var verts = new DVec3[v.Length];
                for (int i = 0; i < v.Length; i++)
                    verts[i] = new DVec3(v[i].x * scale.x * k, v[i].y * scale.y * k, v[i].z * scale.z * k);
                e.Occluder = new ScaledMeshOccluder(verts, tris);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} cam={_flightId} event=scaled-occluder body={b.bodyName} build threw, sphere stands in: {ex}");
                e.Occluder = null;
                return null;
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MeshAxes(b, out var ax, out _, out _);
            var drawn = go.transform.rotation * Vector3.right;
            double skewDeg = CommandCameraMath.AngleBetween(ax, ToD(drawn)) * 180.0 / Math.PI;
            var o = e.Occluder;
            var s = go.transform.lossyScale;
            var pqs = b.pqsController;
            Debug.Log($"{Tag} cam={_flightId} event=scaled-occluder body={b.bodyName} triangles={o.TriangleCount} "
                + $"rMin={o.RMin:F0}m rMax={o.RMax:F0}m rInner={o.RInner:F0}m radius={b.Radius:F0}m "
                + $"radiusMax={(pqs != null ? pqs.radiusMax.ToString("F0") : "-")}m scale={s.x:G6},{s.y:G6},{s.z:G6} "
                + $"scaleFactor={ScaledSpace.ScaleFactor:F0} axisSkewDeg={skewDeg:G3} buildMs={ms:F1}");
            return o;
        }

        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
        private static DVec3 ToD(Vector3 v) => new DVec3(v.x, v.y, v.z);
    }
}
