// Unit tests for PhaseTimings + GcTracker — the Unity-free perf-telemetry
// accumulators surfaced into global.status.json when EnableTelemetry=true.
//
// What these pin:
//   - Record() tracks last / EMA / rolling-max per phase correctly, and the
//     EMA seeds to the first sample rather than ramping from zero.
//   - ResetMax() clears the spike peak per status interval (last/EMA survive).
//   - GcTracker.Sample() computes per-frame deltas off the monotonic absolute
//     counts, accumulates per-interval gen deltas, and correlates the worst
//     frame time with whether a collection coincided (the GC-spike proof).
//
// Exit code 0 = pass, 1 = fail.

using System;
using Kerbcam;

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}
bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

// --- 1. Record: last + max track the raw samples; EMA seeds to first. ---
{
    var pt = new PhaseTimings();
    pt.Record(RenderPhase.Near, 2.0);
    pt.FrameComplete();
    Check(Near(pt.Last(RenderPhase.Near), 2.0), "Last == first sample");
    Check(Near(pt.Ema(RenderPhase.Near), 2.0), "EMA seeds to first sample (no zero-ramp)");
    Check(Near(pt.Max(RenderPhase.Near), 2.0), "Max == first sample");

    pt.Record(RenderPhase.Near, 4.0);
    pt.FrameComplete();
    Check(Near(pt.Last(RenderPhase.Near), 4.0), "Last == latest sample");
    Check(Near(pt.Max(RenderPhase.Near), 4.0), "Max rises to the larger sample");
    // EMA after second sample: 2 + 0.1*(4-2) = 2.2
    Check(Near(pt.Ema(RenderPhase.Near), 2.2), "EMA moves a fraction toward the new sample");

    pt.Record(RenderPhase.Near, 1.0);
    pt.FrameComplete();
    Check(Near(pt.Max(RenderPhase.Near), 4.0), "Max holds the peak through a lower sample");
    Check(Near(pt.Last(RenderPhase.Near), 1.0), "Last drops with the lower sample");
}

// --- 2. Phases are independent; an unrecorded phase reads zero. ---
{
    var pt = new PhaseTimings();
    pt.Record(RenderPhase.Galaxy, 5.0);
    pt.Record(RenderPhase.Scaled, 7.0);
    pt.FrameComplete();
    Check(Near(pt.Last(RenderPhase.Galaxy), 5.0), "Galaxy isolated");
    Check(Near(pt.Last(RenderPhase.Scaled), 7.0), "Scaled isolated");
    Check(Near(pt.Last(RenderPhase.Near), 0.0), "never-recorded Near reads zero");
}

// --- 2b. BeginFrame zeros Last so a phase shed THIS frame reads 0 (not the
//         stale last-rendered value) — the adaptive-shedding correctness case.
//         EMA and rolling-max survive the reset. ---
{
    var pt = new PhaseTimings();
    pt.Record(RenderPhase.Galaxy, 5.0);   // galaxy rendered last frame
    pt.FrameComplete();
    pt.BeginFrame();                        // new frame: galaxy is shed this tick
    Check(Near(pt.Last(RenderPhase.Galaxy), 0.0), "BeginFrame zeros Last for a shed phase (no stale value)");
    Check(Near(pt.Ema(RenderPhase.Galaxy), 5.0), "BeginFrame preserves EMA");
    Check(Near(pt.Max(RenderPhase.Galaxy), 5.0), "BeginFrame preserves rolling-max");
    // A phase that DOES render after BeginFrame records normally.
    pt.Record(RenderPhase.Scaled, 7.0);
    Check(Near(pt.Last(RenderPhase.Scaled), 7.0), "post-BeginFrame Record lands");
}

// --- 3. ResetMax clears the peak but leaves last / EMA intact. ---
{
    var pt = new PhaseTimings();
    pt.Record(RenderPhase.Blit, 3.0);
    pt.FrameComplete();
    pt.Record(RenderPhase.Blit, 9.0);
    pt.FrameComplete();
    Check(Near(pt.Max(RenderPhase.Blit), 9.0), "Max captured the interval peak");
    pt.ResetMax();
    Check(Near(pt.Max(RenderPhase.Blit), 0.0), "ResetMax clears the peak");
    Check(Near(pt.Last(RenderPhase.Blit), 9.0), "ResetMax leaves Last");
    Check(pt.Ema(RenderPhase.Blit) > 3.0, "ResetMax leaves EMA");
    // Next interval's max starts fresh from the new sample.
    pt.Record(RenderPhase.Blit, 2.0);
    pt.FrameComplete();
    Check(Near(pt.Max(RenderPhase.Blit), 2.0), "post-reset Max tracks the new interval");
}

// --- 4. GcTracker: first Sample only seeds the baseline (no phantom delta). ---
{
    var gc = new GcTracker();
    gc.Sample(100, 10, 1, 0.016);
    Check(gc.FrameDelta0 == 0 && gc.IntervalGen0 == 0, "first Sample seeds baseline, no delta");
}

// --- 5. GcTracker: per-frame deltas + interval accumulation. ---
{
    var gc = new GcTracker();
    gc.Sample(100, 10, 1, 0.016);       // seed
    gc.Sample(102, 10, 1, 0.016);       // +2 gen0
    Check(gc.FrameDelta0 == 2, "frame delta picks up 2 gen-0 collections");
    Check(gc.IntervalGen0 == 2, "interval accumulates gen-0");
    gc.Sample(103, 11, 1, 0.016);       // +1 gen0, +1 gen1
    Check(gc.FrameDelta0 == 1 && gc.FrameDelta1 == 1, "next frame delta (1 gen0, 1 gen1)");
    Check(gc.IntervalGen0 == 3 && gc.IntervalGen1 == 1, "interval accumulates across frames");
}

// --- 6. GcTracker: worst-frame / GC correlation (the spike-cause proof). ---
{
    var gc = new GcTracker();
    gc.Sample(0, 0, 0, 0.016);          // seed
    gc.Sample(0, 0, 0, 0.020);          // 20ms, no GC
    gc.Sample(1, 0, 0, 0.100);          // 100ms spike WITH a gen-0 collection
    gc.Sample(1, 0, 0, 0.018);          // 18ms, no GC
    Check(Near(gc.WorstFrameMs, 100.0), "worst frame is the 100ms spike");
    Check(gc.WorstFrameWasGc, "worst frame coincided with a collection -> GC-caused");
    Check(Near(gc.WorstGcFrameMs, 100.0), "worst GC-frame == the spike");
}

// --- 7. GcTracker: a non-GC spike is NOT attributed to GC. ---
{
    var gc = new GcTracker();
    gc.Sample(0, 0, 0, 0.016);          // seed
    gc.Sample(0, 0, 0, 0.120);          // 120ms spike, NO collection
    gc.Sample(1, 0, 0, 0.030);          // 30ms WITH a collection
    Check(Near(gc.WorstFrameMs, 120.0), "worst frame is the 120ms spike");
    Check(!gc.WorstFrameWasGc, "worst frame had no collection -> NOT GC-caused");
    Check(Near(gc.WorstGcFrameMs, 30.0), "worst GC-coinciding frame is the lesser 30ms one");
}

// --- 8. GcTracker.ResetInterval clears interval stats, keeps the baseline. ---
{
    var gc = new GcTracker();
    gc.Sample(50, 5, 0, 0.016);         // seed at non-zero baseline
    gc.Sample(53, 5, 0, 0.090);         // +3 gen0, 90ms GC frame
    Check(gc.IntervalGen0 == 3 && Near(gc.WorstFrameMs, 90.0), "interval populated");
    gc.ResetInterval();
    Check(gc.IntervalGen0 == 0 && Near(gc.WorstFrameMs, 0.0) && !gc.WorstFrameWasGc, "ResetInterval clears interval stats");
    // Baseline preserved: a +1 from 53 must read as delta 1, not 54.
    gc.Sample(54, 5, 0, 0.016);
    Check(gc.FrameDelta0 == 1 && gc.IntervalGen0 == 1, "baseline preserved across ResetInterval");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
