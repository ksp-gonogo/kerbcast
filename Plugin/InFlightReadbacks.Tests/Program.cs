// Unit tests for InFlightReadbacks and CaptureCounters.
//
// InFlightReadbacks: a resolution change *between* issuing a readback and
// draining it must not change the dimensions the drain (ring write) sees, and
// with two readbacks in flight each keeps its own. CaptureCounters: every
// streaming frame is accounted for once, and the report rates them per second.
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

// --- THE corruption-risk test: resize between issuing and draining. ---
{
    var t = new InFlightReadbacks(2);
    t.SetCurrent(768, 432);   // pooled set A is current
    int a = t.Issue(10);      // readback issued against A
    t.SetCurrent(1024, 576);  // adaptive shed switches to set B
    // The ring write must still use A's dimensions:
    Check(t.Width(a) == 768 && t.Height(a) == 432,
        "pending dimensions survive a resolution change before draining");
    Check(t.CurrentWidth == 1024 && t.CurrentHeight == 576,
        "current dimensions reflect the resize");
    int b = t.Issue(11);
    Check(t.Width(b) == 1024 && t.Height(b) == 576,
        "a readback issued after the switch carries the new size");
    Check(t.Width(a) == 768, "and the older one keeps its own");
}

// --- Oldest first, two in flight, slots reused around the ring. ---
{
    var t = new InFlightReadbacks(2);
    t.SetCurrent(640, 360);
    Check(t.Oldest == -1 && t.Count == 0 && !t.Full, "empty at the start");
    int a = t.Issue(1);
    int b = t.Issue(2);
    Check(t.Full && t.Count == 2, "full with two in flight");
    Check(t.Oldest == a && t.IssuedFrame(a) == 1, "the first issued is the oldest");
    bool threw = false;
    try { t.Issue(3); } catch (InvalidOperationException) { threw = true; }
    Check(threw, "a third readback is refused, never overwriting one in flight");
    t.Land();
    Check(t.Oldest == b && t.IssuedFrame(b) == 2 && !t.Full, "landing the oldest leaves the next");
    int c = t.Issue(4);
    Check(c == a, "the freed slot is reused");
    t.Land();
    Check(t.Oldest == c && t.IssuedFrame(c) == 4, "order holds across the wrap");
    t.Land();
    t.Land();
    Check(t.Count == 0 && t.Oldest == -1, "landing past empty stays empty");
}

// --- Only Land frees a slot, so a free slot seen after draining is still free
//     when the camera publishes, however its readbacks land meanwhile. ---
{
    var t = new InFlightReadbacks(2);
    t.Issue(1);
    t.Issue(2);
    Check(t.Full, "full after draining nothing: the camera skips rendering");
    _ = t.Oldest;
    _ = t.IssuedFrame(t.Oldest);
    Check(t.Full, "reading the queue never frees a slot");
    t.Land();
    bool free = !t.Full;
    Check(free && !t.Full, "after a drain lands one, the slot stays free until the next Issue");
    t.Issue(3);
    Check(t.Full, "and only that Issue fills it");
}

// --- A single slot behaves as the old one-in-flight invariant. ---
{
    var t = new InFlightReadbacks(1);
    t.Issue(0);
    Check(t.Full, "one slot is full after one readback");
}

// --- CaptureCounters: no report before the window, then rates per second. ---
{
    var c = new CaptureCounters();
    Check(c.TakeReport(100f, 7) == null, "no report for a camera that never streamed");
    float now = 0f;
    for (int i = 0; i < 250; i++)
    {
        now = i * 0.02f;
        c.Frame(now);
        switch (i % 5)
        {
            case 0: c.Grant(); c.Publish(i % 10 == 0 ? 1 : 0); c.Land(i % 10 == 0 ? 2 : 3); break;
            case 1: c.Miss(CaptureMiss.NotDue); break;
            case 2: c.Miss(CaptureMiss.ReadbackPending); break;
            case 3: c.Miss(CaptureMiss.SameUt); break;
            case 4: c.Grant(); break; // a render the camera skipped
        }
    }
    Check(c.TakeReport(4.9f, 7) == null, "no report before five seconds");
    string line = c.TakeReport(5f, 7);
    Check(line != null && line.StartsWith("[Kerbcast] cam=7 capture over 5.0 s at 50.0 game fps: captured 10.0/s"),
        "report names the camera, the window and the capture rate: " + line);
    Check(line != null && line.Contains("not due 10.0/s, same UT 10.0/s, no permit 0.0/s, readback pending 10.0/s, render skipped 10.0/s"),
        "every reason is a rate per second");
    Check(line != null && line.Contains("0/1/2/3/4/5+ frames: 0/0/25/25/0/0 (mean 2.50)"),
        "the latency histogram and mean");
    Check(line != null && line.EndsWith("issued with another in flight 50%"), "the overlap share");
    Check(c.Frames == 0 && c.Published == 0 && c.Landed == 0, "taking a report starts a new window");
    Check(c.TakeReport(20f, 7) == null, "the new window opens at its first frame, not at the last report");
}

// --- A camera that stops streaming drops its partial window. ---
{
    var c = new CaptureCounters();
    c.Frame(0f);
    c.Grant();
    c.Publish(0);
    c.Frame(30f);
    Check(c.Frames == 1 && c.Published == 0, "a gap over a second starts a fresh window");
    Check(c.TakeReport(34f, 1) == null, "timed from the frame after the gap");
}

// --- Latency clamps into the last bucket. ---
{
    var c = new CaptureCounters();
    c.Land(9);
    c.Land(-1);
    Check(c.LatencyCount(CaptureCounters.LatencyBuckets - 1) == 1 && c.LatencyCount(0) == 1,
        "long and impossible latencies land in the end buckets");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
