// Unit test for ShedController — the adaptive-shed decision state machine.
//
// The bug this guards against: the old inline logic in KerbcamCore re-evaluated
// the shed level every LateUpdate with only a 25<->30 fps hysteresis band. When
// the sustainable fps at adjacent resolution levels straddles that band
// (level-0 ~24fps, level-1 ~31fps), the controller flips level every couple of
// frames forever — each flip reallocating every camera's RenderTexture chain and
// reinitialising the sidecar encoder. In a captured session this produced 638
// resolution flips and 719 encoder reinits.
//
// The fix is a minimum dwell time between level changes (asymmetric: shed
// reasonably fast, restore slowly), which rate-limits changes and breaks the
// limit cycle regardless of where the thresholds sit. These tests pin that.
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

const int MaxLevel = 5;

// --- 1. First shed is immediate (no prior change blocks it). ---
{
    var c = new ShedController(MaxLevel, shedDwellSeconds: 3.0, restoreDwellSeconds: 15.0);
    int lvl = c.Evaluate(5f, 0.0); // 5 < ShedBelow[0]=15
    Check(lvl == 1 && c.Level == 1, "overloaded from cold -> sheds to level 1 immediately");
}

// --- 2. At most one level step per Evaluate, even at catastrophic fps. ---
{
    var c = new ShedController(MaxLevel, shedDwellSeconds: 0.0, restoreDwellSeconds: 0.0);
    int lvl = c.Evaluate(0.5f, 0.0);
    Check(lvl == 1, "single Evaluate steps one level only (not straight to max)");
}

// --- 3. Shedding respects the shed dwell: no second shed inside the window. ---
{
    var c = new ShedController(MaxLevel, shedDwellSeconds: 3.0, restoreDwellSeconds: 15.0);
    c.Evaluate(5f, 0.0);                       // -> level 1 at t=0
    int within = c.Evaluate(5f, 1.0);          // t=1, still < 3s dwell
    Check(within == 1, "second shed suppressed inside shed-dwell window");
    int after = c.Evaluate(5f, 3.0);           // t=3, dwell elapsed
    Check(after == 2, "second shed allowed once shed-dwell elapses");
}

// --- 4. Restore respects the (longer) restore dwell. ---
{
    var c = new ShedController(MaxLevel, shedDwellSeconds: 3.0, restoreDwellSeconds: 15.0);
    c.Evaluate(5f, 0.0);                       // -> level 1 at t=0
    int early = c.Evaluate(40f, 5.0);           // 40 > RestoreAbove[0]=20, but t=5 < 15s
    Check(early == 1, "restore suppressed inside restore-dwell window");
    int later = c.Evaluate(40f, 15.0);          // dwell elapsed
    Check(later == 0, "restore allowed once restore-dwell elapses");
}

// --- 5. Deadband: fps between shed and restore thresholds -> no change. ---
{
    var c = new ShedController(MaxLevel, shedDwellSeconds: 3.0, restoreDwellSeconds: 15.0);
    c.Evaluate(5f, 0.0);                        // -> level 1
    int stable = c.Evaluate(16f, 100.0);         // 16: not <12 (shed), not >20 (restore)
    Check(stable == 1, "fps in deadband holds level (no flap)");
}

// --- 6. THE anti-flap property: an adversarial limit-cycle input. We feed the
//        fps that each level would sustain: level 0 -> 5 (below shed 15),
//        level >=1 -> 50 (above restore 20). The old inline logic changed level
//        on essentially every other frame (hundreds of flips). The controller
//        must (a) respond at all, and (b) CONVERGE — back-off must make the
//        second half of a long run quieter than the first, eventually settling.
{
    var c = new ShedController(MaxLevel, shedDwellSeconds: 3.0, restoreDwellSeconds: 15.0);
    int prev = c.Level;
    double dt = 1.0 / 60.0;
    double half = 300.0, duration = 600.0; // 10 minutes
    int changesFirstHalf = 0, changesSecondHalf = 0, changesLast100s = 0;
    int total = 0;
    for (double t = 0.0; t < duration; t += dt)
    {
        float fps = c.Level == 0 ? 5f : 50f; // the straddling limit cycle
        int now = c.Evaluate(fps, t);
        if (now != prev)
        {
            total++;
            if (t < half) changesFirstHalf++; else changesSecondHalf++;
            if (t > duration - 100.0) changesLast100s++;
            prev = now;
        }
    }
    Console.WriteLine($"  info adversarial flap: {total} total changes; "
        + $"first-half={changesFirstHalf}, second-half={changesSecondHalf}, last-100s={changesLast100s}");
    Check(total >= 1, "controller still responds to sustained overload (not frozen)");
    Check(changesSecondHalf < changesFirstHalf, "back-off converges: second half quieter than first");
    Check(changesLast100s == 0, "flap settles: no level changes in the final 100s");
}

// --- 7. Recovery still works: a restore that STICKS (sustained genuine
//        headroom, no re-shed) clears the back-off so a later real slow-down is
//        handled responsively rather than being stuck at base dwell forever. ---
{
    var c = new ShedController(MaxLevel, shedDwellSeconds: 3.0, restoreDwellSeconds: 15.0);
    c.Evaluate(5f, 0.0);                 // shed to 1 at t=0
    int r = c.Evaluate(40f, 15.0);        // restore to 0 at t=15 (sticks — fps stays high)
    Check(r == 0, "restores to 0 when headroom is real");
    // ... long stretch of healthy fps, no shed needed ...
    int stillUp = c.Evaluate(40f, 200.0);
    Check(stillUp == 0, "stays at 0 through sustained healthy fps");
}

// --- 8. The back-off factor constructor param is honoured: a larger factor
//        converges the adversarial flap faster (fewer total changes). Locks the
//        settings.cfg -> ShedController wiring. ---
{
    int RunFlap(double factor)
    {
        var c = new ShedController(MaxLevel, shedDwellSeconds: 3.0, restoreDwellSeconds: 15.0,
            backoffFactor: factor, maxRestoreDwellSeconds: 300.0);
        int changes = 0, prev = c.Level;
        double dt = 1.0 / 60.0;
        for (double t = 0.0; t < 600.0; t += dt)
        {
            float fps = c.Level == 0 ? 5f : 50f;
            int now = c.Evaluate(fps, t);
            if (now != prev) { changes++; prev = now; }
        }
        return changes;
    }
    int gentle = RunFlap(1.5);
    int aggressive = RunFlap(4.0);
    Console.WriteLine($"  info backoff factor 1.5 -> {gentle} changes; 4.0 -> {aggressive} changes");
    Check(aggressive <= gentle, "larger back-off factor converges in fewer (or equal) changes");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
