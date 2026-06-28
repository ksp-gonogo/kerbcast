// Unit test for AdaptiveQualityController, the opt-in quality ladder layered
// on the stagger budget regulator. Asserts the hard contract: flag off means
// no movement at all, promote only after sustained headroom, a demote always
// beats a pending promote, the post-demote cooldown blocks re-promotes, and
// the ladder never promotes past level 0 (the user-configured quality).
//
// Budget numbers mirror the StaggerBudgetController tests: 12ms budget,
// 15% deadband (over > 13.8ms, under < 10.2ms), 18fps floor + 4fps hysteresis
// (headroom needs >= 22fps). Dwells: demote 3s, promote 30s, cooldown 60s.
//
// Exit code 0 = pass, 1 = fail.

using System;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

const double BudgetMs = 12.0;
const int MaxLevel = 3;
const int Cams = 4;

AdaptiveQualityController Make(bool enabled) => new AdaptiveQualityController(
    enabled: enabled,
    maxLevel: MaxLevel,
    budgetMs: BudgetMs,
    minKspFps: 18.0,
    deadbandFrac: 0.15,
    demoteDwellSeconds: 3.0,
    promoteDwellSeconds: 30.0,
    demoteCooldownSeconds: 60.0,
    floorHystFps: 4.0);

// Overloaded frame: stagger pinned at its 1-camera floor, way over budget.
int Overload(AdaptiveQualityController c, double t) =>
    c.Evaluate(20.0, staggerBudget: 1, camCount: Cams, gameFps: 60.0, now: t);

// Quiet frame: stagger fully restored, comfortably under budget, fps high.
int Headroom(AdaptiveQualityController c, double t) =>
    c.Evaluate(8.0, staggerBudget: Cams, camCount: Cams, gameFps: 60.0, now: t);

// --- 1. Flag off: no movement, ever (neither demote nor promote). ---
{
    var c = Make(enabled: false);
    bool moved = false;
    for (double t = 0.0; t < 300.0; t += 1.0)
    {
        int l = t < 150.0 ? Overload(c, t) : Headroom(c, t);
        if (l != 0) moved = true;
    }
    Check(!moved, "disabled controller holds level 0 through overload and headroom");
}

// --- 2. Demote engages only when staggering is exhausted. Over budget with
//        stagger NOT at its floor is the stagger controller's job. ---
{
    var c = Make(enabled: true);
    int l = c.Evaluate(20.0, staggerBudget: 3, camCount: Cams, gameFps: 60.0, now: 0.0);
    Check(l == 0, $"over budget with stagger above its floor: no quality demote (got {l})");
    l = Overload(c, 1.0);
    Check(l == 1, $"over budget with stagger at the floor: demote (got {l})");
}

// --- 3. Immediate demote on a spike: the first overloaded evaluate moves,
//        no dwell to wait out, and it also beats a nearly-complete promote
//        window (demote wins over a pending promote). ---
{
    var c = Make(enabled: true);
    int l = Overload(c, 0.0);
    Check(l == 1, $"first spike demotes on the spot (got {l})");

    // Build headroom to just short of the promote dwell (28s of the 30s),
    // then spike. The demote must land on that very evaluate.
    for (double t = 1.0; t <= 29.0; t += 1.0) Headroom(c, t);
    int spiked = Overload(c, 30.0);
    Check(spiked == 2, $"spike wins over a nearly-complete promote window (got {spiked})");
}

// --- 4. Promote needs SUSTAINED headroom: a broken stretch resets the clock. ---
{
    var c = Make(enabled: true);
    Overload(c, 0.0); // level 1, lastDemote = 0
    // Alternate 20s of headroom with a single busy (but not overloaded) frame:
    // within the deadband, so no demote, but the headroom clock must reset.
    bool promoted = false;
    double t = 1.0;
    for (int cycle = 0; cycle < 10; cycle++)
    {
        for (int i = 0; i < 20; i++, t += 1.0)
            if (Headroom(c, t) == 0) promoted = true;
        if (c.Evaluate(12.0, staggerBudget: Cams, camCount: Cams, gameFps: 60.0, now: t) == 0) promoted = true;
        t += 1.0;
    }
    Check(!promoted, "interrupted headroom (19s stretches) never promotes");
    // Now an unbroken stretch: promote lands once 30s of headroom accrue
    // (cooldown long expired by t > 210).
    double start = t;
    int level = 1;
    for (int i = 0; i <= 30; i++, t += 1.0) level = Headroom(c, t);
    Check(level == 0, $"unbroken 30s headroom stretch promotes (got {level})");
    Check(t - start <= 32.0, "promote landed within one step of the dwell");
}

// --- 5. Cooldown after a demote blocks re-promote even with perfect headroom. ---
{
    var c = Make(enabled: true);
    Overload(c, 0.0); // level 1, lastDemote = 0
    // Perfect headroom from t=1: the 30s promote dwell is satisfied from t=31,
    // but the 60s cooldown must hold the promote back until t=60.
    bool early = false;
    int final = 1;
    for (double t = 1.0; t <= 60.0; t += 1.0)
    {
        final = Headroom(c, t);
        if (t < 60.0 && final == 0) early = true;
    }
    Check(!early, "no promote inside the 60s post-demote cooldown");
    Check(final == 0, $"promote lands once the cooldown expires (got {final})");
}

// --- 6. Ceilings both ways: demotes stop at maxLevel; promotes stop at the
//        user-configured level 0 and stay there. One step per stretch. ---
{
    var c = Make(enabled: true);
    // Sustained overload: levels step 0->1->2->3 spaced by the 3s demote
    // dwell, then hold at maxLevel.
    double t = 0.0;
    for (; t <= 30.0; t += 1.0) Overload(c, t);
    Check(c.Level == MaxLevel, $"sustained overload stops at maxLevel={MaxLevel} (got {c.Level})");

    // Sustained headroom: promotes one level per 30s stretch (cooldown expired
    // after the first wait), down to 0, never below.
    int min = int.MaxValue;
    int changes = 0;
    int prev = c.Level;
    for (; t <= 400.0; t += 1.0)
    {
        int l = Headroom(c, t);
        if (l != prev) { changes++; prev = l; }
        if (l < min) min = l;
    }
    Check(min == 0, $"promotes walk back to level 0 (got min {min})");
    Check(c.Level == 0, $"holds at level 0 under continued headroom (got {c.Level})");
    Check(changes == MaxLevel, $"one level per sustained stretch ({MaxLevel} promotes, got {changes})");
}

// --- 7. Physics floor: below MinKspFps with stagger floored demotes even when
//        kerbcast itself is under budget; headroom needs floor + hysteresis. ---
{
    var c = Make(enabled: true);
    int l = c.Evaluate(8.0, staggerBudget: 1, camCount: Cams, gameFps: 15.0, now: 0.0);
    Check(l == 1, $"below the fps floor demotes despite low kerbcast cost (got {l})");
    // fps recovers to 20 (above floor 18, below floor+hyst 22): not headroom.
    bool promoted = false;
    for (double t = 1.0; t <= 200.0; t += 1.0)
        if (c.Evaluate(8.0, staggerBudget: Cams, camCount: Cams, gameFps: 20.0, now: t) == 0) promoted = true;
    Check(!promoted, "fps inside the hysteresis band never counts as headroom");
}

// --- 8. No signal: nothing streaming or no cost measurement holds the level. ---
{
    var c = Make(enabled: true);
    Overload(c, 0.0);
    int l1 = c.Evaluate(8.0, staggerBudget: 0, camCount: 0, gameFps: 60.0, now: 10.0);
    int l2 = c.Evaluate(0.0, staggerBudget: Cams, camCount: Cams, gameFps: 60.0, now: 11.0);
    Check(l1 == 1 && l2 == 1, $"no streams / no cost signal holds the level (got {l1}, {l2})");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
