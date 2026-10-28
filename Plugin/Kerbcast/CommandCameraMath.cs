using System;
using System.Collections.Generic;

namespace Kerbcast
{
    /* Double-precision 3-vector for the command camera maths. Unity's Vector3 is
       float and KSP's Vector3d lives in Assembly-CSharp, so neither can be used
       by the Unity-free test harness. */
    public readonly struct DVec3
    {
        public readonly double X, Y, Z;

        public DVec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static DVec3 operator +(DVec3 a, DVec3 b) => new DVec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static DVec3 operator -(DVec3 a, DVec3 b) => new DVec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static DVec3 operator *(DVec3 a, double s) => new DVec3(a.X * s, a.Y * s, a.Z * s);

        public double Magnitude => Math.Sqrt(X * X + Y * Y + Z * Z);
        public DVec3 Normalized
        {
            get
            {
                double m = Magnitude;
                return m > 0 ? new DVec3(X / m, Y / m, Z / m) : new DVec3(0, 0, 0);
            }
        }

        public static double Dot(DVec3 a, DVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static DVec3 Cross(DVec3 a, DVec3 b) =>
            new DVec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public override string ToString() => $"({X:F1},{Y:F1},{Z:F1})";
    }

    /* Which local layers the command camera renders this tick, and with
       what clip planes. Produced by CommandCameraMath.PlanClips. */
    public struct CommandClipPlan
    {
        /* Extra local layer covering the terrain between the station and the
           craft, so a hill in front of the craft still hides it. */
        public bool MidActive;
        public float MidNear;
        public float MidFar;

        /* The far-local layer. Raised to start just short of the craft when the
           split is active, so the craft's own depth range gets the precision. */
        public bool FarRender;
        public float FarNear;
        public float FarFar;

        /* The near layer's far plane. Only moves on a stack with no far layer
           (D3D11, where KSP runs one camera for the whole local range). */
        public float NearFar;

        public bool Split;
    }

    /* Where one point lands in a frame; see CommandCameraMath.ProjectCameraSpace. */
    public struct FrameProjection
    {
        /* Pixels from the frame's top left. */
        public double X, Y;
        public bool InFrame, Behind;
        public double AngleOffAxisDeg;
        /* Lens to point in metres; 0 for a direction. */
        public double Distance;
    }

    /* What a craft aim's view follows this solve; see SubjectWait. */
    public enum SubjectAimMode
    {
        /* The subject, where it is known to be. */
        Subject,
        /* The placeholder: the subject has not been positioned since it, the aim or the vantage last changed. */
        Placeholder,
        /* The last aim solved, held: the subject was positioned and is not now. */
        Hold,
    }

    /*
     * Whether a craft aim waits for its subject or holds its last aim. The
     * placeholder stands only until the subject is first positioned; losing
     * it after that (a refusal, a stale record, a light time that will not
     * solve) holds the view where it was, so it never swings back to the
     * placeholder and a flickering fix never wobbles it. A new subject, aim
     * or vantage waits again. A new rig, as on a scene load, starts waiting.
     */
    public sealed class SubjectWait
    {
        private bool _seen;

        public SubjectAimMode Step(bool changed, bool positioned)
        {
            if (changed) _seen = false;
            if (positioned)
            {
                _seen = true;
                return SubjectAimMode.Subject;
            }
            return _seen ? SubjectAimMode.Hold : SubjectAimMode.Placeholder;
        }
    }

    /*
     * How far one frame advances an ease (a slew, a zoom). A frame more than
     * HitchFactor times the usual one is a hitch, as when the first fix of a
     * subject builds its horizon mask and forecast, and advances the ease by
     * a usual frame only; stepped by the whole gap, a short turn would finish
     * inside that one frame and show as a snap. The usual frame follows the
     * frame rate, a lasting slowdown included. A gap over snapAfter (a feed
     * resumed, a scene load) is left to the ease's own snap: it passes
     * through and is not learned.
     */
    public sealed class FrameStep
    {
        public const double HitchFactor = 2.0;
        private const double Follow = 0.1;
        private readonly double _snapAfter;
        private double _usual;

        public FrameStep(double snapAfter) { _snapAfter = snapAfter; }

        /* Learns one frame's gap and returns how far it advances an ease. */
        public double Next(double dt)
        {
            double step = Clip(dt);
            Learn(dt);
            return step;
        }

        /* How far a gap advances an ease, without learning from it. */
        public double Clip(double dt)
        {
            if (!(dt > 0) || dt > _snapAfter || !(_usual > 0)) return dt;
            return dt > HitchFactor * _usual ? _usual : dt;
        }

        public void Learn(double dt)
        {
            if (!(dt > 0) || dt > _snapAfter) return;
            if (!(_usual > 0)) _usual = dt;
            else _usual += Follow * (Math.Min(dt, 2 * HitchFactor * _usual) - _usual);
        }
    }

    /* Where a command camera looks while it has no subject to aim at. */
    public enum PlaceholderAim
    {
        /* It has a subject, or an aim of its own that needs none. */
        None,
        /* At the launch site the lens stands beside, where a craft would sit before liftoff. */
        LaunchSite,
        /* Level toward the horizon, due east. */
        Horizon,
        /* Along the forward axis of the craft the lens rides. */
        MountForward,
    }

    /* A craft's shape for framing, as offsets from its centre of mass, each point padded by a radius of its own. */
    public sealed class CraftExtent
    {
        private readonly List<DVec3> _points = new List<DVec3>();
        private readonly List<double> _pads = new List<double>();

        public int Count => _points.Count;

        public void Clear()
        {
            _points.Clear();
            _pads.Clear();
        }

        public void Add(DVec3 offset, double pad)
        {
            _points.Add(offset);
            _pads.Add(pad);
        }

        /* The corners of an axis-aligned box (a renderer's world bounds) about `centre`. */
        public void AddBox(DVec3 centre, DVec3 extents)
        {
            double x = Math.Abs(extents.X), y = Math.Abs(extents.Y), z = Math.Abs(extents.Z);
            for (int i = 0; i < 8; i++)
                Add(centre + new DVec3((i & 1) != 0 ? x : -x, (i & 2) != 0 ? y : -y, (i & 4) != 0 ? z : -z), 0);
        }

        public void AddFrom(CraftExtent other)
        {
            for (int i = 0; i < other._points.Count; i++) Add(other._points[i], other._pads[i]);
        }

        public DVec3 Point(int i) => _points[i];
        public double Pad(int i) => _pads[i];

        /* The farthest the extent reaches from `from`, an offset from the centre of mass; 0 when empty. */
        public double ReachFrom(DVec3 from)
        {
            double r = 0;
            for (int i = 0; i < _points.Count; i++) r = Math.Max(r, (_points[i] - from).Magnitude + _pads[i]);
            return r;
        }

        /*
         * A sphere holding every padded point, close to the smallest
         * (Badoiu and Clarkson's steps toward the farthest point, from the
         * points' mean). It depends on the shape alone, so a craft rolling
         * or the view turning moves neither its centre nor its size. Never
         * larger than the sphere about the centre of mass, which it falls
         * back to, and never below `minHalf`.
         */
        public void Enclose(double minHalf, out DVec3 centre, out double half)
        {
            centre = default;
            double about = ReachFrom(default);
            if (_points.Count > 0)
            {
                var c = default(DVec3);
                for (int i = 0; i < _points.Count; i++) c = c + _points[i];
                c = c * (1.0 / _points.Count);
                for (int k = 1; k <= EncloseSteps; k++)
                {
                    int far = 0;
                    double farReach = -1;
                    for (int i = 0; i < _points.Count; i++)
                    {
                        double r = (_points[i] - c).Magnitude + _pads[i];
                        if (r > farReach)
                        {
                            farReach = r;
                            far = i;
                        }
                    }
                    var toward = _points[far] - c;
                    double d = toward.Magnitude;
                    if (d < 1e-9) break;
                    c = c + toward * ((farReach / d) / (k + 1));
                }
                if (ReachFrom(c) < about) centre = c;
            }
            half = Math.Max(minHalf, ReachFrom(centre));
        }

        private const int EncloseSteps = 256;
    }

    /* Unity-free maths for the command camera: where the station
       is, where it looks, how wide it zooms, which layers carry the craft and
       whether a body is in the way. Compiled verbatim by CommandCameraMath.Tests. */
    public static class CommandCameraMath
    {
        private const double Deg2Rad = Math.PI / 180.0;
        private const double Rad2Deg = 180.0 / Math.PI;

        /* Depth slack either side of the craft, on top of its bounding diameter. */
        public const double ClipSlackMeters = 50.0;

        /* The split turns on once the stretch between the far layer's native near
           plane and the craft is this many times that near plane, and off again
           below the lower ratio, so a craft hovering near the boundary does not
           flap the layer set every frame. */
        public const double SplitOnRatio = 2.0;
        public const double SplitOffRatio = 1.5;

        /* KSP's CelestialBody.GetWorldSurfacePosition, reproduced: the body-local
           spherical vector (z up) scaled to radius + alt, rotated by the body
           frame's axes (given in KSP's z-up world), swizzled to Unity's y-up
           world and offset by the body centre. bodyCentre is Unity world space;
           frameX/Y/Z are body.BodyFrame.X/Y/Z as KSP stores them. */
        public static DVec3 SurfacePosition(
            DVec3 bodyCentre, DVec3 frameX, DVec3 frameY, DVec3 frameZ,
            double radius, double latDeg, double lonDeg, double alt)
        {
            double lat = latDeg * Deg2Rad, lon = lonDeg * Deg2Rad;
            double cl = Math.Cos(lat);
            double r = radius + alt;
            double sx = cl * Math.Cos(lon) * r, sy = cl * Math.Sin(lon) * r, sz = Math.Sin(lat) * r;
            var zup = frameX * sx + frameY * sy + frameZ * sz;
            return new DVec3(zup.X, zup.Z, zup.Y) + bodyCentre;
        }

        /* The point `distance` metres from lat/lon along the great circle that
           leaves it on `bearingDeg` (0 = north, 90 = east), on a sphere of
           `radius`. Used to stand the lens off from a launch site. */
        public static void Destination(double latDeg, double lonDeg, double bearingDeg, double distance,
            double radius, out double outLatDeg, out double outLonDeg)
        {
            double lat = latDeg * Deg2Rad, lon = lonDeg * Deg2Rad, brg = bearingDeg * Deg2Rad;
            double d = distance / radius;
            double lat2 = Math.Asin(Math.Sin(lat) * Math.Cos(d) + Math.Cos(lat) * Math.Sin(d) * Math.Cos(brg));
            double lon2 = lon + Math.Atan2(Math.Sin(brg) * Math.Sin(d) * Math.Cos(lat),
                Math.Cos(d) - Math.Sin(lat) * Math.Sin(lat2));
            outLatDeg = lat2 * Rad2Deg;
            outLonDeg = (lon2 * Rad2Deg + 540.0) % 360.0 - 180.0;
        }

        /* Forward and up for a camera at `from` looking at `to`, with up as close
           to `upHint` (the station's local vertical) as the view allows, so the
           horizon stays level. When the craft is straight overhead the hint is
           parallel to forward and `fallbackUp` (body north) takes over. */
        public static void AimBasis(DVec3 from, DVec3 to, DVec3 upHint, DVec3 fallbackUp,
            out DVec3 forward, out DVec3 up)
        {
            forward = (to - from).Normalized;
            up = Reject(upHint, forward);
            if (up.Magnitude < 1e-6) up = Reject(fallbackUp, forward);
            if (up.Magnitude < 1e-6) up = Reject(new DVec3(0, 1, 0), forward);
            up = up.Normalized;
        }

        private static DVec3 Reject(DVec3 v, DVec3 unitAxis) => v - unitAxis * DVec3.Dot(v, unitAxis);

        /* Elevation of the look direction above the station's horizon, degrees. */
        public static double ElevationDeg(DVec3 forward, DVec3 localUp)
        {
            double s = DVec3.Dot(forward.Normalized, localUp.Normalized);
            if (s > 1) s = 1;
            if (s < -1) s = -1;
            return Math.Asin(s) * Rad2Deg;
        }

        /* Full angle a sphere of `radius` subtends at `distance`, degrees. */
        public static double AngularDiameterDeg(double radius, double distance)
        {
            if (radius <= 0) return 0;
            if (distance <= radius) return 180.0;
            return 2.0 * Math.Asin(radius / distance) * Rad2Deg;
        }

        /* Vertical field of view that makes the craft span `fraction` of the
           frame height, clamped to [fovMin, fovMax]. Unity's fieldOfView is the
           vertical angle. Uses tan so the fraction is exact on screen rather
           than in angle (they differ at wide fields). */
        public static double FovForFraction(double radius, double distance, double fraction,
            double fovMin, double fovMax)
        {
            if (fraction <= 0 || distance <= radius) return fovMax;
            double half = AngularDiameterDeg(radius, distance) * 0.5 * Deg2Rad;
            double fov = 2.0 * Math.Atan(Math.Tan(half) / fraction) * Rad2Deg;
            if (fov < fovMin) return fovMin;
            if (fov > fovMax) return fovMax;
            return fov;
        }

        /* Exponential approach to `target` in log space, so a zoom from 20° to
           0.02° takes the same time per factor of ten instead of crawling at the
           narrow end. A gap longer than `snapAfter` (resubscribe, scene load)
           snaps rather than replaying a stale ease. */
        public static double SmoothFov(double current, double target, double dt, double tau, double snapAfter)
        {
            if (current <= 0 || target <= 0 || dt <= 0 || dt > snapAfter || tau <= 0) return target;
            double alpha = 1.0 - Math.Exp(-dt / tau);
            return Math.Exp(Math.Log(current) + alpha * (Math.Log(target) - Math.Log(current)));
        }

        /* True when no part of the segment a-b passes inside the sphere: a body
           blocks a sight line, nothing else does. */
        public static bool SegmentClearOfSphere(DVec3 a, DVec3 b, DVec3 centre, double radius)
        {
            if (radius <= 0) return true;
            var ab = b - a;
            double len2 = DVec3.Dot(ab, ab);
            double t = len2 > 0 ? DVec3.Dot(centre - a, ab) / len2 : 0;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            var closest = a + ab * t;
            return (centre - closest).Magnitude >= radius;
        }

        /*
         * True when the segment a-b clears the body's terrain, not just its
         * sea-level sphere. Only the stretch of the segment inside the
         * `radiusMax` shell (the highest peak) can be blocked by terrain, so
         * that stretch is sampled and each point's distance from the centre is
         * compared with `surfaceRadiusAt` (the terrain radius under that point).
         * A grazing line of sight past a limb with mountains is the case the
         * sphere test misses.
         */
        public static bool SegmentClearOfTerrain(DVec3 a, DVec3 b, DVec3 centre, double radius, double radiusMax,
            Func<DVec3, double> surfaceRadiusAt, int samples)
        {
            if (!SegmentClearOfSphere(a, b, centre, radius)) return false;
            if (surfaceRadiusAt == null || samples < 1 || radiusMax <= radius) return true;
            if (!SegmentInsideSphere(a, b, centre, radiusMax, out double t0, out double t1)) return true;

            var ab = b - a;
            for (int i = 0; i <= samples; i++)
            {
                var p = a + ab * (t0 + (t1 - t0) * i / samples);
                if ((p - centre).Magnitude < surfaceRadiusAt(p)) return false;
            }
            return true;
        }

        /* Parameter range [t0, t1] (clamped to the segment) where a-b lies inside
           the sphere; false when it never enters. */
        public static bool SegmentInsideSphere(DVec3 a, DVec3 b, DVec3 centre, double radius,
            out double t0, out double t1)
        {
            t0 = t1 = 0;
            var ab = b - a;
            var ac = a - centre;
            double qa = DVec3.Dot(ab, ab);
            double qb = 2 * DVec3.Dot(ab, ac);
            double qc = DVec3.Dot(ac, ac) - radius * radius;
            if (qa <= 0) return qc < 0;
            double disc = qb * qb - 4 * qa * qc;
            if (disc <= 0) return false;
            double s = Math.Sqrt(disc);
            double lo = (-qb - s) / (2 * qa), hi = (-qb + s) / (2 * qa);
            if (hi < 0 || lo > 1) return false;
            t0 = Math.Max(lo, 0);
            t1 = Math.Min(hi, 1);
            return true;
        }

        /* Bounding radius of a craft about its centre of mass: the farthest part
           origin plus a pad for the part's own size, never below `min`. */
        public static double BoundingRadius(IList<DVec3> partPositions, DVec3 centre, double pad, double min)
        {
            double r = 0;
            if (partPositions != null)
            {
                for (int i = 0; i < partPositions.Count; i++)
                {
                    double d = (partPositions[i] - centre).Magnitude;
                    if (d > r) r = d;
                }
            }
            r += pad;
            return r < min ? min : r;
        }

        /* A parachute's deploy state as stock's ModuleParachute.deploymentStates names it. */
        public enum ChuteState { Stowed, Active, SemiDeployed, Deployed, Cut }

        /* A canopy is out while its chute is semi or fully deployed; armed, packed and cut chutes show none. */
        public static bool CanopyOpen(ChuteState state) => state == ChuteState.SemiDeployed || state == ChuteState.Deployed;

        /* Still unfolding: the deploy animation has not finished and moved since the last look. Stock holds it short of 1 in vacuum and while paused. */
        public static bool CanopyUnfolding(float animTime, float lastAnimTime) => animTime < 1f && animTime != lastAnimTime;

        /*
         * Whether a craft's cached chutes must be found again. A reload keeps
         * the craft's id and part count but makes every part, module and
         * renderer anew, so a new root part or a destroyed cached object
         * means the cache is stale. So does a chute that just opened, since
         * a kerbal's chute model can be fitted after the cache was built.
         */
        public static bool ChutesStale(int cachedParts, int parts, bool sameRoot, bool anyGone, bool opened) =>
            cachedParts != parts || !sameRoot || anyGone || opened;

        /* How far an axis-aligned box (a renderer's world bounds) reaches from `from`: the distance to its farthest corner. */
        public static double BoxReach(DVec3 centre, DVec3 extents, DVec3 from)
        {
            double x = Math.Abs(centre.X - from.X) + Math.Abs(extents.X);
            double y = Math.Abs(centre.Y - from.Y) + Math.Abs(extents.Y);
            double z = Math.Abs(centre.Z - from.Z) + Math.Abs(extents.Z);
            return Math.Sqrt(x * x + y * y + z * z);
        }

        /* Exponential approach of a vector to `target`, at the pace SmoothFov eases with; a gap longer than `snapAfter` snaps. */
        public static DVec3 EaseToward(DVec3 current, DVec3 target, double dt, double tau, double snapAfter)
        {
            if (dt <= 0 || dt > snapAfter || tau <= 0) return target;
            double alpha = 1.0 - Math.Exp(-dt / tau);
            return current + (target - current) * alpha;
        }

        /* The boundary light time τ, one 60 fps frame: craft nearer to the lens than its light covers in τ are drawn at render UT. */
        public const double BoundaryLightTimeS = 1.0 / 60.0;

        /*
         * A frame's live bound: the light time covering what of the lens's
         * own craft and site it draws at render UT. That is the larger of τ
         * and the light time from the lens to the farthest reach of its own
         * craft (`farthestOwnPartM`, 0 with none). 0 with no light speed,
         * when nothing is delayed. Other loaded craft are still drawn at
         * render UT and are not covered until those beyond the boundary are
         * hidden.
         */
        public static double LiveLightTimeS(double? lightSpeed, double farthestOwnPartM)
        {
            if (!lightSpeed.HasValue || !(lightSpeed.Value > 0)) return 0;
            double own = farthestOwnPartM > 0 && !double.IsInfinity(farthestOwnPartM) ? farthestOwnPartM / lightSpeed.Value : 0;
            return Math.Max(BoundaryLightTimeS, own);
        }

        /* How far light travels in τ, the boundary distance; NaN with no light speed. */
        public static double BoundaryM(double? lightSpeed) =>
            lightSpeed.HasValue && lightSpeed.Value > 0 ? lightSpeed.Value * BoundaryLightTimeS : double.NaN;

        /* The farthest reach of a craft from `lens`: each part's origin distance plus that part's own radius. 0 for no parts. */
        public static double FarthestPartM(DVec3 lens, IList<DVec3> origins, IList<double> radii)
        {
            double far = 0;
            if (origins == null) return far;
            for (int i = 0; i < origins.Count; i++)
            {
                double r = radii != null && i < radii.Count && radii[i] > 0 ? radii[i] : 0;
                double d = (origins[i] - lens).Magnitude + r;
                if (d > far) far = d;
            }
            return far;
        }

        /* Float epsilon: Unity world positions are single precision. */
        public const double FloatEpsilon = 1.1920929e-7;

        /* How far along the true line of sight to stand an impostor: `preferred`,
           but never beyond the target itself, and never so close that an object
           `sizeAtUnit` metres across per metre of distance spans fewer than
           `minUlps` float steps at `worldExtent` (the largest coordinate in
           play). Precision wins over `range` only when it has to. */
        public static double ImpostorDistance(double range, double preferred, double sizeAtUnit,
            double worldExtent, double minUlps)
        {
            double floor = sizeAtUnit > 0 ? minUlps * FloatEpsilon * worldExtent / sizeAtUnit : 0;
            double d = Math.Max(preferred, floor);
            return range < d ? range : d;
        }

        /* Metres per pixel per metre of distance for a vertical `fovDeg` over
           `pixels` rows. */
        public static double PixelSizeAtUnit(double fovDeg, int pixels) =>
            2.0 * Math.Tan(fovDeg * 0.5 * Deg2Rad) / Math.Max(1, pixels);

        /* Half-extent on screen, in pixels of a `rows`-high frame at vertical
           `fovDeg`, of a sphere of `radius` at `distance` centred in the view.
           Capped at the frame height so a lens inside the sphere stays finite. */
        public static double BracketHalfPx(double radius, double distance, double fovDeg, int rows)
        {
            int h = Math.Max(1, rows);
            if (radius <= 0) return 0;
            if (distance <= radius) return h;
            double halfTan = radius / Math.Sqrt(distance * distance - radius * radius);
            double perPx = PixelSizeAtUnit(fovDeg, h);
            return perPx > 0 ? Math.Min(halfTan / perPx, h) : h;
        }

        /* Under this many pixels across a subject is a point of light; wider, a model, as the picture's stand-ins draw it. */
        public const double PointBelowPx = 6.0;
        /* A subject drawn as a model goes back to the point of light only under this, so one near the line does not flip. */
        public const double ModelLeaveBelowPx = 4.0;

        /*
         * The radius a rig judges its unloaded subject by: the model built
         * for it, else the size the history records for the parts it shows,
         * else `unknownM`, generous, so a craft of unknown size is tried as a
         * model (and then judged by that model) rather than kept behind a dot.
         */
        public static double SubjectSizeM(double modelRadiusM, double partsSizeM, double unknownM) =>
            modelRadiusM > 0 ? modelRadiusM : partsSizeM > 0 ? partsSizeM : unknownM;

        /*
         * Whether a rig draws its unloaded subject as a model: always with the
         * model setting, and with the marker setting once the subject spans
         * PointBelowPx or more, until it drops under ModelLeaveBelowPx
         * (`wasModel` says which it was last frame).
         */
        public static bool SubjectAsModel(bool modelSetting, bool marker, bool wasModel, double radius, double distance, double fovDeg,
            int rows)
        {
            if (modelSetting) return true;
            if (!marker) return false;
            double across = 2.0 * BracketHalfPx(radius, distance, fovDeg, rows);
            return across >= (wasModel ? ModelLeaveBelowPx : PointBelowPx);
        }

        /*
         * Whether a rig shows the model it decided on this frame: only one
         * built for the subject it draws (`modelMatches`), and, under the
         * marker setting, not on the frame a model was built only to learn a
         * size the rig guessed (`sizeGuessed`): the marker stays that frame
         * and the model's own radius decides from the next.
         */
        public static bool ShowsModel(bool decided, bool modelMatches, bool modelSetting, bool sizeGuessed) =>
            decided && modelMatches && (modelSetting || !sizeGuessed);

        /* Whether the whole sphere fits inside the frame height at the widest
           field the camera allows. */
        public static bool FramingFits(double radius, double distance, double fovMaxDeg)
        {
            if (radius <= 0) return true;
            if (distance <= radius) return false;
            return Math.Asin(radius / distance) <= fovMaxDeg * 0.5 * Deg2Rad;
        }

        /* What a silent lock holder's feeds show when it gave no words of its own. */
        public const string DefaultSilentText = "Authority not responding";

        [System.Obsolete("A silent holder's feeds show its own silence text, or DefaultSilentText")]
        public const string BlankNotResponding = DefaultSilentText;

        public const string BlankVantageGone = "vantage craft gone";

        /* Why a command feed is blanked, or null to show it. A lens whose
           vantage craft no longer exists has no position, so that blanks with
           or without a lock. Otherwise only a held lock blanks: a silent holder
           beats everything, showing its silence text, then the holder's own
           blank. */
        public static string BlankReason(bool governed, bool timedOut, string explicitBlank, bool vantageGone = false) =>
            BlankReason(governed, timedOut, explicitBlank, vantageGone, null);

        /* As above, showing `silentText` (null for DefaultSilentText) for a silent holder. */
        public static string BlankReason(bool governed, bool timedOut, string explicitBlank, bool vantageGone,
            string silentText)
        {
            if (vantageGone) return BlankVantageGone;
            if (!governed) return null;
            if (timedOut) return string.IsNullOrEmpty(silentText) ? DefaultSilentText : silentText;
            if (!string.IsNullOrEmpty(explicitBlank)) return explicitBlank;
            return null;
        }

        /*
         * Unit vector `azimuthDeg` clockwise from north and `elevationDeg` above
         * the horizon, at a point whose up is `up`. `north` and `east` only need
         * to point roughly that way along the ground; they are squared up to
         * `up` here.
         */
        public static DVec3 DirectionVector(DVec3 up, DVec3 north, DVec3 east, double azimuthDeg, double elevationDeg)
        {
            var u = up.Normalized;
            var n = (north - u * DVec3.Dot(north, u)).Normalized;
            var e = east - u * DVec3.Dot(east, u);
            e = (e - n * DVec3.Dot(e, n)).Normalized;
            double az = azimuthDeg * Math.PI / 180.0, el = elevationDeg * Math.PI / 180.0;
            return (n * (Math.Cos(el) * Math.Cos(az)) + e * (Math.Cos(el) * Math.Sin(az)) + u * Math.Sin(el)).Normalized;
        }

        /* Tightest zoom (vertical field of view, degrees) of the Tracking
           Station lens at normalised level `normLevel` (0 the lowest level, 1
           the top). Interpolates in log space from `lowest` to `middle` to
           `top`, so stock's three levels land exactly on them and modded level
           counts fall between. Never tighter than `top`. */
        public static double LensFovMin(double normLevel, double lowest, double middle, double top)
        {
            if (double.IsNaN(normLevel)) normLevel = 0;
            double t = Math.Max(0.0, Math.Min(1.0, normLevel));
            middle = Math.Max(middle, top);
            lowest = Math.Max(lowest, middle);
            double from = t <= 0.5 ? lowest : middle;
            double to = t <= 0.5 ? middle : top;
            double f = t <= 0.5 ? t * 2.0 : (t - 0.5) * 2.0;
            return from * Math.Pow(to / from, f);
        }

        /* The craft to follow after KSP merges craft `oldPid` into `newPid` (a
           dock or a claw grab): the survivor when `watched` was absorbed, else
           `watched` unchanged. KSP announces each merge twice, so a repeat is a
           no-op. */
        public static uint MergedPersistentId(uint watched, uint oldPid, uint newPid) =>
            watched != 0 && watched == oldPid && newPid != 0 ? newPid : watched;

        /*
         * Whether a craft's saved record still stands for a real craft. Where
         * live Vessel objects are authoritative (flight, once they are built),
         * a record never adds a craft: the save's list is a snapshot from the
         * last save, so it still holds craft since docked into another, crashed
         * or recovered. Elsewhere a record counts unless the Vessel built from
         * it has been destroyed.
         */
        public static bool SavedRecordCounts(bool liveVesselsAuthoritative, bool vesselBuilt, bool vesselDestroyed) =>
            !liveVesselsAuthoritative && !(vesselBuilt && vesselDestroyed);

        public const string EligibleOk = "ok";
        public const string EligibleTooFar = "tooFar";
        public const string EligibleLevelTooLow = "levelTooLow";
        public const string EligibleNotTrackable = "notTrackable";
        internal const string EligibleNotPositioned = "notPositioned";
        internal const string EligibleRefused = "refused";
        internal const string EligibleStale = "stale";
        internal const string EligibleTooUncertain = "tooUncertain";
        /* The radius a craft is framed by when its source gives no size. */
        internal const double NominalCraftRadiusM = 2.0;

        /*
         * A launch site placeholder frames this much around the point, filling
         * PlaceholderSiteFraction of the frame whatever the craft framing is
         * set to: about 9 degrees from the default 500 m vantage, so the pad
         * and its tower show. The radius is fixed, so a farther vantage frames
         * the site tighter.
         */
        public const double PlaceholderSiteRadiusM = 20.0;
        public const double PlaceholderSiteFraction = 0.5;
        /* How far above a launch site's ground the placeholder looks: about where a craft's centre sits on it. */
        public const double PlaceholderSiteHeightM = 10.0;
        public const double PlaceholderAzimuthDeg = 90.0;

        /* The placeholder a waiting camera looks at: along the craft a lens rides, at the launch site a lens stands beside, else level toward the horizon. */
        public static PlaceholderAim PlaceholderFor(bool ridesCraft, bool besideLaunchSite)
        {
            if (ridesCraft) return PlaceholderAim.MountForward;
            return besideLaunchSite ? PlaceholderAim.LaunchSite : PlaceholderAim.Horizon;
        }
        /* The refusal code of a record outside its window, or of any record while the snapshot is invalid. */
        internal const string RefusalStale = "stale";

        /* The 1-based Tracking Station level from KSP's normalised level and its
           highest level index (stock 2, three levels). A highest index below 1
           (facility not registered in this scene) reads as stock's. */
        public static int TrackingLevel(double normLevel, int maxLevelIndex)
        {
            if (maxLevelIndex < 1) maxLevelIndex = 2;
            if (double.IsNaN(normLevel)) normLevel = 0;
            int idx = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, normLevel)) * maxLevelIndex);
            return idx + 1;
        }

        /* Whether the network could watch a craft. When `gated` (career with the
           tracking rules on), an unowned object at a level that tracks no unowned
           objects (`trackedObjectLimit` zero) is levelTooLow, whether or not it is
           tracked, since tracking it is what the upgrade unlocks. Otherwise an
           untracked unowned object (`trackable` false) is never watchable, and a
           gated craft must lie within `maxRangeM`. A NaN range (no position)
           passes the range test. */
        public static string Eligibility(bool gated, bool trackable, bool unowned, int trackedObjectLimit,
            double rangeM, double maxRangeM)
        {
            if (gated && unowned && trackedObjectLimit <= 0) return EligibleLevelTooLow;
            if (!trackable) return EligibleNotTrackable;
            if (!gated) return EligibleOk;
            if (rangeM > maxRangeM) return EligibleTooFar;
            return EligibleOk;
        }

        /*
         * A targets-list row's code from what the fix source holds, first match
         * wins: no row for an unknown craft (null), stale or refused for a
         * refused record, notPositioned for a listed one (it has no range for
         * the career rules), and `careerCode` (from Eligibility) for a
         * positioned one, then tooUncertain when the career rules pass. With
         * `listedTakesCareerRules` a listed craft takes the career rules with
         * no range instead, as 1.11 listed a craft KSP could not place.
         */
        internal static string TargetEligibility(CraftFixState state, string refusalCode, bool listedTakesCareerRules,
            string careerCode, bool tooUncertain = false)
        {
            switch (state)
            {
                case CraftFixState.Refused:
                    return refusalCode == RefusalStale ? EligibleStale : EligibleRefused;
                case CraftFixState.Listed:
                    return listedTakesCareerRules ? careerCode : EligibleNotPositioned;
                case CraftFixState.Positioned:
                    return careerCode == EligibleOk && tooUncertain ? EligibleTooUncertain : careerCode;
                default:
                    return null;
            }
        }

        public static string EligibilityText(string code)
        {
            switch (code)
            {
                case EligibleOk: return "Trackable";
                case EligibleTooFar: return "Out of tracking range";
                case EligibleLevelTooLow: return "Upgrade the Tracking Station";
                case EligibleNotTrackable: return "Not tracked";
                case EligibleNotPositioned: return "Position not known";
                case EligibleRefused: return "Refused";
                case EligibleStale: return "Out of date";
                case EligibleTooUncertain: return "Position too uncertain";
                default: return "";
            }
        }

        /* `v` to `digits` significant figures, so a listed range only changes
           when it moves visibly. NaN and infinities pass through. */
        public static double RoundSignificant(double v, int digits)
        {
            if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return v;
            double scale = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(v))) + 1 - digits);
            return Math.Round(v / scale) * scale;
        }

        /* `v` turned by `angle` radians about `unitAxis` (Rodrigues), in the
           same sense as Cross: the rate of turn of a point is Cross(axis, v).
           KSP's getRFrmVel gives a surface point's velocity as
           Cross(angularVelocity, r), so a body's spin vector turns its
           surface points with this directly. */
        public static DVec3 RotateAbout(DVec3 v, DVec3 unitAxis, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return v * c + DVec3.Cross(unitAxis, v) * s + unitAxis * (DVec3.Dot(unitAxis, v) * (1 - c));
        }

        /* A point fixed to a spinning body: `relNow` (its offset from the body
           centre now) carried round by `spin` (rad/s, world axes) for `dt`
           seconds, placed on the body's centre at that time. */
        public static DVec3 CarriedByBody(DVec3 relNow, DVec3 centreAtT, DVec3 spin, double dt)
        {
            double w = spin.Magnitude;
            if (w <= 0 || dt == 0) return centreAtT + relNow;
            return centreAtT + RotateAbout(relNow, spin * (1.0 / w), w * dt);
        }

        /* The inverse of CarriedByBody: where a world point at time now + dt
           sits on the body as it stands now, so latitude and longitude (and
           so terrain) can be read with the body's current frame. */
        public static DVec3 ToBodyFrameNow(DVec3 worldAtT, DVec3 centreAtT, DVec3 centreNow, DVec3 spin, double dt)
        {
            var rel = worldAtT - centreAtT;
            double w = spin.Magnitude;
            if (w <= 0 || dt == 0) return centreNow + rel;
            return centreNow + RotateAbout(rel, spin * (1.0 / w), -w * dt);
        }

        /* A world point at time now + dt in a body's own axes: its offset from
           the body centre at that time, turned back by the spin to the body
           as it stands now, then read along the body's current axes (unit
           vectors in world space). A point fixed to the body reads the same
           at every dt. */
        public static DVec3 ToBodyLocal(DVec3 worldAtT, DVec3 centreAtT, DVec3 spin, double dt,
            DVec3 axisX, DVec3 axisY, DVec3 axisZ)
        {
            var rel = worldAtT - centreAtT;
            double w = spin.Magnitude;
            if (w > 0 && dt != 0) rel = RotateAbout(rel, spin * (1.0 / w), -w * dt);
            return new DVec3(DVec3.Dot(rel, axisX), DVec3.Dot(rel, axisY), DVec3.Dot(rel, axisZ));
        }

        /* Least distance from `p` to the segment a-b. */
        public static double DistanceToSegment(DVec3 p, DVec3 a, DVec3 b)
        {
            var ab = b - a;
            double len2 = DVec3.Dot(ab, ab);
            double t = len2 > 0 ? DVec3.Dot(p - a, ab) / len2 : 0;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return (p - (a + ab * t)).Magnitude;
        }

        /*
         * Whether a sphere of `radius` can come within reach of the segment
         * a-b over the next `span` seconds. The distance from a point to a
         * segment changes no faster than the point's speed plus the faster
         * end's, so with `rate` that sum and d0, d1 the distances at the
         * start and end of the span, the distance never falls below
         * max(d0 - rate * span, (d0 + d1 - rate * span) / 2).
         */
        public static bool MayReach(double d0, double d1, double rate, double span, double radius)
        {
            double floor = Math.Max(d0 - rate * span, 0.5 * (d0 + d1 - rate * span));
            return floor <= radius;
        }

        /* Angle between two directions, radians, stable near 0 and pi. */
        public static double AngleBetween(DVec3 a, DVec3 b) =>
            Math.Atan2(DVec3.Cross(a, b).Magnitude, DVec3.Dot(a, b));

        /* An angle as a count of pixels of a `rows`-high frame at vertical
           `fovDeg`, at the frame's angular pixel pitch (fov / rows). */
        public static double AngleInPixels(double angleRad, double fovDeg, int rows)
        {
            if (fovDeg <= 0) return 0;
            return angleRad * Rad2Deg / (fovDeg / Math.Max(1, rows));
        }

        /* A world direction in a lens's own axes: x right, y up, z forward.
           `forward` and `up` are the lens's unit axes in world space. */
        public static DVec3 ToCameraSpace(DVec3 v, DVec3 forward, DVec3 up)
        {
            var right = DVec3.Cross(up, forward);
            return new DVec3(DVec3.Dot(v, right), DVec3.Dot(v, up), DVec3.Dot(v, forward));
        }

        /* The m00 and m11 terms of a symmetric perspective projection with
           vertical `fovDeg` over a frame `aspect` wide per unit high, for a
           lens with no projection matrix to read. */
        public static void PerspectiveTerms(double fovDeg, double aspect, out double m00, out double m11)
        {
            m11 = 1.0 / Math.Tan(Math.Max(1e-6, fovDeg) * 0.5 * Deg2Rad);
            m00 = m11 / Math.Max(1e-6, aspect);
        }

        /*
         * Where a camera-space point (x right, y up, z forward) lands in a
         * `width` by `height` frame, pixels from the top left, through the
         * m00, m11, m02 and m12 terms of the lens's projection matrix (Unity's
         * layout, so an off-centre lens is honoured). A point behind the lens
         * is placed off the frame on the side it is nearest to turning
         * towards, at the frame's diagonal from the centre.
         */
        public static FrameProjection ProjectCameraSpace(DVec3 c, double m00, double m11, double m02, double m12,
            int width, int height)
        {
            double w = Math.Max(1, width), h = Math.Max(1, height);
            var p = new FrameProjection
            {
                AngleOffAxisDeg = AngleBetween(new DVec3(0, 0, 1), c) * Rad2Deg,
            };
            const double MinDepth = 1e-9;
            if (c.Z > MinDepth * Math.Max(1.0, Math.Abs(c.X) + Math.Abs(c.Y)))
            {
                double ndcX = m00 * c.X / c.Z - m02;
                double ndcY = m11 * c.Y / c.Z - m12;
                p.X = (ndcX + 1.0) * 0.5 * w;
                p.Y = (1.0 - ndcY) * 0.5 * h;
                p.InFrame = p.X >= 0 && p.X <= w && p.Y >= 0 && p.Y <= h;
                return p;
            }
            p.Behind = true;
            double dx = c.X, dy = -c.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            /* Dead behind the lens the side is only rounding noise, so it is
               pinned below the frame. */
            if (len < 1e-6 * Math.Max(1e-12, c.Magnitude)) { dx = 0; dy = 1; len = 1; }
            double reach = Math.Sqrt(w * w + h * h);
            p.X = w * 0.5 + dx / len * reach;
            p.Y = h * 0.5 + dy / len * reach;
            return p;
        }

        /* `point` seen from a lens at `eye` with unit axes `forward` and `up`,
           with the lens-to-point distance filled in. */
        public static FrameProjection Project(DVec3 eye, DVec3 forward, DVec3 up, double m00, double m11, double m02,
            double m12, int width, int height, DVec3 point)
        {
            var d = point - eye;
            var p = ProjectCameraSpace(ToCameraSpace(d, forward, up), m00, m11, m02, m12, width, height);
            p.Distance = d.Magnitude;
            return p;
        }

        /* Screen radius, in pixels of a `rows`-high frame at vertical `fovDeg`,
           of a sphere of `radius` at `distance`; 0 for no radius. The bracket
           half-size, which the command camera's subject marker is drawn at. */
        public static double ApparentRadiusPx(double radius, double distance, double fovDeg, int rows) =>
            radius > 0 && distance > 0 ? BracketHalfPx(radius, distance, fovDeg, rows) : 0;

        /*
         * The changes of a discrete state over [from, to]. The span is sampled
         * at `samples` even steps; each change between two samples is narrowed
         * by bisection for at most `maxSteps` halvings or until the bracket is
         * under `tolerance`. Each edge is reported at the earliest time known
         * to show the new state, with that state. A state that comes and goes
         * between two samples is missed, and a change through a third state
         * inside one bracket is reported as two edges, the second at the
         * sample.
         */
        public static void FindEdges<T>(double from, double to, int samples, int maxSteps, double tolerance,
            Func<double, T> stateAt, Func<T, T, bool> same, List<KeyValuePair<double, T>> edges)
        {
            edges.Clear();
            if (samples < 1 || !(to > from)) return;
            double step = (to - from) / samples;
            T prev = stateAt(from);
            double prevT = from;
            for (int i = 1; i <= samples; i++)
            {
                double t = i == samples ? to : from + step * i;
                T s = stateAt(t);
                if (!same(prev, s))
                {
                    double lo = prevT, hi = t;
                    T atHi = s;
                    for (int k = 0; k < maxSteps && hi - lo > tolerance; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        T m = stateAt(mid);
                        if (same(m, prev)) lo = mid;
                        else
                        {
                            hi = mid;
                            atHi = m;
                        }
                    }
                    edges.Add(new KeyValuePair<double, T>(hi, atHi));
                    if (!same(atHi, s)) edges.Add(new KeyValuePair<double, T>(t, s));
                }
                prev = s;
                prevT = t;
            }
        }

        /* Bits in the debug UT stripe's payload (see CommandCameraUtStripe). */
        public const int UtStripeBits = 48;

        /* The stripe's payload: UT in whole milliseconds, clamped to what 48
           bits hold. */
        public static ulong UtStripeValue(double ut)
        {
            if (!(ut > 0)) return 0;
            double ms = Math.Round(ut * 1000.0);
            const double max = (double)((1UL << UtStripeBits) - 1);
            return ms >= max ? (1UL << UtStripeBits) - 1 : (ulong)ms;
        }

        /* Block `i` of the payload, most significant bit first. */
        public static bool UtStripeBit(ulong value, int i) =>
            ((value >> (UtStripeBits - 1 - i)) & 1UL) != 0;

        /* Depth range the craft occupies, centred on its CoM. */
        public static double ClipSpan(double craftRadius) => 2.0 * craftRadius + ClipSlackMeters;

        /* Clip planes for one tick.

           KSP on OpenGL/Metal splits the local scene between Camera 00 (near,
           0.21 m to about 400 m) and Camera 01 (about 400 m to 750 km), each
           with its own depth buffer, drawn far first. The station is that far
           from the craft for most of a flight, so the craft lands in the far
           layer, whose 24-bit depth spread over 400 m..750 km is too coarse to
           sort a craft's parts once it is tens of km out.

           So once the craft is well past the far layer's near plane, the far
           layer is pulled in to start just short of the craft, and a mid layer
           (a clone of the far one) covers the stretch from the native near plane
           up to that point. Drawn far, mid, near, each clearing depth, the mid
           layer paints anything between the station and the craft over it,
           exactly as KSP's own split does. The far plane extends past 750 km
           when the craft is farther than that.

           When a body hides the craft the far layer is skipped and the mid layer
           stops short of the craft, so terrain still draws and the craft does
           not show through the planet (a local layer always draws over scaled
           space, which would otherwise leak it).

           With no far layer (D3D11: KSP disables Camera 01 and runs Camera 00
           over the whole range with reversed-Z) only the near far plane moves. */
        public static CommandClipPlan PlanClips(
            double distance, double craftRadius, bool occluded, bool hasFarLayer, bool wasSplit,
            float nearNativeNear, float nearNativeFar, float farNativeNear, float farNativeFar)
        {
            var plan = new CommandClipPlan();
            double span = ClipSpan(craftRadius);
            double front = distance - span;
            double back = distance + span;

            if (!hasFarLayer)
            {
                plan.NearFar = occluded
                    ? (float)Math.Max(nearNativeNear * 2.0, front)
                    : (float)Math.Max(nearNativeFar, back);
                return plan;
            }

            plan.NearFar = nearNativeFar;
            double ratio = wasSplit ? SplitOffRatio : SplitOnRatio;
            bool split = front > farNativeNear * ratio;
            plan.Split = split;

            if (occluded)
            {
                plan.FarRender = false;
                plan.MidActive = front > farNativeNear;
                plan.MidNear = farNativeNear;
                plan.MidFar = (float)Math.Min(front, farNativeFar);
                if (front < nearNativeFar)
                    plan.NearFar = (float)Math.Max(nearNativeNear * 2.0, front);
                return plan;
            }

            plan.FarRender = true;
            plan.FarFar = (float)Math.Max(farNativeFar, back);
            if (split)
            {
                plan.MidActive = true;
                plan.MidNear = farNativeNear;
                plan.MidFar = (float)front;
                plan.FarNear = (float)front;
            }
            else
            {
                plan.FarNear = farNativeNear;
            }
            return plan;
        }
    }

    /* The discrete state of a command feed at one UT. Occluder is empty when nothing blocks. Predicted marks a
       forecast camera-axis edge rather than one a solve saw. */
    public readonly struct CommandTrackEvent
    {
        public readonly double Ut;
        public readonly bool Occluded;
        public readonly string Occluder;
        public readonly bool Sunlit;
        public readonly string Blank;
        public readonly bool Predicted;

        public CommandTrackEvent(double ut, bool occluded, string occluder, bool sunlit, string blank,
            bool predicted = false)
        {
            Ut = ut;
            Occluded = occluded;
            Occluder = occluder ?? "";
            Sunlit = sunlit;
            Blank = string.IsNullOrEmpty(blank) ? null : blank;
            Predicted = predicted;
        }

        public bool SameStateAs(CommandTrackEvent o) =>
            Occluded == o.Occluded && Occluder == o.Occluder && Sunlit == o.Sunlit
            && Blank == o.Blank;

        public bool SameCameraAxisAs(CommandTrackEvent o) =>
            Occluded == o.Occluded && Occluder == o.Occluder && Sunlit == o.Sunlit;

        /* Forecast camera-axis edges appended after the recorded history in
           `into`: only those later than `recordedUt`, each flagged predicted
           and carrying `blank`, and none that would repeat the
           state before it. */
        public static void AppendPredicted(List<CommandTrackEvent> into, List<KeyValuePair<double, CommandTrackEvent>> edges,
            double recordedUt, string blank)
        {
            for (int i = 0; i < edges.Count; i++)
            {
                double ut = edges[i].Key;
                if (ut <= recordedUt) continue;
                var s = edges[i].Value;
                var e = new CommandTrackEvent(ut, s.Occluded, s.Occluder, s.Sunlit, blank, true);
                if (into.Count > 0 && into[into.Count - 1].SameStateAs(e)) continue;
                into.Add(e);
            }
        }
    }

    /* The last few transitions of a command feed's discrete state, each stamped
       with the UT of the solve that first saw it, so a client can line the
       state up with the frame it is showing. Only changes are kept; time
       running backwards (a revert) starts the history again. */
    public sealed class CommandTrackEventRing
    {
        private readonly CommandTrackEvent[] _items;
        private int _start;

        public int Count { get; private set; }

        public CommandTrackEventRing(int capacity)
        {
            _items = new CommandTrackEvent[Math.Max(1, capacity)];
        }

        public CommandTrackEvent this[int i] => _items[(_start + i) % _items.Length];

        /* True when `e` was recorded as a transition. */
        public bool Record(CommandTrackEvent e)
        {
            if (Count > 0)
            {
                var last = this[Count - 1];
                if (e.Ut < last.Ut) Clear();
                else if (e.SameStateAs(last)) return false;
            }
            if (Count == _items.Length)
            {
                _items[_start] = e;
                _start = (_start + 1) % _items.Length;
            }
            else
            {
                _items[(_start + Count) % _items.Length] = e;
                Count++;
            }
            return true;
        }

        public void Clear()
        {
            _start = 0;
            Count = 0;
        }
    }

    /*
     * The skyline a surface station sees on its own body. A sight line from
     * the station lies in the vertical plane of its azimuth, which passes
     * through the body centre, and every point on it has the line's own
     * elevation. So terrain at arc distance theta blocks the line exactly
     * when the terrain point's elevation from the station is higher, and a
     * target is hidden when its elevation is below the highest terrain
     * elevation closer to the station than itself.
     * A target with a radius is hidden only when its top is: at a grazing
     * angle the heightfield and the drawn terrain differ by metres, so a
     * craft whose centre sits just under a crest still shows above it.
     *
     * Per azimuth bin the terrain is sampled out to ArcLimit (past which no
     * line still inside the radiusMax shell can be), and the running maximum
     * of elevation by distance is kept. Bins are built on first need and
     * kept: the mask is fixed to the body, so it holds at any UT. Queries
     * interpolate linearly between the two bins either side.
     *
     * Vectors are relative to the body centre, in any one frame per call,
     * with `axis` the body's spin axis in that frame. terrainRadius takes a
     * unit direction from the centre in the frame of the call that builds
     * the bin.
     */
    public sealed class HorizonMask
    {
        public const double FirstSampleM = 20.0;
        public const double MinStepM = 10.0;
        public const double MaxStepM = 1000.0;
        public const double StepFraction = 0.04;

        private readonly double _rs;
        private readonly double _seaRadius;
        private readonly double _binRad;
        private readonly double[] _arcs;
        private readonly double[][] _bins;
        private readonly double[] _sea;
        private readonly Func<DVec3, double> _terrainRadius;

        /* Bins this mask may still build before falling back to the
           sea-level profile (not kept) for the rest of the call. */
        public int Budget = int.MaxValue;
        public int BinsBuilt { get; private set; }
        public int Fallbacks { get; private set; }
        public int SampleCount => _arcs.Length;
        public double ArcLimitRad { get; }

        public HorizonMask(double stationRadius, double seaRadius, double radiusMax, double binDeg,
            Func<DVec3, double> terrainRadius)
        {
            _rs = stationRadius;
            _seaRadius = seaRadius;
            _binRad = binDeg * Math.PI / 180.0;
            _terrainRadius = terrainRadius;
            ArcLimitRad = ArcLimit(stationRadius, seaRadius, Math.Max(radiusMax, seaRadius));
            _arcs = SampleArcs(seaRadius, ArcLimitRad);
            _bins = new double[Math.Max(1, (int)Math.Round(2 * Math.PI / _binRad))][];
            _sea = new double[_arcs.Length];
            double run = double.NegativeInfinity;
            for (int k = 0; k < _arcs.Length; k++)
            {
                run = Math.Max(run, ElevationOf(_rs, seaRadius, _arcs[k]));
                _sea[k] = run;
            }
        }

        /* Arc from the station past which a line still inside the shell
           cannot be: the line grazing the sea-level sphere touches it at
           acos(R / rs) and leaves the shell acos(R / radiusMax) further on.
           Any steeper line meets the sphere sooner; any shallower one leaves
           the shell sooner. */
        public static double ArcLimit(double stationRadius, double seaRadius, double radiusMax)
        {
            double touch = stationRadius > seaRadius ? Math.Acos(seaRadius / stationRadius) : 0;
            double leave = radiusMax > seaRadius ? Math.Acos(seaRadius / radiusMax) : 0;
            return Math.Min(Math.PI, touch + leave);
        }

        /* Sample arcs, radians: fine near the station, where small terrain
           subtends large angles, coarsening with distance. */
        public static double[] SampleArcs(double radius, double arcLimit)
        {
            var arcs = new List<double>();
            double limit = arcLimit * radius;
            for (double d = FirstSampleM; d < limit; d += Math.Min(MaxStepM, Math.Max(MinStepM, d * StepFraction)))
                arcs.Add(d / radius);
            arcs.Add(arcLimit);
            return arcs.ToArray();
        }

        /* Elevation from a station at radius rs of the point at radius r,
           `arc` radians away round the body, in their common vertical plane. */
        public static double ElevationOf(double rs, double r, double arc) =>
            Math.Atan2(r * Math.Cos(arc) - rs, r * Math.Sin(arc));

        /* Local vertical, north and east at the station. */
        public static void Basis(DVec3 relStation, DVec3 axis, out DVec3 up, out DVec3 north, out DVec3 east)
        {
            up = relStation.Normalized;
            north = axis - up * DVec3.Dot(axis, up);
            if (north.Magnitude < 1e-9)
            {
                var any = Math.Abs(up.X) < 0.9 ? new DVec3(1, 0, 0) : new DVec3(0, 1, 0);
                north = any - up * DVec3.Dot(any, up);
            }
            north = north.Normalized;
            east = DVec3.Cross(up, north);
        }

        /* Azimuth (radians in [0, 2pi), from north towards east), arc from
           the station round the body, and elevation of the target. */
        public static void Locate(DVec3 relStation, DVec3 relTarget, DVec3 up, DVec3 north, DVec3 east,
            out double azimuth, out double arc, out double elevation)
        {
            var d = relTarget - relStation;
            double v = DVec3.Dot(d, up);
            var h = d - up * v;
            azimuth = Math.Atan2(DVec3.Dot(h, east), DVec3.Dot(h, north));
            if (azimuth < 0) azimuth += 2 * Math.PI;
            arc = CommandCameraMath.AngleBetween(relStation, relTarget);
            elevation = Math.Atan2(v, h.Magnitude);
        }

        /* True when the station's own terrain hides the target, all of it
           when it has a radius. */
        public bool Blocks(DVec3 relStation, DVec3 relTarget, DVec3 axis, double targetRadius = 0)
        {
            Basis(relStation, axis, out var up, out var north, out var east);
            Locate(relStation, relTarget, up, north, east, out double az, out double arc, out double el);
            if (targetRadius > 0)
            {
                double range = (relTarget - relStation).Magnitude;
                el += range > targetRadius ? Math.Asin(targetRadius / range) : Math.PI / 2;
            }
            return el < MaskAt(az, arc, up, north, east);
        }

        /* Highest terrain elevation closer than `arc` along `azimuth`, or
           negative infinity with no terrain sampled that close. */
        public double MaskAt(double azimuth, double arc, DVec3 up, DVec3 north, DVec3 east)
        {
            int k = CountBelow(_arcs, arc);
            if (k == 0) return double.NegativeInfinity;
            int n = _bins.Length;
            double x = azimuth / _binRad;
            double fl = Math.Floor(x);
            double f = x - fl;
            int i0 = ((int)fl % n + n) % n;
            int i1 = (i0 + 1) % n;
            double m0 = Bin(i0, up, north, east)[k - 1];
            double m1 = Bin(i1, up, north, east)[k - 1];
            return m0 + (m1 - m0) * f;
        }

        /* How many of the ascending `arcs` lie strictly below `x`. */
        public static int CountBelow(double[] arcs, double x)
        {
            int lo = 0, hi = arcs.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (arcs[mid] < x) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private double[] Bin(int i, DVec3 up, DVec3 north, DVec3 east)
        {
            var bin = _bins[i];
            if (bin != null) return bin;
            if (Budget <= 0 || _terrainRadius == null)
            {
                Fallbacks++;
                return _sea;
            }
            Budget--;
            double az = i * _binRad;
            var h = north * Math.Cos(az) + east * Math.Sin(az);
            bin = new double[_arcs.Length];
            double run = double.NegativeInfinity;
            for (int k = 0; k < _arcs.Length; k++)
            {
                double a = _arcs[k];
                double r = _terrainRadius(up * Math.Cos(a) + h * Math.Sin(a));
                run = Math.Max(run, ElevationOf(_rs, r, a));
                bin[k] = run;
            }
            _bins[i] = bin;
            BinsBuilt++;
            return bin;
        }
    }

    /*
     * Segment tests against a closed triangle mesh, such as a body's
     * scaled-space mesh with its vertices already in metres in the body's
     * own axes. Triangles sit in a bounding-volume hierarchy so a segment
     * touches only the few near its path; a segment that never enters the
     * outer sphere (the farthest vertex) is clear at once, and one that
     * enters the inner sphere (inside every triangle's plane, so inside a
     * mesh that is star-shaped about its centre) is blocked at once.
     */
    public sealed class ScaledMeshOccluder
    {
        private const int LeafSize = 4;

        private readonly double[] _tri; // 9 doubles per triangle, in BVH order
        private readonly List<double> _boxes = new List<double>(); // 6 per node
        private readonly List<int> _nodes = new List<int>(); // 2 per node: leaf (start, -count) or inner (left, right)
        private readonly double[] _box;
        private readonly int[] _node;
        private int[] _stack = new int[64];
        private double[] _keys;

        public double RMin { get; }
        public double RMax { get; }
        public double RInner { get; }
        public int TriangleCount { get; }

        public ScaledMeshOccluder(IList<DVec3> vertices, IList<int> triangles)
        {
            int n = triangles.Count / 3;
            TriangleCount = n;
            double rMin = double.MaxValue, rMax = 0;
            for (int i = 0; i < vertices.Count; i++)
            {
                double r = vertices[i].Magnitude;
                if (r < rMin) rMin = r;
                if (r > rMax) rMax = r;
            }
            RMin = vertices.Count > 0 ? rMin : 0;
            RMax = rMax;

            var order = new int[n];
            var cx = new double[n * 3];
            var tb = new double[n * 6];
            double inner = double.MaxValue;
            for (int t = 0; t < n; t++)
            {
                order[t] = t;
                var a = vertices[triangles[3 * t]];
                var b = vertices[triangles[3 * t + 1]];
                var c = vertices[triangles[3 * t + 2]];
                cx[3 * t] = (a.X + b.X + c.X) / 3;
                cx[3 * t + 1] = (a.Y + b.Y + c.Y) / 3;
                cx[3 * t + 2] = (a.Z + b.Z + c.Z) / 3;
                tb[6 * t] = Math.Min(a.X, Math.Min(b.X, c.X));
                tb[6 * t + 1] = Math.Min(a.Y, Math.Min(b.Y, c.Y));
                tb[6 * t + 2] = Math.Min(a.Z, Math.Min(b.Z, c.Z));
                tb[6 * t + 3] = Math.Max(a.X, Math.Max(b.X, c.X));
                tb[6 * t + 4] = Math.Max(a.Y, Math.Max(b.Y, c.Y));
                tb[6 * t + 5] = Math.Max(a.Z, Math.Max(b.Z, c.Z));
                var normal = DVec3.Cross(b - a, c - a).Normalized;
                if (normal.Magnitude > 0) inner = Math.Min(inner, Math.Abs(DVec3.Dot(normal, a)));
            }
            RInner = n > 0 ? Math.Min(inner, RMin) : 0;

            _keys = new double[n];
            if (n > 0) Build(order, 0, n, tb, cx);
            _keys = null;
            _box = _boxes.ToArray();
            _node = _nodes.ToArray();
            _tri = new double[n * 9];
            for (int t = 0; t < n; t++)
            {
                for (int v = 0; v < 3; v++)
                {
                    var p = vertices[triangles[3 * order[t] + v]];
                    _tri[9 * t + 3 * v] = p.X;
                    _tri[9 * t + 3 * v + 1] = p.Y;
                    _tri[9 * t + 3 * v + 2] = p.Z;
                }
            }
        }

        private int Build(int[] order, int start, int count, double[] tb, double[] cx)
        {
            int node = _nodes.Count / 2;
            _nodes.Add(0);
            _nodes.Add(0);
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            double cx0 = double.MaxValue, cy0 = double.MaxValue, cz0 = double.MaxValue;
            double cx1 = double.MinValue, cy1 = double.MinValue, cz1 = double.MinValue;
            for (int i = start; i < start + count; i++)
            {
                int t = order[i];
                int j = 6 * t;
                x0 = Math.Min(x0, tb[j]); y0 = Math.Min(y0, tb[j + 1]); z0 = Math.Min(z0, tb[j + 2]);
                x1 = Math.Max(x1, tb[j + 3]); y1 = Math.Max(y1, tb[j + 4]); z1 = Math.Max(z1, tb[j + 5]);
                cx0 = Math.Min(cx0, cx[3 * t]); cx1 = Math.Max(cx1, cx[3 * t]);
                cy0 = Math.Min(cy0, cx[3 * t + 1]); cy1 = Math.Max(cy1, cx[3 * t + 1]);
                cz0 = Math.Min(cz0, cx[3 * t + 2]); cz1 = Math.Max(cz1, cx[3 * t + 2]);
            }
            _boxes.Add(x0); _boxes.Add(y0); _boxes.Add(z0);
            _boxes.Add(x1); _boxes.Add(y1); _boxes.Add(z1);
            if (count <= LeafSize)
            {
                _nodes[2 * node] = start;
                _nodes[2 * node + 1] = -count;
                return node;
            }
            double ex = cx1 - cx0, ey = cy1 - cy0, ez = cz1 - cz0;
            int axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;
            for (int i = start; i < start + count; i++) _keys[i] = cx[3 * order[i] + axis];
            int half = count / 2;
            Select(_keys, order, start, start + count - 1, start + half);
            int left = Build(order, start, half, tb, cx);
            int right = Build(order, start + half, count - half, tb, cx);
            _nodes[2 * node] = left;
            _nodes[2 * node + 1] = right;
            return node;
        }

        /* Partially orders keys[lo..hi] (and order with them) so the k-th
           is in place with no larger key before it and no smaller after. */
        private static void Select(double[] keys, int[] order, int lo, int hi, int k)
        {
            while (lo < hi)
            {
                double pivot = keys[(lo + hi) >> 1];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (keys[i] < pivot) i++;
                    while (keys[j] > pivot) j--;
                    if (i <= j)
                    {
                        double tk = keys[i]; keys[i] = keys[j]; keys[j] = tk;
                        int to = order[i]; order[i] = order[j]; order[j] = to;
                        i++;
                        j--;
                    }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return;
            }
        }

        /* True when the segment a-b passes through the mesh, ignoring the
           last `endSlack` metres before b (a target on the surface, where
           the mesh may sit a little above the real ground). */
        public bool Blocks(DVec3 a, DVec3 b, double endSlack)
        {
            if (TriangleCount == 0) return false;
            var ab = b - a;
            double len = ab.Magnitude;
            double tMax = 1.0;
            if (endSlack > 0)
            {
                if (len <= endSlack) return false;
                tMax = 1.0 - endSlack / len;
            }
            var end = a + ab * tMax;
            var origin = new DVec3(0, 0, 0);
            if (CommandCameraMath.SegmentClearOfSphere(a, end, origin, RMax)) return false;
            if (!CommandCameraMath.SegmentClearOfSphere(a, end, origin, RInner)) return true;
            return Traverse(a, ab, tMax);
        }

        private bool Traverse(DVec3 o, DVec3 d, double tMax)
        {
            double ix = 1.0 / d.X, iy = 1.0 / d.Y, iz = 1.0 / d.Z;
            var stack = _stack;
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                int node = stack[--sp];
                if (!SlabHit(node, o, ix, iy, iz, tMax)) continue;
                int p = _node[2 * node], q = _node[2 * node + 1];
                if (q <= 0)
                {
                    for (int t = p; t < p - q; t++)
                        if (TriangleHit(t, o, d, tMax)) return true;
                    continue;
                }
                if (sp + 2 > stack.Length)
                {
                    Array.Resize(ref stack, stack.Length * 2);
                    _stack = stack;
                }
                stack[sp++] = p;
                stack[sp++] = q;
            }
            return false;
        }

        private bool SlabHit(int node, DVec3 o, double ix, double iy, double iz, double tMax)
        {
            int b = 6 * node;
            double t0 = 0, t1 = tMax;
            if (!Slab(_box[b], _box[b + 3], o.X, ix, ref t0, ref t1)) return false;
            if (!Slab(_box[b + 1], _box[b + 4], o.Y, iy, ref t0, ref t1)) return false;
            return Slab(_box[b + 2], _box[b + 5], o.Z, iz, ref t0, ref t1);
        }

        private static bool Slab(double lo, double hi, double o, double inv, ref double t0, ref double t1)
        {
            if (double.IsInfinity(inv))
                return o >= lo && o <= hi;
            double a = (lo - o) * inv, b = (hi - o) * inv;
            if (a > b) { double s = a; a = b; b = s; }
            if (a > t0) t0 = a;
            if (b < t1) t1 = b;
            return t0 <= t1;
        }

        /* Möller-Trumbore, both faces, hit parameter within [0, tMax]. */
        private bool TriangleHit(int t, DVec3 o, DVec3 d, double tMax)
        {
            int i = 9 * t;
            var v0 = new DVec3(_tri[i], _tri[i + 1], _tri[i + 2]);
            var e1 = new DVec3(_tri[i + 3], _tri[i + 4], _tri[i + 5]) - v0;
            var e2 = new DVec3(_tri[i + 6], _tri[i + 7], _tri[i + 8]) - v0;
            var p = DVec3.Cross(d, e2);
            double det = DVec3.Dot(e1, p);
            if (Math.Abs(det) < 1e-300) return false;
            double inv = 1.0 / det;
            var s = o - v0;
            double u = DVec3.Dot(s, p) * inv;
            if (u < 0 || u > 1) return false;
            var q = DVec3.Cross(s, e1);
            double w = DVec3.Dot(d, q) * inv;
            if (w < 0 || u + w > 1) return false;
            double h = DVec3.Dot(e2, q) * inv;
            return h >= 0 && h <= tMax;
        }

        /* Every triangle, no hierarchy: the reference the tests hold the
           hierarchy to. */
        public bool BlocksBruteForce(DVec3 a, DVec3 b)
        {
            var d = b - a;
            for (int t = 0; t < TriangleCount; t++)
                if (TriangleHit(t, a, d, 1.0)) return true;
            return false;
        }
    }

    /*
     * How far each part's model reaches from its origin, for the live bound.
     * A reach only ever grows: a canopy that opens or a panel that unfolds
     * raises it at once and it never falls back, so the bound can be late
     * but never early. A part whose shape can move (`moves`) is measured on
     * every frame; any other one every RefreshFrames frames. A measurement
     * of nothing (NaN: no renderer active yet) keeps the reach so far and is
     * tried again on the next frame, so a part is picked up once it shows.
     */
    internal sealed class ReachCache
    {
        public const int RefreshFrames = 120;
        /* Past this many remembered parts the cache starts again, so parts long gone do not pile up. */
        public const int Cap = 4096;

        private struct Entry
        {
            public double Reach;
            public long MeasuredFrame;
            public bool Measured;
        }

        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();

        public int Count => _entries.Count;

        public double Reach<T>(int key, long frame, bool moves, Func<T, double> measure, T subject)
        {
            bool known = _entries.TryGetValue(key, out var e);
            bool due = !known || !e.Measured || moves || frame - e.MeasuredFrame >= RefreshFrames;
            if (!due) return e.Reach;
            if (e.MeasuredFrame == frame && known && e.Measured) return e.Reach;
            double m = measure(subject);
            if (!known && _entries.Count >= Cap) _entries.Clear();
            if (double.IsNaN(m) || double.IsInfinity(m) || m < 0)
            {
                _entries[key] = new Entry { Reach = e.Reach, MeasuredFrame = frame, Measured = false };
                return e.Reach;
            }
            e.Reach = Math.Max(e.Reach, m);
            e.MeasuredFrame = frame;
            e.Measured = true;
            _entries[key] = e;
            return e.Reach;
        }
    }
}
