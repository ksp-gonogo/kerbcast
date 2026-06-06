// ReadbackScheduler — round-robins a per-frame "capture budget" across the
// active cameras so they don't all issue a GPU render + AsyncGPUReadback on the
// same frame. Bounding the number of simultaneous in-flight readbacks is what
// keeps the render-thread readback pump cheap (each in-flight task is walked /
// waited on every frame); it also rate-caps each camera toward the stream fps
// instead of capturing at the full game frame rate.
//
// Deliberately Unity-free (same approach as ControlBlock.cs / ShedController.cs)
// so the scheduling decision is unit-tested without KSP. KerbcamCore owns one
// instance and applies the per-frame permit set to its camera list.

using System;

namespace Kerbcam
{
    public sealed class ReadbackScheduler
    {
        private int _cursor;

        /// <summary>
        /// How many of <paramref name="count"/> cameras should capture this
        /// frame to sustain <paramref name="captureFps"/> given the current
        /// <paramref name="gameFps"/>. When the game is at or below the target
        /// fps we can't stagger — every camera must capture every frame to keep
        /// up — so the budget is the full count. Above it, the budget is the
        /// fraction needed, so each camera captures every ~count/budget frames.
        /// </summary>
        public static int Budget(int count, double captureFps, double gameFps)
        {
            if (count <= 0) return 0;
            if (captureFps <= 0.0) return count;        // pacing disabled
            if (gameFps <= captureFps) return count;    // can't stagger; need every frame
            int b = (int)Math.Ceiling(count * captureFps / gameFps);
            if (b < 1) b = 1;
            if (b > count) b = count;
            return b;
        }

        // Fraction of cameras allowed to capture per frame at each degrade
        // level. Level 0 = no cut; higher levels (driven by ShedController as
        // fps drops below the shed thresholds) progressively *temporally*
        // degrade — fewer cameras captured per frame, so each updates less often
        // but at full quality. This is the default adaptive response now that
        // quality shedding is opt-in. Index clamps to the last entry.
        private static readonly double[] DegradeFraction =
            { 1.0, 0.66, 0.5, 0.33, 0.25, 0.12 };

        /// <summary>
        /// Capture budget from the degrade level alone: as fps falls and the
        /// level rises, fewer cameras capture per frame. Floored at 1 so feeds
        /// never freeze entirely.
        /// </summary>
        public static int DegradeBudget(int count, int level)
        {
            if (count <= 0) return 0;
            if (level < 0) level = 0;
            if (level >= DegradeFraction.Length) level = DegradeFraction.Length - 1;
            int b = (int)Math.Ceiling(count * DegradeFraction[level]);
            if (b < 1) b = 1;
            if (b > count) b = count;
            return b;
        }

        /// <summary>
        /// The per-frame capture budget: the tighter of the stream rate-cap
        /// (don't capture faster than CaptureFps) and the degrade budget (cut
        /// captures as performance degrades). At level 0 with a healthy game
        /// this is just the rate-cap; under load the degrade term dominates.
        /// </summary>
        public static int EffectiveBudget(int count, double captureFps, double gameFps, int level)
        {
            int rate = Budget(count, captureFps, gameFps);
            int degrade = DegradeBudget(count, level);
            return rate < degrade ? rate : degrade;
        }

        /// <summary>
        /// Fill <paramref name="permit"/>[0..count) with the contiguous,
        /// round-robin window of <paramref name="budget"/> cameras allowed to
        /// capture this tick, then advance the cursor. Over consecutive ticks
        /// every camera is granted in turn with no starvation. <paramref
        /// name="permit"/> must have length &gt;= count.
        /// </summary>
        public void NextTick(int count, int budget, bool[] permit)
        {
            for (int i = 0; i < count; i++) permit[i] = false;
            if (count <= 0 || budget <= 0) return;
            if (budget >= count)
            {
                for (int i = 0; i < count; i++) permit[i] = true;
                _cursor = 0;
                return;
            }
            if (_cursor >= count) _cursor %= count;
            for (int k = 0; k < budget; k++) permit[(_cursor + k) % count] = true;
            _cursor = (_cursor + budget) % count;
        }
    }
}
