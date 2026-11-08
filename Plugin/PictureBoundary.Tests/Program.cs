using System;
using System.Collections.Generic;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

bool Near(DVec3 a, DVec3 b, double tol) => (a - b).Magnitude <= tol;

/* ---- the boundary and its hysteresis ---- */
{
    const double c = 299792458;
    double b = PictureBoundary.BoundaryM(c);
    Check(Math.Abs(b - 4996541) < 1, $"at real light speed the boundary is one frame of light, about 5,000 km ({b:F0} m)");

    Check(!PictureBoundary.Hidden(false, 0.5 * b, c), "a craft well inside is live");
    Check(PictureBoundary.Hidden(false, 1.0001 * b, c), "a craft that reaches past c·τ is hidden at once");
    Check(!PictureBoundary.Hidden(false, 0.95 * b, c), "a craft between 0.9 c·τ and c·τ that was live stays live");
    Check(PictureBoundary.Hidden(true, 0.95 * b, c), "a craft between 0.9 c·τ and c·τ that was hidden stays hidden");
    Check(!PictureBoundary.Hidden(true, 0.89 * b, c), "a hidden craft comes back live inside 0.9 c·τ");
    Check(PictureBoundary.Hidden(false, b * 3, c), "a craft that loads already beyond c·τ starts hidden, never live for a frame");
    Check(PictureBoundary.Hidden(false, double.NaN, c) && PictureBoundary.Hidden(false, double.PositiveInfinity, c) && PictureBoundary.Hidden(false, -1, c),
        "a distance that cannot be read hides the craft");

    /* Swept in and out at many light speeds, a live craft is never beyond c·τ. */
    bool neverLiveBeyond = true, flips = true;
    var rng = new Random(7);
    foreach (double cs in new[] { 3.0, 300.0, 3e5, 3e8 })
    {
        double bb = PictureBoundary.BoundaryM(cs);
        bool hidden = false;
        int changes = 0;
        for (int i = 0; i < 20000; i++)
        {
            double d = bb * (0.8 + 0.3 * rng.NextDouble());
            bool was = hidden;
            hidden = PictureBoundary.Hidden(hidden, d, cs);
            if (!hidden && d > bb) neverLiveBeyond = false;
            if (hidden != was) changes++;
        }
        if (changes == 0) flips = false;
    }
    Check(neverLiveBeyond, "swept across the band at 3 m/s to real light speed, no live craft is ever beyond c·τ");
    Check(flips, "the sweep crossed both ways at every speed");

    /* A crossing does not flicker: jitter of a few percent about c·τ switches once. */
    bool hid = false;
    int switches = 0;
    for (int i = 0; i < 1000; i++)
    {
        double d = b * (1.0 + 0.02 * Math.Sin(i * 0.37)) + i * 10;
        bool was = hid;
        hid = PictureBoundary.Hidden(hid, d, c);
        if (hid != was) switches++;
    }
    Check(switches == 1, $"a craft drifting out through c·τ with 2% jitter switches once ({switches})");

    double lowest = PictureBoundary.BoundaryM(299792458 * 1e-8);
    Check(Math.Abs(lowest - 0.04997) < 1e-4, $"at a light-speed scale of 1e-8 the boundary is about 5 cm ({lowest:F4} m)");
}

/* ---- carrying a point from the live craft to its stand-in ---- */
{
    var livePivot = new DVec3(100, 0, 0);
    var drawnPivot = new DVec3(5000, 20, -30);
    var still = DQuat.Identity;
    var p = PictureBoundary.Carry(new DVec3(103, 1, 0), livePivot, still, drawnPivot, still);
    Check(Near(p, new DVec3(5003, 21, -30), 1e-9), "with no turn a point keeps its offset from the pivot");

    /* Live craft turned 90 degrees about y, drawn unturned: the offset turns back. */
    var quarter = DQuat.FromRotationVector(new DVec3(0, Math.PI / 2, 0));
    var liveOffset = quarter.Rotate(new DVec3(0, 0, 4));
    p = PictureBoundary.Carry(livePivot + liveOffset, livePivot, quarter, drawnPivot, still);
    Check(Near(p, drawnPivot + new DVec3(0, 0, 4), 1e-9), "an offset is carried through the turn between live and drawn attitudes");

    var turn = PictureBoundary.Turn(quarter, quarter);
    Check(Math.Abs(Math.Abs(turn.W) - 1) < 1e-12, "the same attitude live and drawn is no turn");
}

/* ---- parts and the centre of mass ---- */
{
    var com = new DVec3(0, 1.5, 0);
    var at = new DVec3(1e6, 0, 0);
    var q = DQuat.FromRotationVector(new DVec3(0, 0, Math.PI));
    Check(Near(PictureBoundary.PartAt(com, com, at, q), at, 1e-9), "the centre of mass sits where the craft is drawn");
    var nose = PictureBoundary.PartAt(new DVec3(0, 4, 0), com, at, q);
    Check(Near(nose, at + new DVec3(0, -2.5, 0), 1e-9), "a part sits at its offset from the centre of mass, turned with the craft");
}

/* ---- proxied stand-ins ---- */
{
    var eye = new DVec3(10, 20, 30);
    var near = eye + new DVec3(0, 0, 40000);
    var placed = PictureBoundary.Proxy(eye, near, out double s);
    Check(s == 1 && Near(placed, near, 0), "a stand-in within 50 km is drawn at its true place");

    var far = eye + new DVec3(3e6, 4e6, 0);
    placed = PictureBoundary.Proxy(eye, far, out s);
    double d = (placed - eye).Magnitude;
    var dirTrue = (far - eye).Normalized;
    var dirDrawn = (placed - eye).Normalized;
    Check(Math.Abs(d - PictureBoundary.ProxyM) < 1e-6, $"beyond it the stand-in is put at 50 km ({d:F3} m)");
    Check(Near(dirTrue, dirDrawn, 1e-12), "on the true ray");
    Check(Math.Abs(s - PictureBoundary.ProxyM / 5e6) < 1e-15, "shrunk so its angular size is unchanged");
}

/* ---- a hidden craft that leaves the scene is drawn until its light runs out ---- */
{
    var lingering = new Lingering();
    var into = new List<uint>();
    var scene = new HashSet<uint> { 1, 7, 8 };
    var hidden = new List<uint> { 7, 8 };
    lingering.Note(hidden);
    lingering.Collect(scene, default, into);
    Check(into.Count == 0, "while the hidden craft are loaded they are drawn from the hidden list, not twice");

    /* 7 docks into 8 and is no longer loaded: its light still shows it apart. */
    scene.Remove(7);
    hidden.Remove(7);
    lingering.Note(hidden);
    into.Clear();
    lingering.Collect(scene, default, into);
    Check(into.Count == 1 && into[0] == 7, "a hidden craft that docks away is still solved and drawn");

    /* Its part anchors resolve from its own past record, not the craft it docked into. */
    var owners = new PartOwners();
    foreach (uint part in new uint[] { 701, 702 }) owners.Add(part, 7);
    foreach (uint part in new uint[] { 801, 701, 702 }) owners.Add(part, 8);
    Check(owners.TryOwner(701, out uint craft) && craft == 7, "a part anchor on the docked-away craft is drawn on it while its light shows it apart");
    Check(owners.TryOwner(801, out craft) && craft == 8 && !owners.TryOwner(999, out _), "and other parts on their own craft, an unknown part on none");

    /* One unsettled solve does not drop it; MissesToDrop in a row from a real lens do. */
    lingering.Missed(7, scene);
    lingering.Seen(7);
    for (int i = 0; i < Lingering.MissesToDrop - 1; i++) lingering.Missed(7, scene);
    Check(lingering.Contains(7), "a solve that settles again between misses keeps it");
    lingering.Missed(7, scene);
    into.Clear();
    lingering.Collect(scene, default, into);
    Check(!into.Contains(7) && !lingering.Contains(7), $"it goes after {Lingering.MissesToDrop} frames in a row with no light (its last light has arrived)");

    /* 8 is destroyed but its rig draws it: the rig keeps it, the pass leaves it. */
    scene.Remove(8);
    hidden.Remove(8);
    into.Clear();
    lingering.Collect(scene, new RigDrawn(8, 0), into);
    Check(!into.Contains(8) && lingering.Contains(8), "a craft the rig draws is left to the rig but not forgotten");

    /* 8 reloads (a revert, or flying back into range): the scene draws it again. */
    scene.Add(8);
    into.Clear();
    lingering.Collect(scene, default, into);
    Check(!into.Contains(8) && lingering.Contains(8), "a craft back in the scene is drawn from there, and again here if it leaves");

    lingering.Note(new List<uint> { 1 });
    for (int i = 0; i < 10; i++) lingering.Missed(1, scene);
    Check(lingering.Contains(1), "a craft the scene draws is never dropped by a failed solve");

    /* A craft that split from 9 is drawn by the rig as 9 until the split's light arrives: the pass draws neither. */
    lingering.Note(new List<uint> { 9 });
    scene.Remove(9);
    into.Clear();
    lingering.Collect(scene, new RigDrawn(12, 9), into);
    Check(!into.Contains(9) && new RigDrawn(12, 9).Draws(12) && !new RigDrawn(12, 0).Draws(0),
        "a rig drawing its subject as the craft it split from leaves that craft to the rig too");

    /* S splits from G, then Q splits from G within the light delay: the rig draws S as G, and Q solves to G as well. */
    var rigG = new RigDrawn(21, 20);
    Check(rigG.Draws(22, 20) && !rigG.Draws(22, 22) && !rigG.Draws(22, 0), "a sibling drawn as the same parent is left to the rig, one drawn as itself is not");

    /* 9 unloads but flies on: the cull takes it over, so it is not solved every frame for good. */
    lingering.LeaveToCull(9);
    into.Clear();
    lingering.Collect(scene, default, into);
    Check(!into.Contains(9) && !lingering.Contains(9), "a hidden craft that unloads and flies on leaves the set");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
