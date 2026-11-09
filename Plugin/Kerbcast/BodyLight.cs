using System;

namespace Kerbcast
{
    /*
     * The maths of lighting scaled bodies from the Sun as each camera draws
     * them. The scaled shaders take the terminator from Unity's one directional
     * light, so a camera can aim that light at one body only: the one whose
     * disc fills most of its frame, not counting the ground the lens stands
     * over. A moon's Sun direction is within a fraction of a degree of its
     * planet's, so the whole planetary system stays right.
     */
    internal static class BodyLight
    {
        /* A challenger's disc must be this much wider than the held body's to take the light, so near-equal discs do not make the lighting jump. */
        public const double StayMargin = 1.25;

        /* Whether this camera lights bodies per body: always under a light speed, otherwise as the player's setting says. */
        public static bool Applies(bool lightSpeedSet, bool phasesSetting) => lightSpeedSet || phasesSetting;

        /* The unit direction from `from` to `to`, or `fallback` when the two coincide (a star's direction to itself). */
        public static DVec3 Direction(DVec3 from, DVec3 to, DVec3 fallback)
        {
            var d = to - from;
            return d.Magnitude > 1 ? d.Normalized : fallback;
        }

        /* The angle from the centre of a sphere's disc to its limb, seen from `distanceM`. A lens at or inside the sphere sees half the sky filled. */
        public static double DiscRadians(double distanceM, double radiusM) =>
            distanceM <= radiusM ? Math.PI / 2 : Math.Asin(radiusM / distanceM);

        /* The angle from a frame's centre to its corner. */
        public static double HalfDiagonalRadians(double verticalFovDeg, double aspect)
        {
            double t = Math.Tan(verticalFovDeg * Math.PI / 360);
            return Math.Atan(t * Math.Sqrt(1 + aspect * aspect));
        }

        /* The disc of a sphere at `centre` as `eye` sees it, or 0 when no part of it can reach a frame looking along `forward`. */
        public static double DiscInFrame(DVec3 eye, DVec3 centre, double radiusM, DVec3 forward, double halfDiagonal)
        {
            var toBody = centre - eye;
            double disc = DiscRadians(toBody.Magnitude, radiusM);
            if (toBody.Magnitude <= radiusM) return disc;
            double cos = Math.Max(-1, Math.Min(1, DVec3.Dot(toBody.Normalized, forward.Normalized)));
            return Math.Acos(cos) <= halfDiagonal + disc ? disc : 0;
        }

        /* Whether a disc reaches past half the way from a frame's centre to its corner, about half the frame's area, which is what a planet underfoot does. */
        public static bool FillsFrame(double disc, double halfDiagonal) => disc > halfDiagonal / 2;

        /*
         * The slot of the body to aim the light at, from `discs` where 0 means
         * not a candidate, or -1 for none. The body in slot `held` keeps the
         * light while it is in frame unless another's disc is StayMargin times
         * wider. Ties go to the lower slot, so the choice does not depend on
         * float noise between two equal discs. Slot `underfoot`, a lens's own
         * planet filling its frame, takes the light only when no other body
         * is in frame, so a telescope low over the horizon lights its target.
         */
        public static int Pick(double[] discs, int count, int held, int underfoot = -1)
        {
            int best = -1;
            for (int i = 0; i < count; i++)
                if (i != underfoot && discs[i] > 0 && (best < 0 || discs[i] > discs[best])) best = i;
            if (best < 0) return underfoot >= 0 && underfoot < count && discs[underfoot] > 0 ? underfoot : -1;
            if (held != underfoot && held >= 0 && held < count && discs[held] > 0 && discs[best] < discs[held] * StayMargin) return held;
            return best;
        }
    }
}
