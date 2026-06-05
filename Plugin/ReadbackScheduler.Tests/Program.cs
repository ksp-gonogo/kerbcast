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

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
