// QualityClamp: the pure arithmetic behind the single resolution-change
// path in KerbcastCamera.ApplyEffectiveQuality. Combines three inputs into
// one effective render size:
//
//   effective = min(operator ceiling, adaptive shed scale, viewer scale)
//
// The operator's settings.cfg Width/Height (global or per-camera Camera
// node) is the hard ceiling: every scale here is <= 1.0 of it, so neither
// the adaptive ladder nor a viewer request can ever exceed it (or the ring's
// allocated max, which the ceiling is bounded by). The viewer clamp is a
// LEVEL into ViewerScales (a fixed preset menu mirroring the shed table's
// resolution steps), never freeform pixels, so a hostile or buggy viewer
// can't request unaligned or ring-overflowing sizes. The min() means a shed
// demote always wins over a viewer target, and when the controller promotes
// back the viewer target is honored again, with the adaptive machinery
// itself never seeing the viewer at all.
//
// Deliberately depends ONLY on System.* (no UnityEngine) so the standalone
// QualityClamp.Tests harness compiles + runs this file with no KSP assemblies.

using System;

namespace Kerbcast
{
    public static class QualityClamp
    {
        /// <summary>
        /// Viewer-selectable resolution scales, indexed by viewer level.
        /// Derived from KerbcastCamera.ShedTable's distinct ResScale steps
        /// (1.0 / 0.75 / 0.5 / 0.25) and mirrored by the protocol's
        /// QualityPreset (full / threeQuarter / half / quarter): the
        /// sidecar maps preset to index, this table maps index to scale.
        /// </summary>
        public static readonly float[] ViewerScales = { 1.00f, 0.75f, 0.50f, 0.25f };

        public static int MaxViewerLevel => ViewerScales.Length - 1;

        /// <summary>Clamp an (untrusted, wire-supplied) viewer level into
        /// the table. Out-of-range values degrade to the nearest valid
        /// level rather than being rejected, so a newer sidecar with more
        /// presets still produces a sane size on an older plugin.</summary>
        public static int ClampViewerLevel(int level)
        {
            if (level < 0) return 0;
            if (level > MaxViewerLevel) return MaxViewerLevel;
            return level;
        }

        /// <summary>
        /// The effective resolution scale: the smaller of the adaptive shed
        /// level's scale and the viewer's requested scale. min() is the
        /// whole precedence model: a demote (smaller shed scale) wins over
        /// the viewer target, and a recovered controller (scale back at 1.0)
        /// hands control back to the viewer target.
        /// </summary>
        public static float EffectiveScale(float shedScale, int viewerLevel)
        {
            float viewerScale = ViewerScales[ClampViewerLevel(viewerLevel)];
            return Math.Min(shedScale, viewerScale);
        }

        /// <summary>Every rendered frame's width and height are a multiple of
        /// this: H.264 codes whole 16x16 macroblocks and crops anything else, and
        /// some phone decoders show that padding as a coloured line at the edge.
        /// Matches the sidecar's FRAME_ALIGNMENT.</summary>
        public const int FrameAlignment = 16;

        /* These and the sizing below make the same choices as the sidecar's
           encoder/frame_size.rs, which explains them: change both together. */
        const double ShapeTolerance = 0.01;
        const double CoverSlackPx = 2.0;
        const int DownSteps = 3;

        /// <summary>The largest multiple of 16 at or under v, never under 16.</summary>
        public static int AlignDown(int v)
        {
            int aligned = v & ~(FrameAlignment - 1);
            return aligned < FrameAlignment ? FrameAlignment : aligned;
        }

        static int AlignUp(double v)
        {
            int aligned = (int)Math.Ceiling(v / FrameAlignment) * FrameAlignment;
            return aligned < FrameAlignment ? FrameAlignment : aligned;
        }

        /// <summary>Scale one operator-ceiling dimension: truncate, floor to a
        /// multiple of 16. Identical rounding to the sidecar's
        /// quality_limited_reason so "honored exactly" compares equal across the
        /// wire.</summary>
        public static int ScaleDimension(int operatorDim, float scale)
        {
            return AlignDown((int)(operatorDim * scale));
        }

        /// <summary>Smallest render side every encoder accepts: the Steam
        /// Deck's VAAPI refuses anything under 128x128. Matches the
        /// sidecar's MIN_ENCODE_DIMENSION.</summary>
        public const int MinEncodeDimension = 128;

        /// <summary>
        /// The effective render size for the operator dims at <paramref name="scale"/>,
        /// raised at the operator's shape if either side lands under
        /// MinEncodeDimension, never past the operator dims. Shedding a small
        /// viewer's size further would only produce frames the hardware encoder
        /// refuses. Aligned as AlignedCover describes, so a size the sidecar chose
        /// comes back unchanged at scale 1.
        /// </summary>
        public static (int w, int h) ScaleSize(int operatorW, int operatorH, float scale)
        {
            if (operatorW <= 0 || operatorH <= 0)
                return (ScaleDimension(operatorW, scale), ScaleDimension(operatorH, scale));
            double lift = Math.Min(1.0, Math.Max(
                Math.Max((double)MinEncodeDimension / operatorW, (double)MinEncodeDimension / operatorH),
                scale));
            return AlignedCover(operatorW * lift, operatorH * lift, operatorW, operatorH, (double)operatorW / operatorH);
        }

        /// <summary>
        /// The smallest aligned size at <paramref name="aspect"/> that covers the
        /// wanted size and fits the limit, or AlignedDown when nothing does.
        /// </summary>
        public static (int w, int h) AlignedCover(double wantW, double wantH, int limitW, int limitH, double aspect)
        {
            int topW = AlignDown(limitW), topH = AlignDown(limitH);
            double minW = wantW - CoverSlackPx;
            for (int h = AlignUp(wantH - CoverSlackPx); h <= topH; h += FrameAlignment)
            {
                double ideal = h * aspect;
                int w = AlignUp(Math.Max(minW, ideal * (1.0 - ShapeTolerance)));
                if (w <= topW && w <= ideal * (1.0 + ShapeTolerance))
                    return (w, h);
            }
            return AlignedDown(wantW, wantH, limitW, limitH, aspect);
        }

        /// <summary>
        /// The largest aligned size at <paramref name="aspect"/> inside the wanted
        /// size and the limit. When no nearby height holds the shape, each side
        /// rounds down on its own.
        /// </summary>
        public static (int w, int h) AlignedDown(double wantW, double wantH, int limitW, int limitH, double aspect)
        {
            int topW = AlignDown(Math.Min((int)wantW, limitW));
            int topH = AlignDown(Math.Min((int)wantH, limitH));
            for (int step = 0; step < DownSteps; step++)
            {
                int h = topH - step * FrameAlignment;
                if (h < MinEncodeDimension) break;
                double ideal = h * aspect;
                int w = Math.Min(topW, AlignDown((int)(ideal * (1.0 + ShapeTolerance))));
                if (w >= MinEncodeDimension && w >= ideal * (1.0 - ShapeTolerance))
                    return (w, h);
            }
            return (topW, topH);
        }
    }
}
