/* StaggerBudgetController regulates the per-frame capture budget (how many
   cameras render + read back this tick) to hold kerbcam's OWN main-thread cost
   within a frametime budget. Deliberately Unity-free (same approach as
   ShedController / ReadbackScheduler) so the decision is unit-tested without KSP.

   Why a budget, not an fps target: cost per capturing camera (msPerCam) is
   roughly constant regardless of how many capture, so the budget that fits a
   target is a direct division. Targeting kerbcam's own cost (not game fps) is
   inherently KSP-independent, so there is no need to chase an fps the game
   cannot reach (the heavy-vessel failure mode). It is also a LOSSLESS temporal
   degrade: each camera still renders full resolution with all its layers, just
   less often.

   Control law (anti-sawtooth, from the 2026-06 Deck field trace where the old
   fit-jump-on-one-sample loop sawtoothed 6 -> 2 -> 6 every ~20s):
   - Cuts need SUSTAINED overload: the cost signal must sit over the band for a
     full attack dwell before any cut. A one-frame spike (a 64ms GC hitch)
     decays out of the caller's EMA well inside the dwell and changes nothing.
   - Cuts are proportionate: one step per decision. Only an overload that is
     LARGE (cost > budgetMs * (1 + largeOvershootFrac)) for the whole dwell may
     jump straight to the level that fits, so a genuine pile-up still recovers
     in one cut but a small overshoot can never crash the budget toward 1.
   - Failure memory: cutting away from level L remembers L as recently bad.
     Climbing back to or past L then needs CONTINUOUS under-budget headroom for
     the release dwell plus a penalty that grows with repeat failures at L and
     decays with time since the last one. Surviving at or above L clears the
     memory. The loop therefore pins at the sustainable level instead of
     re-probing a known-bad level every release dwell.
   - Restores stay one step per release dwell, gated by the fps floor.

   The MinKspFps physics floor stays one-way safety: below the floor it only
   TIGHTENS (one step per sustained attack dwell, fps is a noisy signal) and it
   gates restores until fps clears floor + hysteresis, so kerbcam never relaxes
   back into time-dilation. It never relaxes toward a target, so it cannot set
   up a headroom-chasing limit cycle. 0 disables it. */

using System;

namespace Kerbcam
{
    public sealed class StaggerBudgetController
    {
        private readonly double _budgetMs;
        private readonly double _deadbandFrac;        // tolerance band around the budget
        private readonly double _attackDwellSeconds;  // overload confirm window AND min seconds between cuts
        private readonly double _releaseDwellSeconds; // min seconds between (1-step) restores
        private readonly double _minKspFps;           // one-way physics floor, 0 = disabled
        private readonly double _floorHystFps;        // restore only once fps is this far above the floor
        private readonly double _largeOvershootFrac;  // overload above this may fit-jump instead of stepping
        private readonly double _failBackoffSeconds;  // extra headroom dwell per repeat failure
        private readonly double _failMemorySeconds;   // failure memory fades to nothing over this window

        private int _budget;
        private double _lastChangeTime = double.NegativeInfinity;

        /* Sustained-signal clocks: a decision only fires when its trigger has
           been continuously true since *Since. This is the spike filter: the
           caller's cost EMA sheds a one-frame spike in a handful of frames,
           far inside the attack dwell. */
        private bool _overTracking;
        private double _overSince;
        private bool _largeOverTracking;
        private double _largeOverSince;
        private bool _underTracking;
        private double _underSince;

        /* Failure memory: the lowest budget level that recently proved
           unsustainable, how many times in a row it failed, and when. Kept at
           the LOWEST recent failure because the penalty applies to any climb
           target at or above it. */
        private int _failLevel;
        private int _failStreak;
        private double _failTime;
        private bool _aboveFailTracking;
        private double _aboveFailSince;

        public const double DefaultBudgetMs = 12.0;
        public const double DefaultDeadbandFrac = 0.15;
        public const double DefaultAttackDwellSeconds = 1.0;
        public const double DefaultReleaseDwellSeconds = 4.0;
        public const double DefaultFloorHystFps = 4.0;
        public const double DefaultLargeOvershootFrac = 0.5;
        public const double DefaultFailBackoffSeconds = 8.0;
        public const double DefaultFailMemorySeconds = 120.0;
        /* Surviving at/above a failed level this long clears its memory. */
        private const double FailClearSeconds = 30.0;
        /* Penalty stops growing past this many repeat failures. */
        private const int MaxFailStreakPenalty = 5;

        public StaggerBudgetController(
            double budgetMs = DefaultBudgetMs,
            int initialBudget = 1,
            double deadbandFrac = DefaultDeadbandFrac,
            double attackDwellSeconds = DefaultAttackDwellSeconds,
            double releaseDwellSeconds = DefaultReleaseDwellSeconds,
            double minKspFps = 0.0,
            double floorHystFps = DefaultFloorHystFps,
            double largeOvershootFrac = DefaultLargeOvershootFrac,
            double failBackoffSeconds = DefaultFailBackoffSeconds,
            double failMemorySeconds = DefaultFailMemorySeconds)
        {
            _budgetMs = Math.Max(0.1, budgetMs);
            _deadbandFrac = Math.Max(0.0, deadbandFrac);
            _attackDwellSeconds = Math.Max(0.0, attackDwellSeconds);
            _releaseDwellSeconds = Math.Max(0.0, releaseDwellSeconds);
            _minKspFps = Math.Max(0.0, minKspFps);
            _floorHystFps = Math.Max(0.0, floorHystFps);
            _largeOvershootFrac = Math.Max(0.0, largeOvershootFrac);
            _failBackoffSeconds = Math.Max(0.0, failBackoffSeconds);
            _failMemorySeconds = Math.Max(1.0, failMemorySeconds);
            _budget = initialBudget < 1 ? 1 : initialBudget;
        }

        /// <summary>The current capture budget (cameras permitted per tick).</summary>
        public int Budget => _budget;

        /// <summary>The budget level currently remembered as recently failed
        /// (0 = none). Telemetry / test observability only.</summary>
        public int RememberedFailLevel => _failLevel;

        /// <summary>Why the budget last changed. Set only when Evaluate moves
        /// the budget, for the caller's log line; allocates only on a change.</summary>
        public string LastChangeReason { get; private set; }

        /// <summary>
        /// Update the budget from the measured per-camera cost.
        /// </summary>
        /// <param name="kerbcamFrameMs">Measured kerbcam main-thread cost this
        /// frame (the capture loop's wall-time, EMA-smoothed by the caller).</param>
        /// <param name="msPerCam">Cost per capturing camera (kerbcamFrameMs /
        /// cameras that actually captured). Used only for the sustained
        /// large-overshoot fit-jump. If &lt;= 0 (nothing measured yet) the
        /// budget is left at camCount.</param>
        /// <param name="camCount">Cameras currently present (the budget ceiling).</param>
        /// <param name="gameFps">Current rolling game fps, for the physics-floor
        /// safety. Ignored when minKspFps is 0.</param>
        /// <param name="now">Monotonic seconds clock (unscaled time).</param>
        public int Evaluate(double kerbcamFrameMs, double msPerCam, int camCount, double gameFps, double now)
        {
            if (camCount <= 0) { _budget = 0; ResetSignalClocks(); return 0; }
            if (_budget > camCount) _budget = camCount;
            if (_budget < 1) _budget = 1;

            /* No cost signal yet: allow everything; the cost measurement on
               the next frames will pull it in. */
            if (kerbcamFrameMs <= 0.0 || msPerCam <= 0.0)
            {
                _budget = camCount;
                ResetSignalClocks();
                return _budget;
            }

            UpdateFailureMemory(now);

            double over = _budgetMs * (1.0 + _deadbandFrac);
            double under = _budgetMs * (1.0 - _deadbandFrac);
            double largeOver = _budgetMs * (1.0 + _largeOvershootFrac);
            /* Physics-floor safety (one-way). belowFloor adds a CUT trigger;
               aboveFloor is a precondition on RESTORE. When the floor is
               disabled (_minKspFps == 0) belowFloor is always false and
               aboveFloor always true: pure ms-budget behaviour. */
            bool floorOn = _minKspFps > 0.0 && gameFps > 0.0;
            bool belowFloor = floorOn && gameFps < _minKspFps;
            bool aboveFloor = !floorOn || gameFps >= _minKspFps + _floorHystFps;

            bool overTrigger = kerbcamFrameMs > over || belowFloor;
            bool largeOverTrigger = kerbcamFrameMs > largeOver;
            bool underTrigger = kerbcamFrameMs < under && aboveFloor;
            TrackSignal(overTrigger, now, ref _overTracking, ref _overSince);
            TrackSignal(largeOverTrigger, now, ref _largeOverTracking, ref _largeOverSince);
            TrackSignal(underTrigger, now, ref _underTracking, ref _underSince);

            /* CUT: only on overload sustained for a full attack dwell (the
               spike filter), paced one decision per dwell. One step normally;
               a sustained LARGE overshoot may jump to the budget that fits. */
            if (_budget > 1 && overTrigger)
            {
                bool confirmed = now - _overSince >= _attackDwellSeconds;
                bool paced = now - _lastChangeTime >= _attackDwellSeconds;
                if (confirmed && paced)
                {
                    int desired = _budget - 1;
                    bool fitJump = false;
                    if (largeOverTrigger && now - _largeOverSince >= _attackDwellSeconds)
                    {
                        int fit = (int)Math.Floor(_budgetMs / msPerCam);
                        if (fit < desired) { desired = fit; fitJump = true; }
                    }
                    if (desired < 1) desired = 1;
                    if (desired < _budget)
                    {
                        RecordFailure(_budget, now);
                        LastChangeReason = fitJump
                            ? "sustained heavy overload, fit to budget"
                            : (kerbcamFrameMs > over
                                ? $"over ms budget, step down (L={_budget} marked bad)"
                                : "below KSP fps floor, step down");
                        _budget = desired;
                        _lastChangeTime = now;
                        ResetSignalClocks();
                    }
                }
                return _budget;
            }

            /* RESTORE: one step per release dwell, only with room on BOTH
               counts (kerbcam under budget AND game fps comfortably above the
               floor). Re-approaching a remembered failed level additionally
               needs CONTINUOUS headroom for the penalty dwell, so the loop
               cannot re-climb into a known-bad level every release dwell. */
            if (_budget < camCount && underTrigger)
            {
                if (now - _lastChangeTime < _releaseDwellSeconds) return _budget;
                int target = _budget + 1;
                bool retry = _failLevel > 0 && target >= _failLevel;
                if (retry)
                {
                    double need = _releaseDwellSeconds + FailPenaltySeconds(now);
                    if (now - _underSince < need) return _budget;
                }
                _budget = target;
                _lastChangeTime = now;
                LastChangeReason = retry
                    ? $"sustained headroom, retry previously failed L={target}"
                    : "headroom, restore one";
                return _budget;
            }

            /* Within the deadband (and above the floor): hold. */
            return _budget;
        }

        private static void TrackSignal(bool active, double now, ref bool tracking, ref double since)
        {
            if (!active) { tracking = false; return; }
            if (!tracking) { tracking = true; since = now; }
        }

        private void ResetSignalClocks()
        {
            _overTracking = false;
            _largeOverTracking = false;
            _underTracking = false;
        }

        /* Remember the level a cut just abandoned. Failures at or below the
           remembered level take it over (repeat failures at the same level
           grow the streak); a failure above it keeps the lower memory, which
           is the binding one for any climb back through it. */
        private void RecordFailure(int fromLevel, double now)
        {
            if (_failLevel > 0 && fromLevel > _failLevel) return;
            _failStreak = fromLevel == _failLevel ? _failStreak + 1 : 1;
            _failLevel = fromLevel;
            _failTime = now;
            _aboveFailTracking = false;
        }

        /* Extra continuous-headroom seconds demanded before re-entering the
           remembered failed level: grows with the repeat streak, fades
           linearly to zero over the memory window. */
        private double FailPenaltySeconds(double now)
        {
            double decay = 1.0 - (now - _failTime) / _failMemorySeconds;
            if (decay <= 0.0) return 0.0;
            int streak = _failStreak < MaxFailStreakPenalty ? _failStreak : MaxFailStreakPenalty;
            return _failBackoffSeconds * streak * decay;
        }

        /* Forget a failure once it is stale, or once the budget has survived
           at/above the failed level long enough to prove it sustainable. */
        private void UpdateFailureMemory(double now)
        {
            if (_failLevel <= 0) return;
            if (now - _failTime >= _failMemorySeconds) { ClearFailureMemory(); return; }
            if (_budget >= _failLevel)
            {
                if (!_aboveFailTracking) { _aboveFailTracking = true; _aboveFailSince = now; }
                if (now - _aboveFailSince >= FailClearSeconds) ClearFailureMemory();
            }
            else
            {
                _aboveFailTracking = false;
            }
        }

        private void ClearFailureMemory()
        {
            _failLevel = 0;
            _failStreak = 0;
            _aboveFailTracking = false;
        }
    }
}
