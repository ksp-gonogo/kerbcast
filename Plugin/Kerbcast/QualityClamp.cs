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
// can't request odd dims or ring-overflowing sizes. The min() means a shed
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

        /// <summary>Scale one operator-ceiling dimension: truncate, floor to
        /// even (H.264 chroma), never below 2. Identical rounding to the
        /// sidecar's quality_limited_reason so "honored exactly" compares
        /// equal across the wire.</summary>
        public static int ScaleDimension(int operatorDim, float scale)
        {
            int v = (int)(operatorDim * scale);
            v -= v & 1;
            return v < 2 ? 2 : v;
        }

        /// <summary>Smallest render side every encoder accepts: the Steam
        /// Deck's VAAPI refuses anything under 128x128. Matches the
        /// sidecar's MIN_ENCODE_DIMENSION.</summary>
        public const int MinEncodeDimension = 128;

        /// <summary>
        /// The effective render size for the operator dims at <paramref name="scale"/>:
        /// ScaleDimension on each axis, then, if either side lands under
        /// MinEncodeDimension, raised at the operator's shape until neither
        /// is, never past the operator dims. Shedding a small viewer's size
        /// further would only produce frames the hardware encoder refuses.
        /// </summary>
        public static (int w, int h) ScaleSize(int operatorW, int operatorH, float scale)
        {
            int w = ScaleDimension(operatorW, scale);
            int h = ScaleDimension(operatorH, scale);
            if ((w >= MinEncodeDimension && h >= MinEncodeDimension) || operatorW <= 0 || operatorH <= 0)
                return (w, h);
            double lift = Math.Max(
                Math.Max((double)MinEncodeDimension / operatorW, (double)MinEncodeDimension / operatorH),
                scale);
            return (RaiseDimension(operatorW, lift), RaiseDimension(operatorH, lift));
        }

        static int RaiseDimension(int operatorDim, double scale)
        {
            int v = (int)Math.Ceiling(operatorDim * scale - 1e-6);
            v += v & 1;
            int ceiling = operatorDim - (operatorDim & 1);
            if (v > ceiling) v = ceiling;
            return v < 2 ? 2 : v;
        }
    }
}
