// Unit test for ReadbackScheduler: the budget formula and the round-robin
// permit windowing that staggers camera captures across frames.
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

// --- Budget formula. ---
Check(ReadbackScheduler.Budget(8, 30, 60) == 4, "8 cams, 30fps stream, 60fps game -> 4/frame");
Check(ReadbackScheduler.Budget(8, 30, 30) == 8, "game at stream fps -> no stagger (all 8)");
Check(ReadbackScheduler.Budget(8, 30, 15) == 8, "game below stream fps -> all 8 (can't stagger)");
Check(ReadbackScheduler.Budget(1, 30, 60) == 1, "single camera -> budget 1 (window == count)");
Check(ReadbackScheduler.Budget(0, 30, 60) == 0, "no cameras -> budget 0");
Check(ReadbackScheduler.Budget(8, 0, 60) == 8, "captureFps 0 (pacing off) -> all 8");

// --- Round-robin: contiguous window, advances, no starvation. ---
{
    var s = new ReadbackScheduler();
    var permit = new bool[8];
    // Tick 1: cams 0-3.
    s.NextTick(8, 4, permit);
    Check(permit[0] && permit[1] && permit[2] && permit[3]
        && !permit[4] && !permit[5] && !permit[6] && !permit[7],
        "tick 1 grants cams 0-3");
    // Tick 2: cams 4-7.
    s.NextTick(8, 4, permit);
    Check(!permit[0] && !permit[1] && !permit[2] && !permit[3]
        && permit[4] && permit[5] && permit[6] && permit[7],
        "tick 2 grants cams 4-7");
    // Tick 3: wraps back to 0-3.
    s.NextTick(8, 4, permit);
    Check(permit[0] && permit[3] && !permit[4], "tick 3 wraps back to 0-3");
}

// --- Over many ticks every camera is granted an equal share (fairness). ---
{
    var s = new ReadbackScheduler();
    int count = 7, budget = 2;   // 7 cams, budget 2: coprime, exercises wraps
    var grants = new int[count];
    var permit = new bool[count];
    int ticks = 7 * 20; // each camera should get budget*ticks/count turns
    for (int t = 0; t < ticks; t++)
    {
        s.NextTick(count, budget, permit);
        int granted = 0;
        for (int i = 0; i < count; i++) if (permit[i]) { grants[i]++; granted++; }
        if (granted != budget) { Check(false, "each tick grants exactly `budget`"); break; }
    }
    int min = int.MaxValue, max = 0;
    for (int i = 0; i < count; i++) { if (grants[i] < min) min = grants[i]; if (grants[i] > max) max = grants[i]; }
    Check(max - min <= 1, $"round-robin is fair across cameras (spread {min}..{max})");
}

// --- budget >= count grants everyone and resets the cursor. ---
{
    var s = new ReadbackScheduler();
    var permit = new bool[3];
    s.NextTick(3, 5, permit);
    Check(permit[0] && permit[1] && permit[2], "budget >= count grants all");
}

// --- Degrade budget: scales cuts up as the level rises, floored at 1. ---
Check(ReadbackScheduler.DegradeBudget(8, 0) == 8, "level 0 -> no temporal cut (all 8)");
Check(ReadbackScheduler.DegradeBudget(8, 1) < 8, "level 1 -> some cameras cut");
{
    int prev = 9;
    bool monotonic = true;
    for (int lvl = 0; lvl <= 5; lvl++)
    {
        int b = ReadbackScheduler.DegradeBudget(8, lvl);
        if (b > prev) monotonic = false;
        if (b < 1) monotonic = false;
        prev = b;
    }
    Check(monotonic, "degrade budget is monotonically non-increasing and never < 1 across levels");
}
Check(ReadbackScheduler.DegradeBudget(8, 99) >= 1, "level clamps past the cascade end (floored at 1)");

// --- EffectiveBudget = min(rate-cap, degrade). ---
// Healthy game above stream fps: rate-cap dominates, degrade (level 0) is full.
Check(ReadbackScheduler.EffectiveBudget(8, 20, 60, 0) == ReadbackScheduler.Budget(8, 20, 60),
    "healthy + level 0 -> just the rate-cap");
// Overloaded (game below stream fps so rate-cap = all) but degraded: the level cut wins.
Check(ReadbackScheduler.EffectiveBudget(8, 20, 12, 3) == ReadbackScheduler.DegradeBudget(8, 3),
    "overloaded + level 3 -> degrade budget dominates (temporal cut engages)");
Check(ReadbackScheduler.EffectiveBudget(8, 20, 12, 3) < 8,
    "overloaded + level 3 actually cuts below all-8");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
