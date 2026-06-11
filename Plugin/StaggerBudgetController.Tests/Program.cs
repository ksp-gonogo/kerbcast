/* Unit test for StaggerBudgetController, the frametime-budget capture-budget
   regulator. Verifies it converges the budget to fit a kerbcam-ms target,
   cuts proportionately on sustained overload (a single spike sample changes
   nothing), remembers recently failed levels so it cannot sawtooth back into
   them, restores slowly, respects a deadband, keeps the MinKspFps floor
   one-way, and is bounded by the camera count.

   Tests 10+ replay the 2026-06 Deck field defect: 8 Hullcam cameras at
   ~4.8ms/cam against MaxKerbcamFrameBudgetMs=24 sawtoothed 6 -> 2 -> 6 every
   ~20s instead of settling at the sustainable 5.

   Exit code 0 = pass, 1 = fail. */

using System;
using Kerbcam;

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

const double MsPerCam = 3.0; // measured on the Deck

/* Helper: simulate a steady scene where cost = budget * msPerCam, driving the
   controller until it settles. Returns the settled budget. */
int Settle(StaggerBudgetController c, int camCount, double msPerCam, double budgetMs, double maxT = 120.0)
{
    double dt = 0.25;
    int last = c.Budget;
    for (double t = 0.0; t < maxT; t += dt)
    {
        double cost = c.Budget * msPerCam;
        last = c.Evaluate(cost, msPerCam, camCount, 60.0, t); // gameFps high: floor inactive
    }
    return last;
}

// --- 1. Converges to the budget that fits the target. 12ms / 3ms = 4 cams. ---
{
    var c = new StaggerBudgetController(budgetMs: 12.0, initialBudget: 8);
    int b = Settle(c, 8, MsPerCam, 12.0);
    Check(b == 4, $"settles to 4 cams for a 12ms budget at 3ms/cam (got {b})");
}

// --- 2. A tighter budget settles lower; a looser one higher. ---
{
    var tight = new StaggerBudgetController(budgetMs: 6.0, initialBudget: 8);
    var loose = new StaggerBudgetController(budgetMs: 21.0, initialBudget: 1);
    int bt = Settle(tight, 8, MsPerCam, 6.0);
    int bl = Settle(loose, 8, MsPerCam, 21.0);
    Check(bt == 2, $"6ms budget -> 2 cams (got {bt})");
    /* Restoring up from 1, it stops at the first budget whose cost enters the
       15% deadband: 6 cams = 18ms >= 17.85ms lower edge. (7 would be dead-on
       21ms, but the deadband halts the climb one step early: conservative,
       stays at/under budget.) */
    Check(bl == 6, $"21ms budget -> 6 cams (deadband halts climb at the lower edge) (got {bl})");
}

// --- 3. Never exceeds the camera count, never drops below 1. ---
{
    var c = new StaggerBudgetController(budgetMs: 100.0, initialBudget: 1);
    int b = Settle(c, 5, MsPerCam, 100.0);
    Check(b == 5, $"huge budget clamps to camCount=5 (got {b})");
    var c2 = new StaggerBudgetController(budgetMs: 0.5, initialBudget: 8);
    int b2 = Settle(c2, 8, MsPerCam, 0.5);
    Check(b2 == 1, $"tiny budget floors at 1 (never freezes) (got {b2})");
}

/* --- 4. Cuts fast on SUSTAINED heavy overload (a full attack dwell over the
       large-overshoot line allows the jump straight to the fitting budget),
       restores slowly (one cam per release dwell). --- */
{
    var c = new StaggerBudgetController(budgetMs: 12.0, initialBudget: 8,
        attackDwellSeconds: 1.0, releaseDwellSeconds: 4.0);
    // overloaded: cost = 8*3 = 24ms >> 12ms (and > the 1.5x large line, 18ms).
    c.Evaluate(24.0, MsPerCam, 8, 60.0, 0.0);          // t=0 starts the overload clock
    int afterCut = c.Evaluate(24.0, MsPerCam, 8, 60.0, 1.0); // sustained one attack dwell
    Check(afterCut == 4, $"sustained heavy overload cuts straight to target (got {afterCut})");
    // now scene lightens (msPerCam drops to 1ms => 4 cams = 4ms < under-band).
    // Restores one cam at a time, gated by the 4s release dwell.
    int r1 = c.Evaluate(4.0 * 1.0, 1.0, 8, 60.0, 1.0 + 4.0);  // one release dwell
    Check(r1 == 5, $"restores one cam after a release dwell (got {r1})");
    int r2soon = c.Evaluate(5.0 * 1.0, 1.0, 8, 60.0, 1.0 + 4.0 + 1.0); // too soon
    Check(r2soon == 5, "no second restore inside the release dwell");
}

// --- 5. Deadband: cost within 15% of budget -> hold, no twitching. ---
{
    var c = new StaggerBudgetController(budgetMs: 12.0, initialBudget: 4,
        deadbandFrac: 0.15);
    // 4 cams * 3ms = 12ms, dead on target. Feed mild noise within the band (10.2..13.8).
    int prev = c.Budget, changes = 0;
    double dt = 0.25;
    var rngVals = new double[] { 12.0, 13.5, 10.5, 12.8, 11.2, 13.0, 10.4, 12.2 };
    for (int i = 0; i < 200; i++)
    {
        double cost = rngVals[i % rngVals.Length];
        int b = c.Evaluate(cost, MsPerCam, 8, 60.0, i * dt);
        if (b != prev) { changes++; prev = b; }
    }
    Check(changes == 0, $"holds budget steady inside the deadband, no hunting (changes={changes})");
}

// --- 6. No cost signal yet -> permits all cameras (don't stall feeds at start). ---
{
    var c = new StaggerBudgetController(budgetMs: 12.0, initialBudget: 1);
    int b = c.Evaluate(0.0, 0.0, 8, 60.0, 0.0);
    Check(b == 8, $"no measurement yet -> full budget (got {b})");
}

/* --- 7. Physics floor (one-way): cuts BELOW the ms-budget when game fps is
       under the floor, even though kerbcam is within its cost budget. --- */
{
    // Generous 100ms budget (cost never triggers a cut), floor 20fps, 8 cams.
    var c = new StaggerBudgetController(budgetMs: 100.0, initialBudget: 8,
        attackDwellSeconds: 0.0, minKspFps: 20.0, floorHystFps: 4.0);
    int b1 = c.Evaluate(24.0, MsPerCam, 8, 15.0, 0.0); // way under budget, fps < floor
    Check(b1 == 7, $"below floor steps budget down despite being under ms-budget (got {b1})");
    int b2 = c.Evaluate(21.0, MsPerCam, 8, 16.0, 1.0); // still below floor
    Check(b2 == 6, $"keeps cutting while still below the floor (got {b2})");
}

/* --- 8. Floor gates restore: won't add cameras back until fps clears
       floor + hysteresis, so it never relaxes into time-dilation. --- */
{
    var c = new StaggerBudgetController(budgetMs: 100.0, initialBudget: 4,
        attackDwellSeconds: 0.0, releaseDwellSeconds: 0.0, minKspFps: 20.0, floorHystFps: 4.0);
    int held = c.Evaluate(12.0, MsPerCam, 8, 21.0, 0.0); // under budget but fps < 24 (floor+hyst)
    Check(held == 4, $"restore blocked while fps below floor+hyst (got {held})");
    int up = c.Evaluate(12.0, MsPerCam, 8, 30.0, 1.0); // fps clears floor+hyst
    Check(up == 5, $"restore proceeds once fps clears floor+hyst (got {up})");
}

// --- 9. Floor disabled (minKspFps=0): pure ms-budget, ignores game fps. ---
{
    var c = new StaggerBudgetController(budgetMs: 100.0, initialBudget: 8,
        attackDwellSeconds: 0.0, minKspFps: 0.0);
    int b = c.Evaluate(24.0, MsPerCam, 8, 5.0, 0.0); // catastrophic fps, but no floor
    Check(b == 8, $"floor disabled -> ignores low fps, holds at budget (got {b})");
}

/* --- 10. FIELD TRACE: 8 cams at ~4.8ms/cam, 24ms budget. Must settle at the
       sustainable 5 within 30s simulated and then never move. The old
       controller sawtoothed 6 -> 2 -> 6 here with a ~20s period. --- */
{
    var c = new StaggerBudgetController(budgetMs: 24.0, initialBudget: 8);
    var sim = new FrameSim(c, 4.8, 8);
    int settled = -1;
    while (sim.T < 30.0) settled = sim.Step();
    Check(settled == 5, $"field load settles at 5/8 within 30s (got {settled})");
    int changes = 0, min = settled, max = settled, prev = settled;
    while (sim.T < 300.0)
    {
        int b = sim.Step();
        if (b != prev) { changes++; prev = b; }
        if (b < min) min = b;
        if (b > max) max = b;
    }
    Check(changes == 0, $"holds the settled budget for minutes, zero changes (changes={changes})");
    Check(min == 5 && max == 5, $"no sawtooth excursions (range {min}..{max})");
}

/* --- 11. Spike immunity: one 64.3ms frame at steady state (the field trace's
       GC hitch) must not move the budget. The spike decays out of the cost
       EMA far inside the attack dwell. --- */
{
    var c = new StaggerBudgetController(budgetMs: 24.0, initialBudget: 8);
    var sim = new FrameSim(c, 4.8, 8);
    while (sim.T < 20.0) sim.Step();
    int before = c.Budget;
    sim.Step(extraMs: 64.3 - before * 4.8); // one raw frame at ~64.3ms
    bool moved = false;
    while (sim.T < 50.0) { if (sim.Step() != before) moved = true; }
    Check(before == 5 && !moved, $"a single 64ms spike never changes the budget (before={before}, moved={moved})");
}

/* --- 12. Proportionate decrease: a SMALL sustained overshoot steps down one
       level, never fit-jumps. (The old controller jumped 6 -> 4 here, and
       with a polluted msPerCam crashed to 1-2 in the field.) --- */
{
    var c = new StaggerBudgetController(budgetMs: 24.0, initialBudget: 6);
    c.Evaluate(30.0, 5.0, 8, 60.0, 0.0);  // 25% over: outside deadband, below the 1.5x line
    c.Evaluate(30.0, 5.0, 8, 60.0, 0.5);
    int b = c.Evaluate(30.0, 5.0, 8, 60.0, 1.0); // sustained one attack dwell
    Check(b == 5, $"small sustained overshoot steps down exactly one (got {b})");
    Check(c.LastChangeReason != null && c.LastChangeReason.Contains("step down"),
        $"change reason recorded for the log line (got '{c.LastChangeReason}')");
}

/* --- 13. Cut needs SUSTAINED overload: over-band samples shorter than the
       attack dwell (signal back in band before the dwell elapses) cut nothing. --- */
{
    var c = new StaggerBudgetController(budgetMs: 24.0, initialBudget: 5);
    c.Evaluate(24.0, 4.8, 8, 60.0, 0.0);
    c.Evaluate(32.1, 6.4, 8, 60.0, 0.1);  // spike enters the EMA
    c.Evaluate(29.0, 5.8, 8, 60.0, 0.2);  // decaying
    c.Evaluate(26.0, 5.2, 8, 60.0, 0.3);  // back inside the band: clock resets
    int b = c.Evaluate(24.0, 4.8, 8, 60.0, 1.5);
    Check(b == 5, $"sub-dwell overload burst cuts nothing (got {b})");
}

/* --- 14. Failure memory: after backing off from level L, re-approaching L
       needs sustained continuous headroom beyond the base release dwell, and
       a repeat failure at L lengthens the next retry. --- */
{
    var c = new StaggerBudgetController(budgetMs: 24.0, initialBudget: 6);

    double RetryGap(double tStart)
    {
        // Sustained small overshoot at 6: one-step cut to 5, L=6 remembered.
        double t = tStart;
        while (c.Budget == 6 && t < tStart + 5.0) { c.Evaluate(30.0, 5.0, 8, 60.0, t); t += 0.1; }
        double cutAt = t - 0.1;
        // Continuous headroom from here; time until it retries 6.
        while (c.Budget == 5 && t < tStart + 80.0) { c.Evaluate(18.0, 3.6, 8, 60.0, t); t += 0.1; }
        return (t - 0.1) - cutAt;
    }

    double gap1 = RetryGap(0.0);
    Check(c.Budget == 6 && gap1 > 8.0 && gap1 < 16.0,
        $"first retry of a failed level needs ~release dwell + backoff of continuous headroom (gap {gap1:F1}s)");
    double gap2 = RetryGap(20.0);
    Check(c.Budget == 6 && gap2 > gap1 + 3.0,
        $"a repeat failure at the same level lengthens the next retry (gap1 {gap1:F1}s -> gap2 {gap2:F1}s)");
}

// --- 15. Failure memory expires: after the memory window, plain restore rules apply. ---
{
    var c = new StaggerBudgetController(budgetMs: 24.0, initialBudget: 6);
    c.Evaluate(30.0, 5.0, 8, 60.0, 0.0);
    c.Evaluate(30.0, 5.0, 8, 60.0, 1.0);   // cut to 5, L=6 remembered
    Check(c.Budget == 5 && c.RememberedFailLevel == 6,
        $"cut remembers the failed level (budget {c.Budget}, L={c.RememberedFailLevel})");
    // Sit in the deadband while the memory ages out (default 120s window).
    for (double t = 1.1; t < 124.0; t += 0.5) c.Evaluate(24.0, 4.8, 8, 60.0, t);
    int b = c.Evaluate(18.0, 3.6, 8, 60.0, 125.0); // first headroom sample after expiry
    Check(b == 6 && c.RememberedFailLevel == 0,
        $"expired memory restores on plain dwell rules again (budget {b}, L={c.RememberedFailLevel})");
}

/* --- 16. Memory clears on proven success, and the budget CAN fully restore
       to camCount under real headroom. This is the property the
       AdaptiveQuality promote gate (staggerBudget >= camCount) relies on. --- */
{
    var c = new StaggerBudgetController(budgetMs: 24.0, initialBudget: 6);
    c.Evaluate(30.0, 5.0, 8, 60.0, 0.0);
    c.Evaluate(30.0, 5.0, 8, 60.0, 1.0);   // cut to 5, L=6 remembered
    // Scene lightens for good: sustained headroom climbs back through 6 to 8.
    double t = 1.1;
    while (t < 45.0) { c.Evaluate(15.0, 3.0, 8, 60.0, t); t += 0.1; }
    Check(c.Budget == 8, $"sustained real headroom fully restores to camCount (got {c.Budget})");
    Check(c.RememberedFailLevel == 0,
        $"surviving at/above the failed level clears the memory (L={c.RememberedFailLevel})");
}

/* --- 17. Step-load changes: cameras removed then re-added adjust within a
       few cycles, without crashing to 1 and without overshooting the
       sustainable level. --- */
{
    var c = new StaggerBudgetController(budgetMs: 24.0, initialBudget: 8);
    var sim = new FrameSim(c, 4.8, 8);
    while (sim.T < 40.0) sim.Step();
    Check(c.Budget == 5, $"settled at 5/8 before the step load (got {c.Budget})");
    sim.CamCount = 4;                      // four cameras unsubscribe
    sim.Step();
    Check(c.Budget == 4, $"camCount drop clamps the budget immediately (got {c.Budget})");
    int min = c.Budget;
    while (sim.T < 60.0) { int b = sim.Step(); if (b < min) min = b; }
    Check(min == 4, $"holds 4/4 after the drop, no crash (min {min})");
    sim.CamCount = 8;                      // they come back
    while (sim.T < 70.0) { int b = sim.Step(); if (b < min) min = b; }
    Check(c.Budget == 5, $"re-adding cameras returns to the sustainable 5 within a few cycles (got {c.Budget})");
    int max = c.Budget;
    while (sim.T < 120.0) { int b = sim.Step(); if (b < min) min = b; if (b > max) max = b; }
    Check(min >= 4 && max <= 5, $"no crash and no over-climb through the transition (range {min}..{max})");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

/* Mirrors KerbcamCore's signal plumbing at 30Hz: raw per-frame cost from the
   budget actually applied last frame plus deterministic jitter, EMA alpha 0.2
   (KerbcamCore.LateUpdate), msPerCam = ema / captured. */
sealed class FrameSim
{
    public const double Dt = 1.0 / 30.0;
    private readonly StaggerBudgetController _c;
    private readonly double _msPerCamTrue;
    public int CamCount;
    public double T;
    private double _ema;
    private int _captured;
    private int _frame;

    public FrameSim(StaggerBudgetController c, double msPerCamTrue, int camCount)
    {
        _c = c;
        _msPerCamTrue = msPerCamTrue;
        CamCount = camCount;
        _captured = Math.Min(c.Budget, camCount);
    }

    public int Step(double extraMs = 0.0)
    {
        double jitter = 1.5 * Math.Sin(_frame * 0.7);
        double raw = _captured * _msPerCamTrue + jitter + extraMs;
        if (raw < 0.1) raw = 0.1;
        _ema = _ema <= 0.0 ? raw : _ema * 0.8 + raw * 0.2;
        double msPerCam = _ema / (_captured > 0 ? _captured : 1);
        int b = _c.Evaluate(_ema, msPerCam, CamCount, 60.0, T);
        _captured = Math.Min(b > 0 ? b : 1, CamCount);
        T += Dt;
        _frame++;
        return b;
    }
}
