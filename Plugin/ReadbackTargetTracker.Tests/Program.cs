// Unit test for ReadbackTargetTracker: the property F2's correctness depends on
// is that a resolution change *between* issuing a readback and processing it
// must not change the dimensions/scratch the process step sees. The scratch is
// modelled as an int id (Texture2D in production).
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
    var t = new ReadbackTargetTracker<int>();
    t.SetCurrent(768, 432, scratch: 1);   // pooled set A is current
    t.CapturePending();                   // readback issued against A
    t.SetCurrent(1024, 576, scratch: 2);  // adaptive shed switches to set B
    // ProcessReadback must still see A's dimensions + scratch:
    Check(t.PendingWidth == 768 && t.PendingHeight == 432 && t.PendingScratch == 1,
        "pending target survives a resolution change before processing");
    Check(t.CurrentWidth == 1024 && t.CurrentHeight == 576 && t.CurrentScratch == 2,
        "current target reflects the resize");
}

// --- Pending is a snapshot, not a live alias: re-capturing after a change
//     picks up the new current. ---
{
    var t = new ReadbackTargetTracker<int>();
    t.SetCurrent(512, 288, scratch: 7);
    t.CapturePending();
    Check(t.PendingScratch == 7, "first capture snapshots current scratch");
    t.SetCurrent(256, 144, scratch: 9);
    t.CapturePending();                   // next readback, after the switch
    Check(t.PendingWidth == 256 && t.PendingScratch == 9,
        "a later capture picks up the new current target");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
