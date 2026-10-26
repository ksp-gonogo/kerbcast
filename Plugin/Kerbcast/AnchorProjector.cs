using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Kerbcast
{
    /* A rig that knows its lens and its subject better than the generic
       projector can: the command camera, whose station is a double-precision
       world position far from the floating origin. */
    internal interface IAnchorRig
    {
        /* The pose the frame about to be captured was rendered from, in world
           space at double precision. False to read the near layer instead. */
        bool TryRenderPose(out DVec3 eye, out DVec3 forward, out DVec3 up);

        /* Kerbcast's own anchors on this camera, added to every frame's set. */
        void AddOwnAnchors(List<KeyValuePair<string, AnchorSpec>> into);

        /* Where the rig itself places craft `pid` this frame, with its size and
           whether its own line-of-sight test finds it hidden. False to locate
           the craft generically. */
        bool TryLocateVessel(uint pid, out DVec3 world, out double radius, out bool occluded);
    }

    /*
     * Projects every anchor on one camera through the lens that rendered the
     * frame about to be captured, and packs the results as the frame's ring
     * metadata. Runs right after the layers render, so the pose, field of view
     * and positions all belong to the same tick as the pixels: floating-origin
     * shifts and Krakensbane move the lens and the anchors together, and a zoom
     * or pan mid-stream is exact.
     */
    internal sealed class AnchorProjector
    {
        /* Ring metadata reserved per slot: 64 anchors with orientation fit
           with room to spare. */
        public const int MetaSlotBytes = 32 * 1024;
        private const int MaxAnchorsPerFrame = 64;
        private const double MinVesselRadius = 2.0;
        private const double PartPad = 1.5;
        private const float RadiusRefreshSeconds = 0.5f;
        /* Physics colliders only exist near the active craft; past this the ray
           cast would only ever find nothing. */
        private const double PhysicsRayMaxM = 5000.0;
        /* A ray stops this short of its target, so the thing marked (or the
           ground a surface anchor sits on) never hides itself. */
        private const float RayStopShortM = 0.5f;
        private const int OcclusionLayers = (1 << 0) | (1 << 15);

        private readonly uint _flightId;
        private readonly List<KeyValuePair<string, AnchorSpec>> _anchors = new List<KeyValuePair<string, AnchorSpec>>();
        private readonly List<AnchorResult> _results = new List<AnchorResult>();
        private readonly StringBuilder _sb = new StringBuilder(1024);
        private byte[] _buffer;
        private readonly Dictionary<uint, KeyValuePair<float, double>> _vesselRadii = new Dictionary<uint, KeyValuePair<float, double>>();
        private readonly Dictionary<uint, KeyValuePair<float, double>> _partRadii = new Dictionary<uint, KeyValuePair<float, double>>();
        private readonly List<DVec3> _partScratch = new List<DVec3>();
        private bool _failLogged;

        public AnchorProjector(uint flightId)
        {
            _flightId = flightId;
        }

        /* The frame's metadata: its field of view, and where every anchor on
           this camera landed in a `width` by `height` frame. */
        public FrameMeta Build(KerbcastCamera cam, int width, int height)
        {
            var near = cam.NearLayer;
            var meta = new FrameMeta { FovDeg = near != null ? near.fieldOfView : cam.Fov };
            var registry = KerbcastCore.Instance != null ? KerbcastCore.Instance.Anchors : null;
            var rig = cam.Rig as IAnchorRig;
            if (registry != null) registry.ForCamera(_flightId, _anchors);
            else _anchors.Clear();
            rig?.AddOwnAnchors(_anchors);
            if (_anchors.Count == 0 || width <= 0 || height <= 0) return meta;

            try
            {
                DVec3 eye, forward, up;
                if (rig == null || !rig.TryRenderPose(out eye, out forward, out up))
                {
                    var t = near != null ? near.transform : null;
                    if (t == null) return meta;
                    eye = ToD(t.position);
                    forward = ToD(t.forward).Normalized;
                    up = ToD(t.up).Normalized;
                }
                double m00, m11, m02 = 0, m12 = 0;
                if (near != null)
                {
                    var p = near.projectionMatrix;
                    m00 = p.m00; m11 = p.m11; m02 = p.m02; m12 = p.m12;
                }
                else
                {
                    CommandCameraMath.PerspectiveTerms(meta.FovDeg, (double)width / height, out m00, out m11);
                }
                /* Vertical field of view as the projection actually has it. */
                double fov = 2.0 * Math.Atan(1.0 / m11) * 180.0 / Math.PI;

                /* Under a held lock browser anchors are observed-only unless the owner relaxed it. */
                var auth = KerbcastCore.Instance != null ? KerbcastCore.Instance.CommandAuthority : null;
                bool locked = auth != null && auth.Registered;
                var policy = locked ? auth.BrowserAnchorPolicy(_flightId) : AnchorPolicy.Full;

                _results.Clear();
                var frame = new Frame
                {
                    Cam = cam, Rig = rig, Registry = registry, Eye = eye, Forward = forward, Up = up,
                    M00 = m00, M11 = m11, M02 = m02, M12 = m12, Width = width, Height = height, Fov = fov,
                };
                for (int i = 0; i < _anchors.Count && _results.Count < MaxAnchorsPerFrame; i++)
                {
                    string owner = _anchors[i].Key;
                    bool observed = AnchorRules.Observed(owner, locked, policy);
                    if (TryProject(frame, owner, _anchors[i].Value, observed, out var r)) _results.Add(r);
                }
                meta.Length = AnchorJson.Encode(_results, _sb, ref _buffer, MetaSlotBytes);
                meta.Json = meta.Length > 0 ? _buffer : null;
            }
            catch (Exception ex)
            {
                if (!_failLogged)
                {
                    _failLogged = true;
                    Debug.LogWarning($"[Kerbcast][ANCHOR] cam={_flightId} projection threw (logged once): {ex}");
                }
            }
            return meta;
        }

        private struct Frame
        {
            public KerbcastCamera Cam;
            public IAnchorRig Rig;
            public AnchorRegistry Registry;
            public DVec3 Eye, Forward, Up;
            public double M00, M11, M02, M12;
            public int Width, Height;
            public double Fov;
        }

        /* An observed anchor always gets its occlusion tested: what the picture
           shows depends on it. */
        private bool TryProject(Frame f, string owner, AnchorSpec a, bool observed, out AnchorResult r)
        {
            r = default;
            DVec3 world = default;
            double radius = 0;
            bool hasAxes = false;
            DVec3 axisForward = default, axisUp = default;
            Part ignorePart = null;
            Vessel ignoreVessel = null;
            bool occlusionKnown = false, occluded = false;

            switch (a.Kind)
            {
                case AnchorKind.Part:
                {
                    if (!FlightGlobals.FindLoadedPart(a.Part, out var part) || part == null || part.transform == null) return false;
                    var basis = DockingFace(part) ?? part.transform;
                    var offset = new Vector3((float)a.OffsetX, (float)a.OffsetY, (float)a.OffsetZ);
                    world = ToD(basis.position) + ToD(basis.rotation * offset);
                    radius = PartRadius(part);
                    if (basis != part.transform)
                    {
                        axisForward = ToD(basis.forward);
                        axisUp = ToD(basis.up);
                    }
                    else
                    {
                        axisForward = ToD(part.transform.up);
                        axisUp = ToD(-part.transform.forward);
                    }
                    hasAxes = true;
                    ignorePart = part;
                    break;
                }
                case AnchorKind.Vessel:
                {
                    uint pid = f.Registry != null ? f.Registry.Resolve(a.Vessel) : a.Vessel;
                    if (f.Rig != null && f.Rig.TryLocateVessel(pid, out world, out radius, out occluded))
                    {
                        occlusionKnown = true;
                        FlightGlobals.FindVessel(pid, out ignoreVessel);
                    }
                    else
                    {
                        if (!FlightGlobals.FindVessel(pid, out var v) || v == null) return false;
                        world = ToD(v.packed ? v.GetWorldPos3D() : v.CoMD);
                        radius = VesselRadius(v, world);
                        ignoreVessel = v;
                    }
                    var rt = ignoreVessel != null && ignoreVessel.loaded ? ignoreVessel.ReferenceTransform : null;
                    if (rt != null)
                    {
                        axisForward = ToD(rt.up);
                        axisUp = ToD(-rt.forward);
                        hasAxes = true;
                    }
                    break;
                }
                case AnchorKind.Surface:
                {
                    var body = FlightGlobals.GetBodyByName(a.Body);
                    if (body == null) return false;
                    world = ToD(body.GetWorldSurfacePosition(a.Latitude, a.Longitude, a.Altitude));
                    break;
                }
                case AnchorKind.Direction:
                {
                    if (!TryDirection(a, f.Registry, out var dir)) return false;
                    var dp = CommandCameraMath.ProjectCameraSpace(CommandCameraMath.ToCameraSpace(dir, f.Forward, f.Up),
                        f.M00, f.M11, f.M02, f.M12, f.Width, f.Height);
                    r = Result(owner, a, dp, double.NaN, 0);
                    r.Observed = observed;
                    if (a.Occlusion || observed)
                    {
                        r.HasOcclusion = true;
                        r.Occluded = Hidden(f, f.Eye + dir * 1e13, null, null, false);
                    }
                    return true;
                }
            }

            var p = CommandCameraMath.Project(f.Eye, f.Forward, f.Up, f.M00, f.M11, f.M02, f.M12, f.Width, f.Height, world);
            double apparent = CommandCameraMath.ApparentRadiusPx(radius + a.MarginM, p.Distance, f.Fov, f.Height);
            r = Result(owner, a, p, p.Distance, apparent);
            r.Observed = observed;
            if (a.Orientation && hasAxes)
            {
                r.HasOrientation = true;
                r.Forward = CommandCameraMath.ToCameraSpace(axisForward.Normalized, f.Forward, f.Up);
                r.Up = CommandCameraMath.ToCameraSpace(axisUp.Normalized, f.Forward, f.Up);
            }
            if (a.Occlusion || observed)
            {
                r.HasOcclusion = true;
                r.Occluded = occlusionKnown ? occluded : Hidden(f, world, ignorePart, ignoreVessel, true);
            }
            return true;
        }

        private static AnchorResult Result(string owner, AnchorSpec a, FrameProjection p, double distance, double apparent) =>
            new AnchorResult
            {
                Id = a.Id,
                Owner = owner,
                X = p.X,
                Y = p.Y,
                InFrame = p.InFrame,
                Behind = p.Behind,
                DistanceM = distance,
                ApparentRadiusPx = apparent,
                AngleOffAxisDeg = p.AngleOffAxisDeg,
            };

        /* A docking port's face: its node transform, which faces out of the port. */
        private static Transform DockingFace(Part part)
        {
            var node = part.FindModuleImplementing<ModuleDockingNode>();
            return node != null && node.nodeTransform != null ? node.nodeTransform : null;
        }

        /*
         * A craft's named direction in world space, or a fixed inertial vector.
         * Orbital directions come from the craft's orbit in the inertial frame
         * of Orbit.getRelativePositionAtUT (z up), turned to world space with
         * the same xzy swizzle Orbit.getPositionAtUT uses.
         */
        private static bool TryDirection(AnchorSpec a, AnchorRegistry registry, out DVec3 dir)
        {
            dir = default;
            if (a.HasVector)
            {
                dir = new DVec3(a.VectorX, a.VectorZ, a.VectorY).Normalized;
                return true;
            }
            Vessel v = null;
            if (a.Vessel != 0) FlightGlobals.FindVessel(registry != null ? registry.Resolve(a.Vessel) : a.Vessel, out v);
            else v = FlightGlobals.ActiveVessel;
            if (v == null) return false;
            var o = v.orbit;
            DVec3 at = ToD(v.packed ? v.GetWorldPos3D() : v.CoMD);
            switch (a.Direction)
            {
                case AnchorDirection.Prograde:
                case AnchorDirection.Retrograde:
                case AnchorDirection.Normal:
                case AnchorDirection.Antinormal:
                case AnchorDirection.RadialIn:
                case AnchorDirection.RadialOut:
                {
                    if (o == null) return false;
                    var pos = new DVec3(o.pos.x, o.pos.y, o.pos.z);
                    var vel = new DVec3(o.vel.x, o.vel.y, o.vel.z);
                    var pro = vel.Normalized;
                    var normal = DVec3.Cross(pos, vel).Normalized;
                    var radialOut = DVec3.Cross(pro, normal).Normalized;
                    DVec3 zup;
                    switch (a.Direction)
                    {
                        case AnchorDirection.Prograde: zup = pro; break;
                        case AnchorDirection.Retrograde: zup = pro * -1; break;
                        case AnchorDirection.Normal: zup = normal; break;
                        case AnchorDirection.Antinormal: zup = normal * -1; break;
                        case AnchorDirection.RadialOut: zup = radialOut; break;
                        default: zup = radialOut * -1; break;
                    }
                    dir = new DVec3(zup.X, zup.Z, zup.Y);
                    break;
                }
                case AnchorDirection.Maneuver:
                {
                    var solver = v.patchedConicSolver;
                    if (solver == null || solver.maneuverNodes == null || solver.maneuverNodes.Count == 0 || o == null) return false;
                    dir = ToD(solver.maneuverNodes[0].GetBurnVector(o));
                    break;
                }
                case AnchorDirection.Sun:
                {
                    var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
                    if (sun == null) return false;
                    dir = ToD(sun.position) - at;
                    break;
                }
                case AnchorDirection.Target:
                {
                    var target = v.targetObject;
                    if (target == null || target.GetTransform() == null) return false;
                    dir = ToD(target.GetTransform().position) - at;
                    break;
                }
                case AnchorDirection.TargetRelativeVelocity:
                {
                    var target = v.targetObject;
                    if (target == null) return false;
                    dir = ToD(v.obt_velocity) - ToD(target.GetObtVelocity());
                    break;
                }
                default:
                    return false;
            }
            if (dir.Magnitude < 1e-9) return false;
            dir = dir.Normalized;
            return true;
        }

        /* Whether a body, terrain or a collider stands between the lens and
           `point`. Bodies count at any range, as spheres; colliders only near
           the lens, where physics has them, never counting the thing marked or
           the camera's own part. */
        private static bool Hidden(Frame f, DVec3 point, Part ignorePart, Vessel ignoreVessel, bool physics)
        {
            var bodies = FlightGlobals.Bodies;
            if (bodies != null)
            {
                for (int i = 0; i < bodies.Count; i++)
                {
                    var b = bodies[i];
                    if (b == null) continue;
                    if (!CommandCameraMath.SegmentClearOfSphere(f.Eye, point, ToD(b.position), b.Radius * (1.0 - 1e-7))) return true;
                }
            }
            var d = point - f.Eye;
            double dist = d.Magnitude;
            if (!physics || dist <= RayStopShortM || dist > PhysicsRayMaxM) return false;
            var origin = new Vector3((float)f.Eye.X, (float)f.Eye.Y, (float)f.Eye.Z);
            var dirF = new Vector3((float)(d.X / dist), (float)(d.Y / dist), (float)(d.Z / dist));
            var hits = Physics.RaycastAll(origin, dirF, (float)dist - RayStopShortM, OcclusionLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i].collider;
                if (c == null) continue;
                var part = c.GetComponentInParent<Part>();
                if (part != null)
                {
                    if (part == ignorePart || (ignoreVessel != null && part.vessel == ignoreVessel) || f.Cam.OwnsPart(part)) continue;
                }
                return true;
            }
            return false;
        }

        private double PartRadius(Part part)
        {
            float now = Time.unscaledTime;
            if (_partRadii.TryGetValue(part.persistentId, out var c) && now - c.Key < RadiusRefreshSeconds) return c.Value;
            double r = 0;
            var renderers = part.FindModelComponents<Renderer>();
            if (renderers != null && renderers.Count > 0)
            {
                bool any = false;
                var bounds = default(Bounds);
                foreach (var rend in renderers)
                {
                    if (rend == null || !rend.enabled || rend is ParticleSystemRenderer) continue;
                    if (!any) { bounds = rend.bounds; any = true; }
                    else bounds.Encapsulate(rend.bounds);
                }
                if (any) r = bounds.extents.magnitude;
            }
            _partRadii[part.persistentId] = new KeyValuePair<float, double>(now, r);
            return r;
        }

        /* The command camera's bracket size rule: the farthest part origin from
           the centre of mass plus a pad, never below a minimum. */
        private double VesselRadius(Vessel v, DVec3 com)
        {
            float now = Time.unscaledTime;
            if (_vesselRadii.TryGetValue(v.persistentId, out var c) && now - c.Key < RadiusRefreshSeconds) return c.Value;
            _partScratch.Clear();
            if (v.loaded && v.parts != null)
            {
                for (int i = 0; i < v.parts.Count; i++)
                {
                    var p = v.parts[i];
                    if (p != null && p.transform != null) _partScratch.Add(ToD(p.transform.position));
                }
            }
            double r = _partScratch.Count > 0
                ? CommandCameraMath.BoundingRadius(_partScratch, com, PartPad, MinVesselRadius)
                : MinVesselRadius;
            _vesselRadii[v.persistentId] = new KeyValuePair<float, double>(now, r);
            return r;
        }

        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
        private static DVec3 ToD(Vector3 v) => new DVec3(v.x, v.y, v.z);
    }
}
