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

        /* The smallest disc radius, in pixels, that can take the light from the lens's own planet. */
        public const double MinOutrankPixels = 2;

        /* How far past the frame's corner, as a multiple of the half diagonal, the held body stays a candidate, so a lens jittering at the frame's edge does not flip the light. */
        public const double HeldConeWidth = 1.1;

        /* The half diagonal to test a body against: the held body's is widened by HeldConeWidth, every other body's is the frame's own. */
        public static double ConeFor(double halfDiagonal, bool held) => held ? halfDiagonal * HeldConeWidth : halfDiagonal;

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
        public static double DiscInFrame(DVec3 eye, DVec3 centre, double radiusM, DVec3 forward, double halfDiagonal) =>
            DiscInFrame(eye, centre, radiusM, radiusM, forward, halfDiagonal);

        /*
         * The disc of a sphere of `radiusM` at `centre` as `eye` sees it, or 0
         * when nothing drawn of it out to `reachM` (ReachRadius) can reach a
         * frame looking along `forward`. A planet's limb seen edge on shows
         * its mountains, clouds and air well above sea level, so a body
         * whose sea-level disc has just left the frame can still fill its
         * bottom edge.
         */
        public static double DiscInFrame(DVec3 eye, DVec3 centre, double radiusM, double reachM, DVec3 forward, double halfDiagonal)
        {
            var toBody = centre - eye;
            double disc = DiscRadians(toBody.Magnitude, radiusM);
            if (toBody.Magnitude <= radiusM) return disc;
            return AngleFrom(eye, centre, forward) <= halfDiagonal + DiscRadians(toBody.Magnitude, Math.Max(radiusM, reachM)) ? disc : 0;
        }

        /* The angle between `forward` and the direction from `eye` to `centre`. */
        public static double AngleFrom(DVec3 eye, DVec3 centre, DVec3 forward)
        {
            double cos = Math.Max(-1, Math.Min(1, DVec3.Dot((centre - eye).Normalized, forward.Normalized)));
            return Math.Acos(cos);
        }

        /* How far from its centre a body is drawn: the top of its air, or of its highest ground, whichever is higher. */
        public static double ReachRadius(double radiusM, double terrainMaxM, bool atmosphere, double atmosphereDepthM) =>
            Math.Max(Math.Max(radiusM, terrainMaxM), atmosphere ? radiusM + atmosphereDepthM : radiusM);

        /* Whether a disc reaches past half the way from a frame's centre to its corner, about half the frame's area, which is what a planet underfoot does. */
        public static bool FillsFrame(double disc, double halfDiagonal) => disc > halfDiagonal / 2;

        /* The radius in pixels of a disc at the centre of a frame `rows` high. */
        public static double DiscPixels(double disc, double verticalFovDeg, int rows) =>
            rows / 2.0 * Math.Tan(disc) / Math.Tan(verticalFovDeg * Math.PI / 360);

        /* Whether a body's centre is hidden by the lens's own planet: inside that planet's disc and no nearer than its limb. */
        public static bool BehindLimb(DVec3 eye, DVec3 ownCentre, double ownRadiusM, DVec3 centre)
        {
            var toOwn = ownCentre - eye;
            var toBody = centre - eye;
            double cos = Math.Max(-1, Math.Min(1, DVec3.Dot(toOwn.Normalized, toBody.Normalized)));
            if (Math.Acos(cos) > DiscRadians(toOwn.Magnitude, ownRadiusM)) return false;
            double d = toOwn.Magnitude;
            return toBody.Magnitude >= Math.Sqrt(Math.Max(0, d * d - ownRadiusM * ownRadiusM));
        }

        /*
         * Whether a body in frame may take the light from the lens's own
         * planet filling that frame. It must be big enough to see and not
         * hidden by the planet: a speck in a wide frame is not what the
         * picture shows, and the planet's own ground would be lit from the
         * wrong side for its sake. A body already `held` needs StayMargin
         * fewer pixels, so a zoom resting at the threshold does not flip it.
         */
        public static bool OutranksOwn(DVec3 eye, DVec3 ownCentre, double ownRadiusM, DVec3 centre, double disc, double verticalFovDeg, int rows, bool held) =>
            DiscPixels(disc, verticalFovDeg, rows) >= (held ? MinOutrankPixels / StayMargin : MinOutrankPixels) && !BehindLimb(eye, ownCentre, ownRadiusM, centre);

        /*
         * The slot of the body to aim the light at, from `discs` where 0 means
         * not a candidate, or -1 for none. The body in slot `held` keeps the
         * light while it is in frame unless another's disc is StayMargin times
         * wider. Ties go to the lower slot, so the choice does not depend on
         * float noise between two equal discs. Slot `underfoot`, a lens's own
         * planet filling its frame, takes the light only when no other body
         * is a candidate, so a telescope low over the horizon lights its
         * target. The caller clears the discs that fail OutranksOwn first.
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

        /* The slot of the planet whose system `slot` is part of, from each body's `parent` slot: the body under a star or with no parent. A star is in none, -1. */
        public static int SystemRoot(int[] parent, bool[] star, int slot)
        {
            if (slot < 0 || slot >= parent.Length || star[slot]) return -1;
            for (int steps = 0; steps < parent.Length; steps++)
            {
                int up = parent[slot];
                if (up < 0 || up >= parent.Length || star[up] || up == slot) return slot;
                slot = up;
            }
            return slot;
        }

        /*
         * Gives the systems in frame the spare layers, each with its own light.
         * `best` is each root slot's widest disc in frame (0 for none), `held`
         * the layer each root had last frame or -1. The main light's system
         * takes none. The widest take a layer, a held one needing StayMargin
         * less so near-equal discs do not swap them, and keep the layer they
         * had; the rest share the main light. Fills `layer` per root with the
         * layer or -1 and returns how many were given one.
         */
        public static int AssignLayers(double[] best, int count, int mainRoot, int[] held, int[] spares, int spareCount, int[] layer)
        {
            for (int i = 0; i < count; i++) layer[i] = -1;
            int given = 0;
            /* Picked in rank order without sorting: the next is the best score not yet given, ties to the lower slot. */
            for (; given < spareCount; given++)
            {
                int pick = -1;
                double pickScore = 0;
                for (int r = 0; r < count; r++)
                {
                    if (r == mainRoot || best[r] <= 0 || layer[r] != -1) continue;
                    double score = held[r] >= 0 ? best[r] * StayMargin : best[r];
                    if (pick < 0 || score > pickScore)
                    {
                        pick = r;
                        pickScore = score;
                    }
                }
                if (pick < 0) break;
                layer[pick] = -2;
            }
            int taken = 0;
            for (int r = 0; r < count; r++)
            {
                if (layer[r] != -2) continue;
                int at = Array.IndexOf(spares, held[r], 0, spareCount);
                if (held[r] < 0 || at < 0 || (taken & (1 << at)) != 0) continue;
                layer[r] = held[r];
                taken |= 1 << at;
            }
            for (int r = 0; r < count; r++)
            {
                if (layer[r] != -2) continue;
                int at = 0;
                while ((taken & (1 << at)) != 0) at++;
                layer[r] = spares[at];
                taken |= 1 << at;
            }
            return given;
        }

        /* A light or projector `mask` that reaches the spare layers in `spareMask` exactly as it reaches layer `from`, so a body moved off it is treated as before. */
        public static int MirrorMask(int mask, int from, int spareMask) =>
            (mask & ~spareMask) | ((mask & (1 << from)) != 0 ? spareMask : 0);

        /* The layers of `candidates` with nothing on them in `usedMask`, as a mask. */
        public static int FreeLayers(int[] candidates, int usedMask)
        {
            int free = 0;
            for (int i = 0; i < candidates.Length; i++)
                if ((usedMask & (1 << candidates[i])) == 0) free |= 1 << candidates[i];
            return free;
        }

        /* The layers set in `mask`, lowest first, into `layers`; returns how many. */
        public static int LayersIn(int mask, int[] layers)
        {
            int n = 0;
            for (int l = 0; l < 32 && n < layers.Length; l++)
                if ((mask & (1 << l)) != 0) layers[n++] = l;
            return n;
        }
    }
}
