using System;
using System.Collections.Generic;

namespace Kerbcast
{
    /* Double-precision 3-vector for the ground-station maths. Unity's Vector3 is
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

    /* Which local layers the ground-station camera renders this tick, and with
       what clip planes. Produced by GroundStationMath.PlanClips. */
    public struct GroundClipPlan
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

    /* Unity-free maths for the ground tracking-station camera: where the station
       is, where it looks, how wide it zooms, which layers carry the craft and
       whether a body is in the way. Compiled verbatim by GroundStationMath.Tests. */
    public static class GroundStationMath
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

        /* True when no part of the segment a-b passes inside the sphere. Same rule
           as gonogo's sight lines: a body blocks, nothing else does. */
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
        public static GroundClipPlan PlanClips(
            double distance, double craftRadius, bool occluded, bool hasFarLayer, bool wasSplit,
            float nearNativeNear, float nearNativeFar, float farNativeNear, float farNativeFar)
        {
            var plan = new GroundClipPlan();
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
}
