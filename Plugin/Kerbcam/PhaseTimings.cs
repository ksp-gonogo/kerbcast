// Unity-free performance-telemetry accumulators (Recommendation 1 from the
// profiling study). Two pieces, both pure C# so they unit-test without KSP /
// Unity assemblies (see PhaseTimings.Tests):
//
//   PhaseTimings — per-camera, per-render-phase rolling stats. Refresh()
//     brackets each phase with Stopwatch.GetTimestamp() (allocation-free) and
//     calls Record(phase, ms). Each phase keeps a last value, an EMA (smooth
//     central tendency), and a rolling max (the spike catcher). The status
//     writer reads these once per second and aggregates across cameras.
//
//   GcTracker — global GC-collection counters. Once per frame KerbcamCore
//     feeds it GC.CollectionCount(0/1/2) + the frame's unscaledDeltaTime; the
//     tracker computes per-frame deltas and, over a status interval, records
//     how many collections of each generation happened and the worst frame
//     time that coincided with one. That coincidence is the proof a ~100ms
//     spike was Mono GC and not something else.
//
// The instrumentation path must never allocate (it's measuring GC, so it can't
// be a GC source). Everything here is structs / primitive fields — no boxing,
// no LINQ, no per-call allocation. Only the 1Hz status write (in KerbcamCore)
// builds strings, which the perf budget explicitly blesses.

namespace Kerbcam
{
    /// <summary>The distinct render phases timed inside KerbcamCamera.Refresh().
    /// Ordering matches the render sequence so the index doubles as a stable
    /// column order in the status JSON.</summary>
    internal enum RenderPhase
    {
        Galaxy = 0,   // _galaxyCam.Render()
        Scaled = 1,   // _scaledCam.Render() (+ fader override bookkeeping)
        Near = 2,     // BuildFxFrameState() + _fxHost.Render() + _nearCam.Render()
        Blit = 3,     // capture→readback Blit(es) + horizontal-flip correction
        Readback = 4, // Request() issue + ProcessReadback() drain (ring memcpy)
        Count = 5,
    }

    /// <summary>
    /// Per-camera rolling stats for each render phase. One instance per
    /// KerbcamCamera; entirely Unity-free so it unit-tests standalone. All
    /// methods are allocation-free.
    /// </summary>
    internal sealed class PhaseTimings
    {
        // Smoothing factor for the EMA: ema = ema + alpha*(sample-ema). 0.1 ≈ a
        // ~10-sample time constant, enough to ride out single-frame noise while
        // still tracking a sustained change within a status interval.
        private const double EmaAlpha = 0.1;

        private readonly double[] _last = new double[(int)RenderPhase.Count];
        private readonly double[] _ema = new double[(int)RenderPhase.Count];
        private readonly double[] _max = new double[(int)RenderPhase.Count];
        private bool _emaSeeded;

        /// <summary>Zero the per-frame Last values (EMA and rolling-max are
        /// preserved). Call once at the start of each capture frame, BEFORE any
        /// phase is recorded, so a phase that is shed this tick (e.g. galaxy
        /// dropped under adaptive shedding) genuinely reads Last == 0 rather than
        /// a stale figure from the last frame it rendered. Without this the
        /// across-camera Last sum would overstate cost while a layer is shed.</summary>
        public void BeginFrame()
        {
            for (int i = 0; i < _last.Length; i++) _last[i] = 0.0;
        }

        /// <summary>Record one phase sample (milliseconds). Called per camera,
        /// per frame, for each phase that actually ran this tick. A phase that is
        /// shed (e.g. galaxy dropped) simply isn't recorded; BeginFrame having
        /// zeroed Last first, its Last then reads 0 — the truth: it cost nothing
        /// this frame.</summary>
        public void Record(RenderPhase phase, double ms)
        {
            int i = (int)phase;
            _last[i] = ms;
            if (!_emaSeeded) _ema[i] = ms;
            else _ema[i] = _ema[i] + EmaAlpha * (ms - _ema[i]);
            if (ms > _max[i]) _max[i] = ms;
        }

        /// <summary>Marks the EMA seeded once a camera has produced at least one
        /// full set of samples. Call after a frame's phases are recorded so the
        /// first frame seeds the EMA to its real value rather than ramping from
        /// zero.</summary>
        public void FrameComplete()
        {
            _emaSeeded = true;
        }

        public double Last(RenderPhase phase) => _last[(int)phase];
        public double Ema(RenderPhase phase) => _ema[(int)phase];
        public double Max(RenderPhase phase) => _max[(int)phase];

        /// <summary>Clear the rolling max for every phase. Called by the status
        /// writer right after it reads the maxes, so each max reflects the spike
        /// peak within one status interval rather than the whole session.</summary>
        public void ResetMax()
        {
            for (int i = 0; i < _max.Length; i++) _max[i] = 0.0;
        }
    }

    /// <summary>
    /// Global GC-collection tracking. Fed GC.CollectionCount(0/1/2) once per
    /// frame plus that frame's unscaledDeltaTime (seconds). Tracks per-frame
    /// deltas and, over a status interval, accumulates how many collections of
    /// each generation occurred and the worst frame time that coincided with a
    /// collection — the spike-vs-GC correlation. Unity-free; a struct so it
    /// lives inline with no allocation.
    /// </summary>
    internal struct GcTracker
    {
        // Absolute counts as of the last Sample() call (CollectionCount is
        // monotonic for the process lifetime).
        private int _lastGen0;
        private int _lastGen1;
        private int _lastGen2;
        private bool _seeded;

        // Most recent per-frame deltas (gen-N collections that completed during
        // the last sampled frame).
        public int FrameDelta0 { get; private set; }
        public int FrameDelta1 { get; private set; }
        public int FrameDelta2 { get; private set; }

        // Accumulated over the current status interval (cleared by ResetInterval).
        public int IntervalGen0 { get; private set; }
        public int IntervalGen1 { get; private set; }
        public int IntervalGen2 { get; private set; }
        /// <summary>Worst unscaledDeltaTime (ms) seen on ANY frame this interval
        /// — the headline frametime spike, GC or not.</summary>
        public double WorstFrameMs { get; private set; }
        /// <summary>Worst unscaledDeltaTime (ms) seen on a frame that ALSO had a
        /// gen-0/1/2 collection complete. If this is close to WorstFrameMs, the
        /// spike is GC; if it's much lower, the spike has another cause.</summary>
        public double WorstGcFrameMs { get; private set; }
        /// <summary>True iff at least one collection of any generation coincided
        /// with the worst frame of the interval.</summary>
        public bool WorstFrameWasGc { get; private set; }

        /// <summary>
        /// Sample the absolute collection counts for this frame and fold the
        /// frame's wall-clock duration into the interval stats. Pass the raw
        /// GC.CollectionCount(gen) values and Time.unscaledDeltaTime (seconds).
        /// </summary>
        public void Sample(int gen0, int gen1, int gen2, double frameSeconds)
        {
            if (!_seeded)
            {
                // First call only seeds the baseline — no meaningful delta yet.
                _lastGen0 = gen0;
                _lastGen1 = gen1;
                _lastGen2 = gen2;
                _seeded = true;
                return;
            }

            FrameDelta0 = gen0 - _lastGen0;
            FrameDelta1 = gen1 - _lastGen1;
            FrameDelta2 = gen2 - _lastGen2;
            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;

            IntervalGen0 += FrameDelta0;
            IntervalGen1 += FrameDelta1;
            IntervalGen2 += FrameDelta2;

            double frameMs = frameSeconds * 1000.0;
            bool collected = (FrameDelta0 | FrameDelta1 | FrameDelta2) != 0;
            if (frameMs > WorstFrameMs)
            {
                WorstFrameMs = frameMs;
                WorstFrameWasGc = collected;
            }
            if (collected && frameMs > WorstGcFrameMs)
            {
                WorstGcFrameMs = frameMs;
            }
        }

        /// <summary>Clear the interval accumulators (call right after the status
        /// write reads them). The absolute-count baseline is preserved so the
        /// next frame's delta stays correct across the reset.</summary>
        public void ResetInterval()
        {
            IntervalGen0 = 0;
            IntervalGen1 = 0;
            IntervalGen2 = 0;
            WorstFrameMs = 0.0;
            WorstGcFrameMs = 0.0;
            WorstFrameWasGc = false;
        }
    }
}
