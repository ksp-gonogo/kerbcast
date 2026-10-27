using System;
using System.Diagnostics;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

const int Sun = 0, Kerbin = 1, Mun = 2;
const double MuSun = 1.1723328e18, MuKerbin = 3.5316e12, MuMun = 6.5138398e10;
var bodies = new[]
{
    new HistoryBody { Index = Sun, Parent = -1, Mu = MuSun, Radius = 2.616e8, SoiRadius = double.PositiveInfinity },
    new HistoryBody { Index = Kerbin, Parent = Sun, Mu = MuKerbin, Radius = 600000, SoiRadius = 84159286, MaxOrbitalSpeed = 9285 },
    new HistoryBody { Index = Mun, Parent = Kerbin, Mu = MuMun, Radius = 200000, SoiRadius = 2429559, MaxOrbitalSpeed = 543 },
};

LightHistory NewRing(double c)
{
    var h = new LightHistory { SystemDiameterM = 2.4e11 };
    h.SetBodies(bodies);
    h.SetSignalSpeed(c);
    return h;
}

HistoryState State(double ut, int body, DVec3 r, DVec3 v) => new HistoryState
{
    Ut = ut, Body = body, Kind = HistoryKind.Conic, Position = r, Velocity = v, Attitude = DQuat.Identity,
};

double G(DVec3 lens, DVec3 p, double ut, double t, double c) => ut + (lens - p).Magnitude / c - t;

/* ---- the equation on straight lines ---- */
{
    const double c = 1000;
    var lens = new DVec3(0, 0, 0);

    var still = new Line { X0 = 5e5 };
    var s = PastInstant.Solve(ref still, lens, 1000, c, 0, double.NegativeInfinity, double.PositiveInfinity, out double tr);
    Check(s == PastStatus.Found && Math.Abs(tr - 500) <= PastInstant.ToleranceS, $"a craft standing 500 light seconds off is seen as it was 500 s ago ({tr:F7})");

    /* x(t') = x0 + v t' along +x, so the root is t_r = (t c - x0) / (c + v). */
    double worst = 0;
    bool neverEarly = true;
    foreach (double frac in new[] { 0.5, -0.5, 0.99, -0.99, 0.0001 })
    {
        var line = new Line { X0 = 5e6, V = frac * c };
        double expect = (1000 * c - line.X0) / (c + line.V);
        s = PastInstant.Solve(ref line, lens, 1000, c, Math.Abs(line.V), double.NegativeInfinity, double.PositiveInfinity, out tr);
        if (s != PastStatus.Found) { worst = double.PositiveInfinity; continue; }
        worst = Math.Max(worst, Math.Abs(tr - expect));
        if (tr > expect + 1e-9) neverEarly = false;
    }
    Check(worst <= PastInstant.ToleranceS, $"receding and closing at up to 0.99 c the root is exact (worst {worst:E1} s)");
    Check(neverEarly, "below c the solved instant is never after the root, so nothing shows early");

    /* Closing at 3 c: g has roots at 5 and 7.5; the latest is the picture. */
    var fast = new Line { X0 = 20, V = -3 };
    s = PastInstant.Solve(ref fast, lens, 10, 1, 3, double.NegativeInfinity, double.PositiveInfinity, out tr);
    Check(s == PastStatus.Found && Math.Abs(tr - 7.5) <= 1e-5, $"closing faster than light, the latest of two roots is taken ({tr:F6}, roots 5 and 7.5)");
    s = PastInstant.Solve(ref fast, lens, 10, 1, 30, double.NegativeInfinity, double.PositiveInfinity, out tr);
    Check(s == PastStatus.Found && Math.Abs(tr - 7.5) <= 1e-5, "a loose speed bound still finds the latest root");
    s = PastInstant.Solve(ref fast, lens, 10, 1, 3, 8, double.PositiveInfinity, out _);
    Check(s == PastStatus.Unseen, "closing faster than light from a history younger than the latest root, nothing is drawn");

    var lineN = new Line { X0 = 5e5 };
    s = PastInstant.Solve(ref lineN, lens, 1000, c, 0, 600, double.PositiveInfinity, out _);
    Check(s == PastStatus.Unseen, "light from before the craft's history is Unseen");
    s = PastInstant.Solve(ref lineN, lens, 1000, c, 0, double.NegativeInfinity, 400, out _);
    Check(s == PastStatus.Gone, "a craft whose last light is in is Gone");
    s = PastInstant.Solve(ref lineN, lens, 1000, c, 0, double.NegativeInfinity, 600, out tr);
    Check(s == PastStatus.Found && Math.Abs(tr - 500) <= PastInstant.ToleranceS, "a craft that ended after its past instant is still seen");
    s = PastInstant.Solve(ref lineN, lens, 1000, 0, 0, double.NegativeInfinity, double.PositiveInfinity, out _);
    Check(s == PastStatus.Unsolved, "no signal speed, no solve");
}

/* ---- loose bounds at low light speed converge within a budget ---- */
{
    var lens = new DVec3(0, 0, 0);
    foreach (var (d, c, bound, budget) in new[] { (2000.0, 30.0, 2300.0, 60), (2000.0, 3.0, 2300.0, 60), (1.2e7, 100.0, 2300.0, 80), (2000.0, 3000.0, 2300.0, 40) })
    {
        var still = new Counted<Line> { Inner = new Line { X0 = d } };
        var st = PastInstant.Solve(ref still, lens, 1e6, c, bound, double.NegativeInfinity, double.PositiveInfinity, out double tr);
        double expect = 1e6 - d / c;
        Check(st == PastStatus.Found && tr <= expect + 1e-9 && expect - tr <= Math.Max(PastInstant.ToleranceS, PastInstant.RelativeTolerance * d / c) * 2
            && still.N <= budget,
            $"a still craft {d:G3} m off with a {bound} m/s bound at c={c} solves in {still.N} reads (budget {budget}), {expect - tr:E1} s early of the root");
    }
}

/* ---- a moving craft with a loose bound at low light speed ---- */
{
    var lens = new DVec3(0, 0, 0);
    foreach (double vel in new[] { 2.0, -2.0, 25.0 })
    {
        const double c = 3;
        var line = new Counted<Line> { Inner = new Line { X0 = 1e6, V = vel } };
        var st = PastInstant.Solve(ref line, lens, 1e4, c, 2300, double.NegativeInfinity, double.PositiveInfinity, out double tr);
        double expect = (1e4 * c - 1e6) / (c + vel);
        double g = tr + Math.Abs(1e6 + vel * tr) / c - 1e4;
        Check(st == PastStatus.Found && g <= 0 && Math.Abs(tr - expect) < 1e-3 && line.N <= 80,
            $"at {vel} m/s with c=3 and a 2300 m/s bound, the root in {line.N} reads (g {g:E1})");
    }
}

/* ---- through the ring ---- */
{
    const double c = 1e5;
    double r0 = 700000, vc = Math.Sqrt(MuKerbin / r0);
    var lensOffset = new DVec3(0, 1.2e7, 0);
    var tree = new Tree { KerbinAt = new DVec3(1.36e10, 0, 0), KerbinVel = new DVec3(0, 9285, 0) };

    var ring = NewRing(c);
    ring.Record(LightHistory.CraftKey(5), State(0, Kerbin, new DVec3(r0, 0, 0), new DVec3(0, vc, 0)), HistoryReason.Start);

    /* Both about Kerbin, the frame a lens at Kerbin solves a craft round Kerbin in. */
    double Reference(double t, double hi)
    {
        double a = 0, b = Math.Min(t, hi);
        for (int i = 0; i < 200; i++)
        {
            double m = 0.5 * (a + b);
            ring.TryStateAt(5u, m, out var st);
            if (G(lensOffset, st.Position, m, t, c) > 0) b = m; else a = m;
        }
        return a;
    }

    var sight = PastInstant.Craft(ring, 5, 2000, Kerbin, lensOffset, c, ref tree);
    double expect = Reference(2000, double.PositiveInfinity);
    Check(sight.Found && sight.Frame == Kerbin && Math.Abs(sight.Ut - expect) <= 2 * PastInstant.ToleranceS && sight.Ut <= expect + 1e-9,
        $"an orbiting craft 12,000 km off at 1e5 m/s is seen {2000 - sight.Ut:F3} s ago in Kerbin's frame, at the root ({Math.Abs(sight.Ut - expect):E1} s)");
    ring.TryStateAt(5u, sight.Ut, out var at);
    Check((sight.State.Position - at.Position).Magnitude < 1e-6 && sight.State.Body == Kerbin && sight.Track == ring.Track(LightHistory.CraftKey(5)),
        "the sight carries the state and life at the past instant");

    sight = PastInstant.Craft(ring, 5, 100, Kerbin, lensOffset, c, ref tree);
    Check(sight.Status == PastStatus.Unseen, "a craft younger than its light time is not drawn");
    sight = PastInstant.Craft(ring, 77, 2000, Kerbin, lensOffset, c, ref tree);
    Check(sight.Status == PastStatus.Unseen && sight.Track == null, "a craft with no history is not drawn");

    double fresh = PastInstant.Craft(ring, 5, 2000, Kerbin, lensOffset, c, ref tree).Ut;
    var held = PastInstant.Craft(ring, 5, 2000, Kerbin, lensOffset, c, ref tree, notBefore: fresh - 5, heldFrame: Kerbin);
    Check(held.Found && held.Ut == fresh, "a held instant older than the solution gives way to it");
    held = PastInstant.Craft(ring, 5, 2000, Kerbin, lensOffset, c, ref tree, notBefore: fresh + 5, heldFrame: Kerbin);
    Check(held.Found && held.Ut == fresh, "a held instant whose light has not arrived is dropped, never drawn early");

    /* The signal speed lowered under a held instant: the old instant's light is far from here. */
    var fast = PastInstant.Craft(ring, 5, 2000, Kerbin, lensOffset, c, ref tree);
    var slow = PastInstant.Craft(ring, 5, 2000.02, Kerbin, lensOffset, c / 10, ref tree, notBefore: fast.Ut, heldFrame: Kerbin);
    var slowFresh = PastInstant.Craft(ring, 5, 2000.02, Kerbin, lensOffset, c / 10, ref tree);
    Check(slow.Found && slow.Ut == slowFresh.Ut && slow.Ut < fast.Ut - 1000, $"lowering c drops the hold ({fast.Ut:F1} held, {slow.Ut:F1} drawn)");
    var hold = new InstantHold();
    hold.Shown(2000, fast.Ut, ring.Generation, c, Kerbin, Kerbin);
    Check(double.IsNegativeInfinity(hold.NotBefore(2000.02, ring.Generation, c / 10, Kerbin))
        && double.IsNegativeInfinity(hold.NotBefore(2000.02, ring.Generation, c, Mun))
        && hold.NotBefore(2000.02, ring.Generation, c, Kerbin) == fast.Ut, "and the hold itself resets on a new signal speed or lens body");

    /* A lens receding faster than light (a re-sited lens does the same in one frame). */
    var near = PastInstant.Craft(ring, 5, 2000, Kerbin, lensOffset, c, ref tree);
    var farLens = lensOffset * 3;
    var far = PastInstant.Craft(ring, 5, 2001, Kerbin, farLens, c, ref tree, notBefore: near.Ut, heldFrame: Kerbin);
    var farFresh = PastInstant.Craft(ring, 5, 2001, Kerbin, farLens, c, ref tree);
    ring.TryStateAt(5u, far.Ut, out var farState);
    double gFar = far.Ut + (farLens - farState.Position).Magnitude / c - 2001;
    Check(far.Found && far.Ut == farFresh.Ut && far.Ut < near.Ut && gFar <= 0, "a receding lens drops the hold rather than show light that has not arrived");

    /* Destroyed at 500, its id back in use at 900: the old life is a ghost. */
    ring.Destroyed(5, 500, ring.Generation);
    ring.Record(LightHistory.CraftKey(5), State(900, Kerbin, new DVec3(-r0, 0, 0), new DVec3(0, -vc, 0)), HistoryReason.Start);
    double lt = lensOffset.Magnitude / c;
    sight = PastInstant.Craft(ring, 5, 560, Kerbin, lensOffset, c, ref tree);
    double expectGhost = Reference(560, 500);
    Check(sight.Found && sight.Track.IsGhost && Math.Abs(sight.Ut - expectGhost) <= 2 * PastInstant.ToleranceS,
        $"before the new life's light arrives, the old life is seen while its light lasts ({sight.Ut:F2})");
    sight = PastInstant.Craft(ring, 5, 500 + lt + 30, Kerbin, lensOffset, c, ref tree);
    Check(sight.Status == PastStatus.Gone, "between the old life's last light and the new life's first, nothing is drawn");
    sight = PastInstant.Craft(ring, 5, 900 + lt + 30, Kerbin, lensOffset, c, ref tree);
    Check(sight.Found && !sight.Track.IsGhost && sight.Ut >= 900, "once the new life's light arrives, it is the one seen");

    /* Cost: one solve per drawn craft per frame. */
    var sw = Stopwatch.StartNew();
    const int N = 200000;
    double sum = 0;
    for (int i = 0; i < N; i++) sum += PastInstant.Craft(ring, 5, 2000 + i * 1e-3, Kerbin, lensOffset, c, ref tree).Ut;
    sw.Stop();
    Console.WriteLine($"     ring solve: {sw.Elapsed.TotalMilliseconds * 1000 / N:F2} us a craft ({sum > 0})");

    var real = NewRing(299792458.0);
    real.Record(LightHistory.CraftKey(6), State(0, Kerbin, new DVec3(r0, 0, 0), new DVec3(0, vc, 0)), HistoryReason.Start);
    var duna = new DVec3(1.36e10 + 2e10, 0, 0);
    sw.Restart();
    for (int i = 0; i < N; i++) sum += PastInstant.Craft(real, 6, 2000 + i * 1e-3, Sun, duna, 299792458.0, ref tree).Ut;
    sw.Stop();
    Console.WriteLine($"     ring solve at real light speed from the Sun's frame, 2e10 m: {sw.Elapsed.TotalMilliseconds * 1000 / N:F2} us a craft");
}

/* ---- the frame: Kerbin's own orbital motion never enters a light time inside its SOI ---- */
{
    double r = 1.26e7, vc = Math.Sqrt(MuKerbin / r);
    var lensOffset = new DVec3(6e5, 0, 0);
    bool bounded = true, same = true;
    string worst = "";
    foreach (double c in new[] { 3e5, 3e4, 1e4 })
    {
        var ring = NewRing(c);
        /* Ahead of the lens along Kerbin's motion. */
        ring.Record(LightHistory.CraftKey(3), State(0, Kerbin, new DVec3(r, 0, 0), new DVec3(0, vc, 0)), HistoryReason.Start);
        var moving = new Tree { KerbinAt = new DVec3(1.36e10, 0, 0), KerbinVel = new DVec3(9285, 0, 0) };
        var still = new Tree { KerbinAt = new DVec3(1.36e10, 0, 0) };
        double t = 1e5;
        var a = PastInstant.Craft(ring, 3, t, Kerbin, lensOffset, c, ref moving);
        var b = PastInstant.Craft(ring, 3, t, Kerbin, lensOffset, c, ref still);
        ring.TryStateAt(3u, t, out var nowState);
        double d = (nowState.Position - lensOffset).Magnitude;
        double v = ring.MaxSpeedAbout(ring.Track(LightHistory.CraftKey(3)), Kerbin) * PastInstant.SpeedMargin;
        double lightTime = t - a.Ut;
        if (!a.Found || lightTime < d / c - v * d / (c * c) - 1e-6 || lightTime > d / (c - v) + 1e-6) bounded = false;
        if (!b.Found || Math.Abs(a.Ut - b.Ut) > 1e-9) same = false;
        worst += $" c={c:G2}: {lightTime:F3} s against {d / c:F3} s;";
    }
    Check(bounded, $"in Kerbin's frame the light time is never shorter than the instantaneous one less v_rel d / c^2 ({worst.Trim()})");
    Check(same, "and does not change with Kerbin's own orbital velocity");
}

/* ---- leaving Kerbin's SOI, the drawn instant never steps back ---- */
{
    const double c = 3e4;
    double soi = bodies[Kerbin].SoiRadius;
    var kerbinVel = new DVec3(-9285, 0, 0);
    var tree = new Tree { KerbinAt = new DVec3(1.36e10, 0, 0), KerbinVel = kerbinVel };
    var ring = NewRing(c);
    var key = LightHistory.CraftKey(4);
    double v0 = 3000, r0 = soi - 2e7, exitUt = (soi - r0) / v0;
    /* A straight run outward; the gravity of either body hardly bends it over this span. */
    ring.Record(key, State(0, Kerbin, new DVec3(r0, 0, 0), new DVec3(v0, 0, 0)), HistoryReason.Start);
    ring.TryStateAt(4u, exitUt, out var atExit);
    tree.TryOffset(Kerbin, exitUt, out var kerbinAtExit);
    ring.Record(key, State(exitUt, Sun, kerbinAtExit + atExit.Position, atExit.Velocity + kerbinVel), HistoryReason.SoiChange);
    var lensOffset = new DVec3(6e5, 0, 0);

    bool stepsBack = false, holdBack = false, frames = false, early = false;
    double exitLight = (atExit.Position - lensOffset).Magnitude / c;
    double last = double.NegativeInfinity, lastHeld = double.NegativeInfinity;
    var hold = new InstantHold();
    double lagAtEnd = 0;
    for (double t = exitUt - 2000; t < exitUt + 8000; t += 5)
    {
        var free = PastInstant.Craft(ring, 4, t, Kerbin, lensOffset, c, ref tree);
        var kept = PastInstant.Craft(ring, 4, t, Kerbin, lensOffset, c, ref tree, hold.NotBefore(t, ring.Generation, c, Kerbin), hold.Frame);
        if (!free.Found || !kept.Found) continue;
        if (free.Frame == Sun) frames = true;
        /* The frame changes once the crossing's light can have arrived, not when the craft crosses. */
        if (free.Frame == Sun && t < exitUt + 0.9 * exitLight) early = true;
        if (free.Ut < last) stepsBack = true;
        if (kept.Ut < lastHeld) holdBack = true;
        hold.Shown(t, kept.Ut, ring.Generation, c, Kerbin, kept.Frame);
        last = free.Ut;
        lastHeld = kept.Ut;
        lagAtEnd = kept.Ut - free.Ut;
    }
    Check(frames && !early, $"the frame turns to the Sun's when the crossing's light arrives, about {exitLight:F0} s after it");
    Console.WriteLine($"     bare solution steps back at the switch: {stepsBack}");
    Check(!holdBack && lagAtEnd == 0, "held, the drawn instant never steps back, and catches the solution up");
}

/* ---- placing against a body drawn at the present ---- */
{
    double w = 2 * Math.PI / 21549.425;
    var s = new HistoryState
    {
        Ut = 100, Body = Kerbin, Kind = HistoryKind.Surface, Position = new DVec3(600000, 0, 0),
        AngularVelocity = new DVec3(0, 0, w), Attitude = DQuat.Identity,
    };
    var p = PastInstant.AboutDrawnBody(s, 100 + 21549.425 / 4);
    Check(Math.Abs(p.X) < 1e-3 && Math.Abs(p.Y - 600000) < 1e-3, "a landed craft is carried round with its body's spin to the drawn instant");
    var e = LightHistoryMath.Predict(s.ToEntry(HistoryReason.Start), MuKerbin, 100 + 21549.425 / 4);
    Check((e.Position - p).Magnitude < 1e-3, "which is where the ring's own surface read puts it");
    var conic = State(100, Kerbin, new DVec3(7e5, 0, 0), new DVec3(0, 2000, 0));
    Check((PastInstant.AboutDrawnBody(conic, 500) - conic.Position).Magnitude == 0, "a craft in flight keeps its offset at the past instant");

    /* KSP: angularVelocity = Vector3d.down * 2 pi / period, and a surface point
       moves at getRFrmVel = Vector3d.Cross(angularVelocity, r). Turning by the
       rotation vector must move a point the same way. */
    var spin = new DVec3(0, -w, 0);
    var r = new DVec3(600000, 0, 250000);
    double dt = 1.0;
    var turned = DQuat.FromRotationVector(spin * dt).Rotate(r);
    var byVelocity = r + DVec3.Cross(spin, r) * dt;
    /* Only the centripetal term, half w^2 r dt^2 (about 3 cm), separates them; the other hand would be 2 w r dt (about 380 m) off. */
    Check((turned - byVelocity).Magnitude < 0.1 && DVec3.Cross(spin, r).Magnitude * dt > 100,
        $"DQuat.FromRotationVector turns the way KSP's Cross(angularVelocity, r) moves a surface point ({(turned - byVelocity).Magnitude:E1} m in 1 s)");
}

/* ---- bodies from the ring (Principia) ---- */
{
    var ring = NewRing(299792458.0);
    ring.SampleBody(Kerbin, State(0, Sun, new DVec3(1.36e10, 0, 0), new DVec3(0, 9285, 0)));
    ring.SampleBody(Mun, State(0, Kerbin, new DVec3(1.2e7, 0, 0), new DVec3(0, 543, 0)));
    var rb = new RingBodies(ring);
    bool okMun = PastInstant.TryAbout(ref rb, Mun, Sun, 0, out var mun);
    bool okSun = PastInstant.TryAbout(ref rb, Sun, Sun, 0, out var sun);
    bool okKerbin = PastInstant.TryAbout(ref rb, Kerbin, Mun, 0, out var kerbinAboutMun);
    Check(okMun && (mun - new DVec3(1.36e10 + 1.2e7, 0, 0)).Magnitude < 1e-3 && okSun && sun.Magnitude == 0,
        "a body about an ancestor is the sum up its parents");
    Check(okKerbin && (kerbinAboutMun - new DVec3(-1.2e7, 0, 0)).Magnitude < 1e-3, "and about a body below it, that body's offset taken away");
    Check(PastInstant.CommonAncestor(ref rb, Mun, Kerbin) == Kerbin && PastInstant.CommonAncestor(ref rb, Mun, Sun) == Sun
        && PastInstant.CommonAncestor(ref rb, Mun, Mun) == Mun, "the common ancestor is the lowest body above or at both");
}

/* ---- one solve shared by the cameras on a lens craft ---- */
{
    const double c = 3000;
    var tree = new Tree { KerbinAt = new DVec3(1.36e10, 0, 0), KerbinVel = new DVec3(0, 9285, 0) };
    var ring = NewRing(c);
    const int Craft = 20;
    for (uint k = 0; k < Craft; k++)
    {
        double r = 700000 + 50000 * k, v = Math.Sqrt(MuKerbin / r), a = k * 0.3;
        ring.Record(LightHistory.CraftKey(100 + k), State(0, Kerbin, new DVec3(r * Math.Cos(a), r * Math.Sin(a), 0),
            new DVec3(-v * Math.Sin(a), v * Math.Cos(a), 0)), HistoryReason.Start);
    }
    /* 12,000 km off Kerbin at 3 km/s: light times near 4,000 s. */
    var point = new DVec3(0, 1.2e7, 0);
    var eyes = new DVec3[8];
    for (int i = 0; i < eyes.Length; i++) eyes[i] = point + new DVec3(Math.Cos(i) * (2 + i), Math.Sin(i) * (2 + i), 0.5 * i);
    double spread = 0;
    foreach (var e in eyes) spread = Math.Max(spread, (e - point).Magnitude);

    bool same = true;
    foreach (double t in new[] { 10000.0, 10500.0 })
    {
        var plain = PastInstant.Craft(ring, 100, t, Kerbin, point, c, ref tree);
        var zero = PastInstant.Craft(ring, 100, t, Kerbin, point, c, ref tree, spreadM: 0);
        same &= plain.Status == zero.Status && plain.Ut == zero.Ut;
    }
    Check(same, "no spread solves exactly as before");

    /* Every eye within the spread has the shared instant's light, and it is no older than twice the spread's light time. */
    bool arrived = true, fresh = true;
    for (uint k = 0; k < Craft; k++)
    {
        var shared = PastInstant.Craft(ring, 100 + k, 10000, Kerbin, point, c, ref tree, spreadM: spread);
        var own = PastInstant.Craft(ring, 100 + k, 10000, Kerbin, point, c, ref tree);
        if (!shared.Found || !own.Found) { arrived = false; continue; }
        ring.TryStateAt(100 + k, shared.Ut, out var s);
        foreach (var e in eyes) arrived &= G(e, s.Position, shared.Ut, 10000, c) <= 0;
        double v = ring.MaxSpeedAbout(ring.Track(LightHistory.CraftKey(100 + k)), Kerbin);
        fresh &= own.Ut - shared.Ut <= spread / c / (1 - v / c) + 2 * PastInstant.ToleranceS && shared.Ut <= own.Ut;
    }
    Check(arrived, $"a shared instant's light has reached every camera within {spread:F1} m of the solve point");
    Check(fresh, "and it is older than the point's own instant by at most the spread's light time over 1 - v/c");

    /* Eight cameras on one craft asking about every craft each frame: one solve per craft per frame. */
    var lens = new LensSights();
    bool agree = true;
    for (int f = 0; f < 10; f++)
    {
        double t = 10000 + f / 60.0;
        foreach (var e in eyes) lens.Cover(t, (e - point).Magnitude);
        for (int i = 0; i < eyes.Length; i++)
            for (uint k = 0; k < Craft; k++)
            {
                var s = lens.See(ring, 100 + k, t, Kerbin, point, c, ref tree);
                ring.TryStateAt(100 + k, s.Ut, out var st);
                agree &= s.Found && G(eyes[i], st.Position, s.Ut, t, c) <= 0;
            }
    }
    Check(lens.Solves == 10 * Craft && lens.Shared == 10 * Craft * (eyes.Length - 1),
        $"8 cameras on one craft run one solve per craft per frame ({lens.Solves} solves, {lens.Shared} shared)");
    Check(agree, "and every camera's sight has its light arrived at that camera");

    var grow = new LensSights();
    grow.See(ring, 100, 10000, Kerbin, point, c, ref tree);
    grow.Cover(10000, 3);
    var wider = grow.See(ring, 100, 10000, Kerbin, point, c, ref tree);
    var narrow = PastInstant.Craft(ring, 100, 10000, Kerbin, point, c, ref tree);
    Check(grow.Solves == 2 && wider.Ut < narrow.Ut, "a camera farther out than the spread so far re-solves for it, older");

    var slower = new LensSights();
    var atFast = slower.See(ring, 100, 10000, Kerbin, point, c, ref tree);
    var atSlow = slower.See(ring, 100, 10000, Kerbin, point, c / 3, ref tree);
    Check(atFast.Found && atSlow.Status == PastStatus.Unseen,
        $"a lower signal speed in the same frame solves again rather than serve the faster one's instant ({atSlow.Status})");
    var other = slower.See(ring, 100, 10000, Mun, point, c, ref tree);
    Check(slower.Solves == 3, "and so does a lens on another body");

    var shrink = new LensSights();
    shrink.Cover(10000, 9);
    shrink.Cover(10000 + 1 / 60.0, 2);
    double both = shrink.SpreadM;
    shrink.Cover(10000 + 2 / 60.0, 2);
    Check(both == 9 && shrink.SpreadM == 2, "the spread covers this frame's and the last frame's cameras, and shrinks once one is gone");

    /* More craft than the cache starts with, all asked every frame: none is evicted, so every hold stays. */
    var many = NewRing(c);
    for (uint k = 0; k < 100; k++)
        many.Record(LightHistory.CraftKey(1000 + k), State(0, Kerbin, new DVec3(7e5 + 1e3 * k, 0, 0), new DVec3(0, 2300, 0)), HistoryReason.Start);
    var wide = new LensSights();
    for (int f = 0; f < 3; f++)
        for (uint k = 0; k < 100; k++) wide.See(many, 1000 + k, 10000 + f / 60.0, Kerbin, point, c, ref tree);
    Check(wide.Solves == 300 && wide.Shared == 0, $"100 craft asked each frame are solved once a frame ({wide.Solves} solves)");
    long before = wide.Solves;
    for (uint k = 0; k < 100; k++) wide.See(many, 1000 + k, 10000 + 2 / 60.0, Kerbin, point, c, ref tree);
    Check(wide.Solves == before, "and every one of them is still kept for a repeat ask, more than the cache started with");

    var hold = new LensSights();
    var first = hold.See(ring, 100, 10000, Kerbin, point, c, ref tree);
    var again = hold.See(ring, 100, 10000 + 1 / 60.0, Kerbin, point, c, ref tree);
    Check(again.Ut >= first.Ut, "a craft's instant never steps back from one frame to the next");

    /* Cost, 8 cameras x 20 craft a frame: shared against a solve per camera. */
    const int Frames = 2000;
    var sw = Stopwatch.StartNew();
    var sharedLens = new LensSights();
    for (int f = 0; f < Frames; f++)
    {
        double t = 11000 + f / 60.0;
        foreach (var e in eyes) sharedLens.Cover(t, (e - point).Magnitude);
        for (int i = 0; i < eyes.Length; i++)
            for (uint k = 0; k < Craft; k++) sharedLens.See(ring, 100 + k, t, Kerbin, point, c, ref tree);
    }
    sw.Stop();
    double sharedUs = sw.Elapsed.TotalMilliseconds * 1000 / Frames;
    var perCamera = new LensSights[eyes.Length];
    for (int i = 0; i < eyes.Length; i++) perCamera[i] = new LensSights();
    sw.Restart();
    for (int f = 0; f < Frames; f++)
    {
        double t = 11000 + f / 60.0;
        for (int i = 0; i < eyes.Length; i++)
            for (uint k = 0; k < Craft; k++) perCamera[i].See(ring, 100 + k, t, Kerbin, eyes[i], c, ref tree);
    }
    sw.Stop();
    double ownUs = sw.Elapsed.TotalMilliseconds * 1000 / Frames;
    Console.WriteLine($"     8 cameras x {Craft} craft a frame: shared {sharedUs:F1} us, a solve per camera {ownUs:F1} us");
    Check(sharedUs < ownUs, "sharing the solve costs less than a solve per camera");
}

/* ---- identity at the past instant: docks, splits, part loss, ends ---- */
{
    const double c = 1e5;
    var tree = new Tree { KerbinAt = new DVec3(1.36e10, 0, 0), KerbinVel = new DVec3(0, 9285, 0) };
    var lens = new DVec3(0, 1.2e7, 0);
    double r0 = 700000, vc = Math.Sqrt(MuKerbin / r0);
    HistoryState Orbit(double ut, double phase) => State(ut, Kerbin,
        new DVec3(r0 * Math.Cos(phase), r0 * Math.Sin(phase), 0), new DVec3(-vc * Math.Sin(phase), vc * Math.Cos(phase), 0));
    PartRecord[] Parts(params uint[] ids)
    {
        var parts = new PartRecord[ids.Length];
        for (int i = 0; i < ids.Length; i++) parts[i] = new PartRecord { Pid = ids[i], Name = "p" + ids[i], Py = i, RadiusM = 1 };
        return parts;
    }
    const double Te = 1000;
    /* Light time from the lens to these orbits is about 120 s: at 1060 the light left before Te, at 1300 after. */
    const double Before = 1060, After = 1300;

    /* B docks into A at Te, and the feed followed B to A. */
    var ring = NewRing(c);
    ring.Record(LightHistory.CraftKey(1), Orbit(0, 0), HistoryReason.Start);
    ring.Record(LightHistory.CraftKey(2), Orbit(0, 1e-4), HistoryReason.Start);
    ring.SetComposition(1, 0, Parts(10, 11), 0, 0, 0);
    ring.SetComposition(2, 0, Parts(20), 0, 0, 0);
    ring.Merge(2, 1, Te);
    ring.SetComposition(1, Te, Parts(10, 11, 20), 0, 0, 0);
    var sights = new LensSights();
    uint drawn = 2;
    var s = sights.SeeSubject(ring, 1, ref drawn, Before, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 2 && drawn == 2 && s.Parts.Parts.Length == 1,
        "a subject followed through a dock is drawn as the craft it was while that craft's light arrives");
    s = sights.SeeSubject(ring, 1, ref drawn, After, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 1 && drawn == 1 && s.Parts.Parts.Length == 3,
        "and as the docked craft, with the merged parts, once the dock's light is in");
    drawn = 1;
    s = sights.SeeSubject(ring, 1, ref drawn, Before, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 1 && s.Parts.Parts.Length == 2, "the survivor before the dock's light arrives has only its own parts");

    /* C comes off A at Te. */
    ring = NewRing(c);
    ring.Record(LightHistory.CraftKey(1), Orbit(0, 0), HistoryReason.Start);
    ring.SetComposition(1, 0, Parts(10, 30), 0, 0, 0);
    ring.Split(1, 3, Te);
    ring.Record(LightHistory.CraftKey(3), Orbit(Te, 1e-4), HistoryReason.Start);
    ring.SetComposition(3, Te, Parts(30), 0, 0, 0);
    ring.SetComposition(1, Te, Parts(10), 0, 0, 0);
    sights = new LensSights();
    drawn = 0;
    s = sights.SeeSubject(ring, 3, ref drawn, Before, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 1 && s.Parts.Parts.Length == 2, "a craft that undocked is drawn inside its parent until the undock's light arrives");
    s = sights.SeeSubject(ring, 3, ref drawn, After, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 3 && s.Parts.Parts.Length == 1, "and apart once it has");
    s = sights.SeeAsItWas(ring, 1, After, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 1 && s.Parts.Parts.Length == 1, "its parent then shows without it");

    /* A part lost at Te, then the craft destroyed at 2000. */
    ring = NewRing(c);
    ring.Record(LightHistory.CraftKey(4), Orbit(0, 0), HistoryReason.Start);
    ring.SetComposition(4, 0, Parts(40, 41), 0, 0, 0);
    ring.PartLost(4, 41, Te);
    ring.SetComposition(4, Te, Parts(40), 0, 0, 0);
    ring.Destroyed(4, 2000, ring.Generation);
    sights = new LensSights();
    drawn = 0;
    s = sights.SeeSubject(ring, 4, ref drawn, Before, Kerbin, lens, c, ref tree);
    bool beforeLoss = s.Found && s.Parts.Parts.Length == 2;
    s = sights.SeeSubject(ring, 4, ref drawn, After, Kerbin, lens, c, ref tree);
    Check(beforeLoss && s.Found && s.Parts.Parts.Length == 1, "a lost part goes when the loss's light arrives");
    s = sights.SeeSubject(ring, 4, ref drawn, 2060, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Ut < 2000, "a destroyed craft's ghost is drawn until its last light");
    s = sights.SeeSubject(ring, 4, ref drawn, 2300, Kerbin, lens, c, ref tree);
    Check(s.Status == PastStatus.Gone, "and nothing after it");

    /* A chain of docks: B into A at Te, A into Z at Te + 100. No link is skipped. */
    ring = NewRing(c);
    ring.Record(LightHistory.CraftKey(1), Orbit(0, 0), HistoryReason.Start);
    ring.Record(LightHistory.CraftKey(2), Orbit(0, 1e-4), HistoryReason.Start);
    ring.Record(LightHistory.CraftKey(9), Orbit(0, 2e-4), HistoryReason.Start);
    ring.SetComposition(1, 0, Parts(10), 0, 0, 0);
    ring.SetComposition(2, 0, Parts(20), 0, 0, 0);
    ring.SetComposition(9, 0, Parts(90), 0, 0, 0);
    ring.Merge(2, 1, Te);
    ring.SetComposition(1, Te, Parts(10, 20), 0, 0, 0);
    ring.Merge(1, 9, Te + 100);
    ring.SetComposition(9, Te + 100, Parts(90, 10, 20), 0, 0, 0);
    sights = new LensSights();
    drawn = 2;
    s = sights.SeeSubject(ring, 9, ref drawn, 1180, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 1 && drawn == 1 && s.Parts.Parts.Length == 2,
        $"two docks in a row: between their lights the middle craft is drawn ({s.Pid}, {s.Parts?.Parts.Length} parts)");
    s = sights.SeeSubject(ring, 9, ref drawn, 1300, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 9 && drawn == 9 && s.Parts.Parts.Length == 3, "and the last survivor once the second dock's light is in");

    /* B docks into A, then a new craft takes B's id: the merge is read from the life that docked. */
    ring = NewRing(c);
    ring.Record(LightHistory.CraftKey(1), Orbit(0, 0), HistoryReason.Start);
    ring.Record(LightHistory.CraftKey(2), Orbit(0, 1e-4), HistoryReason.Start);
    ring.SetComposition(1, 0, Parts(10), 0, 0, 0);
    ring.SetComposition(2, 0, Parts(20), 0, 0, 0);
    ring.Merge(2, 1, Te);
    ring.SetComposition(1, Te, Parts(10, 20), 0, 0, 0);
    ring.Record(LightHistory.CraftKey(2), Orbit(Te + 10, 3), HistoryReason.Start);
    sights = new LensSights();
    drawn = 2;
    s = sights.SeeSubject(ring, 1, ref drawn, Before, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 2 && s.Parts.Parts.Length == 1, "a docked craft whose id came back is still drawn approaching until the dock's light arrives");

    /* The id's new life is its own craft: picking the craft its old life docked into draws that craft at once, not the new one. */
    bool released = true;
    foreach (double at in new[] { 2000.0, 4000.0, 6000.0 })
    {
        drawn = 2;
        s = sights.SeeSubject(ring, 1, ref drawn, at, Kerbin, lens, c, ref tree);
        released &= s.Found && s.Pid == 1 && drawn == 1;
    }
    Check(released, "a live craft reusing the id of one that docked long ago does not hold a new pick of the craft it docked into");

    /* C comes off A at 900 and docks into Z at Te; the feed followed C to Z. */
    ring = NewRing(c);
    ring.Record(LightHistory.CraftKey(1), Orbit(0, 0), HistoryReason.Start);
    ring.Record(LightHistory.CraftKey(9), Orbit(0, 2e-4), HistoryReason.Start);
    ring.SetComposition(1, 0, Parts(10, 30), 0, 0, 0);
    ring.SetComposition(9, 0, Parts(90), 0, 0, 0);
    ring.Split(1, 3, 900);
    ring.Record(LightHistory.CraftKey(3), Orbit(900, 1e-4), HistoryReason.Start);
    ring.SetComposition(3, 900, Parts(30), 0, 0, 0);
    ring.SetComposition(1, 900, Parts(10), 0, 0, 0);
    ring.Merge(3, 9, Te);
    sights = new LensSights();
    drawn = 3;
    s = sights.SeeSubject(ring, 9, ref drawn, 1000, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 1 && s.Parts.Parts.Length == 2, "a held craft whose own split's light has not arrived is drawn inside its parent, not blank");

    /* A split older than the ring: the child is never drawn as its parent. */
    var shortRing = new LightHistory { SystemDiameterM = 1e7 };
    shortRing.SetBodies(bodies);
    shortRing.SetSignalSpeed(c);
    shortRing.Record(LightHistory.CraftKey(1), Orbit(0, 0), HistoryReason.Start);
    shortRing.Split(1, 3, 100);
    shortRing.Record(LightHistory.CraftKey(3), Orbit(100, 1e-4), HistoryReason.Start);
    uint parentBefore = IdentityReplay.SplitParent(shortRing, 3, 200);
    for (int i = 1; i <= 50; i++)
    {
        shortRing.Record(LightHistory.CraftKey(1), Orbit(100 + i * 100, 0), HistoryReason.Departure);
        shortRing.Record(LightHistory.CraftKey(3), Orbit(100 + i * 100, 1e-4), HistoryReason.Departure);
    }
    shortRing.Trim(5100);
    Check(parentBefore == 1 && shortRing.FloorUt > 100 && IdentityReplay.SplitParent(shortRing, 3, 5100) == 0,
        $"a craft whose split is below the ring's floor ({shortRing.FloorUt:F0}) has no parent to be drawn as");


    ring = NewRing(c);
    ring.Record(LightHistory.CraftKey(5), Orbit(0, 0), HistoryReason.Start);
    ring.Record(LightHistory.CraftKey(6), Orbit(0, 1), HistoryReason.Start);
    sights = new LensSights();
    drawn = 5;
    s = sights.SeeSubject(ring, 6, ref drawn, After, Kerbin, lens, c, ref tree);
    Check(s.Found && s.Pid == 6 && drawn == 6, "a subject picked anew is drawn at once, at its own past instant");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

struct Line : IPastPath
{
    public double X0, V;
    public bool TryAt(double ut, out DVec3 position)
    {
        position = new DVec3(X0 + V * ut, 0, 0);
        return true;
    }
}

/* Counts the reads a solve makes. */
struct Counted<T> : IPastPath where T : IPastPath
{
    public T Inner;
    public int N;
    public bool TryAt(double ut, out DVec3 position)
    {
        N++;
        return Inner.TryAt(ut, out position);
    }
}

/* The Sun at the root, Kerbin drifting about it at one velocity, the Mun still about Kerbin. */
struct Tree : IBodyTree
{
    public DVec3 KerbinAt, KerbinVel, MunAt;
    public bool TryParent(int body, out int parent)
    {
        parent = body == 2 ? 1 : 0;
        return body == 1 || body == 2;
    }
    public bool TryOffset(int body, double ut, out DVec3 offset)
    {
        offset = body == 1 ? KerbinAt + KerbinVel * ut : body == 2 ? MunAt : default;
        return body == 1 || body == 2;
    }
}
