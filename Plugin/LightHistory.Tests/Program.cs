using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}
bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;

const int Sun = 0, Kerbin = 1, Mun = 2;
const double MuSun = 1.1723328e18, MuKerbin = 3.5316e12, MuMun = 6.5138398e10;
var bodies = new[]
{
    new HistoryBody { Index = Sun, Parent = -1, Mu = MuSun, Radius = 2.616e8, SoiRadius = double.PositiveInfinity },
    new HistoryBody { Index = Kerbin, Parent = Sun, Mu = MuKerbin, Radius = 600000, SoiRadius = 84159286, MaxOrbitalSpeed = 9285 },
    new HistoryBody { Index = Mun, Parent = Kerbin, Mu = MuMun, Radius = 200000, SoiRadius = 2429559, MaxOrbitalSpeed = 543 },
};
double[] mus = { MuSun, MuKerbin, MuMun };

LightHistory NewRing()
{
    var h = new LightHistory { SystemDiameterM = 2.4e11 };
    h.SetBodies(bodies);
    h.SetSignalSpeed(299792458.0);
    return h;
}

HistoryState State(double ut, int body, DVec3 r, DVec3 v, DQuat q = default, DVec3 w = default) => new HistoryState
{
    Ut = ut, Body = body, Kind = HistoryKind.Conic, Position = r, Velocity = v,
    Attitude = q.W == 0 && q.X == 0 && q.Y == 0 && q.Z == 0 ? DQuat.Identity : q, AngularVelocity = w,
};

/* RK4 under the body's gravity plus a thrust acceleration along the velocity,
   the way the game's physics moves a loaded craft. */
void Step(ref DVec3 r, ref DVec3 v, double mu, double thrust, double dt)
{
    DVec3 Acc(DVec3 p, DVec3 u)
    {
        double m = p.Magnitude;
        var g = p * (-mu / (m * m * m));
        return thrust != 0 ? g + u.Normalized * thrust : g;
    }
    var k1v = Acc(r, v); var k1r = v;
    var k2v = Acc(r + k1r * (dt / 2), v + k1v * (dt / 2)); var k2r = v + k1v * (dt / 2);
    var k3v = Acc(r + k2r * (dt / 2), v + k2v * (dt / 2)); var k3r = v + k2v * (dt / 2);
    var k4v = Acc(r + k3r * dt, v + k3v * dt); var k4r = v + k3v * dt;
    r = r + (k1r + k2r * 2 + k3r * 2 + k4r) * (dt / 6);
    v = v + (k1v + k2v * 2 + k3v * 2 + k4v) * (dt / 6);
}

/* ---- Kepler reads ---- */
{
    double r0 = 700000, vc = Math.Sqrt(MuKerbin / r0), period = 2 * Math.PI * Math.Sqrt(r0 * r0 * r0 / MuKerbin);
    LightHistoryMath.Kepler(new DVec3(r0, 0, 0), new DVec3(0, vc, 0), MuKerbin, period / 4, out var r, out var v);
    Check(Near(r.X, 0, 1e-3) && Near(r.Y, r0, 1e-3) && Near(v.X, -vc, 1e-6), "a circular orbit turns a quarter in a quarter period");

    /* Against an independent mean-anomaly solve. */
    double e = 0.5, rp = 700000, a = rp / (1 - e), n = Math.Sqrt(MuKerbin / (a * a * a));
    double vp = Math.Sqrt(MuKerbin * (1 + e) / rp);
    double worst = 0;
    foreach (double t in new[] { 100.0, 1234.5, 5000, 100.3 * 2 * Math.PI / n, -777 })
    {
        double M = n * t, E = M;
        for (int i = 0; i < 50; i++) E -= (E - e * Math.Sin(E) - M) / (1 - e * Math.Cos(E));
        var expect = new DVec3(a * (Math.Cos(E) - e), a * Math.Sqrt(1 - e * e) * Math.Sin(E), 0);
        LightHistoryMath.Kepler(new DVec3(rp, 0, 0), new DVec3(0, vp, 0), MuKerbin, t, out r, out _);
        worst = Math.Max(worst, (r - expect).Magnitude);
    }
    Check(worst < 1e-3, $"an eccentric orbit matches Kepler's equation, forward, backward and after 100 periods (worst {worst:E1} m)");

    double eh = 1.5, ah = rp / (eh - 1), nh = Math.Sqrt(MuKerbin / (ah * ah * ah));
    double vph = Math.Sqrt(MuKerbin * (1 + eh) / rp);
    worst = 0;
    foreach (double t in new[] { 60.0, 3000, 40000, -2500 })
    {
        double M = nh * t, H = Math.Asinh(M / eh);
        for (int i = 0; i < 100; i++) H -= (eh * Math.Sinh(H) - H - M) / (eh * Math.Cosh(H) - 1);
        var expect = new DVec3(ah * (eh - Math.Cosh(H)), ah * Math.Sqrt(eh * eh - 1) * Math.Sinh(H), 0);
        LightHistoryMath.Kepler(new DVec3(rp, 0, 0), new DVec3(0, vph, 0), MuKerbin, t, out r, out _);
        worst = Math.Max(worst, (r - expect).Magnitude / expect.Magnitude);
    }
    Check(worst < 1e-9, $"a hyperbola matches the hyperbolic Kepler equation (worst relative {worst:E1})");

    LightHistoryMath.Kepler(new DVec3(rp, 0, 0), new DVec3(0, vp, 0), MuKerbin, 4321, out r, out v);
    LightHistoryMath.Kepler(r, v, MuKerbin, -4321, out var back, out _);
    Check((back - new DVec3(rp, 0, 0)).Magnitude < 1e-3, "propagating forward then back returns to the start");
}

/* ---- Hermite and rotations ---- */
{
    DVec3 P(double t) => new DVec3(1 + 2 * t + 3 * t * t + 4 * t * t * t, -t * t * t, 5);
    DVec3 V(double t) => new DVec3(2 + 6 * t + 12 * t * t, -3 * t * t, 0);
    LightHistoryMath.Hermite(P(0), V(0), P(2), V(2), 2, 0.7, out var p, out var v);
    Check((p - P(0.7)).Magnitude < 1e-9 && (v - V(0.7)).Magnitude < 1e-9, "Hermite is exact for a cubic path");

    var w = new DVec3(0, 0, 0.3);
    var turned = DQuat.FromRotationVector(w * 1e-6).Rotate(new DVec3(1, 0, 0));
    var expect = new DVec3(1, 0, 0) + DVec3.Cross(w, new DVec3(1, 0, 0)) * 1e-6;
    Check((turned - expect).Magnitude < 1e-12, "a rotation vector moves a point at w x p, as KSP's surface velocity does");
    var q0 = DQuat.FromRotationVector(new DVec3(0.3, -0.2, 0.9));
    var q1 = DQuat.FromRotationVector(new DVec3(0.01, 0.02, -0.03)) * q0;
    var rv = DQuat.RotationVector(q0, q1);
    Check((rv - new DVec3(0.01, 0.02, -0.03)).Magnitude < 1e-12, "the rotation vector between two attitudes is recovered");
}

/* ---- The entry is 96 bytes ---- */
Check(Unsafe.SizeOf<HistoryEntry>() == LightHistory.EntryBytes, $"an entry is {Unsafe.SizeOf<HistoryEntry>()} bytes, as accounted");
Check(Unsafe.SizeOf<PartRecord>() <= LightHistory.PartBytes, $"a part record is {Unsafe.SizeOf<PartRecord>()} bytes, within its accounting");

/* ---- Coasting and burning craft: entries, bytes and read accuracy ---- */
const double Tick = 0.02;
(long entries, long bytes, double worstErr, double rate) Fly(double coast1, double burn, double coast2, double thrust, bool spin)
{
    var h = NewRing();
    double r0 = 700000;
    var r = new DVec3(r0, 0, 0);
    var v = new DVec3(0, Math.Sqrt(MuKerbin / r0), 0);
    var w = spin ? new DVec3(0.02, 0, 0.05) : new DVec3(0, 0, 0);
    var truth = new List<(double ut, DVec3 r)>();
    double ut = 1000, end = ut + coast1 + burn + coast2;
    var q = DQuat.Identity;
    long maxIn1s = 0;
    var recent = new Queue<double>();
    while (ut <= end)
    {
        if (h.Sample(1, State(ut, Kerbin, r, v, q, w), gated: true, checkAttitude: true))
        {
            recent.Enqueue(ut);
            while (recent.Peek() < ut - 1) recent.Dequeue();
            maxIn1s = Math.Max(maxIn1s, recent.Count);
        }
        truth.Add((ut, r));
        double tRel = ut - 1000;
        double a = tRel >= coast1 && tRel < coast1 + burn ? thrust : 0;
        Step(ref r, ref v, MuKerbin, a, Tick);
        q = (DQuat.FromRotationVector(w * Tick) * q).Normalized;
        ut += Tick;
    }
    double worst = 0;
    foreach (var (tu, tr) in truth)
    {
        if (!h.TryStateAt(1, tu, out var s)) { worst = double.PositiveInfinity; break; }
        worst = Math.Max(worst, (s.Position - tr).Magnitude);
    }
    return (h.EntryTotal, h.Bytes, worst, maxIn1s);
}

var coast = Fly(3600, 0, 0, 0, spin: true);
Console.WriteLine($"     coasting, spinning loaded craft, 1 h: {coast.entries} entries, {coast.bytes} bytes, read error {coast.worstErr:F3} m");
Check(coast.entries <= 3, "a coasting craft adds next to nothing in an hour");
Check(coast.worstErr <= 1.0, "a coasting craft reads back within the 1 m tolerance");

var burnRun = Fly(120, 60, 120, 10, spin: false);
Console.WriteLine($"     coast 2 min, burn 60 s at 1 g, coast 2 min: {burnRun.entries} entries, {burnRun.bytes} bytes "
    + $"(about {burnRun.bytes / 60.0 / 1024:F2} KB per burning second), read error {burnRun.worstErr:F3} m");
Check(burnRun.worstErr <= 1.0, "a burn reads back within the 1 m tolerance by Hermite between entries");
Check(burnRun.rate <= 11, $"gated entries stay at about 10 Hz or less (peak {burnRun.rate} in a second)");

var hard = Fly(10, 30, 10, 200, spin: false);
Console.WriteLine($"     20 g burn: {hard.entries} entries, read error {hard.worstErr:F3} m, peak {hard.rate} a second");
Check(hard.rate <= 11, "a violent burn is still capped at 10 Hz");

/* ---- The gate, piece by piece ---- */
{
    var h = NewRing();
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    h.Sample(7, State(0, Kerbin, r, v), gated: false, checkAttitude: false);
    h.Sample(7, State(10, Kerbin, r * 2, v), gated: false, checkAttitude: false);
    Check(h.Track(7).EntryCount == 1, "a craft on rails adds nothing on its own between events");
    h.Sample(7, State(10.01, Mun, new DVec3(2e6, 0, 0), v), gated: false, checkAttitude: false);
    Check(h.Track(7).EntryCount == 3 && h.Track(7).Last.Reason == HistoryReason.SoiChange, "a change of body always adds an entry, even on rails and inside the rate cap, after the old body's closing one");
    Check(h.TryStateAt(7, 5, out var before) && before.Body == Kerbin, "before the SOI change the craft reads about Kerbin");
    Check(h.TryStateAt(7, 20, out var after) && after.Body == Mun, "after it, about the Mun");
    h.Record(7, State(30, Mun, new DVec3(2e6, 0, 0), v), HistoryReason.Load);
    Check(h.Track(7).Last.Reason == HistoryReason.Load, "load and unload add entries through Record");

    var g = NewRing();
    LightHistoryMath.Kepler(r, v, MuKerbin, 0.5, out var rp, out var vp);
    g.Sample(8, State(0, Kerbin, r, v), true, true);
    g.Sample(8, State(0.5, Kerbin, rp + new DVec3(0, 0, 0.9), vp), true, true);
    Check(g.Track(8).EntryCount == 1, "0.9 m off the prediction is inside the tolerance");
    g.Sample(8, State(0.52, Kerbin, rp + new DVec3(0, 0, 1.1), vp), true, true);
    Check(g.Track(8).EntryCount == 2, "1.1 m off adds an entry");
    LightHistoryMath.Kepler(r, v, MuKerbin, 1.0, out var r2, out var v2);
    var tilted = DQuat.FromRotationVector(new DVec3(0, 0.15 * Math.PI / 180, 0));
    var a = NewRing();
    a.Sample(9, State(0, Kerbin, r, v), true, true);
    a.Sample(9, State(1, Kerbin, r2, v2, tilted), true, true);
    Check(a.Track(9).EntryCount == 2, "turning 0.15 degrees off the predicted attitude adds an entry");
    var b = NewRing();
    b.Sample(9, State(0, Kerbin, r, v), true, false);
    b.Sample(9, State(1, Kerbin, r2, v2, tilted), true, false);
    Check(b.Track(9).EntryCount == 1, "a packed craft under Principia is gated on position only");
    var c = NewRing();
    c.Sample(9, State(0, Kerbin, r, v), true, true);
    c.Sample(9, State(0.05, Kerbin, r + new DVec3(0, 0, 50), v), true, true);
    Check(c.Track(9).EntryCount == 1, "nothing is added within 0.1 s of the last entry");
}

/* ---- A landed craft turns with its body and adds nothing while parked ---- */
{
    var h = NewRing();
    var spin = new DVec3(0, 0, 2 * Math.PI / 21549.425);
    var p0 = new DVec3(600000, 0, 0);
    var q0 = DQuat.Identity;
    long added = 0;
    for (double t = 0; t < 600; t += Tick)
    {
        var turn = DQuat.FromRotationVector(spin * t);
        var p = turn.Rotate(p0);
        var s = new HistoryState { Ut = t, Body = Kerbin, Kind = HistoryKind.Surface, Position = p, Velocity = DVec3.Cross(spin, p), Attitude = turn * q0, AngularVelocity = spin };
        if (h.Sample(3, s, true, true)) added++;
    }
    Check(added == 1, $"a parked landed craft adds only its first entry in ten minutes ({added})");
    var expect = DQuat.FromRotationVector(spin * 300).Rotate(p0);
    Check(h.TryStateAt(3, 300, out var mid) && (mid.Position - expect).Magnitude < 0.01, "and reads back on the turning surface");
}

/* ---- Identity: dock, undock, destroy, recover ---- */
PartRecord[] Parts(uint first, int count)
{
    var p = new PartRecord[count];
    for (int i = 0; i < count; i++) p[i] = new PartRecord { Pid = first + (uint)i, Name = "part", Px = 0, Py = i * 2f, Pz = 0, Qx = 0, Qy = 0, Qz = 0, Qw = 1, RadiusM = 1 };
    return p;
}
{
    var h = NewRing();
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    h.Sample(10, State(0, Kerbin, r, v), true, true);
    h.Sample(20, State(0, Kerbin, r + new DVec3(0, 0, 30), v), true, true);
    h.SetComposition(10, 0, Parts(100, 3), 0, 2, 0);
    h.SetComposition(20, 0, Parts(200, 2), 0, 1, 0);

    /* Dock: 20 into 10 at t = 50. */
    h.Merge(20, 10, 50);
    h.Merge(20, 10, 50);
    var merged = new List<PartRecord>(Parts(100, 3)); merged.AddRange(Parts(200, 2));
    h.SetComposition(10, 50, merged.ToArray(), 0, 3, 0);
    Check(h.TryStateAt(20, 49, out _), "the absorbed craft reads as itself before the dock");
    Check(!h.TryStateAt(20, 51, out _), "and not at all after it");
    var e20 = h.IdentityOf(20, 49);
    Check(e20[e20.Count - 1].Kind == IdentityKind.MergedInto && e20[e20.Count - 1].Other == 10 && h.Track(20).EndUt == 50, "it ends merged into the survivor");
    int absorbed = 0;
    foreach (var e in h.IdentityOf(10, 60)) if (e.Kind == IdentityKind.Absorbed && e.Other == 20) absorbed++;
    Check(absorbed == 1, "the survivor records the merge once, though KSP fires it twice");
    Check(h.CompositionAt(10, 49).Parts.Length == 3 && h.CompositionAt(10, 51).Parts.Length == 5, "the survivor's composition changes at the dock");
    Check(!h.SetComposition(10, 60, merged.ToArray(), 0, 3, 0), "an unchanged part set adds no composition");

    /* Undock: 30 comes off 10 at t = 80. */
    h.Split(10, 30, 80);
    h.SetComposition(10, 80, Parts(100, 3), 0, 2, 0);
    h.Sample(30, State(80.02, Kerbin, r + new DVec3(0, 0, 5), v), true, true);
    h.SetComposition(30, 80.02, Parts(200, 2), 0, 1, 0);
    var t30 = h.Track(30);
    Check(t30.ParentPid == 10 && t30.Identity[1].Kind == IdentityKind.SplitFrom, "the new craft's history starts with a link to its parent");
    Check(!h.TryStateAt(30, 79, out _), "before the split the new craft has no state of its own");
    Check(h.CompositionAt(10, 79).Parts.Length == 5, "its parts are inside the parent's composition before the split");
    bool off = false;
    foreach (var e in h.IdentityOf(10, 60)) off |= e.Kind == IdentityKind.SplitOff && e.Other == 30;
    Check(off, "the parent records what came off it");

    /* Part loss and destroy. */
    h.PartLost(10, 102, 90);
    h.PartLost(10, 102, 90);
    h.SetComposition(10, 90, Parts(100, 2), 0, 1, 0);
    Check(h.CompositionAt(10, 91).Parts.Length == 2, "part loss is a composition change");
    h.Destroyed(10, 100, h.Generation);
    Check(h.TryStateAt(10, 99.9, out _) && !h.TryStateAt(10, 100.1, out _), "a destroyed craft reads up to its end, for its ghost, and not after");
    Check(h.Track(10).Ended && h.Track(10).EndKind == IdentityKind.Destroyed, "the end says destroyed");
    h.Recovered(30, 120);
    Check(h.Track(30).EndKind == IdentityKind.Recovered, "a recovered craft ends recovered");
    int tracks = 0;
    foreach (var _ in h.Tracks) tracks++;
    Check(tracks == 3, "ended and merged-away craft stay in the craft set for the cull");

    h.Trim(100 + h.HorizonS + 1);
    Check(h.Track(10) == null && h.Track(20) == null && h.Track(30) != null, "an ended craft leaves the ring once its end is older than the horizon");
}

/* ---- Horizon and memory cap ---- */
{
    var h = NewRing();
    Check(Near(h.HorizonS, 2.4e11 / 299792458.0, 1e-6), $"the horizon is the system's diameter over the signal speed ({h.HorizonS:F0} s)");
    h.SetSignalSpeed(299792458.0 * 1e-3);
    Check(Near(h.HorizonS, 2.4e11 / 299792.458, 1e-3), "a slower signal lengthens it");
    h.SetSignalSpeed(299792458.0);

    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    h.Sample(1, State(0, Kerbin, r, v), true, true);
    for (int i = 1; i <= 2000; i++) h.Sample(1, State(i, Kerbin, r + new DVec3(0, 0, 5 * i), v), true, true);
    h.Trim(2000);
    var t = h.Track(1);
    Check(t.First.Ut <= h.FloorUt && t.Find(h.FloorUt, out _, out _), "trimming keeps an entry at or before the floor");
    Check(t.EntryCount < 1000, $"and drops whole chunks before it ({t.EntryCount} of 2001 entries kept)");
    Check(h.TryStateAt(1, h.FloorUt + 0.5, out _) && !h.TryStateAt(1, h.FloorUt - 1, out _), "reads stop at the floor");

    var m = NewRing();
    m.MemoryCapBytes = 1024 * 1024;
    m.SetSignalSpeed(1);
    for (int i = 0; i <= 20000; i++)
        for (uint k = 1; k <= 5; k++)
            m.Sample(k, State(i * 0.1, Kerbin, r + new DVec3(0, 0, 5 * i), v), true, true);
    Check(m.Bytes <= m.MemoryCapBytes, $"a ring far over its cap is held to it ({m.Bytes} of {m.MemoryCapBytes} bytes)");
    Check(m.FloorUt > 1000 && m.TryStateAt(3, 1999, out _), $"the oldest went first (floor {m.FloorUt:F0} s)");

    var inf = NewRing();
    inf.MemoryCapBytes = 50 * 1024;
    inf.SystemDiameterM = double.PositiveInfinity;
    for (int i = 0; i <= 5000; i++) inf.Sample(1, State(i * 0.1, Kerbin, r + new DVec3(0, 0, 5 * i), v), true, true);
    Check(inf.Bytes <= inf.MemoryCapBytes && double.IsPositiveInfinity(inf.HorizonS), "with no horizon the memory cap alone bounds the ring");
}

/* ---- Rewind ---- */
{
    var h = NewRing();
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    for (int i = 0; i <= 100; i++) h.Sample(1, State(i, Kerbin, r + new DVec3(0, 0, 5 * i), v), true, true);
    h.Destroyed(1, 100, h.Generation);
    h.Sample(2, State(80, Kerbin, r, v), true, true);
    h.Split(1, 2, 80);
    h.Rewind(50);
    Check(h.Track(1).Last.Ut <= 50 && !h.Track(1).Ended, "a quickload drops what came after and undoes the destroy");
    Check(h.Track(2) == null, "a craft that began after the load UT is gone");
}

/* ---- Maxima for the cull ---- */
{
    var h = NewRing();
    double rp = 700000, e = 0.3, a = rp / (1 - e);
    double vp = Math.Sqrt(MuKerbin * (1 + e) / rp);
    h.Sample(1, State(0, Kerbin, new DVec3(rp, 0, 0), new DVec3(0, vp, 0)), true, true);
    var s = h.Track(1).BodySpheres[0];
    Check(s.Body == Kerbin && Near(s.RadiusM, a * (1 + e), 1) && Near(s.SpeedMs, vp, 1e-6), "a closed orbit's sphere is its apoapsis, its speed the periapsis speed");
    Check(Near(h.MaxInertialSpeed(h.Track(1)), vp + 9285, 1e-6), "the inertial speed adds Kerbin's own bound");

    double vEsc = Math.Sqrt(2 * MuKerbin / rp) * 1.1;
    h.Sample(2, State(0, Kerbin, new DVec3(rp, 0, 0), new DVec3(0, vEsc, 0)), true, true);
    Check(h.Track(2).BodySpheres[0].RadiusM == 84159286, "an open orbit's sphere is the SOI");

    h.Sample(2, State(1, Mun, new DVec3(300000, 0, 0), new DVec3(0, 500, 0)), true, true);
    Check(h.Track(2).BodySpheres.Count == 2, "one sphere per body occupied");
    Check(h.MaxInertialSpeed(h.Track(2)) >= 500 + 543 + 9285, "the Mun's sphere adds the Mun's and Kerbin's bounds");

    /* A suborbital arc whose periapsis is deep inside the body: Kepler reads go there too. */
    var sr = new DVec3(650000, 0, 0); var sv = new DVec3(100, 300, 0);
    h.Sample(3, State(0, Kerbin, sr, sv), true, true);
    double energy = (100.0 * 100 + 300 * 300) / 2 - MuKerbin / 650000;
    double hh = 650000.0 * 300, ecc = Math.Sqrt(1 + 2 * energy * hh * hh / (MuKerbin * MuKerbin)), rper = hh * hh / (MuKerbin * (1 + ecc));
    Check(Near(h.Track(3).BodySpheres[0].SpeedMs, Math.Sqrt(2 * (energy + MuKerbin / rper)), 1e-6), "a suborbital arc's speed is bounded at its periapsis, below the surface");
    double fastest = 0;
    for (double t = 0; t < 4000; t += 1) { LightHistoryMath.Kepler(sr, sv, MuKerbin, t, out _, out var vt); fastest = Math.Max(fastest, vt.Magnitude); }
    Check(fastest <= h.Track(3).BodySpheres[0].SpeedMs + 1e-6, "no Kepler read of it is faster than the bound");

    h.SetComposition(1, 0, Parts(1, 10), 0, 9, 0);
    h.SetComposition(1, 5, Parts(1, 3), 0, 2, 0);
    Check(Near(h.Track(1).MaxSizeM, 10, 1e-6), "the size bound keeps the larger craft after it lost parts");
    h.Sample(1, State(10000, Kerbin, new DVec3(rp, 0, 0), new DVec3(0, vp + 50, 0)), true, true);
    h.Trim(10000 + h.HorizonS);
    Check(Near(h.Track(1).MaxSizeM, 3, 1e-6), "and shrinks once the larger craft is older than the horizon");
}

/* ---- Principia: on-rails craft through the tolerance gate, bodies in the ring ---- */
{
    var h = NewRing();
    double r0 = 700000;
    var r = new DVec3(r0, 0, 0); var v = new DVec3(0, Math.Sqrt(MuKerbin / r0), 0);
    long n = 0;
    for (double t = 0; t < 600; t += Tick)
    {
        if (h.Sample(1, State(t, Kerbin, r, v), gated: true, checkAttitude: false)) n++;
        /* A small third-body pull, as Principia's n-body integration adds. */
        Step(ref r, ref v, MuKerbin, 0, Tick);
        v = v + new DVec3(0, 0, 1e-4) * Tick;
    }
    Console.WriteLine($"     craft under a small n-body pull, 10 min: {n} entries");
    Check(n < 100, "a perturbed coast is sampled sparsely by the tolerance gate");

    var mr = new DVec3(12000000, 0, 0); var mv = new DVec3(0, 543, 0);
    h.SampleBody(Mun, State(0, Kerbin, mr, mv));
    long gen = h.Generation;
    h.Sample(1, State(700, Kerbin, r, v), true, true);
    h.SampleBody(Mun, State(699.98, Kerbin, mr, mv));
    Check(h.Generation == gen && h.TrackCount == 2, "a body sampled a tick back is not taken for a rewind");
    Check(h.TryBodyStateAt(Mun, 100, out var munAt) && munAt.Body == Kerbin, "a body's track reads like a craft's, about its parent");
    Check(h.BodySpeedBound(Mun) >= 543 + 9285, "a body's track feeds its speed bound");
}

/* ---- Ghosts: a relaunch under an id whose craft ended ---- */
{
    var h = NewRing();
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    h.Sample(5, State(0, Kerbin, r, v), true, true);
    h.Sample(6, State(0, Kerbin, r, v), true, true);
    h.Merge(5, 6, 50);
    h.Sample(5, State(80, Kerbin, r * 1.1, v), true, true);
    var live = h.Track(LightHistory.CraftKey(5));
    Check(live != null && !live.Ended && live.StartUt == 80, "a craft that appears under an ended id gets a fresh track");
    Check(h.TrackFor(5, 40).IsGhost && h.TryStateAt(h.TrackFor(5, 40), 40, out _), "the earlier life is a ghost, still readable before its end");
    Check(!h.TryStateAt(h.TrackFor(5, 60), 60, out _), "and nothing is drawn between the merge and the relaunch");
    var ghostEvents = h.TrackFor(5, 40).Identity;
    Check(ghostEvents[ghostEvents.Count - 1].Kind == IdentityKind.MergedInto, "the ghost keeps its identity events");

    h.Recovered(5, 120);
    h.Sample(5, State(150, Kerbin, r, v), true, true);
    int ghosts = 0;
    foreach (var t in h.Tracks) if (t.IsGhost && t.Pid == 5) ghosts++;
    Check(ghosts == 2 && h.TrackFor(5, 100).StartUt == 80, "launch, recover and relaunch: each life its own track");
    h.Rewind(100);
    Check(!h.Track(LightHistory.CraftKey(5)).IsGhost && h.Track(LightHistory.CraftKey(5)).StartUt == 80 && !h.Track(LightHistory.CraftKey(5)).Ended,
        "a rewind to before the relaunch brings the earlier life back as the live track");
}

/* ---- A rewind across a relaunch restores the earlier life intact ---- */
{
    var h = NewRing();
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    for (int i = 0; i < 1200; i++) h.Sample(1, State(i, Kerbin, r + new DVec3(0, 0, 5 * i), v), true, true);
    h.Destroyed(1, 1200, h.Generation);
    h.Sample(1, State(1300, Kerbin, r, v), true, true);
    h.Rewind(1250);
    for (int i = 0; i < 3000; i++) h.Sample(2, State(1250 + i, Kerbin, r + new DVec3(0, 0, -7 * i), v), true, true);
    var t = h.TrackFor(1, 100);
    bool clean = t != null && !t.IsGhost;
    int seen = 0;
    foreach (var c in t.Chunks)
        for (int i = 0; i < c.Count; i++, seen++)
            clean &= c.Items[i].Rz >= 0 && c.Items[i].Ut < 1201;
    Check(clean && seen == t.EntryCount, $"the restored track holds only its own entries ({seen}), none of another craft's");
}

/* ---- A destroy reported across a load ---- */
{
    var h = NewRing();
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    for (int i = 0; i <= 18; i++) h.Sample(1, State(i, Kerbin, r + new DVec3(0, 0, 5 * i), v), true, true);
    h.PartLost(1, 77, 18);
    long stale = h.Generation;
    h.Rewind(6);
    h.Destroyed(1, 17, stale);
    Check(!h.Track(1).Ended, "a destroy queued in the old timeline is dropped after the rewind");
    h.Destroyed(1, 6, h.Generation);
    bool partLossLeft = false;
    foreach (var e in h.IdentityOf(1, 6)) partLossLeft |= e.Kind == IdentityKind.PartLoss;
    Check(h.Track(1).EndUt == 6 && !partLossLeft, "one from the loaded timeline lands after it, with the old part loss gone");

    var g = NewRing();
    for (int i = 0; i <= 15; i++) g.Sample(2, State(i, Kerbin, r + new DVec3(0, 0, 5 * i), v), true, true);
    g.Destroyed(2, 15, g.Generation);
    g.Rewind(6.5);
    g.Destroyed(2, 6.5, g.Generation);
    Check(g.Track(2).Ended && g.Track(2).EndUt == 6.5, "a craft that ended later in the old timeline ends at its new destroy, not live forever");
}

/* ---- A load: craft the loaded game does not have end there ---- */
{
    var h = NewRing();
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    h.Sample(1, State(10, Kerbin, r, v), true, true);
    h.Sample(2, State(10, Kerbin, r, v), true, true);
    int ended = h.EndMissing(new HashSet<uint> { 1 }, 10);
    Check(ended == 1 && h.Track(2).EndKind == IdentityKind.Removed && !h.Track(1).Ended, "a craft missing from the loaded game ends as removed");
    h.Trim(10 + h.HorizonS + 1);
    Check(h.Track(2) == null, "and leaves once older than the horizon, instead of coasting forever");
}

/* ---- An SOI change under warp is stamped when it happened ---- */
{
    var h = NewRing();
    /* An escape from low Kerbin orbit; under warp the tick that sees the Mun's SOI comes late. */
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 3200, 0);
    h.Sample(1, State(0, Kerbin, r, v), false, false);
    LightHistoryMath.Kepler(r, v, MuKerbin, 20000, out var atExitR, out _);
    var atChange = State(20000, Mun, new DVec3(2.4e6, 0, 0), new DVec3(0, -300, 0));
    h.BodyChanged(1, atChange, 26000);
    var t = h.Track(1);
    Check(t.EntryCount == 3 && t.Last.Ut == 20000 && t.Last.Reason == HistoryReason.SoiChange, "the change is stamped at the patch start, not the late tick");
    Check(h.TryStateAt(1, 20500, out var after) && after.Body == Mun, "reads after it are about the new body");
    LightHistoryMath.Kepler(r, v, MuKerbin, 19999, out var at19999, out _);
    Check(h.TryStateAt(1, 19999, out var before) && before.Body == Kerbin && (before.Position - at19999).Magnitude < 1e-3, "reads before it are the old conic");
    double kerbinSphere = 0;
    foreach (var s in t.BodySpheres) if (s.Body == Kerbin) kerbinSphere = s.RadiusM;
    Check(kerbinSphere <= Math.Max(84159286, atExitR.Magnitude) + 1, "the old body's sphere covers only reads up to the change");
}

/* ---- A mutation earlier than anything seen is a rewind ---- */
{
    var h = NewRing();
    double rewoundTo = double.NaN;
    h.Rewound += ut => rewoundTo = ut;
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    for (int i = 0; i <= 100; i++) h.Sample(1, State(i, Kerbin, r + new DVec3(0, 0, 5 * i), v), true, true);
    h.Record(1, State(40, Kerbin, r, v), HistoryReason.Load);
    Check(rewoundTo == 40 && h.Track(1).Last.Ut == 40 && h.LatestUt == 40, "a load entry from before the latest UT rewinds first and stays sorted");
    h.Destroyed(1, 39.5, h.Generation);
    Check(rewoundTo == 40 && h.Track(1).EndUt == 39.5, "a deferred destroy a frame old is not a rewind");
}

/* ---- The cap does not thrash when the newest state alone is over it ---- */
{
    var h = NewRing();
    h.MemoryCapBytes = 200 * 1024;
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    for (uint k = 1; k <= 30; k++)
    {
        h.Sample(k, State(0, Kerbin, r, v), true, true);
        h.SetComposition(k, 0, Parts(1000 * k, 200), 0, 0, 0);
    }
    var sw = Stopwatch.StartNew();
    int samples = 0;
    for (int i = 1; i <= 2000; i++)
        for (uint k = 1; k <= 30; k++, samples++)
            h.Sample(k, State(i * 0.2, Kerbin, r + new DVec3(0, 0, 5 * i), v), true, true);
    double us = sw.Elapsed.TotalMilliseconds * 1000 / samples;
    Console.WriteLine($"     30 craft of 200 parts under a 200 KB cap: {us:F2} us per sample, floor {h.FloorUt:F1} of {h.LatestUt:F1}");
    Check(us < 20, "a cap the newest state alone exceeds does not spin trimming on every sample");
    Check(h.FloorUt < h.LatestUt && h.TryStateAt(1, h.LatestUt, out _), "the floor stops short of the newest instant");
}

/* ---- Chunks: trimming drops whole chunks and maxima follow ---- */
{
    var h = NewRing();
    var r = new DVec3(700000, 0, 0);
    double vEsc = Math.Sqrt(2 * MuKerbin / 700000) * 1.1;
    h.Sample(1, State(0, Kerbin, r, new DVec3(0, vEsc, 0)), true, true);
    var v = new DVec3(0, 2246, 0);
    for (int i = 1; i <= 3000; i++) h.Sample(1, State(i, Kerbin, r + new DVec3(0, 0, 5 * i), v), true, true);
    Check(h.Track(1).BodySpheres[0].RadiusM == 84159286, "an early open orbit sets the sphere to the SOI");
    h.Trim(3000 + h.HorizonS - 1);
    Check(h.Track(1).BodySpheres[0].RadiusM < 84159286, "once its chunk is older than the floor the sphere shrinks back");
}

/* ---- Surface spin in double precision ---- */
{
    var spin = new DVec3(0, 0, 2 * Math.PI / 21549.425);
    var e = new HistoryState { Ut = 0, Body = Kerbin, Kind = HistoryKind.Surface, Position = new DVec3(600000, 0, 0), Attitude = DQuat.Identity, AngularVelocity = spin }
        .ToEntry(HistoryReason.Start);
    double dt = 1e8;
    var p = LightHistoryMath.Predict(e, MuKerbin, dt).Position;
    double ang = Math.IEEERemainder(spin.Z * dt, 2 * Math.PI);
    var exact = new DVec3(600000 * Math.Cos(ang), 600000 * Math.Sin(ang), 0);
    Check((p - exact).Magnitude < 1, $"a landed craft read 1e8 s on is where the body's turn puts it ({(p - exact).Magnitude:F4} m)");
}

/* ---- The delay-off gate ---- */
{
    var h = new LightHistory { SystemDiameterM = 2.4e11 };
    h.SetBodies(bodies);
    Check(!h.Active, "with no holder and no signal speed the ring is off");
    h.SetReservationHeld(true);
    Check(h.Active && Near(h.HorizonS, 2.4e11 / 299792458.0, 1e-6), "a reservation turns it on; with no speed ever set the horizon uses real light speed");
    var r = new DVec3(700000, 0, 0); var v = new DVec3(0, 2246, 0);
    h.Sample(1, State(0, Kerbin, r, v), true, true);
    h.SetSignalSpeed(1e5);
    h.SetSignalSpeed(1e6);
    Check(h.TrackCount == 1, "setting a speed while already on keeps the history");
    h.SetSignalSpeed(null);
    Check(h.Active && Near(h.HorizonS, 2.4e11 / 1e5, 1e-6), "a reservation with no speed uses the slowest speed set in this game");
    h.SetReservationHeld(false);
    Check(!h.Active && h.TrackCount == 0, "releasing the reservation with no speed turns it off and empties it");
    h.SetSignalSpeed(3e8);
    Check(h.Active && h.TrackCount == 0 && double.IsNegativeInfinity(h.LatestUt), "turning on again starts from an empty ring");
    h.Clear();
    Check(h.SlowestSignalSpeed == 3e8, "a new game forgets the slowest speed of the last one");
}

/* ---- Per-tick cost ---- */
{
    var h = NewRing();
    double r0 = 700000;
    var r = new DVec3(r0, 0, 0); var v = new DVec3(0, Math.Sqrt(MuKerbin / r0), 0);
    var states = new HistoryState[20000];
    double t = 0;
    for (int i = 0; i < states.Length; i++)
    {
        states[i] = State(t, Kerbin, r, v);
        Step(ref r, ref v, MuKerbin, 0, Tick);
        t += Tick;
    }
    for (int i = 0; i < 2000; i++) h.Sample(1, states[i], true, true);
    var sw = Stopwatch.StartNew();
    for (int i = 2000; i < states.Length; i++) h.Sample(1, states[i], true, true);
    double perSample = sw.Elapsed.TotalMilliseconds * 1000 / (states.Length - 2000);
    sw.Restart();
    int reads = 0;
    for (int i = 0; i < 20000; i++) if (h.TryStateAt(1, (i % 18000) * Tick, out _)) reads++;
    double perRead = sw.Elapsed.TotalMilliseconds * 1000 / 20000;
    Console.WriteLine($"     cost on this machine: {perSample:F2} us per gated sample of a loaded craft, {perRead:F2} us per read");
    Check(perSample < 50 && reads > 0, "a gated sample costs microseconds");
}

/* ---- a craft's centre of mass moving with the same parts ---- */
{
    double r0 = 700000, vc = Math.Sqrt(MuKerbin / r0);
    var parts = new[] { new PartRecord { Pid = 10, Name = "a", Qw = 1, RadiusM = 1 }, new PartRecord { Pid = 11, Name = "b", Py = 2, Qw = 1, RadiusM = 1 } };
    var moving = NewRing();
    moving.Record(1, State(0, Kerbin, new DVec3(r0, 0, 0), new DVec3(0, vc, 0)), HistoryReason.Start);
    moving.SetComposition(1, 0, parts, 0, 0, 0);
    moving.SetComposition(1, 5, parts, 0, 0.05f, 0);
    float stayed = moving.CompositionAt(1, 10).ComY;
    long bytesBefore = moving.Bytes;
    moving.MoveCom(1, 10, 0, 2, 0);
    var moved = moving.CompositionAt(1, 10);
    var earlier = moving.CompositionAt(1, 7);
    Check(stayed == 0 && moved.ComY == 2 && Math.Abs(moved.SizeM - 3) < 1e-6 && earlier.ComY == 0 && ReferenceEquals(moved.Parts, earlier.Parts)
        && moving.Bytes - bytesBefore == LightHistory.CompositionBytes,
        "a centre of mass moved past the tolerance starts a record sharing the parts, and earlier instants keep theirs");
}

/* ---- the ring in a save ---- */
{
    double r0 = 700000, vc = Math.Sqrt(MuKerbin / r0);
    PartRecord[] SaveParts(params uint[] ids)
    {
        var parts = new PartRecord[ids.Length];
        for (int i = 0; i < ids.Length; i++)
            parts[i] = new PartRecord { Pid = ids[i], Name = "part" + (ids[i] % 3), Px = i, Qw = 1, RadiusM = 1.5f };
        return parts;
    }
    LightHistory Built()
    {
        var h = NewRing();
        h.Record(1, State(0, Kerbin, new DVec3(r0, 0, 0), new DVec3(0, vc, 0)), HistoryReason.Start);
        h.SetComposition(1, 0, SaveParts(10, 11, 12), 0.1f, 0.2f, 0.3f);
        h.Record(2, State(0, Kerbin, new DVec3(-r0, 0, 0), new DVec3(0, -vc, 0)), HistoryReason.Start);
        h.SetComposition(2, 0, SaveParts(20), 0, 0, 0);
        /* A burn on craft 1: an entry a tick. */
        DVec3 r = new DVec3(r0, 0, 0), v = new DVec3(0, vc, 0);
        for (int i = 1; i <= 300; i++)
        {
            Step(ref r, ref v, MuKerbin, 5, 0.1);
            h.Record(1, State(100 + i * 0.1, Kerbin, r, v), HistoryReason.Departure);
        }
        h.Split(1, 3, 140);
        h.Record(3, State(140, Kerbin, r + new DVec3(5, 0, 0), v), HistoryReason.Start);
        h.SetComposition(3, 140, SaveParts(12), 0, 0, 0);
        h.SetComposition(1, 140, SaveParts(10, 11), 0, 0, 0);
        h.Merge(2, 1, 150);
        h.Destroyed(3, 160, h.Generation);
        /* Craft 3's id comes back: the old life is a ghost. */
        h.Record(3, State(170, Kerbin, new DVec3(0, r0, 0), new DVec3(-vc, 0, 0)), HistoryReason.Start);
        h.MoveCom(1, 175, 0.1f, 1.2f, 0.3f);
        h.PartLost(1, 11, 180);
        h.SetComposition(1, 180, SaveParts(10), 0, 0, 0);
        h.Trim(200);
        return h;
    }

    bool Same(LightHistory a, LightHistory b, uint pid, double from, double to)
    {
        for (double ut = from; ut <= to; ut += 0.37)
        {
            bool ha = a.TryStateAt(pid, ut, out var sa), hb = b.TryStateAt(pid, ut, out var sb);
            if (ha != hb) return false;
            if (ha && ((sa.Position - sb.Position).Magnitude > 1e-6 || sa.Body != sb.Body)) return false;
            var ca = a.CompositionAt(pid, ut);
            var cb = b.CompositionAt(pid, ut);
            if ((ca == null) != (cb == null) || (ca != null && (!ca.SameParts(cb) || ca.SizeM != cb.SizeM))) return false;
        }
        return true;
    }

    var src = Built();
    var bytes = src.Save(200, 1 << 20, out double floor);
    var dst = new LightHistory { SystemDiameterM = 2.4e11 };
    dst.SetBodies(bodies);
    bool loaded = dst.Load(bytes, 200, out string err);
    Check(loaded && err == null && dst.TrackCount == src.TrackCount && dst.EntryTotal == src.EntryTotal,
        $"a save loads back whole ({dst.TrackCount} tracks, {dst.EntryTotal} entries, {bytes.Length} bytes)");
    Check(Same(src, dst, 1, 0, 200) && Same(src, dst, 2, 0, 200) && Same(src, dst, 3, 0, 200),
        "every craft reads the same states and parts at every instant, through the burn");
    var ghost = dst.TrackFor(3, 150);
    var live = dst.TrackFor(3, 180);
    Check(ghost != null && ghost.IsGhost && ghost.EndKind == IdentityKind.Destroyed && ghost.ParentPid == 1 && live != null && !live.IsGhost,
        "a ghost life comes back as a ghost, with its parent and its end");
    var merged = dst.Track(LightHistory.CraftKey(2));
    Check(merged != null && merged.Ended && merged.EndKind == IdentityKind.MergedInto && merged.EndUt == 150,
        "a merged craft keeps its end");
    int lossAt = -1;
    var events = dst.Track(LightHistory.CraftKey(1)).Identity;
    for (int i = 0; i < events.Count; i++) if (events[i].Kind == IdentityKind.PartLoss) lossAt = i;
    Check(lossAt >= 0 && events[lossAt].Other == 11 && dst.Bytes == src.Bytes, "identity events and the memory count come back");
    var comps = dst.Track(LightHistory.CraftKey(1)).Compositions;
    bool sharedBack = false;
    for (int i = 1; i < comps.Count; i++) sharedBack |= ReferenceEquals(comps[i].Parts, comps[i - 1].Parts) && comps[i].ComY != comps[i - 1].ComY;
    Check(sharedBack, "a record that only moved the centre of mass comes back sharing its parts");
    Check(dst.RestoredUt == 200 && dst.LatestUt == 200 && dst.FloorUt == src.FloorUt, "the loaded ring is marked as a save's, as of the load");

    /* Loaded earlier than the history runs: what came after is dropped. */
    var early = new LightHistory { SystemDiameterM = 2.4e11 };
    early.SetBodies(bodies);
    early.Load(bytes, 145, out _);
    Check(early.LatestUt == 145 && early.CompositionAt(1, 199).Parts.Length == 2
        && early.Track(LightHistory.CraftKey(2)).Ended == false && early.TrackFor(3, 145).IsGhost == false,
        "a load before the history's end drops what came after: no merge, no loss, no ghost");
    bool noLater = true;
    foreach (var t in early.Tracks)
        if (t.HasEntries && t.Last.Ut > 145) noLater = false;
    Check(noLater, "and no entry after the load UT is kept");

    /* The horizon: at real light speed with a 2.4e11 m system, about 800 s. */
    var old = NewRing();
    old.Record(5, State(0, Kerbin, new DVec3(r0, 0, 0), new DVec3(0, vc, 0)), HistoryReason.Start);
    for (int i = 1; i <= 20; i++) old.Record(5, State(i * 100, Kerbin, new DVec3(r0, i, 0), new DVec3(0, vc, 0)), HistoryReason.Departure);
    var horizonBytes = old.Save(2000, 1 << 20, out double hfloor);
    var back = new LightHistory { SystemDiameterM = 2.4e11 };
    back.SetBodies(bodies);
    back.Load(horizonBytes, 2000, out _);
    Check(Math.Abs(hfloor - (2000 - old.HorizonS)) < 1e-6 && !back.TryStateAt(5, hfloor - 1, out _) && back.TryStateAt(5, hfloor + 1, out _)
        && back.EntryTotal < old.EntryTotal,
        $"a save keeps the horizon back from its UT ({old.HorizonS:F0} s), and the entry a read at its floor starts from");

    /* The cap: the oldest instant kept rises until it fits. */
    int full = src.Save(200, 1 << 20, out _).Length;
    var capped = src.Save(200, full / 2, out double cfloor);
    Check(capped != null && capped.Length <= full / 2 && cfloor > floor, $"over the cap, a save keeps less ({capped?.Length} of {full} bytes, from UT {cfloor:F1})");
    Check(src.Save(200, 64, out _) == null, "under a cap even the newest instant cannot meet, nothing is saved");

    /* A save from before the history was saved, and a damaged one. */
    var none = NewRing();
    none.Record(9, State(0, Kerbin, new DVec3(r0, 0, 0), new DVec3(0, vc, 0)), HistoryReason.Start);
    bool ok = none.Load(null, 500, out err);
    Check(ok && none.TrackCount == 0 && none.LatestUt == 500 && none.RestoredUt == 500,
        "a save with no history starts the ring empty at the load: a distant craft shows only after its past instant passes it");
    var broken = (byte[])bytes.Clone();
    for (int i = 40; i < broken.Length; i += 7) broken[i] ^= 0x5a;
    var hurt = NewRing();
    ok = hurt.Load(broken, 200, out err);
    Check(!ok && err != null && hurt.TrackCount == 0, $"a damaged history is refused and the ring starts empty ({err})");
    ok = hurt.Load(new byte[] { 1, 2, 3 }, 200, out err);
    Check(!ok && hurt.TrackCount == 0, "and so is something that is not a history");

    /* A save's history survives the ring turning on, unless game time has passed. */
    var idle = new LightHistory { SystemDiameterM = 2.4e11 };
    idle.SetBodies(bodies);
    idle.Load(bytes, 200, out _);
    idle.SetSignalSpeed(3e5);
    Check(idle.Active && idle.TrackCount == src.TrackCount, "turning on just after a load keeps the save's history");
    idle.Sample(1, State(200.5, Kerbin, new DVec3(r0, 0, 0), new DVec3(0, vc, 0)), gated: true, checkAttitude: false);
    Check(idle.RestoredUt == null, "the first sample makes it the ring's own");
    var later = new LightHistory { SystemDiameterM = 2.4e11 };
    later.SetBodies(bodies);
    later.Load(bytes, 200, out _);
    later.DropRestored();
    later.SetSignalSpeed(3e5);
    Check(later.TrackCount == 0, "once game time passed with the ring off, it starts empty");

    /* The text a ConfigNode carries: cut at `//` as a comment, trimmed, split at the first `=`. */
    string ThroughConfigNode(string text)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length; i += 16 * 1024)
        {
            string line = "\t\td = " + text.Substring(i, Math.Min(16 * 1024, text.Length - i));
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line.Substring(0, comment);
            int eq = line.IndexOf('=');
            sb.Append(line.Substring(eq + 1).Trim());
        }
        return sb.ToString();
    }
    var plain = Convert.ToBase64String(bytes);
    var saveText = LightHistory.ToSaveText(bytes);
    var carried = LightHistory.FromSaveText(ThroughConfigNode(saveText));
    bool sameBytes = carried.Length == bytes.Length && LightHistory.Crc32(carried) == LightHistory.Crc32(bytes);
    for (int i = 0; sameBytes && i < bytes.Length; i++) sameBytes = carried[i] == bytes[i];
    Check(saveText.IndexOfAny(new[] { '/', '+', '=' }) < 0 && sameBytes,
        $"the save's text has no `/`, `+` or `=` and comes through a ConfigNode whole ({plain.Split(new[] { "//" }, StringSplitOptions.None).Length - 1} `//` in plain base64)");
    Check(LightHistory.Crc32(System.Text.Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926u, "the checksum is CRC-32");

    /* A save the ring wrote is skipped on the next load only while the ring still holds all of it. */
    var holder = Built();
    holder.Save(200, 1 << 20, out double holderFloor);
    long holderGen = holder.Generation;
    Check(holder.HoldsSave(holderGen, holderFloor), "a scene change after a save finds the ring still holding it");
    holder.SetSignalSpeed(null);
    Check(!holder.HoldsSave(holderGen, holderFloor), "quit to the menu, which clears the ring, then resume: the save is read back");
    var flown = Built();
    flown.Save(200, 1 << 20, out double flownFloor);
    long flownGen = flown.Generation;
    for (int i = 1; i <= 40; i++) flown.Record(2000 + i, State(200 + i * 100, Kerbin, new DVec3(0, r0, 0), new DVec3(-vc, 0, 0)), HistoryReason.Start);
    flown.Trim(4200);
    Check(flown.FloorUt > flownFloor && !flown.HoldsSave(flownGen, flownFloor), "flown past the horizon after the save: the save is read back");

    /* Damage of every kind is refused without throwing. */
    var rng = new Random(7);
    bool neverThrew = true, emptyOnError = true;
    int refused = 0;
    for (int i = 0; i < 3000; i++)
    {
        byte[] hurtBytes;
        if (i % 3 == 0) hurtBytes = bytes.AsSpan(0, rng.Next(bytes.Length)).ToArray();
        else
        {
            hurtBytes = (byte[])bytes.Clone();
            for (int k = 0, n = 1 + rng.Next(8); k < n; k++) hurtBytes[rng.Next(hurtBytes.Length)] ^= (byte)(1 << rng.Next(8));
        }
        var fuzz = NewRing();
        try
        {
            if (!fuzz.Load(hurtBytes, 200, out _)) { refused++; emptyOnError &= fuzz.TrackCount == 0; }
        }
        catch (Exception) { neverThrew = false; }
    }
    Check(neverThrew && emptyOnError, $"3000 damaged histories never throw out of a load ({refused} refused, each leaving the ring empty)");

    /* Over the cap the floor is chosen by size, and the ring written once. */
    bool exactSize = true;
    foreach (double f in new[] { double.NegativeInfinity, 0.0, 120.0, 140.0, 150.0, 175.0, 199.0 })
        exactSize &= src.SizeAt(f, new Dictionary<PartRecord[], long>()) == src.Write(f, 200).Length;
    Check(exactSize, "the sizing pass gives exactly the length a save at each floor writes");
    int writesBefore = src.SaveWrites;
    var oneWrite = src.Save(200, full / 3, out double oneFloor);
    Check(oneWrite != null && oneWrite.Length <= full / 3 && src.SaveWrites == writesBefore + 1,
        $"over the cap a save is written once, at a floor its sizing chose ({oneWrite?.Length} of {full / 3} bytes, from UT {oneFloor:F1})");

    /* Before the bodies are known there is no horizon: the ring's own floor stands. */
    var noBodies = Built();
    noBodies.SystemDiameterM = 0;
    noBodies.Save(200, 1 << 20, out double noBodiesFloor);
    Check(noBodiesFloor == noBodies.FloorUt, $"a save before the bodies are known keeps the ring's floor ({noBodiesFloor})");

    var slow = new LightHistory();
    slow.SetSignalSpeed(5e5);
    slow.RestoreSlowest(3e4);
    slow.RestoreSlowest(9e9);
    Check(slow.SlowestSignalSpeed == 3e4, "the save's slowest speed comes back unless a slower one is set");

    /* Size and load time for a busy game: 500 coasting craft of 30 parts, and 10 craft burning at 1 g through the whole horizon, sampled every physics tick. */
    var busy = NewRing();
    for (uint k = 0; k < 500; k++)
    {
        busy.Record(100 + k, State(0, Kerbin, new DVec3(r0 + k * 1000, 0, 0), new DVec3(0, vc, 0)), HistoryReason.Start);
        var ids = new uint[30];
        for (int i = 0; i < ids.Length; i++) ids[i] = 100000 + k * 100 + (uint)i;
        busy.SetComposition(100 + k, 0, SaveParts(ids), 0, 0, 0);
    }
    double horizon = busy.HorizonS;
    for (uint k = 0; k < 10; k++)
    {
        DVec3 r = new DVec3(r0, k * 1e4, 0), v = new DVec3(0, vc, 0);
        busy.Record(1000 + k, State(0, Kerbin, r, v), HistoryReason.Start);
        for (int i = 1; i <= horizon * 50; i++)
        {
            Step(ref r, ref v, MuKerbin, 9.8, 0.02);
            busy.Sample(1000 + k, State(i * 0.02, Kerbin, r, v), gated: true, checkAttitude: false);
        }
    }
    const int CapChars = 4 * 1024 * 1024;
    var sw = Stopwatch.StartNew();
    var big = busy.Save(horizon, CapChars / 4 * 3, out double bigFloor);
    string text = LightHistory.ToSaveText(big);
    int plainCuts = Convert.ToBase64String(big).Split(new[] { "//" }, StringSplitOptions.None).Length - 1;
    sw.Stop();
    double saveMs = sw.Elapsed.TotalMilliseconds;
    sw.Restart();
    var reload = new LightHistory { SystemDiameterM = 2.4e11 };
    reload.SetBodies(bodies);
    reload.Load(LightHistory.FromSaveText(ThroughConfigNode(text)), horizon, out err);
    sw.Stop();
    Console.WriteLine($"     busy game: {busy.EntryTotal} entries in the ring, saved {reload.EntryTotal} from UT {bigFloor:F0} to {horizon:F0}: "
        + $"{text.Length / 1024} KB of text, save {saveMs:F0} ms, load {sw.Elapsed.TotalMilliseconds:F0} ms");
    Check(err == null && text.Length <= CapChars && reload.TrackCount == busy.TrackCount, $"a busy game's history fits the 4 MB cap with every craft kept, through a ConfigNode ({plainCuts} `//` plain base64 would have cut)");

    var quiet = NewRing();
    for (uint k = 0; k < 500; k++)
    {
        quiet.Record(100 + k, State(0, Kerbin, new DVec3(r0 + k * 1000, 0, 0), new DVec3(0, vc, 0)), HistoryReason.Start);
        var ids = new uint[30];
        for (int i = 0; i < ids.Length; i++) ids[i] = 100000 + k * 100 + (uint)i;
        quiet.SetComposition(100 + k, 0, SaveParts(ids), 0, 0, 0);
    }
    sw.Restart();
    string quietText = LightHistory.ToSaveText(quiet.Save(100, CapChars / 4 * 3, out _));
    sw.Stop();
    Console.WriteLine($"     500 coasting craft of 30 parts: {quietText.Length / 1024} KB of text, save {sw.Elapsed.TotalMilliseconds:F1} ms");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
