using System;

namespace Kerbcast
{
    /* One camera's view for the cull, in the world frame the scene draws in now. */
    internal struct CullView
    {
        public DVec3 Eye;
        public DVec3 Forward, Right, Up;
        public double TanHalfH, TanHalfV;
        /* Radians a pixel covers at the centre of the frame; off axis by θ it covers cos²θ of that. */
        public double RadPerPx;
        /* What a pixel covers in the frame's corners, the least anywhere in it. */
        public double MinRadPerPx;
        private double _cornerCos;

        /* Radians a pixel covers looking along `direction`, clamped to the frame's corners. */
        public double RadPerPxToward(DVec3 direction)
        {
            double cos = DVec3.Dot(direction.Normalized, Forward);
            if (cos < _cornerCos) cos = _cornerCos;
            return RadPerPx * cos * cos;
        }

        public static CullView Of(DVec3 eye, DVec3 forward, DVec3 up, double fovDeg, double aspect, int rows)
        {
            var f = forward.Normalized;
            var r = DVec3.Cross(up, f).Normalized;
            var u = DVec3.Cross(f, r);
            double tv = Math.Tan(fovDeg * Math.PI / 360.0);
            double th = tv * aspect;
            double centre = 2.0 * tv / Math.Max(1, rows);
            double cornerCos = 1.0 / Math.Sqrt(1 + th * th + tv * tv);
            return new CullView
            {
                Eye = eye, Forward = f, Right = r, Up = u,
                TanHalfV = tv, TanHalfH = th,
                RadPerPx = centre,
                MinRadPerPx = centre * cornerCos * cornerCos,
                _cornerCos = cornerCos,
            };
        }
    }

    /*
     * Which craft a camera can skip solving, cheaply, without the decision
     * depending on anything present-tense. A craft is skipped only when, at
     * every past state it could be drawn at, it would be outside the view or
     * too faint to change the 8-bit frame. Every input is bounded over the
     * ring's horizon: the craft set is the ring's (ghosts and ended craft
     * included), its size is the largest it has been, its speed the largest
     * in the inertial frame, and a craft with no present is placed at its
     * last state and widened from then.
     *
     * Tier 1 tests one sphere per body the craft occupied; tier 2 places the
     * craft now (or at its end) and widens by how far it could have moved
     * within the light-time bound. A craft whose speed bound reaches 0.9 c is
     * never skipped.
     */
    internal static class PictureCull
    {
        /* An apparent radius at which a dot, at full albedo, phase and a gain of 4, stays under one 8-bit level. */
        public const double CutoffPx = 0.02;
        /* Under this many pixels across a craft is a point of light. */
        public const double PointBelowPx = CommandCameraMath.PointBelowPx;
        /* Part models per camera; the rest are points. */
        public const int ModelCap = 8;
        public const double NoCullFraction = 0.9;
        /* Size assumed for a craft whose parts the ring never recorded. */
        public const double UnknownSizeM = 100.0;

        /*
         * The longest light time to a craft now `distanceM` from the lens,
         * closing or receding at no more than `speed`: D / (c - v), capped by
         * the ring's horizon, and the horizon once v reaches 0.9 c.
         */
        public static double LightTimeBound(double distanceM, double c, double speed, double horizonS)
        {
            if (!(c > 0) || speed >= NoCullFraction * c) return horizonS;
            double t = Math.Max(0, distanceM) / (c - speed);
            return Math.Min(t, horizonS);
        }

        /* Whether a sphere is wholly outside the view's frustum. */
        public static bool OutOfView(in CullView v, DVec3 centre, double radius)
        {
            var d = centre - v.Eye;
            double z = DVec3.Dot(d, v.Forward);
            if (z < -radius) return true;
            double x = DVec3.Dot(d, v.Right), y = DVec3.Dot(d, v.Up);
            double nh = Math.Sqrt(1 + v.TanHalfH * v.TanHalfH), nv = Math.Sqrt(1 + v.TanHalfV * v.TanHalfV);
            if ((Math.Abs(x) - z * v.TanHalfH) / nh > radius) return true;
            if ((Math.Abs(y) - z * v.TanHalfV) / nv > radius) return true;
            return false;
        }

        /* Whether a craft of radius `sizeM` no nearer than `nearestM` is under the cutoff. */
        public static bool TooFaint(double sizeM, double nearestM, double radPerPx)
        {
            if (!(nearestM > sizeM) || !(radPerPx > 0)) return false;
            return Math.Atan(sizeM / nearestM) / radPerPx < CutoffPx;
        }

        /* A craft's apparent radius in pixels at `distanceM`. */
        public static double RadiusPx(double sizeM, double distanceM, double radPerPx) =>
            distanceM > 0 && radPerPx > 0 ? Math.Atan(sizeM / distanceM) / radPerPx : double.PositiveInfinity;

        /* The light a point's soft dot spreads over, in square pixels per square pixel of its width. */
        public const double DotArea = 0.559;

        /*
         * How bright a point of light `acrossPx` wide is drawn, 0 to 1: the
         * light a craft of `radiusPx` reflects at full albedo and a gain of 4,
         * times the lit fraction of its face seen at `phaseCos` (the cosine of
         * the angle between the Sun and the lens as seen from the craft),
         * spread over the dot it is drawn as, so a craft drawn wider than it
         * is gives no more light in all.
         */
        public static double PointBrightness(double radiusPx, double phaseCos, double acrossPx)
        {
            double lit = 0.5 * (1 + Math.Max(-1, Math.Min(1, phaseCos)));
            double light = 4.0 * Math.PI * radiusPx * radiusPx * lit;
            return Math.Min(1.0, light / (DotArea * acrossPx * acrossPx));
        }

        /*
         * Whether track `t` can be skipped by `view` at render UT `now`.
         * `bodies` holds each body's world position as the scene draws it now
         * (NaN where unknown, which keeps the craft), `toWorld` turns the
         * ring's frame into the world's, and `c` is the light speed.
         */
        public static bool Skip(LightHistory ring, HistoryTrack t, double now, in CullView view, DQuat toWorld, DVec3[] bodies, double c,
            int lensBody)
        {
            if (t == null || t.IsBody || !t.HasEntries) return false;
            double size = t.MaxSizeM > 0 ? t.MaxSizeM : UnknownSizeM;
            /* The light time is solved in the frame of the lens's and the craft's
               common body, so its bound takes the craft's speed in that frame. */
            int frame = lensBody;
            foreach (var s in t.Spheres) frame = CommonAncestor(ring, frame, s.Body);
            if (frame < 0) return false;
            /* A body's motion counts about the same frame: the scene keeps the lens's frame still. */
            double bodyBound = 0;
            foreach (var s in t.Spheres) bodyBound = Math.Max(bodyBound, ring.BodySpeedBound(s.Body, frame));
            double lightSpeed = ring.MaxSpeedAbout(t, frame);
            if (lightSpeed >= NoCullFraction * c) return false;
            /*
             * How fast the drawn craft can stray from where it is placed now.
             * About one body, by its own speed about that body and the body's
             * own motion, should bodies be drawn at their past places. Across
             * bodies, a drawn craft sits against one body and its present
             * against perhaps another, so its speed in the frame and both
             * bodies' motion count.
             */
            double drift = t.Spheres.Count == 1
                ? t.Spheres[0].SpeedMs + bodyBound
                : lightSpeed + 2 * bodyBound;
            double horizon = ring.HorizonS;
            if (Tier1(ring, t, view, bodies, c, lightSpeed, frame, horizon, size)) return true;
            return Tier2(ring, t, now, view, toWorld, bodies, c, lightSpeed, drift, horizon, size);
        }

        /* The nearest body both chains of parents share; -1 when they share none. */
        public static int CommonAncestor(LightHistory ring, int a, int b)
        {
            for (int x = a, i = 0; x >= 0 && i < 16; i++)
            {
                for (int y = b, j = 0; y >= 0 && j < 16; j++)
                {
                    if (x == y) return x;
                    if (!ring.TryBody(y, out var by)) break;
                    y = by.Parent;
                }
                if (!ring.TryBody(x, out var bx)) break;
                x = bx.Parent;
            }
            return -1;
        }

        private static bool Tier1(LightHistory ring, HistoryTrack t, in CullView view, DVec3[] bodies, double c, double lightSpeed,
            int frame, double horizon, double size)
        {
            if (t.Spheres.Count == 0) return false;
            foreach (var s in t.Spheres)
            {
                if (!TryBody(bodies, s.Body, out var b)) return false;
                double d = (b - view.Eye).Magnitude;
                double reach = s.RadiusM;
                if (double.IsInfinity(reach) || double.IsNaN(reach)) return false;
                double lt = LightTimeBound(d + reach, c, lightSpeed, horizon);
                double r = reach + ring.BodySpeedBound(s.Body, frame) * lt;
                if (!(TooFaint(size, d - r, view.MinRadPerPx) || OutOfView(view, b, r + size))) return false;
            }
            return true;
        }

        private static bool Tier2(LightHistory ring, HistoryTrack t, double now, in CullView view, DQuat toWorld, DVec3[] bodies,
            double c, double lightSpeed, double drift, double horizon, double size)
        {
            double at = Math.Min(now, t.EndUt);
            if (!ring.TryStateAt(t, at, out var st) || !TryBody(bodies, st.Body, out var b)) return false;
            var anchor = b + toWorld.Rotate(PastInstant.AboutDrawnBody(st, now));
            double age = Math.Max(0, now - at);
            double d = (anchor - view.Eye).Magnitude;
            double lt = LightTimeBound(d + drift * age, c, lightSpeed, horizon);
            double w = drift * (age + lt);
            if (double.IsInfinity(w) || double.IsNaN(w)) return false;
            if (TooFaint(size, d - w, view.MinRadPerPx)) return true;
            return OutOfView(view, anchor, w + size);
        }

        private static bool TryBody(DVec3[] bodies, int index, out DVec3 at)
        {
            at = default;
            if (bodies == null || index < 0 || index >= bodies.Length) return false;
            at = bodies[index];
            return !double.IsNaN(at.X);
        }
    }

    /*
     * The bodies as one camera's picture draws them this frame, by list
     * index: each centre where the picture puts it, or NaN for a body it
     * leaves out, and its radius. What a frame shows is tested against these,
     * never against the bodies where they are now.
     */
    internal static class DrawnSpheres
    {
        /* Whether any drawn body other than the one at `skip` stands between `a` and `b`. */
        public static bool Blocked(DVec3 a, DVec3 b, DVec3[] centres, double[] radii, int count, int skip = -1)
        {
            if (centres == null || radii == null) return false;
            int n = Math.Min(count, Math.Min(centres.Length, radii.Length));
            for (int i = 0; i < n; i++)
            {
                if (i == skip || double.IsNaN(centres[i].X)) continue;
                if (!CommandCameraMath.SegmentClearOfSphere(a, b, centres[i], radii[i])) return true;
            }
            return false;
        }

        /* A stand-in is drawn only where a real craft would be seen: no body in the way of the lens, and sunlit at its drawn place. */
        public static bool Shown(DVec3 eye, DVec3 at, DVec3[] centres, double[] radii, int count, int sun)
        {
            if (Blocked(eye, at, centres, radii, count)) return false;
            if (sun < 0 || sun >= count || double.IsNaN(centres[sun].X)) return true;
            return !Blocked(at, centres[sun], centres, radii, count, sun);
        }
    }
}
