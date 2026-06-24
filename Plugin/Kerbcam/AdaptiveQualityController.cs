// AdaptiveQualityController -- the opt-in quality ladder layered on top of the
// stagger budget regulator. Deliberately Unity-free (same approach as
// StaggerBudgetController / ShedController) so the decision logic is
// unit-tested without KSP (Plugin/AdaptiveQualityController.Tests).
//
// Division of labour: the stagger regulator is the FIRST line of defence and
// is lossless (fewer cameras captured per frame, full quality each). This
// controller only acts when temporal degrade has run out of room:
//
//   DEMOTE (level up, lower quality): only when the stagger budget is pinned
//   at its one-camera floor AND kerbcam is still over its ms budget (or the
//   game is below the MinKspFps physics floor). Each level maps, in
//   KerbcamCamera.ShedTable, to a render-resolution multiplier plus FX layers
//   to drop. Runtime resolution changes are safe here: SetRenderSize switches
//   between pooled RenderTexture sets, so in-flight readbacks drain at their
//   captured dimensions and nothing reallocates per change.
//
//   PROMOTE (level down, restore quality): only after SUSTAINED headroom,
//   on every count at once: the stagger budget fully restored to the camera
//   count (temporal recovery comes first), kerbcam comfortably under its ms
//   budget, and game fps clear of the floor plus hysteresis. The headroom
//   clock resets on any frame that fails a condition, so a promote needs an
//   unbroken quiet stretch, not an average. One level per step, and a fresh
//   stretch is required before the next step.
//
// Anti-flap rules (the hard contract):
//   - A demote always wins immediately over a pending promote: the demote
//     branch is checked first and any overload resets the headroom clock.
//   - After any demote, promotes are blocked for a cooldown window even if
//     headroom returns sooner, so a borderline scene cannot ping-pong.
//   - The ladder never promotes past level 0, which IS the user-configured
//     quality (OperatorWidth/Height and operator layers); this controller
//     only ever returns toward that ceiling, never above it.
//
// When constructed disabled, Evaluate is a single branch returning level 0
// and mutates nothing: the flag-off behaviour is bit-for-bit today's.

using System;

namespace Kerbcam
{
    public sealed class AdaptiveQualityController
    {
        public const double DefaultDeadbandFrac = 0.15;
        // Seconds between consecutive demotes, so each step's effect lands in
        // the cost EMA before the next decision. The FIRST demote after quiet
        // is never blocked (last-demote time starts at negative infinity).
        public const double DefaultDemoteDwellSeconds = 3.0;
        // Unbroken headroom required before a single promote step. Tens of
        // seconds by design: promotes are rare and deliberate.
        public const double DefaultPromoteDwellSeconds = 30.0;
        // Promotes stay blocked for this long after any demote.
        public const double DefaultDemoteCooldownSeconds = 60.0;
        // Game fps must clear the floor by this margin before it counts as
        // headroom (mirrors StaggerBudgetController's restore gate).
        public const double DefaultFloorHystFps = 4.0;

        private readonly bool _enabled;
        private readonly int _maxLevel;
        private readonly double _budgetMs;
        private readonly double _deadbandFrac;
        private readonly double _demoteDwellSeconds;
        private readonly double _promoteDwellSeconds;
        private readonly double _demoteCooldownSeconds;
        private readonly double _minKspFps; // 0 = floor disabled
        private readonly double _floorHystFps;

        private int _level;
        private double _lastDemoteTime = double.NegativeInfinity;
        private bool _headroomTracking;
        private double _headroomSince;

        public AdaptiveQualityController(
            bool enabled,
            int maxLevel,
            double budgetMs,
            double minKspFps = 0.0,
            double deadbandFrac = DefaultDeadbandFrac,
            double demoteDwellSeconds = DefaultDemoteDwellSeconds,
            double promoteDwellSeconds = DefaultPromoteDwellSeconds,
            double demoteCooldownSeconds = DefaultDemoteCooldownSeconds,
            double floorHystFps = DefaultFloorHystFps)
        {
            _enabled = enabled;
            _maxLevel = maxLevel < 0 ? 0 : maxLevel;
            _budgetMs = Math.Max(0.1, budgetMs);
            _deadbandFrac = Math.Max(0.0, deadbandFrac);
            _demoteDwellSeconds = Math.Max(0.0, demoteDwellSeconds);
            _promoteDwellSeconds = Math.Max(0.0, promoteDwellSeconds);
            _demoteCooldownSeconds = Math.Max(0.0, demoteCooldownSeconds);
            _minKspFps = Math.Max(0.0, minKspFps);
            _floorHystFps = Math.Max(0.0, floorHystFps);
        }

        /// <summary>Current quality level. 0 = the user-configured quality;
        /// higher = more shed (see KerbcamCamera.ShedTable).</summary>
        public int Level => _level;

        /// <summary>Why the level last changed. Set only when Evaluate moves
        /// the level, for the caller's log line; allocates only on a change.</summary>
        public string LastChangeReason { get; private set; }

        /// <summary>
        /// Update the quality level from the stagger regulator's signals.
        /// </summary>
        /// <param name="kerbcamFrameMs">EMA of kerbcam's own main-thread cost
        /// per frame (the same signal the stagger controller regulates).</param>
        /// <param name="staggerBudget">The stagger controller's current budget
        /// (cameras permitted per tick), BEFORE the MaxCaptureFps rate cap:
        /// the rate cap is user config, not load.</param>
        /// <param name="camCount">Streaming cameras (the stagger budget's
        /// ceiling).</param>
        /// <param name="gameFps">Rolling game fps, for the physics floor.
        /// Ignored when minKspFps is 0.</param>
        /// <param name="now">Monotonic seconds clock (unscaled time).</param>
        public int Evaluate(double kerbcamFrameMs, int staggerBudget, int camCount, double gameFps, double now)
        {
            // Disabled: never evaluates, never moves. Level stays 0.
            if (!_enabled) return _level;

            // Nothing streaming, or no cost signal yet: hold, and require a
            // fresh headroom stretch once signals return.
            if (camCount <= 0 || kerbcamFrameMs <= 0.0)
            {
                ResetHeadroom();
                return _level;
            }

            double over = _budgetMs * (1.0 + _deadbandFrac);
            double under = _budgetMs * (1.0 - _deadbandFrac);
            bool floorOn = _minKspFps > 0.0 && gameFps > 0.0;
            bool belowFloor = floorOn && gameFps < _minKspFps;
            bool aboveFloor = !floorOn || gameFps >= _minKspFps + _floorHystFps;

            // DEMOTE, checked first so a spike always beats a pending promote.
            // Trigger only when temporal degrade is exhausted: the stagger
            // budget sits at its one-camera floor and we are STILL over the ms
            // budget or under the fps floor.
            if (staggerBudget <= 1 && (kerbcamFrameMs > over || belowFloor))
            {
                ResetHeadroom();
                if (_level < _maxLevel && now - _lastDemoteTime >= _demoteDwellSeconds)
                {
                    _level++;
                    _lastDemoteTime = now;
                    LastChangeReason = kerbcamFrameMs > over
                        ? "demote: over ms budget with stagger at floor"
                        : "demote: below KSP fps floor with stagger at floor";
                }
                return _level;
            }

            // PROMOTE candidate: quality to restore, temporal capacity fully
            // recovered (budget back at the camera count), comfortably under
            // the ms budget, and fps clear of the floor plus hysteresis.
            bool headroom = _level > 0
                && staggerBudget >= camCount
                && kerbcamFrameMs < under
                && aboveFloor;
            if (!headroom)
            {
                ResetHeadroom();
                return _level;
            }

            if (!_headroomTracking)
            {
                _headroomTracking = true;
                _headroomSince = now;
            }
            if (now - _headroomSince < _promoteDwellSeconds) return _level;
            if (now - _lastDemoteTime < _demoteCooldownSeconds) return _level;

            _level--;
            LastChangeReason = "promote: sustained headroom";
            // One step at a time: the next promote needs its own unbroken
            // headroom stretch at the new (more expensive) level.
            ResetHeadroom();
            return _level;
        }

        private void ResetHeadroom()
        {
            _headroomTracking = false;
        }
    }
}
