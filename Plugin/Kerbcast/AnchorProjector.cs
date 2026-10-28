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
           the craft generically, unless `unseen`: no light from it is arriving,
           so it has no place in the frame at all. */
        bool TryLocateVessel(uint pid, out DVec3 world, out double radius, out bool occluded, out bool unseen);
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
        private DVec3[] _bodyCentres;
        private double[] _bodyRadii;

        public AnchorProjector(uint flightId)
        {
            _flightId = flightId;
        }

        /* The frame's metadata: its field of view, the boundary distance, a
           command camera's state, and where every anchor on this camera
           landed in a `width` by `height` frame. */
        public FrameMeta Build(KerbcastCamera cam, int width, int height)
        {
            var near = cam.NearLayer;
            var meta = new FrameMeta { FovDeg = near != null ? near.fieldOfView : cam.Fov };
            _results.Clear();
            Project(cam, near, width, height, meta.FovDeg);
            return Encode(cam, meta);
        }

        /* A blanked frame's metadata: no anchors, since it shows nothing, but the state that says why. */
        public FrameMeta BuildBlank(KerbcastCamera cam)
        {
            _results.Clear();
            return Encode(cam, new FrameMeta { FovDeg = cam.Fov });
        }

        private FrameMeta Encode(KerbcastCamera cam, FrameMeta meta)
        {
            CommandFrameState? command = cam.Rig is CommandCameraRig rig ? rig.FrameState : (CommandFrameState?)null;
            if (KerbcastSettings.EnableTelemetry)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                meta.Length = FrameMetaJson.Encode(LiveLightTime.BoundaryM, command, _results, _sb, ref _buffer, MetaSlotBytes);
                Telemetry.MetaEncode.Record(Telemetry.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t0));
                Telemetry.MetaBytes.Record(meta.Length);
            }
            else meta.Length = FrameMetaJson.Encode(LiveLightTime.BoundaryM, command, _results, _sb, ref _buffer, MetaSlotBytes);
            meta.Json = meta.Length > 0 ? _buffer : null;
            return meta;
        }

        private void Project(KerbcastCamera cam, Camera near, int width, int height, float fovDeg)
        {
            var registry = KerbcastCore.Instance != null ? KerbcastCore.Instance.Anchors : null;
            var rig = cam.Rig as IAnchorRig;
            /* Kerbcast's own anchors go first, so however many others a camera carries they are never cut at the frame's cap. */
            _anchors.Clear();
            rig?.AddOwnAnchors(_anchors);
            registry?.AddForCamera(_flightId, _anchors);
            if (_anchors.Count == 0 || width <= 0 || height <= 0) return;

            try
            {
                DVec3 eye, forward, up;
                if (rig == null || !rig.TryRenderPose(out eye, out forward, out up))
                {
                    var t = near != null ? near.transform : null;
                    if (t == null) return;
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
                    CommandCameraMath.PerspectiveTerms(fovDeg, (double)width / height, out m00, out m11);
                }
                /* Vertical field of view as the projection actually has it. */
                double fov = 2.0 * Math.Atan(1.0 / m11) * 180.0 / Math.PI;

                /* Under a held lock browser anchors are observed-only unless the owner relaxed it. */
                var auth = KerbcastCore.Instance != null ? KerbcastCore.Instance.CommandAuthority : null;
                bool locked = auth != null && auth.Registered;
                var policy = locked ? auth.BrowserAnchorPolicy(_flightId) : AnchorPolicy.Full;

                int bodyCount = cam.Picture.DrawnBodies(ref _bodyCentres, ref _bodyRadii);
                var frame = new Frame
                {
                    Cam = cam, Rig = rig, Registry = registry, Eye = eye, Forward = forward, Up = up,
                    M00 = m00, M11 = m11, M02 = m02, M12 = m12, Width = width, Height = height, Fov = fov,
                    BodyCentres = _bodyCentres, BodyRadii = _bodyRadii, BodyCount = bodyCount,
                };
                for (int i = 0; i < _anchors.Count && _results.Count < MaxAnchorsPerFrame; i++)
                {
                    string owner = _anchors[i].Key;
                    var kind = _anchors[i].Value.Kind;
                    bool observed = !_anchors[i].Value.NeverObserved && AnchorRules.Observed(owner, locked, policy, kind);
                    if (!TryProject(frame, owner, _anchors[i].Value, observed, out var r)) continue;
                    r.PoseWithheld = AnchorRules.PoseWithheld(owner, locked, policy, kind);
                    _results.Add(r);
                }
            }
            catch (Exception ex)
            {
                _results.Clear();
                if (!_failLogged)
                {
                    _failLogged = true;
                    Debug.LogWarning($"[Kerbcast][ANCHOR] cam={_flightId} projection threw (logged once): {ex}");
                }
            }
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
            /* Each body where the picture draws it (PicturePass.DrawnBodies). */
            public DVec3[] BodyCentres;
            public double[] BodyRadii;
            public int BodyCount;
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
                    var offset = new Vector3((float)a.OffsetX, (float)a.OffsetY, (float)a.OffsetZ);
                    /* A part a hidden craft carried as its light shows it is marked where
                       that craft's stand-in draws it, whatever has become of it since. */
                    if (f.Cam.Picture.TryHiddenPart(a.Part, out world, out var turned, out radius))
                    {
                        /* A docking port's face keeps its pose on the part, carried with it. */
                        var face = HiddenPartFace(a.Part, out var faceAt, out var faceTurn);
                        if (face)
                        {
                            world = world + ToD(turned * faceAt);
                            turned = turned * faceTurn;
                        }
                        world = world + ToD(turned * offset);
                        axisForward = ToD(turned * (face ? Vector3.forward : Vector3.up));
                        axisUp = ToD(turned * (face ? Vector3.up : Vector3.back));
                        hasAxes = true;
                        break;
                    }
                    if (!FlightGlobals.FindLoadedPart(a.Part, out var part) || part == null || part.transform == null) return false;
                    if (f.Cam.Picture.Hides(part.vessel)) return false;
                    radius = PartRadius(part);
                    ignorePart = part;
                    var basis = DockingFace(part) ?? part.transform;
                    world = ToD(basis.position) + ToD(basis.rotation * offset);
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
                    break;
                }
                case AnchorKind.Vessel:
                {
                    uint pid = f.Registry != null ? f.Registry.Resolve(a.Vessel) : a.Vessel;
                    bool unseen = false;
                    if (f.Rig != null && f.Rig.TryLocateVessel(pid, out world, out radius, out occluded, out unseen))
                    {
                        occlusionKnown = true;
                        FlightGlobals.FindVessel(pid, out ignoreVessel);
                    }
                    else
                    {
                        if (unseen) return false;
                        /* Every camera's anchors follow its picture: a craft drawn at its past instant is marked there. */
                        var sight = f.Cam.Picture.Locate(pid, out world, out radius);
                        if (sight == OtherSight.Unseen) return false;
                        if (sight == OtherSight.Past) FlightGlobals.FindVessel(pid, out ignoreVessel);
                        else
                        {
                            if (!FlightGlobals.FindVessel(pid, out var v) || v == null) return false;
                            world = ToD(v.packed ? v.GetWorldPos3D() : v.CoMD);
                            radius = VesselRadius(v, world);
                            ignoreVessel = v;
                        }
                    }
                    var rt = ignoreVessel != null && ignoreVessel.loaded ? ignoreVessel.ReferenceTransform : null;
                    if (rt != null && !f.Cam.Picture.Hides(ignoreVessel))
                    {
                        axisForward = ToD(rt.up);
                        axisUp = ToD(-rt.forward);
                        hasAxes = true;
                    }
                    else if (rt != null && f.Cam.Picture.TryCarry(pid, ToD(ignoreVessel.CoMD), ignoreVessel.transform.rotation, out _, out var turn))
                    {
                        /* A hidden craft's axes turn with its stand-in. */
                        axisForward = ToD(turn * rt.up);
                        axisUp = ToD(turn * -rt.forward);
                        hasAxes = true;
                    }
                    break;
                }
                case AnchorKind.Surface:
                {
                    var body = FlightGlobals.GetBodyByName(a.Body);
                    if (body == null || f.Cam.Picture.HidesBody(body)) return false;
                    world = OnDrawnBody(f.Cam.Picture, body, ToD(body.GetWorldSurfacePosition(a.Latitude, a.Longitude, a.Altitude)));
                    break;
                }
                case AnchorKind.Kinematic:
                {
                    if (!TryKinematic(a, f.Eye, f.Cam.Picture, out world)) return false;
                    break;
                }
                case AnchorKind.Direction:
                {
                    if (!TryDirection(a, f.Registry, f.Cam.Picture, out var dir)) return false;
                    var dp = CommandCameraMath.ProjectCameraSpace(CommandCameraMath.ToCameraSpace(dir, f.Forward, f.Up),
                        f.M00, f.M11, f.M02, f.M12, f.Width, f.Height);
                    r = Result(owner, a, dp, double.NaN, 0);
                    r.Observed = observed;
                    if (a.Occlusion || observed)
                    {
                        r.HasOcclusion = true;
                        r.Occluded = Hidden(f, f.Eye + dir * 1e13, null, null, true, false);
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
                /* The rig's own test covers bodies, where the picture draws them, and terrain;
                   buildings and other colliders near the lens still need the physics ray. */
                r.Occluded = occlusionKnown
                    ? occluded || Hidden(f, world, ignorePart, ignoreVessel, false, true)
                    : Hidden(f, world, ignorePart, ignoreVessel, true, true);
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

        /*
         * A kinematic anchor's point this frame, propagated as a world model's
         * records are. With a light speed set, a conic or state vector is
         * where the light arriving at the lens now left it, solved in the frame
         * of the lens's and the point's common body as the known subject is;
         * a surface point stays on the body as drawn (AnchorRules.PlacedAtLightInstant).
         * Otherwise at the render UT. Placed as the picture draws a craft:
         * against a body drawn at its past instant, or left out, at its own
         * past place in the solve's frame; otherwise on the body as drawn.
         * Caller-supplied knowledge, so nothing about any craft is read.
         */
        private static bool TryKinematic(AnchorSpec a, DVec3 eye, PicturePass picture, out DVec3 world)
        {
            world = default;
            var body = FlightGlobals.GetBodyByName(a.Body);
            if (body == null) return false;
            var kinematics = KinematicsWorld.Now();
            var kb = kinematics.Of(body);
            if (!kb.Known) return false;
            if (a.Motion != AnchorMotion.Surface)
            {
                if (!a.PathTried)
                {
                    a.PathTried = true;
                    a.PathOk = a.Motion == AnchorMotion.Conic
                        ? KeplerPath.TryFromElements(a.Inclination, a.Eccentricity, a.SemiMajorAxis, a.Lan, a.ArgumentOfPeriapsis,
                            a.MeanAnomalyAtEpoch, a.Epoch, kb.Mu, out a.Path)
                        : KeplerPath.TryFromState(a.Position, a.Velocity, a.Ut, kb.Mu, out a.Path, kb.Radius);
                }
                if (!a.PathOk) return false;
            }
            var zup = kinematics.Zup;
            double ut = Planetarium.GetUniversalTime();
            double at = ut;
            double? c = LightTime.LightSpeed;
            if (c.HasValue && AnchorRules.PlacedAtLightInstant(a.Motion))
            {
                var lensBody = FlightGlobals.getMainBody(new Vector3d(eye.X, eye.Y, eye.Z));
                if (lensBody == null) return false;
                var tree = KeplerBodyTree.World;
                int frame = PastInstant.CommonAncestor(ref tree, lensBody.flightGlobalsIndex, body.flightGlobalsIndex);
                var frameBody = tree.Body(frame);
                if (frameBody == null) return false;
                var motion = BodyMotion.OnPath(body, a.Path, zup);
                var path = new KinematicPath { Spec = a, Body = kb, Zup = zup, Tree = tree, BodyIndex = body.flightGlobalsIndex, Frame = frame };
                if (PastInstant.Solve(ref path, eye - ToD(frameBody.position), ut, c.Value,
                        CommandCameraForecast.SpeedBound(motion, frameBody), double.NegativeInfinity, ut, out at) != PastStatus.Found)
                    return false;
                if (picture.HidesBody(body) || picture.TryDrawnBody(body, out _, out _))
                {
                    if (!path.TryAt(at, out var about)) return false;
                    world = ToD(frameBody.position) + about;
                    return true;
                }
            }
            else if (c.HasValue && picture.HidesBody(body)) return false;
            if (!KinematicAt(a, kb, at, out var rel)) return false;
            world = OnDrawnBody(picture, body, ToD(body.position) + zup.ToWorld(rel));
            return true;
        }

        /* A point fixed to body `b`, carried from where it is now to where this camera draws it: on the body at its past instant, turned back by its spin. */
        private static DVec3 OnDrawnBody(PicturePass picture, CelestialBody b, DVec3 live)
        {
            if (!picture.TryDrawnBody(b, out var centre, out double dt)) return live;
            return CommandCameraMath.CarriedByBody(live - ToD(b.position), centre, CommandCameraOcclusion.Spin(b), dt);
        }

        /* A kinematic anchor's offset from its body's centre at `ut`, in KSP's fixed frame. */
        private static bool KinematicAt(AnchorSpec a, in KinematicsBody kb, double ut, out DVec3 rel)
        {
            if (a.Motion == AnchorMotion.Surface)
            {
                rel = kb.SurfaceAt(a.Latitude, a.Longitude, a.Altitude, ut, out _);
                return true;
            }
            return a.Path.TryAt(ut, out rel, out _);
        }

        /* A kinematic anchor back through time about the frame body, for the light-time solve. */
        private struct KinematicPath : IPastPath
        {
            public AnchorSpec Spec;
            public KinematicsBody Body;
            public ZupFrame Zup;
            public KeplerBodyTree Tree;
            public int BodyIndex;
            public int Frame;

            public bool TryAt(double ut, out DVec3 position)
            {
                position = default;
                if (!KinematicAt(Spec, Body, ut, out var rel)) return false;
                if (!PastInstant.TryAbout(ref Tree, BodyIndex, Frame, ut, out var centre)) return false;
                position = centre + Zup.ToWorld(rel);
                return true;
            }
        }


        /* Where a docking port's face sits on part `partPid`, in the part's frame, from the live part; false for a part with no port or none loaded. */
        private static bool HiddenPartFace(uint partPid, out Vector3 at, out Quaternion turn)
        {
            at = Vector3.zero;
            turn = Quaternion.identity;
            if (!FlightGlobals.FindLoadedPart(partPid, out var part) || part == null || part.transform == null) return false;
            var face = DockingFace(part);
            if (face == null) return false;
            var inv = Quaternion.Inverse(part.transform.rotation);
            at = inv * (face.position - part.transform.position);
            turn = inv * face.rotation;
            return true;
        }

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
        private static bool TryDirection(AnchorSpec a, AnchorRegistry registry, PicturePass picture, out DVec3 dir)
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
            /* Under a light speed an unloaded craft is drawn at its past instant,
               and its directions read here are present truth: withheld. */
            bool delayed = LightTime.LightSpeed.HasValue;
            if (delayed && !v.loaded) return false;
            if (picture.Hides(v)) return false;
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
                    if (delayed && target.GetVessel() != null && !target.GetVessel().loaded) return false;
                    if (picture.Hides(target.GetVessel())) return false;
                    dir = ToD(target.GetTransform().position) - at;
                    break;
                }
                case AnchorDirection.TargetRelativeVelocity:
                {
                    var target = v.targetObject;
                    if (target == null) return false;
                    if (delayed && target.GetVessel() != null && !target.GetVessel().loaded) return false;
                    if (picture.Hides(target.GetVessel())) return false;
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
           `point`. Bodies count at any range, as spheres where the picture
           draws them; colliders only near the lens, where physics has them,
           never counting the thing marked or the camera's own part. */
        private static bool Hidden(Frame f, DVec3 point, Part ignorePart, Vessel ignoreVessel, bool bodies, bool physics)
        {
            if (bodies && DrawnSpheres.Blocked(f.Eye, point, f.BodyCentres, f.BodyRadii, f.BodyCount)) return true;
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
                    /* A craft this camera hides is not where the frame draws it. */
                    if (f.Cam.Picture.Hides(part.vessel)) continue;
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
