// Unit test for ReadbackTargetTracker: the property F2's correctness depends on
// is that a resolution change *between* issuing a readback and processing it
// must not change the dimensions the process step (ring write) sees.
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

// --- THE corruption-risk test: resize between CapturePending and reading it. ---
{
    var t = new ReadbackTargetTracker();
    t.SetCurrent(768, 432);   // pooled set A is current
    t.CapturePending();       // readback issued against A
    t.SetCurrent(1024, 576);  // adaptive shed switches to set B
    // The ring write must still use A's dimensions:
    Check(t.PendingWidth == 768 && t.PendingHeight == 432,
        "pending dimensions survive a resolution change before processing");
    Check(t.CurrentWidth == 1024 && t.CurrentHeight == 576,
        "current dimensions reflect the resize");
}

// --- Pending is a snapshot, not a live alias: re-capturing picks up current. ---
{
    var t = new ReadbackTargetTracker();
    t.SetCurrent(512, 288);
    t.CapturePending();
    Check(t.PendingWidth == 512, "first capture snapshots current");
    t.SetCurrent(256, 144);
    t.CapturePending();       // next readback, after the switch
    Check(t.PendingWidth == 256 && t.PendingHeight == 144,
        "a later capture picks up the new current size");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
