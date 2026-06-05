// ShedController — the adaptive-shed decision state machine, deliberately
// Unity-free so a standalone dotnet test project can exercise it without KSP
// assemblies (same approach as ControlBlock.cs).
//
// Background: KerbcamCore samples a rolling fps average and asks this controller
// each LateUpdate whether to raise or lower the shed level. Each level maps (in
// KerbcamCamera.ShedTable) to a render-resolution multiplier + a set of FX
// layers to drop. Changing level is EXPENSIVE: every camera reallocates its
// RenderTexture chain and the sidecar reinitialises its encoder.
//
// The original inline logic used only a 25<->30 fps hysteresis band and
// re-evaluated every frame. When the sustainable fps at two adjacent resolution
// levels straddles that band (e.g. level-0 ~24fps, level-1 ~31fps), it flips
// level every couple of frames forever — a self-sustaining limit cycle, because
// the cost of changing resolution itself perturbs the fps it's reacting to. A
// captured Steam-Deck session showed 638 resolution flips and 719 encoder
// reinits, each a frametime spike.
//
// Fix, in two parts:
//   1. A MINIMUM DWELL TIME between level changes, asymmetric — shed reasonably
//      quickly when overloaded (fast attack), restore quality only after a quiet
//      period (slow release). This rate-limits changes.
//   2. ADAPTIVE RESTORE BACK-OFF — rate-limiting alone only *slows* a true limit
//      cycle (it still flaps, just every ~dwell seconds). So when a restore is
//      immediately "regretted" (we're forced to shed again right after restoring,
//      proving the restore caused the overload), we grow the restore dwell
//      geometrically. After a few regrets the restore dwell is large enough that
//      the controller simply stays shed — the flap converges to nothing. If a
//      restore instead *sticks* (survives a long success window before any new
//      shed), the back-off resets so genuine improvements still get a responsive
//      restore.
// The fps thresholds themselves are unchanged; re-tuning them (e.g. a wider
// restore margin) is a follow-up that needs in-game measurement we don't have.

using System;

namespace Kerbcam
{
    /// <summary>
    /// Decides the adaptive-shed level from a rolling fps average, rate-limited
    /// by a minimum dwell time so it cannot flap between levels. Pure logic, no
    /// UnityEngine dependency — see the file header for why.
    /// </summary>
    public sealed class ShedController
    {
        // Per-transition fps thresholds. ShedBelow[n] = escalate n -> n+1 when
        // the average drops below it; RestoreAbove[n] = de-escalate (n+1) -> n
        // when the average climbs above it. +5 fps of hysteresis per level.
        // The first shed only triggers below 15 fps — i.e. kerbcam tolerates a
        // slow-but-playable game and only degrades cameras when it's genuinely
        // struggling; deeper levels cascade down for severe cases.
        private static readonly float[] ShedBelow    = { 15f, 12f, 9f, 6f, 3f };
        private static readonly float[] RestoreAbove = { 20f, 17f, 14f, 11f, 8f };

        /// <summary>Default seconds to wait after any change before shedding
        /// further (fast attack — react to genuine sustained slow-downs).</summary>
        public const double DefaultShedDwellSeconds = 3.0;

        /// <summary>Default base seconds to wait after any change before
        /// restoring quality (slow release — avoids the restore->overload->shed
        /// flap). Grows under back-off; see the file header.</summary>
        public const double DefaultRestoreDwellSeconds = 15.0;

        /// <summary>Default geometric factor the restore dwell is multiplied by
        /// each time a restore is regretted.</summary>
        public const double DefaultBackoffFactor = 2.0;

        /// <summary>Default cap on the backed-off restore dwell, so recovery is
        /// merely slow, never impossible.</summary>
        public const double DefaultMaxRestoreDwellSeconds = 300.0;

        private readonly int _maxLevel;
        private readonly double _shedDwellSeconds;
        private readonly double _baseRestoreDwellSeconds;
        private readonly double _backoffFactor;
        private readonly double _maxRestoreDwellSeconds;
        // A shed within (shedDwell + this grace) of a restore counts as a regret.
        private readonly double _regretGraceSeconds;
        // A restore that survives this long without a new shed is judged a
        // success and clears the back-off.
        private readonly double _successWindowSeconds;

        private int _level;
        // Time of the last level change. Negative-infinity so the first decision
        // is never blocked by the dwell.
        private double _lastChangeTime = double.NegativeInfinity;
        // Time of the last restore (de-escalation), for regret/success detection.
        private double _lastRestoreTime = double.NegativeInfinity;
        // Current restore dwell, grown by back-off; reset on a sticking restore.
        private double _effectiveRestoreDwell;

        public ShedController(
            int maxLevel,
            double shedDwellSeconds = DefaultShedDwellSeconds,
            double restoreDwellSeconds = DefaultRestoreDwellSeconds,
            double backoffFactor = DefaultBackoffFactor,
            double maxRestoreDwellSeconds = DefaultMaxRestoreDwellSeconds)
        {
            if (maxLevel < 0) maxLevel = 0;
            _maxLevel = maxLevel;
            _shedDwellSeconds = Math.Max(0.0, shedDwellSeconds);
            _baseRestoreDwellSeconds = Math.Max(0.0, restoreDwellSeconds);
            // A factor <= 1 would disable convergence; clamp up so back-off
            // always grows the dwell. Cap must be at least the base dwell.
            _backoffFactor = Math.Max(1.0, backoffFactor);
            _maxRestoreDwellSeconds = Math.Max(_baseRestoreDwellSeconds, maxRestoreDwellSeconds);
            _effectiveRestoreDwell = _baseRestoreDwellSeconds;
            _regretGraceSeconds = _shedDwellSeconds + 2.0;
            // A restore is "successful" once it has outlived a couple of base
            // restore windows without provoking a new shed.
            _successWindowSeconds = Math.Max(30.0, _baseRestoreDwellSeconds * 2.0);
        }

        /// <summary>The current shed level.</summary>
        public int Level => _level;

        /// <summary>
        /// Evaluate the desired level given the current rolling fps average and
        /// the current time (any monotonic seconds clock). Moves at most one
        /// level per call, and never more than once per (asymmetric) dwell
        /// window. Returns the resulting level; compare with the prior
        /// <see cref="Level"/> to decide whether to apply it to the cameras.
        /// </summary>
        public int Evaluate(float fpsAvg, double now)
        {
            double sinceChange = now - _lastChangeTime;

            // Escalate (shed) — we're overloaded at the current level.
            if (_level < _maxLevel && fpsAvg < ShedBelow[_level])
            {
                if (sinceChange >= _shedDwellSeconds)
                {
                    double sinceRestore = now - _lastRestoreTime;
                    if (sinceRestore <= _regretGraceSeconds)
                    {
                        // Regret: we shed almost immediately after restoring, so
                        // that restore was premature — grow the restore dwell so
                        // we stop trying so eagerly. This is what makes the flap
                        // converge instead of cycling forever.
                        _effectiveRestoreDwell =
                            Math.Min(_effectiveRestoreDwell * _backoffFactor, _maxRestoreDwellSeconds);
                    }
                    else if (sinceRestore >= _successWindowSeconds)
                    {
                        // The last restore stuck for a good while; this shed is
                        // fresh load, not a regret — clear the back-off so we
                        // stay responsive to genuine improvements later.
                        _effectiveRestoreDwell = _baseRestoreDwellSeconds;
                    }
                    _level++;
                    _lastChangeTime = now;
                }
                return _level;
            }

            // De-escalate (restore) — we have headroom above the level below us.
            if (_level > 0 && fpsAvg > RestoreAbove[_level - 1])
            {
                if (sinceChange >= _effectiveRestoreDwell)
                {
                    _level--;
                    _lastChangeTime = now;
                    _lastRestoreTime = now;
                }
                return _level;
            }

            // Deadband: between the shed and restore thresholds — hold.
            return _level;
        }
    }
}
