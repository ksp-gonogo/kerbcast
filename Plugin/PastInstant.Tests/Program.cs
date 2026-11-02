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
    h.SetLightSpeed(c);
    return h;
}

HistoryState State(double ut, int body, DVec3 r, DVec3 v) => new HistoryState
{
    Ut = ut, Body = body, Kind = HistoryKind.Conic, Position = r, Velocity = v, Attitude = DQuat.Identity,
};

double G(DVec3 lens, DVec3 p, double ut, double t, double c) => ut + (lens - p).Magnitude / c - t;

/* How near the latest root a drawn instant must be, in seconds. */
const double LatestS = 1e-3;

/*
 * Whether `tr` is the latest root of `path` to within `eps`, by a plain
 * trace from t: steps of g / k never pass a root, so they get below tr + eps
 * only if no root lies above it.
 */
bool IsLatest<T>(ref T path, DVec3 lens, double tt, double c, double k, double tr, double eps) where T : IPastPath
{
    double x = tt;
    for (int i = 0; i < 4000000; i++)
    {
        if (x <= tr + eps) return true;
        path.TryAt(x, out var p);
        double g = x + (lens - p).Magnitude / c - tt;
        if (g <= 0) return false;
        x -= Math.Max(g / k, 1e-9);
    }
    return false;
}

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
    Check(s == PastStatus.Unsolved, "no light speed, no solve");
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

    /* The light speed lowered under a held instant: the old instant's light is far from here. */
    var fast = PastInstant.Craft(ring, 5, 2000, Kerbin, lensOffset, c, ref tree);
    var slow = PastInstant.Craft(ring, 5, 2000.02, Kerbin, lensOffset, c / 10, ref tree, notBefore: fast.Ut, heldFrame: Kerbin);
    var slowFresh = PastInstant.Craft(ring, 5, 2000.02, Kerbin, lensOffset, c / 10, ref tree);
    Check(slow.Found && slow.Ut == slowFresh.Ut && slow.Ut < fast.Ut - 1000, $"lowering c drops the hold ({fast.Ut:F1} held, {slow.Ut:F1} drawn)");
    var hold = new InstantHold();
    hold.Shown(2000, fast.Ut, ring.Generation, c, Kerbin, Kerbin);
    Check(double.IsNegativeInfinity(hold.NotBefore(2000.02, ring.Generation, c / 10, Kerbin))
        && double.IsNegativeInfinity(hold.NotBefore(2000.02, ring.Generation, c, Mun))
        && hold.NotBefore(2000.02, ring.Generation, c, Kerbin) == fast.Ut, "and the hold itself resets on a new light speed or lens body");

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

/* ---- bodies from the ring once they leave their Kepler paths ---- */
{
    var ring = NewRing(299792458.0);
    ring.SampleBody(Kerbin, State(0, Sun, new DVec3(1.36e10, 0, 0), new DVec3(0, 9285, 0)));
    ring.SampleBody(Mun, State(0, Kerbin, new DVec3(1.2e7, 0, 0), new DVec3(0, 543, 0)));
    var rb = new RingBodies<Circles>(ring, Circles.Stock());
    bool okMun = PastInstant.TryAbout(ref rb, Mun, Sun, 0, out var mun);
    bool okSun = PastInstant.TryAbout(ref rb, Sun, Sun, 0, out var sun);
    bool okKerbin = PastInstant.TryAbout(ref rb, Kerbin, Mun, 0, out var kerbinAboutMun);
    Check(okMun && (mun - new DVec3(1.36e10 + 1.2e7, 0, 0)).Magnitude < 1e-3 && okSun && sun.Magnitude == 0,
        "a body about an ancestor is the sum up its parents");
    Check(okKerbin && (kerbinAboutMun - new DVec3(-1.2e7, 0, 0)).Magnitude < 1e-3, "and about a body below it, that body's offset taken away");
    Check(PastInstant.CommonAncestor(ref rb, Mun, Kerbin) == Kerbin && PastInstant.CommonAncestor(ref rb, Mun, Sun) == Sun
        && PastInstant.CommonAncestor(ref rb, Mun, Mun) == Mun, "the common ancestor is the lowest body above or at both");
}

/* ---- bodies at their past positions ---- */
{
    /* A stock-like system on circles: Sun, Kerbin, Mun, Minmus, Duna, Ike, Jool and five moons, and more planets to make sixteen. */
    var sys = Circles.Stock();
    var ring = new LightHistory { SystemDiameterM = 2.4e11 };
    ring.SetBodies(sys.Bodies());
    var lens = new DVec3(0, 600000, 0);
    const int KerbinC = 1, MunC = 2, DunaC = 4, IkeC = 5;
    double Reference(int body, int frame, DVec3 at, double t, double c)
    {
        double a = t - 1e10, b = t;
        for (int i = 0; i < 400; i++)
        {
            double m = 0.5 * (a + b);
            PastInstant.TryAbout(ref sys, body, frame, m, out var p);
            if (G(at, p, m, t, c) > 0) b = m; else a = m;
        }
        return a;
    }

    const double t = 5e6;
    bool BodyLatest(int body, int frame, DVec3 at, double tt, double c, double tr)
    {
        var path = new BodyPath<Circles> { Tree = sys, Body = body, Frame = frame };
        return IsLatest(ref path, at, tt, c, 1 + ring.BodySpeedBound(body, frame) * PastInstant.SpeedMargin / c, tr, LatestS);
    }

    /*
     * Every body followed from one lens for `frames` frames `step` apart. A
     * lens on the Sun stands 1e9 m out and moves straight out at u c; a lens
     * on Kerbin stands on its surface. Every `every` frames, and on any jump
     * forward, the drawn instant is checked to be the latest root. A step back is
     * wrong when light from the instant drawn before has already arrived.
     */
    (int Drawn, int NotDrawn, int Early, int NotRoot, int Present, int Back, int BadBack, int Births, int NotLatest, int Checked) Follow(
        int lensBody, double u, double c, double step, int frames, int budget, int every)
    {
        var traces = new BodyTrace[sys.Count];
        var prev = new double[sys.Count];
        for (int b = 0; b < sys.Count; b++)
        {
            traces[b] = new BodyTrace();
            prev[b] = double.NaN;
        }
        var outward = new DVec3(0.6, 0.8, 0);
        int drawn = 0, notDrawn = 0, early = 0, notRoot = 0, present = 0, back = 0, badBack = 0, births = 0, notLatest = 0, checks = 0;
        for (int f = 0; f < frames; f++)
        {
            double tt = t + f * step;
            var off = lensBody == 0 ? outward * (1e9 + u * c * f * step) : lens;
            for (int b = 0; b < sys.Count; b++)
            {
                var s = PastInstant.Body(ref sys, ring, b, lensBody, off, tt, c, double.NegativeInfinity, traces[b], budget);
                if (s.Frame == b) continue;
                if (!s.Found)
                {
                    notDrawn++;
                    continue;
                }
                drawn++;
                PastInstant.TryAbout(ref sys, lensBody, s.Frame, tt, out var at);
                at = at + off;
                PastInstant.TryAbout(ref sys, b, s.Frame, s.Ut, out var p);
                double g = G(at, p, s.Ut, tt, c);
                double k = 1 + ring.BodySpeedBound(b, s.Frame) * PastInstant.SpeedMargin / c;
                double tol = 2 * Math.Max(PastInstant.ToleranceS, 1e-14 * (Math.Abs(tt) + (tt - s.Ut)));
                if (!(g <= 0)) early++;
                if (!(g >= -k * tol)) notRoot++;
                if (s.Ut >= tt) present++;
                double moved = s.Ut - prev[b];
                if (moved < -1e-6)
                {
                    /* Light from the last instant drawn has arrived, so the latest root is no earlier. */
                    PastInstant.TryAbout(ref sys, b, s.Frame, prev[b], out var then);
                    if (G(at, then, prev[b], tt, c) <= 0) badBack++;
                    else back++;
                }
                bool jump = moved > 100 * step;
                if (jump) births++;
                if (jump || f % every == 0)
                {
                    checks++;
                    if (!BodyLatest(b, s.Frame, at, tt, c, s.Ut)) notLatest++;
                }
                prev[b] = s.Ut;
            }
        }
        return (drawn, notDrawn, early, notRoot, present, back, badBack, births, notLatest, checks);
    }
    foreach (var c in new[] { 299792458.0, 3e5, 1e4 })
    {
        PastInstant.TryAbout(ref sys, KerbinC, KerbinC, t, out var lensKerbin);
        PastInstant.TryAbout(ref sys, KerbinC, 0, t, out var lensSun);
        var mun = PastInstant.Body(ref sys, ring, MunC, KerbinC, lens, t, c, double.NegativeInfinity);
        var duna = PastInstant.Body(ref sys, ring, DunaC, KerbinC, lens, t, c, double.NegativeInfinity);
        var ike = PastInstant.Body(ref sys, ring, IkeC, KerbinC, lens, t, c, double.NegativeInfinity);
        double munRef = Reference(MunC, KerbinC, lensKerbin + lens, t, c);
        double dunaRef = Reference(DunaC, 0, lensSun + lens, t, c);
        double tol = Math.Max(PastInstant.ToleranceS, PastInstant.RelativeTolerance * (t - dunaRef)) * 2;
        Check(mun.Found && mun.Frame == KerbinC && Math.Abs(mun.Ut - munRef) <= tol && mun.Ut <= munRef + 1e-9,
            $"c={c:G3}: the Mun from Kerbin is solved in Kerbin's frame ({t - mun.Ut:G4} s back)");
        Check(duna.Found && duna.Frame == 0 && Math.Abs(duna.Ut - dunaRef) <= Math.Max(tol, 1e-3 * (t - dunaRef)) && duna.Ut <= dunaRef + 1e-6,
            $"c={c:G3}: Duna from Kerbin in the Sun's ({t - duna.Ut:G4} s back, reference {t - dunaRef:G4})");
        PastInstant.TryAbout(ref sys, DunaC, 0, duna.Ut, out var dunaThen);
        Check((duna.About - dunaThen).Magnitude < 1e-6, $"c={c:G3}: and is placed where it was then about the Sun");
        double ikeRef = Reference(IkeC, 0, lensSun + lens, t, c);
        PastInstant.TryAbout(ref sys, IkeC, 0, ike.Ut, out var ikeThen);
        Check(ike.Found && ike.Frame == 0 && Math.Abs(ike.Ut - ikeRef) <= Math.Max(tol, 1e-3 * (t - ikeRef)) && ike.Ut <= ikeRef + 1e-6
            && (ike.About - ikeThen).Magnitude < 1e-6,
            $"c={c:G3}: Ike, a moon of a far planet, is solved in the Sun's frame too ({t - ike.Ut:G4} s back, reference {t - ikeRef:G4})");
    }

    /*
     * At slow light speeds every body outruns light, and each is followed
     * frame by frame from one lens: on the Sun standing still, moving at half
     * c, and moving at twice c towards and away from the images it sees (the
     * latest image of each orbit is on the lens's own side of it); and on
     * Kerbin. Each drawn instant is a root with its light in, never the
     * present, and the latest root as an independent trace finds it. The
     * instant steps back only when the lens recedes from it faster than
     * light, so it never does for a lens still, slower than light or
     * approaching, and every jump forward, where a new root is born, lands on
     * the latest root.
     */
    foreach (var c in new[] { 30.0, 3.0 })
    {
        foreach (var (name, lensBody, u, step, forward) in new[]
        {
            ("still on the Sun", 0, 0.0, 1.0 / 60, true), ("still on the Sun, warped", 0, 0.0, 2000.0, true),
            ("on the Sun at c/2", 0, 0.5, 2000.0, true), ("approaching at 2c", 0, 2.0, 2000.0, true),
            ("receding at 2c", 0, -2.0, 2000.0, false),
            ("on Kerbin", KerbinC, 0.0, 1.0 / 60, true), ("on Kerbin, warped", KerbinC, 0.0, 2000.0, false),
        })
        {
            var run = Follow(lensBody, u, c, step, 400, int.MaxValue, 20);
            Check(run.Drawn > 0 && run.NotDrawn == 0 && run.Early == 0 && run.NotRoot == 0 && run.Present == 0
                && run.NotLatest == 0 && run.BadBack == 0 && (forward ? run.Back == 0 : u >= 0 || run.Back > 0),
                $"c={c:G3}, lens {name}: {run.Drawn} instants drawn, all roots with their light in, the latest root at {run.Checked} checks "
                + $"({run.Births} jumps forward, {run.Back} steps back, {run.BadBack} wrongly, early {run.Early}, not a root {run.NotRoot}, "
                + $"present {run.Present}, not latest {run.NotLatest})");
        }
    }

    /*
     * Image births on a point swinging along the line of sight at ten times
     * c, from a lens standing still: each time a new nearer root is born the
     * trace jumps forward to it on that frame, and every frame it draws the
     * latest root as a plain trace finds it.
     */
    {
        const double c = 30;
        var w = new Wobble { D = 3e5, A = 300 * 600 / (2 * Math.PI), W = 2 * Math.PI / 600 };
        double k = 1 + w.A * w.W * PastInstant.SpeedMargin / c;
        var trace = new BodyTrace();
        int frames = 0, births = 0, caught = 0, wrong = 0;
        double prevRef = double.NaN;
        for (double tt = 2e4; tt < 2.3e4; tt += 1)
        {
            var status = trace.Follow(ref w, 0, default, tt, c, w.A * w.W, double.NegativeInfinity, int.MaxValue, out double tr);
            double x = tt;
            w.TryAt(x, out var p);
            double g = x + p.Magnitude / c - tt;
            for (int i = 0; i < 2000000 && g > 0 && g / k > 1e-9; i++)
            {
                x -= g / k;
                w.TryAt(x, out p);
                g = x + p.Magnitude / c - tt;
            }
            frames++;
            bool match = status == PastStatus.Found && tr <= x + 1e-6 && x - tr < 1e-3;
            if (!match) wrong++;
            if (x - prevRef > 10)
            {
                births++;
                if (match) caught++;
            }
            prevRef = x;
        }
        Check(births >= 3 && caught == births && wrong == 0,
            $"a point outrunning light: {births} new roots born in {frames} frames, the trace on each the frame it is born, every frame the latest root ({wrong} not)");
    }

    /*
     * New roots born just above the one drawn, closer than a second: a
     * point whose light arrives along h(x) = 100 + x/2 + 0.05 sin(20 x), seen
     * at c = 1, so every 0.3 s a pair is born a fraction of a second above
     * the root drawn. The trace must take the newborn root each time, not
     * close back on the old one from the instant drawn before.
     */
    {
        const double c = 1;
        var w = new Wiggle();
        double k = 1 + 1.5 * PastInstant.SpeedMargin / c;
        var trace = new BodyTrace();
        int frames = 0, wrong = 0, jumps = 0;
        double prev = double.NaN;
        for (double tt = 110; tt < 130; tt += 0.01)
        {
            var status = trace.Follow(ref w, 0, default, tt, c, 1.5, double.NegativeInfinity, int.MaxValue, out double tr);
            frames++;
            if (status != PastStatus.Found || !IsLatest(ref w, default, tt, c, k, tr, LatestS)) wrong++;
            if (tr - prev > 0.05) jumps++;
            prev = tr;
        }
        Check(jumps > 10 && wrong == 0, $"roots born a fraction of a second above the one drawn: {jumps} taken, {wrong} of {frames} frames not the latest root");
    }

    /* The same swinging point from a lens closing on it at c/2, every frame checked: births still land on the latest root. */
    {
        const double c = 30;
        var w = new Wobble { D = 3e5, A = 300 * 600 / (2 * Math.PI), W = 2 * Math.PI / 600 };
        double k = 1 + w.A * w.W * PastInstant.SpeedMargin / c;
        var trace = new BodyTrace();
        int frames = 0, jumps = 0, wrong = 0;
        double prev = double.NaN;
        for (double tt = 2e4; tt < 2.3e4; tt += 1)
        {
            var closing = new DVec3(0.5 * c * (tt - 2e4), 0, 0);
            var status = trace.Follow(ref w, 0, closing, tt, c, w.A * w.W, double.NegativeInfinity, int.MaxValue, out double tr);
            frames++;
            if (status != PastStatus.Found || !IsLatest(ref w, closing, tt, c, k, tr, LatestS)) wrong++;
            if (tr - prev > 10) jumps++;
            prev = tr;
        }
        Check(jumps >= 3 && wrong == 0, $"and from a lens closing at c/2: {jumps} births, {wrong} of {frames} frames not the latest root");
    }

    /*
     * What starts a trace afresh: time going back, a new light speed, a new
     * frame body, a speed bound that grows, and a top point whose reach no
     * longer covers up to t. A frame that changes none of them carries on
     * from the points kept, and a smaller speed bound keeps them too.
     */
    {
        const double c = 30;
        var w = new Wobble { D = 3e5, A = 300 * 600 / (2 * Math.PI), W = 2 * Math.PI / 600 };
        double v = w.A * w.W, k = 1 + v * PastInstant.SpeedMargin / c;
        var trace = new BodyTrace();
        double tt = 2e4;
        bool Step(double at, double speed, double cc, int frame)
        {
            var status = trace.Follow(ref w, frame, default, at, cc, speed, double.NegativeInfinity, int.MaxValue, out double tr);
            return status == PastStatus.Found && IsLatest(ref w, default, at, cc, 1 + speed * PastInstant.SpeedMargin / cc, tr, LatestS);
        }
        bool settled = Step(tt, v, c, 0) && Step(tt + 1, v, c, 0);
        bool carriesOn = !trace.Restarted;
        bool back = Step(tt + 0.5, v, c, 0) && trace.Restarted;
        Step(tt + 2, v, c, 0);
        bool speed = Step(tt + 3, v, 31, 0) && trace.Restarted;
        Step(tt + 4, v, c, 0);
        bool frame = Step(tt + 5, v, c, 1) && trace.Restarted;
        Step(tt + 6, v, c, 1);
        bool grows = Step(tt + 7, 2 * v, c, 1) && trace.Restarted;
        bool shrinks = Step(tt + 8, v, c, 1) && !trace.Restarted;
        bool top = Step(tt + 8 + 2 * 3e5 / c / k, v, c, 1) && trace.Restarted;
        Check(settled && carriesOn && back && speed && frame && grows && shrinks && top,
            $"a trace starts afresh on time going back ({back}), a new c ({speed}), frame ({frame}), a larger speed bound ({grows}) "
            + $"and a top point out of reach ({top}), and otherwise carries on ({carriesOn}, {shrinks}), each the latest root");
    }

    /*
     * A path that changes under the trace, as a ring's does: told where, the
     * trace reads its points there again (a change near t) or drops them (a
     * change deep in the past), and finds the latest root of the new path.
     */
    {
        const double c = 30;
        var near = new Shifted { Inner = new Wobble { D = 3e5, A = 300 * 600 / (2 * Math.PI), W = 2 * Math.PI / 600 } };
        double v = near.Inner.A * near.Inner.W, k = 1 + v * PastInstant.SpeedMargin / c;
        var trace = new BodyTrace();
        double tt = 2e4;
        trace.Follow(ref near, 0, default, tt, c, v, double.NegativeInfinity, int.MaxValue, out _);
        /* From just before t the point stands one light second from the lens, so the latest root is a second back. */
        near.From = tt - 1;
        near.To = double.PositiveInfinity;
        near.At = new DVec3(c, 0, 0);
        trace.Revise(near.From, double.NegativeInfinity);
        var status = trace.Follow(ref near, 0, default, tt + 1, c, v, double.NegativeInfinity, int.MaxValue, out double tr);
        bool top = status == PastStatus.Found && IsLatest(ref near, default, tt + 1, c, k, tr, LatestS) && tr > tt - 1;

        var deep = new Shifted { Inner = near.Inner };
        trace = new BodyTrace();
        trace.Follow(ref deep, 0, default, tt, c, v, double.NegativeInfinity, int.MaxValue, out double before);
        /* Before an instant just above the root drawn, the point stands a light second from the lens. */
        deep.From = double.NegativeInfinity;
        deep.To = before + 50;
        deep.At = new DVec3(c, 0, 0);
        trace.Revise(double.PositiveInfinity, deep.To);
        status = trace.Follow(ref deep, 0, default, tt + 1, c, v, double.NegativeInfinity, int.MaxValue, out tr);
        bool bottom = status == PastStatus.Found && IsLatest(ref deep, default, tt + 1, c, k, tr, LatestS);
        Check(top && bottom, $"a path changed near t ({top}) or deep in the past ({bottom}) is traced anew where it changed");
    }

    /* The old guard drew a body that may reach c at the present: now it is drawn in its past, with or without a trace. */
    {
        var duna = PastInstant.Body(ref sys, ring, DunaC, KerbinC, lens, t, 3.0, double.NegativeInfinity);
        var mun = PastInstant.Body(ref sys, ring, MunC, KerbinC, lens, t, 3.0, double.NegativeInfinity);
        Check(duna.Found && duna.Frame == 0 && duna.Ut < t - 1e8 && mun.Found && mun.Frame == KerbinC && mun.Ut < t,
            $"c=3: Duna and the Mun are drawn in their past, not the present ({t - duna.Ut:G3} s and {t - mun.Ut:G3} s back)");
    }

    /*
     * A tiny cap: the search carries across frames until it lands, nothing is
     * drawn before it does, and after a warp outruns the cap the last instant
     * stays drawn only while its light has arrived.
     */
    {
        const double c = 3.0;
        const int Cap = 8;
        var traces = new BodyTrace[sys.Count];
        for (int b = 0; b < sys.Count; b++) traces[b] = new BodyTrace();
        int carried = 0, skipped = 0, held = 0, wrong = 0, heldEarly = 0, drawnWhileFirstCarry = 0;
        int firstFound = -1;
        double tt = t;
        for (int f = 0; f < 4000; f++)
        {
            tt += f < 3000 ? 1.0 / 60 : 5000;
            PastInstant.TryAbout(ref sys, KerbinC, 0, tt, out var lensSun);
            var s = PastInstant.Body(ref sys, ring, DunaC, KerbinC, lens, tt, c, double.NegativeInfinity, traces[DunaC], Cap);
            var tr = traces[DunaC];
            if (!(tr.Reads <= Cap + 2)) wrong++;
            if (tr.Carried) carried++;
            if (tr.Carried && !s.Found) skipped++;
            if (firstFound < 0 && s.Found) firstFound = f;
            if (firstFound < 0 && s.Found && tr.Carried) drawnWhileFirstCarry++;
            if (!s.Found) continue;
            PastInstant.TryAbout(ref sys, DunaC, 0, s.Ut, out var p);
            double g = G(lensSun + lens, p, s.Ut, tt, c);
            if (!(g <= 0) || s.Ut >= tt) wrong++;
            if (tr.Held)
            {
                held++;
                if (!(g <= 0)) heldEarly++;
            }
        }
        Check(firstFound > 1 && drawnWhileFirstCarry == 0 && skipped >= firstFound,
            $"with {Cap} reads a frame Duna's first search carries over {firstFound} frames, drawn in none of them");
        Check(held > 0 && heldEarly == 0 && wrong == 0,
            $"a warp outrunning the cap holds the last instant while its light has arrived ({held} held, {skipped} skipped, {carried} carried), never early");
    }

    /*
     * Four lenses at c = 3 warped to ten seconds a frame, with the picture's
     * frame of reads: enough for all four shared evenly, not for each to take
     * its most. Offered a fair share whatever order they are solved in, every
     * lens keeps finding fresh roots, where first come first served leaves
     * the last lenses holding or hidden. The bodies' periapses are left
     * unknown, so the traces step by g / k alone and the frame stays tight.
     */
    {
        const double c = 3.0;
        const int Lenses = 4, Warped = 300, Pool = 4096, PerLens = 2048;
        var unbent = sys.Bodies();
        for (int b = 0; b < unbent.Length; b++) unbent[b].PeriapsisM = 0;
        var flat = new LightHistory { SystemDiameterM = 2.4e11 };
        flat.SetBodies(unbent);
        (int[] Fresh, long[] Reads) Share(bool fair)
        {
            var pool = new BodyReadPool(Pool, PerLens);
            var traces = new BodyTrace[Lenses, sys.Count];
            for (int l = 0; l < Lenses; l++)
                for (int b = 0; b < sys.Count; b++) traces[l, b] = new BodyTrace();
            var fresh = new int[Lenses];
            var reads = new long[Lenses];
            for (int f = 0; f < Warped; f++)
            {
                double tt = t + f * 10.0;
                int poolLeft = Pool;
                for (int l = 0; l < Lenses; l++)
                {
                    var at = new DVec3(0, 600000 + 1e5 * l, 0);
                    int share = fair ? pool.Offer(f) : Math.Min(PerLens, poolLeft);
                    int left = share;
                    for (int n = 0; n < sys.Count; n++)
                    {
                        int b = (f + n) % sys.Count;
                        var trace = traces[l, b];
                        int budget = Math.Min(BodyTrace.DefaultBudget, Math.Max(2, left));
                        var s = PastInstant.Body(ref sys, flat, b, KerbinC, at, tt, c, double.NegativeInfinity, trace, budget);
                        left -= trace.Reads;
                        if (s.Found && s.Frame != b && !trace.Held && !trace.Carried) fresh[l]++;
                    }
                    reads[l] += share - left;
                    if (fair) pool.Spend(share - left);
                    else poolLeft -= share - left;
                }
            }
            return (fresh, reads);
        }
        var shared = Share(true);
        var first = Share(false);
        int Min(int[] a) { int m = int.MaxValue; foreach (var x in a) m = Math.Min(m, x); return m; }
        int Max(int[] a) { int m = 0; foreach (var x in a) m = Math.Max(m, x); return m; }
        Check(Min(shared.Fresh) > 0 && Min(shared.Fresh) * 2 >= Max(shared.Fresh) && Min(first.Fresh) * 2 < Max(first.Fresh),
            $"lenses sharing a tight frame of reads each find fresh roots: [{string.Join(", ", shared.Fresh)}] "
            + $"(reads [{string.Join(", ", shared.Reads)}]); first come first served gives [{string.Join(", ", first.Fresh)}]");
    }

    /*
     * A lens receding from a body faster than light: its root moves back
     * every frame and each frame's steps land below the last. Points whose
     * neighbours clear the span across them are dropped, so the trace stays
     * short rather than filling and thinning itself into a retrace: stepping
     * by g / k alone, and with the bend of a path that does not accelerate.
     */
    foreach (var (name, accel) in new[] { ("by g / k", double.PositiveInfinity), ("by the bend", 0.0) })
    {
        const double c = 3.0;
        var trace = new BodyTrace();
        int most = 0, carried = 0, late = 0;
        for (int f = 0; f < 4000; f++)
        {
            var line = new Line { X0 = 3e6, V = 300 };
            double tt = 1e6 + f / 22.0;
            var at = new DVec3(-3000.0 * f / 22, 0, 0);
            var status = trace.Follow(ref line, 0, at, tt, c, 350, double.NegativeInfinity, BodyTrace.DefaultBudget, out double tr, accel);
            if (f < 200) continue;
            most = Math.Max(most, trace.Points);
            if (trace.Carried) carried++;
            if (f % 100 == 0 && !(status == PastStatus.Found && IsLatest(ref line, at, tt, c, 1 + 350 * PastInstant.SpeedMargin / c, tr, LatestS))) late++;
        }
        Check(most < 64 && carried == 0 && late == 0,
            $"a lens receding faster than light keeps its trace short, stepping {name} (at most {most} points, {carried} carried, {late} not latest)");
    }

    /* Cost: the bodies from a lens on Kerbin, frame by frame at 1x once settled, and the first frames. */
    foreach (var c in new[] { 30.0, 3.0 })
    {
        var traces = new BodyTrace[sys.Count];
        for (int b = 0; b < sys.Count; b++) traces[b] = new BodyTrace();
        long firstReads = 0;
        for (int b = 0; b < sys.Count; b++)
        {
            PastInstant.Body(ref sys, ring, b, KerbinC, lens, t, c, double.NegativeInfinity, traces[b], int.MaxValue);
            firstReads += traces[b].Reads;
        }
        const int Steady = 2000;
        long reads = 0;
        int maxReads = 0, notFound = 0, carried = 0;
        var watch = Stopwatch.StartNew();
        for (int f = 1; f <= Steady; f++)
        {
            int frameReads = 0;
            for (int b = 0; b < sys.Count; b++)
            {
                var s = PastInstant.Body(ref sys, ring, b, KerbinC, lens, t + f / 60.0, c, double.NegativeInfinity, traces[b]);
                if (!s.Found) notFound++;
                if (traces[b].Carried) carried++;
                frameReads += traces[b].Reads;
            }
            reads += frameReads;
            maxReads = Math.Max(maxReads, frameReads);
        }
        watch.Stop();
        double perFrame = watch.Elapsed.TotalMilliseconds * 1000 / Steady;
        Console.WriteLine($"     c={c:G3}: first sight {firstReads} reads; then {(double)reads / Steady:F0} reads a frame (max {maxReads}), {perFrame:F1} us a frame");
        Console.WriteLine($"     c={c:G3}: not found {notFound}, carried {carried}");
        Check(notFound == 0 && carried == 0 && (double)reads / Steady < 40 * sys.Count,
            $"c={c:G3}: once settled every body is found each frame within its budget, at a few reads each");
    }
    /*
     * A lens in orbit at 1x at c = 3, every body solved each frame with the
     * picture's reads, on stock orbits with their eccentricities and
     * inclinations. The lens moves thousands of times faster than light, so
     * each body's latest root moves seconds a frame and often backwards. Once
     * settled every body is drawn each frame, never carried or hidden, at
     * its latest root. Stepping by g / k alone this took hundreds of reads a
     * body a frame, and a trace full of points thinned itself into
     * retracing, so a body or two was hidden most frames.
     */
    {
        const double c = 3.0, Dt = 1.0 / 22;
        const int Settle = 400, Run = 800, Pool = 4096, PerLens = 2048;
        var orbits = Orbits.Stock();
        var stock = new LightHistory { SystemDiameterM = 2.4e11 };
        stock.SetBodies(orbits.Bodies());
        foreach (var (name, lensBody, radius, t0) in new[]
        {
            ("Mun orbit", Orbits.Mun, 250000.0, 6.42629e8), ("low Kerbin orbit", Orbits.Kerbin, 700000.0, 6.3913e8),
        })
        {
            double w = Math.Sqrt(orbits.Mu[lensBody] / (radius * radius * radius));
            var traces = new BodyTrace[orbits.Count];
            for (int b = 0; b < orbits.Count; b++) traces[b] = new BodyTrace();
            var pool = new BodyReadPool(Pool, PerLens);
            int carried = 0, hidden = 0, ahead = 0, notLatest = 0, checks = 0, maxReads = 0;
            long reads = 0;
            for (int f = 0; f < Settle + Run; f++)
            {
                double tt = t0 + f * Dt;
                var at = new DVec3(radius * Math.Cos(w * tt), radius * Math.Sin(w * tt), 0);
                int share = pool.Offer(f), left = share;
                for (int n = 0; n < orbits.Count; n++)
                {
                    int b = (f + n) % orbits.Count;
                    int budget = Math.Min(BodyTrace.DefaultBudget, Math.Max(2, left));
                    var s = PastInstant.Body(ref orbits, stock, b, lensBody, at, tt, c, double.NegativeInfinity, traces[b], budget);
                    left -= traces[b].Reads;
                    if (f < Settle || s.Frame == b) continue;
                    if (traces[b].Carried) carried++;
                    if (!s.Found)
                    {
                        hidden++;
                        continue;
                    }
                    PastInstant.TryAbout(ref orbits, lensBody, s.Frame, tt, out var lensAt);
                    PastInstant.TryAbout(ref orbits, b, s.Frame, s.Ut, out var p);
                    if (!(G(lensAt + at, p, s.Ut, tt, c) <= 0) || s.Ut >= tt) ahead++;
                    if (f % 200 == 0)
                    {
                        checks++;
                        var path = new BodyPath<Orbits> { Tree = orbits, Body = b, Frame = s.Frame };
                        double k = 1 + orbits.SpeedBound(b, s.Frame) * PastInstant.SpeedMargin / c;
                        if (!IsLatest(ref path, lensAt + at, tt, c, k, s.Ut, LatestS)) notLatest++;
                    }
                }
                pool.Spend(share - left);
                if (f >= Settle)
                {
                    reads += share - left;
                    maxReads = Math.Max(maxReads, share - left);
                }
            }
            Check(carried == 0 && hidden == 0 && ahead == 0 && notLatest == 0 && checks > 0,
                $"c=3, a lens in {name} at 1x: every body drawn every frame at its latest root ({carried} carried, {hidden} hidden, "
                + $"{ahead} early, {notLatest} not latest at {checks} checks; {(double)reads / Run:F0} reads a frame, max {maxReads})");
        }
    }

    /*
     * The Deck's shape: six lenses sharing the frame's reads at 35 fps,
     * c = 3, 300 frames at 1x, 300 at 100000x, then 2000 at 1x, in Mun orbit
     * and low Kerbin orbit. The warp hides bodies, as the budget allows;
     * back at 1x every body is drawn again within RecoverFrames and stays
     * drawn, never early. An instant found afresh, not held while the search
     * carries on, is the latest root wherever checked.
     */
    {
        const double c = 3.0, Fps = 35, Warp = 100000;
        const int Lenses = 6, Before = 300, Warped = 300, After = 2000, RecoverFrames = 70, Pool = 4096, PerLens = 2048;
        var orbits = Orbits.Stock();
        var stock = new LightHistory { SystemDiameterM = 2.4e11 };
        stock.SetBodies(orbits.Bodies());
        foreach (var (name, lensBody, radius, t0) in new[]
        {
            ("Mun orbit", Orbits.Mun, 250000.0, 6.42629e8), ("low Kerbin orbit", Orbits.Kerbin, 700000.0, 6.3913e8),
        })
        {
            double w = Math.Sqrt(orbits.Mu[lensBody] / (radius * radius * radius));
            var traces = new BodyTrace[Lenses, orbits.Count];
            for (int l = 0; l < Lenses; l++)
                for (int b = 0; b < orbits.Count; b++) traces[l, b] = new BodyTrace();
            var pool = new BodyReadPool(Pool, PerLens);
            int warpHidden = 0, recovering = 0, lastHidden = -1, afterHidden = 0, afterHeld = 0, ahead = 0, notLatest = 0, checks = 0;
            double tt = t0;
            for (int f = 0; f < Before + Warped + After; f++)
            {
                tt += f >= Before && f < Before + Warped ? Warp / Fps : 1 / Fps;
                int since = f - Before - Warped;
                for (int l = 0; l < Lenses; l++)
                {
                    double phase = w * tt + l;
                    var at = new DVec3(radius * Math.Cos(phase), radius * Math.Sin(phase), 0);
                    int share = pool.Offer(f), left = share;
                    for (int n = 0; n < orbits.Count; n++)
                    {
                        int b = (f + n) % orbits.Count;
                        int budget = Math.Min(BodyTrace.DefaultBudget, Math.Max(2, left));
                        var s = PastInstant.Body(ref orbits, stock, b, lensBody, at, tt, c, double.NegativeInfinity, traces[l, b], budget);
                        left -= traces[l, b].Reads;
                        if (s.Frame == b || f < Before / 2) continue;
                        if (!s.Found)
                        {
                            if (since < 0) warpHidden++;
                            else if (since < RecoverFrames) recovering++;
                            else afterHidden++;
                            if (since >= 0) lastHidden = since;
                            continue;
                        }
                        PastInstant.TryAbout(ref orbits, lensBody, s.Frame, tt, out var lensAt);
                        PastInstant.TryAbout(ref orbits, b, s.Frame, s.Ut, out var p);
                        if (!(G(lensAt + at, p, s.Ut, tt, c) <= 0) || s.Ut >= tt) ahead++;
                        if (traces[l, b].Held)
                        {
                            if (since >= RecoverFrames) afterHeld++;
                            continue;
                        }
                        if (since >= 0 && f % 10 == 0 && (f / 10) % Lenses == l && (f / 10 / Lenses) % orbits.Count == b % orbits.Count)
                        {
                            checks++;
                            var path = new BodyPath<Orbits> { Tree = orbits, Body = b, Frame = s.Frame };
                            double k = 1 + orbits.SpeedBound(b, s.Frame) * PastInstant.SpeedMargin / c;
                            if (!IsLatest(ref path, lensAt + at, tt, c, k, s.Ut, LatestS)) notLatest++;
                        }
                    }
                    pool.Spend(share - left);
                }
            }
            Console.WriteLine($"     {name}: {warpHidden} body frames hidden over the warp, {recovering} after it, the last {lastHidden} frames after");
            Check(afterHidden == 0 && afterHeld == 0 && lastHidden < RecoverFrames && ahead == 0 && notLatest == 0 && checks > 0,
                $"c=3, six lenses in {name} after 100000x: every body drawn again within {RecoverFrames} frames ({lastHidden}) and after "
                + $"({afterHidden} hidden, {afterHeld} held), never early ({ahead}), the latest root at {checks} checks ({notLatest} not)");
        }
    }

    /*
     * With no acceleration bound (a body off its Kepler path, or one whose
     * periapsis is not known) the trace steps by g / k alone, at 1x with one
     * lens and the picture's reads. Joined spans down to the root would run
     * out within frames and cost the whole run again, so thinning keeps them
     * short: Eeloo from Mun orbit and Minmus from low Kerbin orbit are hidden
     * in no more frames than before per-span clear clocks (122 and 448 of
     * 2600).
     */
    {
        const double c = 3.0, Fps = 35;
        const int Run = 2600;
        var orbits = Orbits.Stock();
        var unknownPe = orbits.Bodies();
        for (int b = 0; b < unknownPe.Length; b++) unknownPe[b].PeriapsisM = 0;
        var unbounded = new LightHistory { SystemDiameterM = 2.4e11 };
        unbounded.SetBodies(unknownPe);
        foreach (var (name, lensBody, radius, t0, watched, most) in new[]
        {
            ("Eeloo from Mun orbit", Orbits.Mun, 250000.0, 6.42629e8, 16, 122),
            ("Minmus from low Kerbin orbit", Orbits.Kerbin, 700000.0, 6.3913e8, 3, 448),
        })
        {
            double w = Math.Sqrt(orbits.Mu[lensBody] / (radius * radius * radius));
            var traces = new BodyTrace[orbits.Count];
            for (int b = 0; b < orbits.Count; b++) traces[b] = new BodyTrace();
            var pool = new BodyReadPool(4096, 2048);
            int hidden = 0, first = -1, ahead = 0;
            for (int f = 0; f < Run; f++)
            {
                double tt = t0 + f / Fps;
                var at = new DVec3(radius * Math.Cos(w * tt), radius * Math.Sin(w * tt), 0);
                int share = pool.Offer(f), left = share;
                for (int n = 0; n < orbits.Count; n++)
                {
                    int b = (f + n) % orbits.Count;
                    var s = PastInstant.Body(ref orbits, unbounded, b, lensBody, at, tt, c, double.NegativeInfinity, traces[b],
                        Math.Min(BodyTrace.DefaultBudget, Math.Max(2, left)));
                    left -= traces[b].Reads;
                    if (b != watched) continue;
                    if (!s.Found)
                    {
                        hidden++;
                        continue;
                    }
                    if (first < 0) first = f;
                    PastInstant.TryAbout(ref orbits, lensBody, s.Frame, tt, out var lensAt);
                    PastInstant.TryAbout(ref orbits, b, s.Frame, s.Ut, out var p);
                    if (!(G(lensAt + at, p, s.Ut, tt, c) <= 0) || s.Ut >= tt) ahead++;
                }
                pool.Spend(share - left);
            }
            Check(first >= 0 && first < 60 && hidden <= most && ahead == 0,
                $"c=3, no acceleration bound, {name}: found at frame {first}, hidden {hidden} of {Run} frames (at most {most}), never early ({ahead})");
        }
    }

    /*
     * Traces held to a few dozen points, so they thin often, while stepping
     * through a gap too: the points still run down from t, every instant
     * drawn has its light in, and one found afresh is the latest root.
     */
    {
        const double c = 3.0, Fps = 35, Radius = 700000.0, T0 = 6.3913e8;
        const int Lenses = 3, Few = 48, Before = 100, Warped = 100, After = 400;
        var orbits = Orbits.Stock();
        var stock = new LightHistory { SystemDiameterM = 2.4e11 };
        stock.SetBodies(orbits.Bodies());
        double w = Math.Sqrt(orbits.Mu[Orbits.Kerbin] / (Radius * Radius * Radius));
        var traces = new BodyTrace[Lenses, orbits.Count];
        for (int l = 0; l < Lenses; l++)
            for (int b = 0; b < orbits.Count; b++) traces[l, b] = new BodyTrace(Few);
        var pool = new BodyReadPool(4096, 2048);
        int disordered = 0, ahead = 0, notLatest = 0, checks = 0, gapThins = 0;
        double tt = T0;
        for (int f = 0; f < Before + Warped + After; f++)
        {
            tt += f >= Before && f < Before + Warped ? 100000 / Fps : 1 / Fps;
            for (int l = 0; l < Lenses; l++)
            {
                double phase = w * tt + l;
                var at = new DVec3(Radius * Math.Cos(phase), Radius * Math.Sin(phase), 0);
                int share = pool.Offer(f), left = share;
                for (int n = 0; n < orbits.Count; n++)
                {
                    int b = (f + n) % orbits.Count;
                    var trace = traces[l, b];
                    var s = PastInstant.Body(ref orbits, stock, b, Orbits.Kerbin, at, tt, c, double.NegativeInfinity, trace,
                        Math.Min(BodyTrace.DefaultBudget, Math.Max(2, left)));
                    left -= trace.Reads;
                    if (!trace.Descending) disordered++;
                    if (!s.Found || s.Frame == b) continue;
                    PastInstant.TryAbout(ref orbits, Orbits.Kerbin, s.Frame, tt, out var lensAt);
                    PastInstant.TryAbout(ref orbits, b, s.Frame, s.Ut, out var p);
                    if (!(G(lensAt + at, p, s.Ut, tt, c) <= 0) || s.Ut >= tt) ahead++;
                    if (!trace.Held && !trace.Carried && f % 7 == 0 && (f / 7) % Lenses == l)
                    {
                        checks++;
                        var path = new BodyPath<Orbits> { Tree = orbits, Body = b, Frame = s.Frame };
                        if (!IsLatest(ref path, lensAt + at, tt, c, 1 + orbits.SpeedBound(b, s.Frame) * PastInstant.SpeedMargin / c, s.Ut, LatestS))
                            notLatest++;
                    }
                }
                pool.Spend(share - left);
            }
        }
        foreach (var trace in traces) gapThins += trace.GapThins;
        Check(gapThins > 0 && disordered == 0 && ahead == 0 && notLatest == 0 && checks > 0,
            $"traces of {Few} points thin while stepping through a gap ({gapThins} times) and keep their points in order ({disordered} not), "
            + $"never early ({ahead}), the latest root at {checks} checks ({notLatest} not)");
    }

    /*
     * A body whose acceleration bound turns finite, as when its ring track
     * is trimmed and it is back on its Kepler path, bends by it from the next
     * frame on: the same traces, read through a ring with no periapses and
     * then the stock one, take far fewer reads once the bound is known.
     */
    {
        const double c = 3.0, Dt = 1.0 / 22, Radius = 250000.0, T0 = 6.42629e8;
        const int Steps = 400;
        var orbits = Orbits.Stock();
        var known = new LightHistory { SystemDiameterM = 2.4e11 };
        known.SetBodies(orbits.Bodies());
        var unknown = new LightHistory { SystemDiameterM = 2.4e11 };
        var nameless = orbits.Bodies();
        for (int b = 0; b < nameless.Length; b++) nameless[b].PeriapsisM = 0;
        unknown.SetBodies(nameless);
        double w = Math.Sqrt(orbits.Mu[Orbits.Mun] / (Radius * Radius * Radius));
        var traces = new BodyTrace[orbits.Count];
        for (int b = 0; b < orbits.Count; b++) traces[b] = new BodyTrace();
        long flat = 0, bent = 0;
        int restarts = 0;
        for (int f = 0; f < Steps; f++)
        {
            double tt = T0 + f * Dt;
            var at = new DVec3(Radius * Math.Cos(w * tt), Radius * Math.Sin(w * tt), 0);
            var through = f < Steps / 2 ? unknown : known;
            for (int b = 0; b < orbits.Count; b++)
            {
                PastInstant.Body(ref orbits, through, b, Orbits.Mun, at, tt, c, double.NegativeInfinity, traces[b], int.MaxValue);
                if (f >= Steps / 2 && traces[b].Restarted) restarts++;
                if (f >= Steps / 4 && f < Steps / 2) flat += traces[b].Reads;
                if (f >= 3 * Steps / 4) bent += traces[b].Reads;
            }
        }
        Check(restarts == 0 && bent * 4 < flat,
            $"a body whose acceleration bound turns finite bends by it without starting afresh ({flat * 4.0 / Steps:F0} reads a frame, then {bent * 4.0 / Steps:F0})");
    }

    var self = PastInstant.Body(ref sys, ring, KerbinC, KerbinC, lens, t, 3e5, double.NegativeInfinity);
    var sun = PastInstant.Body(ref sys, ring, 0, KerbinC, lens, t, 3e5, double.NegativeInfinity);
    var up = PastInstant.Body(ref sys, ring, KerbinC, MunC, lens, t, 3e5, double.NegativeInfinity);
    Check(self.Found && self.Ut == t && self.Frame == KerbinC && sun.Found && sun.Ut == t && sun.Frame == 0
        && up.Found && up.Ut == t && up.Frame == KerbinC,
        "the lens's own body and the bodies above it are their frame's centre: drawn where they are");

    /* A small pull moves the Mun off its circle; Kerbin keeps to its own. */
    var pr = new LightHistory { SystemDiameterM = 2.4e11 };
    pr.SetBodies(sys.Bodies());
    pr.SetLightSpeed(3e5);
    DVec3 Pulled(double ut)
    {
        sys.TryOffset(MunC, ut, out var m);
        double dt = ut - 1000;
        return m + new DVec3(0, 0, 0.5 * 2e-3 * dt * dt);
    }
    double latched = double.NaN;
    for (int k = 0; k <= 100; k++)
    {
        double ut = 1000 + k;
        sys.TryOffset(KerbinC, ut, out var kp);
        sys.TryOffset(MunC, ut, out var mk);
        pr.ObserveBody(KerbinC, State(ut, 0, kp, default), 0);
        var mp = Pulled(ut);
        var mv = (Pulled(ut + 0.01) - Pulled(ut - 0.01)) * 50;
        pr.ObserveBody(MunC, State(ut, KerbinC, mp, mv), (mp - mk).Magnitude);
        if (double.IsNaN(latched) && pr.IsOffKepler(MunC)) latched = ut;
    }
    Check(pr.IsOffKepler(MunC) && !pr.IsOffKepler(KerbinC) && pr.BodiesOffKepler == 1
        && pr.Track(LightHistory.BodyKey(KerbinC)) == null && pr.Track(LightHistory.BodyKey(MunC)) != null,
        $"the pulled Mun departs its Kepler path and gets a track (at {latched - 1000:F0} s), Kerbin gets none");
    var rb = new RingBodies<Circles>(pr, sys);
    var ringMun = PastInstant.Body(ref rb, pr, MunC, KerbinC, lens, 1100, 3e5, double.NegativeInfinity);
    PastInstant.TryAbout(ref rb, MunC, KerbinC, ringMun.Ut, out var munThen);
    double ringG = G(lens, munThen, ringMun.Ut, 1100, 3e5);
    Check(ringMun.Found && ringMun.Ut > latched && ringG <= 0 && ringG > -1e-3 && (munThen - Pulled(ringMun.Ut)).Magnitude < 1,
        $"the Mun is read from its track, on its pulled path ({1100 - ringMun.Ut:F2} s back)");
    var early = PastInstant.Body(ref rb, pr, MunC, KerbinC, lens, latched + 10, 3e5, double.NegativeInfinity);
    sys.TryOffset(MunC, early.Ut, out var munKepler);
    PastInstant.TryAbout(ref rb, MunC, KerbinC, early.Ut, out var munEarly);
    Check(early.Found && early.Ut < latched && (munEarly - munKepler).Magnitude < 1e-6,
        "before its track starts it is read from its Kepler orbit, so it is never left unsolved");
    var kerbinRead = PastInstant.Body(ref rb, pr, KerbinC, MunC, lens, 1100, 3e5, double.NegativeInfinity);
    Check(kerbinRead.Found, "a body on its Kepler path still reads from its orbit");

    /* Cost: every body of a sixteen-body system each frame. */
    const int Frames = 2000;
    int found = 0;
    var sw = Stopwatch.StartNew();
    for (int f = 0; f < Frames; f++)
        for (int b = 0; b < sys.Count; b++)
            if (PastInstant.Body(ref sys, ring, b, KerbinC, lens, t + f / 60.0, 299792458.0, double.NegativeInfinity).Found) found++;
    sw.Stop();
    double us = sw.Elapsed.TotalMilliseconds * 1000 / Frames;
    Console.WriteLine($"     {sys.Count} bodies a frame at real light speed: {us:F1} us");
    Check(found == Frames * sys.Count && us < 500, "every body is solved each frame well inside a millisecond");
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
        $"a lower light speed in the same frame solves again rather than serve the faster one's instant ({atSlow.Status})");
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
    shortRing.SetLightSpeed(c);
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

/* ---- a source that places the craft only from its record's start ---- */
{
    /* A world model's record from the scope's station 447 m out, read by a lens 500 m out: about 7.45 s against 8.33 s. */
    const double c = 60, t = 1000;
    var lens = new DVec3(0, 0, 0);
    double root = t - 500 / c;

    var late = new FromUt { Inner = new Line { X0 = 500 }, From = t - 7.45 };
    var st = PastInstant.SolveFrom(ref late, lens, t, c, 0, late.From, t, out double tr);
    Check(st == PastStatus.Found && tr == late.From,
        $"a record starting after the light-time root places the craft at the record's start ({st}, {tr - t:F3} s)");

    var early = new FromUt { Inner = new Line { X0 = 500 }, From = t - 9 };
    st = PastInstant.SolveFrom(ref early, lens, t, c, 0, early.From, t, out tr);
    Check(st == PastStatus.Found && tr <= root && root - tr <= PastInstant.ToleranceS * 2,
        $"a record starting before the root places the craft at the root ({st}, {tr - t:F3} s)");

    st = PastInstant.SolveFrom(ref late, lens, t, c, 0, double.NegativeInfinity, t, out _);
    Check(st == PastStatus.Unsolved, $"with no start given, a path that refuses the root still has no instant ({st})");
    var line = new Line { X0 = 500 };
    st = PastInstant.SolveFrom(ref line, lens, t, c, 0, t - 7.45, t, out _);
    Check(st == PastStatus.Found, "a whole path with a start after its root is held at the start too");
    st = PastInstant.Solve(ref line, lens, t, c, 0, t - 7.45, t, out _);
    Check(st == PastStatus.Unseen, "plain Solve keeps Unseen for a craft whose history starts after its root");

    var future = new FromUt { Inner = new Line { X0 = 500 }, From = t + 1 };
    st = PastInstant.SolveFrom(ref future, lens, t, c, 0, future.From, t, out _);
    Check(st == PastStatus.Unseen, $"a record that starts after the present places nothing ({st})");

    /* Just launched: the record froze at the last sample on the pad, and the craft climbs away from the lens at up to 80 m/s. */
    double frozen = t - 7.45;
    var climb = new FromUt { Inner = new Line { X0 = 500 - 40 * frozen, V = 40 }, From = frozen };
    const double step = 0.5;
    bool held = true, arrived = true;
    double last = double.NegativeInfinity, firstPast = double.NaN;
    for (double now = t; now < t + 30; now += step)
    {
        st = PastInstant.SolveFrom(ref climb, lens, now, c, 80, frozen, now, out tr);
        if (st != PastStatus.Found || tr < frozen || !(tr >= last)) held = false;
        if (tr > frozen && !PastInstant.Arrived(ref climb, lens, now, c, tr)) arrived = false;
        if (tr > frozen && double.IsNaN(firstPast)) firstPast = tr - frozen;
        last = tr;
    }
    Check(held && last > frozen + 1, $"a craft that just launched is placed from its record's start on, never refused ({last - frozen:F2} s past it at the end)");
    /* The root moves on at c / (c + v) = 0.6 s a second, so the first frame past the start is within a step of it. */
    Check(arrived && firstPast <= step,
        $"once its root passes the record's start the craft is drawn at the root, whose light has arrived, without a jump ({firstPast:F3} s past the start)");
}

/* ---- a far lens on a world model republished more often than its light time ---- */
{
    /* A craft 300 km out crossing at 200 m/s, light at 100 km/s: 3 s of light time against a record republished every 2 s from its publish UT. */
    const double c = 100000, x0 = 300000, v = 200, publish = 2.0, t0 = 1000;
    var lens = new DVec3(0, 0, 0);
    DVec3 Truth(double ut) => new DVec3(x0, v * ut, 0);
    double heldRun = 0, oldRun = 0, worstMiss = 0, lastY = double.NaN, worstStill = 0;
    bool found = true, rising = true;
    for (double t = t0; t < t0 + 20; t += 1.0 / 60)
    {
        double from = t0 + Math.Floor((t - t0) / publish) * publish;
        var record = new Crossing { X = x0, V = v };
        var windowed = new CrossingFrom { Inner = record, From = from };
        var old = PastInstant.SolveFrom(ref windowed, lens, t, c, v, from, t, out double oldTr);
        var st = PastInstant.SolveAlong(ref record, ref windowed, lens, t, c, v, from, t, out double tr);
        if (st != PastStatus.Found) { found = false; continue; }
        record.TryAt(tr, out var at);
        /* Where the light arriving now left the craft. */
        double truthTr = t - Truth(tr).Magnitude / c;
        worstMiss = Math.Max(worstMiss, (at - Truth(truthTr)).Magnitude);
        if (!double.IsNaN(lastY))
        {
            if (!(at.Y > lastY)) rising = false;
            worstStill = Math.Max(worstStill, Math.Abs(at.Y - lastY - v / 60));
        }
        lastY = at.Y;
        if (old == PastStatus.Found && oldTr == from) oldRun += 1.0 / 60;
        if (tr == from) heldRun += 1.0 / 60;
    }
    Check(oldRun > 19, $"solved from the record's start, the far craft stands at the start for the whole interval ({oldRun:F1} of 20 s)");
    Check(found && heldRun == 0 && rising, $"read back along its record, the far craft moves on every frame and is never held at a start ({heldRun:F1} s held)");
    Check(worstMiss < 1 && worstStill < 0.1, $"it is placed where its light left it ({worstMiss:F3} m off), moving at its own speed every frame ({worstStill:F4} m off a frame's travel)");

    var refuses = new CrossingFrom { Inner = new Crossing { X = x0, V = v }, From = t0 };
    var st2 = PastInstant.SolveAlong(ref refuses, ref refuses, lens, t0 + 1, c, v, t0, t0 + 1, out double tr2);
    Check(st2 == PastStatus.Found && tr2 == t0, $"a record that cannot be read back is still placed at its start ({st2})");
}

/* ---- a read-back is capped, and held at the cap past it ---- */
{
    /* 600 km at 100 km/s: 6 s of light time, deeper than the cap on every record. From UT 0, so the crossing stays near its closest. */
    const double c = 100000, v = 200, t0 = 0;
    var lens = new DVec3(0, 0, 0);
    bool capped = true, found = true;
    double lastTr = double.NegativeInfinity;
    for (double t = t0; t < t0 + 1.9; t += 1.0 / 60)
    {
        var record = new Crossing { X = 600000, V = v };
        var windowed = new CrossingFrom { Inner = record, From = t0 };
        var st = PastInstant.SolveAlong(ref record, ref windowed, lens, t, c, v, t0, t, out double tr);
        found &= st == PastStatus.Found;
        capped &= tr == t0 - PastInstant.MaxReadBackS && tr >= lastTr;
        lastTr = tr;
    }
    Check(found && capped, $"a light time past the cap holds the craft {PastInstant.MaxReadBackS} s before the record's start, never further");

    /* 350 km: 3.5 s, inside the cap, read back to the root itself. */
    var near = new Crossing { X = 350000, V = v };
    var nearWindowed = new CrossingFrom { Inner = near, From = t0 };
    var st2 = PastInstant.SolveAlong(ref near, ref nearWindowed, lens, t0, c, v, t0, t0, out double tr2);
    near.TryAt(tr2, out var at2);
    Check(st2 == PastStatus.Found && tr2 > t0 - PastInstant.MaxReadBackS && Math.Abs(tr2 + at2.Magnitude / c - t0) < 1e-5,
        $"a light time inside the cap is read back to its root ({t0 - tr2:F3} s back)");
}

/* ---- a place on a turning body whose ground outruns light ---- */
{
    /* Kerbin's equator turns at about 175 m/s; a lens and a craft on the pad 500 m apart, c = 60. */
    const double c = 60, radius = 600000, t0 = 1000;
    double w = 175 / radius;
    double lo = t0 - 7.45;
    DVec3 OnGround(double angle, double ut) => new DVec3(radius * Math.Cos(angle + w * ut), radius * Math.Sin(angle + w * ut), 0);
    DVec3 ToBody(DVec3 v, double ut) => new DVec3(
        v.X * Math.Cos(-w * ut) - v.Y * Math.Sin(-w * ut), v.X * Math.Sin(-w * ut) + v.Y * Math.Cos(-w * ut), v.Z);
    double craftAngle = 500 / radius;

    /* As the rig solved it before, on the inertial path. Behind the lens in the turn there is no root after the
       record's start, so the craft stays at its inertial place then; ahead, the root is 128 m off, not 500. */
    foreach (double side in new[] { -1.0, 1.0 })
    {
        var inertial = new Spun { Radius = radius, Angle = side * craftAngle, W = w, From = lo };
        double firstOld = double.NaN, lastOld = double.NaN;
        for (double t = t0; t <= t0 + 300; t += 1)
        {
            var lensAt = OnGround(0, t);
            var st = PastInstant.SolveFrom(ref inertial, lensAt, t, c, 175, lo, t, out double tr);
            inertial.TryAt(tr, out var drawn);
            double range = (drawn - lensAt).Magnitude;
            if (double.IsNaN(firstOld)) firstOld = range;
            lastOld = st == PastStatus.Found ? range : double.NaN;
        }
        Check(side < 0 ? firstOld > 1500 && lastOld > 50000 : Math.Abs(lastOld - 500) > 300,
            $"solved on its inertial path, a craft {(side < 0 ? "behind" : "ahead of")} the lens is drawn wrong ({firstOld:F0} m at once, {lastOld:F0} m after 5 minutes)");
    }

    /* Placed where the source puts it now, at the instant of its light time. */
    bool steady = true, instants = true;
    double prev = double.NegativeInfinity;
    DVec3 firstDir = default;
    double worstDir = 0;
    for (double t = t0; t <= t0 + 300; t += 1)
    {
        var lensAt = OnGround(0, t);
        var drawn = OnGround(craftAngle, t);
        double d = (drawn - lensAt).Magnitude;
        double at = PastInstant.BodyFixedInstant(d, t, c, lo);
        if (Math.Abs(d - 500) > 0.01) steady = false;
        if (at < lo || at > t || at < prev || Math.Abs(at - Math.Max(lo, t - d / c)) > 1e-9) instants = false;
        prev = at;
        var dir = ToBody(drawn - lensAt, t) * (1 / d);
        if (t == t0) firstDir = dir;
        worstDir = Math.Max(worstDir, (dir - firstDir).Magnitude);
    }
    Check(steady && worstDir < 1e-9, $"placed on its ground, the craft stays 500 m off in one direction on the body over 5 minutes ({worstDir:E1})");
    Check(instants, "its instant is its light time back from now, never before the record's start nor after now, never stepping back");
    Check(PastInstant.BodyFixedInstant(500, t0, c, lo) == lo && PastInstant.BodyFixedInstant(500, t0, c, t0 - 9) == t0 - 500 / c,
        "the record's start bounds the instant while the light time reaches past it");
    Check(PastInstant.BodyFixedInstant(500, t0, c, t0 + 1) == t0, "a record that starts after now takes the present");
}

/* ---- the turning weight about the lens's body ---- */
{
    double w = 175 / 600000.0;
    var spin = BodySpin.Of(Kerbin, new DVec3(0, 0, w), 600000, 70000);
    Check(PastInstant.TurningWeight(spin, 0) == 1 && PastInstant.TurningWeight(spin, 70000) == 1
        && PastInstant.TurningWeight(spin, 140000) == 0 && PastInstant.TurningWeight(spin, 1e7) == 0
        && Math.Abs(PastInstant.TurningWeight(spin, 105000) - 0.5) < 1e-12,
        "a craft turns with the body up to the threshold, not at all from twice it, and half way at the band's middle");
    bool monotone = true;
    double last = 1, steepest = 0;
    for (double h = 60000; h <= 150000; h += 10)
    {
        double f = PastInstant.TurningWeight(spin, h);
        if (f > last) monotone = false;
        steepest = Math.Max(steepest, (last - f) / 10);
        last = f;
    }
    Check(monotone && steepest <= PastInstant.TurningSlope(spin) * 1.0001,
        $"the weight only falls with altitude, never faster than its stated slope ({steepest * 1e5:F3} per 100 km)");
    var p = new DVec3(700000, 0, 0);
    Check(PastInstant.Turn(spin, Mun, p, false, 0, 100).Magnitude == 0 && PastInstant.Turn(default, Kerbin, p, false, 0, 100).Magnitude == 0
        && !BodySpin.Of(Kerbin, default, 600000, 70000).Turns && !BodySpin.Of(Kerbin, new DVec3(0, 0, w), 600000, double.PositiveInfinity).Turns,
        "nothing turns about another body, with no spin, or with no threshold");
    var high = new DVec3(600000 + 300000, 0, 0);
    Check((PastInstant.Turn(spin, Kerbin, high, true, 0, 100) - new DVec3(0, 0, w * 100)).Magnitude < 1e-15,
        "a craft on the surface turns in full whatever its altitude, as AboutDrawnBody carries it");
}

/* ---- a live place from truth is never turned; a conic read at its past is ---- */
{
    /*
     * Truth gives a landed or loaded craft's place as it is now whatever UT
     * is asked. Solved and aimed as the rig does (KnownTurned on the path,
     * then on the fix at the instant), it must stay exactly on that place,
     * which the picture draws it on. An unloaded craft's conic is read at
     * its past and must still be carried round to now.
     */
    const double radius = 600000, w = 2 * Math.PI / 21549.425, t = 1e6;
    var spin = BodySpin.Of(Kerbin, new DVec3(0, 0, w), radius, 70000);
    var pad = new DVec3(radius + 70, 0, 0);
    foreach (double c in new[] { 3000.0, 300000.0, 299792458.0 })
    foreach (double d in new[] { 2400.0, 50000.0 })
    {
        double a = d / radius;
        var lens = new DVec3((radius + 2) * Math.Cos(a), (radius + 2) * Math.Sin(a), 0);
        var known = new Known { Spin = spin, Body = Kerbin, T = t, Live = true, F = _ => pad };
        var st = PastInstant.Solve(ref known, lens, t, c, 0, double.NegativeInfinity, t, out double tr);
        var aim = PastInstant.KnownTurned(spin, Kerbin, pad, true, tr, t);
        double off = (aim - pad).Magnitude / (pad - lens).Magnitude;
        Check(st == PastStatus.Found && off < 1e-6 && Math.Abs(t - tr - (pad - lens).Magnitude / c) < 1e-9,
            $"c={c} d={d}: a live landed subject is aimed on its drawn place ({off:E1} rad), its light time d/c");
        var twice = PastInstant.Turned(spin, Kerbin, pad, false, tr, t);
        if (c == 3000) Check((twice - pad).Magnitude / (pad - lens).Magnitude > 1e-2, "turned as though read at its past, it would be aimed degrees off");
    }
    {
        const double c = 3000;
        var lens = new DVec3(radius + 2, 0, 0);
        DVec3 Orbit(double ut) => new DVec3((radius + 50000) * Math.Cos(2.0e-3 * (ut - t)), (radius + 50000) * Math.Sin(2.0e-3 * (ut - t)), 0);
        var known = new Known { Spin = spin, Body = Kerbin, T = t, Live = false, F = Orbit };
        double bound = 2.0e-3 * (radius + 50000) + PastInstant.TurnSpeedBound(spin, 0, 0, radius + 50000);
        var st = PastInstant.Solve(ref known, lens, t, c, bound, double.NegativeInfinity, t, out double tr);
        var aim = PastInstant.KnownTurned(spin, Kerbin, Orbit(tr), false, tr, t);
        var expected = PastInstant.Turned(spin, Kerbin, Orbit(tr), false, tr, t);
        double turned = (aim - Orbit(tr)).Magnitude, want = w * (t - tr) * (radius + 50000);
        Check(st == PastStatus.Found && (aim - expected).Magnitude < 1e-9 && Math.Abs(turned - want) < 1e-3 * want,
            $"an unloaded conic subject is still carried round with the body ({turned:F0} m over {t - tr:F1} s)");
    }
}

/* ---- a craft just launched beside the lens, solved on the turning body ---- */
{
    /*
     * The lens and the pad 500 m apart on Kerbin's equator, c = 60: the
     * ground turns at 175 m/s, faster than light. The craft lifts off at
     * 15 m/s^2 and is published every 2 s as a state vector record about
     * Kerbin's inertial frame, read back along it as the rig reads it, with
     * the path carried round with the body (PastInstant.Turn) as KnownPath
     * carries it.
     */
    const double c = 60, radius = 600000, w = 175 / radius, accel = 15, tL = 1000, publish = 2, fps = 60;
    var spin = BodySpin.Of(Kerbin, new DVec3(0, 0, w), radius, 70000);
    DVec3 Rot(DVec3 v, double a) => new DVec3(v.X * Math.Cos(a) - v.Y * Math.Sin(a), v.X * Math.Sin(a) + v.Y * Math.Cos(a), v.Z);
    var lensB = new DVec3(radius, 0, 0);
    foreach (double side in new[] { 1.0, -1.0 })
    {
        double theta = side * 500 / radius;
        DVec3 CraftB(double ut)
        {
            double h = ut > tL ? 0.5 * accel * (ut - tL) * (ut - tL) : 0;
            return new DVec3((radius + h) * Math.Cos(theta), (radius + h) * Math.Sin(theta), 0);
        }
        DVec3 CraftI(double ut) => Rot(CraftB(ut), w * ut);
        Launch Record(double from) => new Launch
        {
            P = CraftI(from), T0 = from, From = double.NegativeInfinity,
            V = (CraftI(from + 1e-3) - CraftI(from - 1e-3)) * (1 / 2e-3),
            A = (CraftI(from + 1e-2) - CraftI(from) * 2 + CraftI(from - 1e-2)) * (1 / 1e-4),
        };
        string where = side > 0 ? "ahead of" : "behind";
        /* The rig's bound: the record's inertial speed, and what turning adds over the read-back. */
        double Bound(double from, double t)
        {
            double own = (175 + 160) * 1.1;
            double span = t - PastInstant.ReadBackFloor(from);
            return own + PastInstant.TurnSpeedBound(spin, own, span, radius + 1000);
        }

        var hold = new InstantHold();
        int steps = 0;
        double lastTr = double.NaN, worstTurn = 0, worstOff = 0, lastFrom = double.NaN;
        DVec3 lastDir = default;
        bool finite = true;
        for (double t = tL + 1 / fps; t < tL + 10; t += 1 / fps)
        {
            double from = tL + Math.Floor((t - tL) / publish) * publish;
            var path = new Turning<Launch> { Inner = Record(from), Spin = spin, Body = Kerbin, T = t };
            var windowed = path;
            windowed.Inner.From = from;
            var lens = Rot(lensB, w * t);
            if (PastInstant.SolveAlong(ref path, ref windowed, lens, t, c, Bound(from, t), from, t, out double tr) != PastStatus.Found)
            {
                finite = false;
                continue;
            }
            double held = hold.NotBefore(t, 0, c, Kerbin);
            if (held > tr && PastInstant.Arrived(ref path, lens, t, c, held)) tr = held;
            hold.Shown(t, tr, 0, c, Kerbin, Kerbin);
            if (tr < lastTr) steps++;
            path.TryAt(tr, out var at);
            /* The drawn offset from the lens on the body as it is now, against the craft over its own ground then. */
            var drawn = Rot(at - lens, -w * t);
            double off = (drawn - (CraftB(tr) - lensB)).Magnitude;
            finite &= !double.IsNaN(off);
            worstOff = Math.Max(worstOff, off);
            var dir = drawn.Normalized;
            /* A republish moves the read-back cap by 2 s, which turns the aim on its own; only frames between publishes count. */
            if (from == lastFrom) worstTurn = Math.Max(worstTurn, Math.Acos(Math.Min(1, DVec3.Dot(dir, lastDir))) * 180 / Math.PI);
            lastDir = dir;
            lastTr = tr;
            lastFrom = from;
        }
        /* What is left off is the first record read back 4 s through liftoff, where its quadratic is a / 4 * 4^2 = 60 m off. */
        Check(finite && steps == 0 && worstOff < 61 && worstTurn < 2,
            $"a craft just launched {where} the lens is aimed over its own ground ({worstOff:F1} m), never steps back "
            + $"and turns at most {worstTurn:F2} deg in a frame between publishes");

        /* At liftoff the rig passes from the surface record, placed as it is now, to the state vector. */
        {
            double t = tL + 1 / fps, from = tL;
            var path = new Turning<Launch> { Inner = Record(from), Spin = spin, Body = Kerbin, T = t };
            var windowed = path;
            windowed.Inner.From = from;
            var lens = Rot(lensB, w * t);
            PastInstant.SolveAlong(ref path, ref windowed, lens, t, c, Bound(from, t), from, t, out double tr);
            path.TryAt(tr, out var at);
            double jump = (Rot(at - lens, -w * t) - (CraftB(tL) - lensB)).Magnitude;
            Check(jump < 61, $"at liftoff a craft {where} the lens moves {jump:F1} m from its pad as its record turns to a state vector");
        }
    }
}

/* ---- the drawn craft and the aim, through the band ---- */
{
    /*
     * A craft on Kerbin's equator rises straight up from its pad over the
     * lens's ground at 20 m/s^2, through the band from 70 to 140 km, with a
     * light speed at which light from the band takes about 40 s, and at
     * c = 60, where the ground outruns light and the craft never leaves the
     * lowest kilometres in the light shown. The ring holds its inertial
     * path, landed then in flight, as the host samples it; the aim solves
     * the same truth as the rig would.
     */
    foreach (double c in new[] { 3000.0, 60.0 })
    {
        const double radius = 600000, w = 175 / radius, accel = 20, tL = 100, dtRing = 0.05;
        const uint pid = 31;
        DVec3 Rot(DVec3 v, double a) => new DVec3(v.X * Math.Cos(a) - v.Y * Math.Sin(a), v.X * Math.Sin(a) + v.Y * Math.Cos(a), v.Z);
        double theta = 2000 / radius;
        double H(double ut) => ut > tL ? 0.5 * accel * (ut - tL) * (ut - tL) : 0;
        DVec3 CraftB(double ut) => new DVec3((radius + H(ut)) * Math.Cos(theta), (radius + H(ut)) * Math.Sin(theta), 0);
        DVec3 CraftI(double ut) => Rot(CraftB(ut), w * ut);
        var lensB = new DVec3(radius, 0, 0);
        var rate = new DVec3(0, 0, w);

        var spun = (HistoryBody[])bodies.Clone();
        spun[Kerbin].Spin = rate;
        spun[Kerbin].RotThresholdM = 70000;
        var ring = new LightHistory { SystemDiameterM = 2.4e11 };
        ring.SetBodies(spun);
        ring.SetLightSpeed(c);
        var key = LightHistory.CraftKey(pid);
        for (double ut = 0; ut <= tL + 400; ut += dtRing)
        {
            var s = new HistoryState { Ut = ut, Body = Kerbin, Attitude = DQuat.Identity };
            if (ut <= tL)
            {
                s.Kind = HistoryKind.Surface;
                s.Position = CraftI(ut);
                s.AngularVelocity = rate;
                s.Velocity = DVec3.Cross(rate, s.Position);
            }
            else
            {
                s.Kind = HistoryKind.Conic;
                s.Position = CraftI(ut);
                s.Velocity = (CraftI(ut + 1e-4) - CraftI(ut - 1e-4)) * (1 / 2e-4);
            }
            ring.Record(key, s, ut == 0 ? HistoryReason.Start : HistoryReason.Departure);
        }
        var spin = ring.SpinOf(Kerbin);
        var tree = new Tree();
        var truth = new Turning<Truth> { Inner = new Truth { F = CraftI }, Spin = spin, Body = Kerbin };

        var hold = new InstantHold();
        int steps = 0;
        double lastTr = double.NegativeInfinity, worstGround = 0, worstStep = 0, worstAccel = 0, worstAgree = 0, worstAgreeS = 0, worstAbout = 0;
        double worstShared = 0, worstAnchor = 0, nearestInertial = double.PositiveInfinity;
        int anchorsMissed = 0;
        double lastStepLen = double.NaN;
        DVec3 lastDrawn = default, lastStep = default;
        bool haveLast = false, found = true;
        string agreeAt = "", accelAt = "";
        bool aboveBand = false;
        const double dt = 0.1;
        for (double t = tL - 5; t < tL + 300; t += dt)
        {
            var lensOffset = Rot(lensB, w * t);
            var sight = PastInstant.Craft(ring, pid, t, Kerbin, lensOffset, c, ref tree, hold.NotBefore(t, ring.Generation, c, Kerbin), hold.Frame);
            if (!sight.Found) { found = false; continue; }
            hold.Shown(t, sight.Ut, ring.Generation, c, Kerbin, sight.Frame);
            if (sight.Ut < lastTr) steps++;
            lastTr = sight.Ut;
            var drawn = PastInstant.AboutDrawnBody(sight.State, t, spin);
            worstAbout = Math.Max(worstAbout, (drawn - sight.About).Magnitude);
            /* The rig and the anchors place a craft by the same helper the ring's path does. */
            var shared = PastInstant.Turned(spin, Kerbin, sight.State.Position, sight.State.Kind == HistoryKind.Surface, sight.Ut, t);
            worstShared = Math.Max(worstShared, (shared - sight.About).Magnitude);
            /* On the body as it is now, against the craft's ground at the instant shown. */
            var onBody = Rot(drawn, -w * t);
            if (H(sight.Ut) <= 70000) worstGround = Math.Max(worstGround, (onBody - CraftB(sight.Ut)).Magnitude);
            else aboveBand |= PastInstant.TurningWeight(spin, H(sight.Ut)) == 0;
            if (haveLast)
            {
                var step = onBody - lastDrawn;
                worstStep = Math.Max(worstStep, step.Magnitude);
                if (!double.IsNaN(lastStepLen) && (step - lastStep).Magnitude > worstAccel)
                {
                    worstAccel = (step - lastStep).Magnitude;
                    accelAt = $"{H(sight.Ut) / 1000:F1} km";
                }
                lastStep = step;
                lastStepLen = step.Magnitude;
            }
            lastDrawn = onBody;
            haveLast = true;

            /* The aim on the same truth, solved as the rig solves it. */
            truth.T = t;
            double own = accel * Math.Max(0, t - tL) * 1.1 + 175;
            double bound = own + PastInstant.TurnSpeedBoundAbout(spin, Kerbin, own, t, radius + H(t), lensOffset.Magnitude, c);
            if (PastInstant.Solve(ref truth, lensOffset, t, c, bound, 0, t, out double tr) == PastStatus.Found)
            {
                truth.TryAt(tr, out var aim);
                double off = (aim - drawn).Magnitude;
                if (off > worstAgree) { worstAgree = off; agreeAt = $"{H(tr) / 1000:F0} km"; }
                worstAgreeS = Math.Max(worstAgreeS, Math.Abs(tr - sight.Ut));
            }
            else found = false;

            /* A state vector anchor on the craft, solved as the anchor projector solves it: no start, the span capped by the lens's distance. */
            double anchorBound = own + PastInstant.TurnSpeedBoundAbout(spin, Kerbin, own, double.PositiveInfinity, double.NaN, lensOffset.Magnitude, c);
            if (PastInstant.Solve(ref truth, lensOffset, t, c, anchorBound, double.NegativeInfinity, t, out double ta) == PastStatus.Found)
            {
                truth.TryAt(ta, out var anchor);
                worstAnchor = Math.Max(worstAnchor, (anchor - drawn).Magnitude);
            }
            else anchorsMissed++;
            /* The same anchor left inertial, as it was placed before, for scale. */
            var inertial = new Truth { F = CraftI };
            if (PastInstant.Solve(ref inertial, lensOffset, t, c, own + 300, double.NegativeInfinity, t, out double ti) == PastStatus.Found)
            {
                inertial.TryAt(ti, out var place);
                if (H(sight.Ut) < 70000) nearestInertial = Math.Min(nearestInertial, (place - drawn).Magnitude);
            }
        }
        Check(found && worstAbout < 1e-6, $"c={c}: the picture draws the craft where its light time was solved ({worstAbout:E1} m)");
        Check(worstShared < 1e-9, $"c={c}: PastInstant.Turned, which the rig and the anchors place by, puts the craft where the ring's path does ({worstShared:E1} m)");
        Check(anchorsMissed == 0 && worstAnchor < 1,
            $"c={c}: a state vector anchor on the craft sits on the drawn craft ({worstAnchor:F3} m)");
        if (c < 100)
            Check(nearestInertial > 1000, $"c={c}: where left inertial it would sit at least {nearestInertial / 1000:F1} km off the craft below the band");
        Check(steps == 0 && worstGround < 1 && (aboveBand || c < 100),
            $"c={c}: climbing to the band the drawn craft stays over its own ground ({worstGround:F3} m) and never steps back"
                + (aboveBand ? ", on up past the band" : ""));
        /* A jump at either edge of the band would show as a change of step far above the climb's own bend. */
        Check(worstAccel < 5, $"c={c}: and moves smoothly across the band: at most {worstStep:F1} m a frame, the step changing by at most {worstAccel:F2} m (at {accelAt})");
        Check(worstAgree < 1 && worstAgreeS < 1e-3,
            $"c={c}: the aim on the same truth agrees with the drawn craft from the pad up past the band ({worstAgree:F3} m at {agreeAt}, {worstAgreeS:E1} s)");

        /* At real light speed the turn over a light time of milliseconds is under a metre. */
        var plain = new LightHistory { SystemDiameterM = 2.4e11 };
        plain.SetBodies(bodies);
        plain.SetLightSpeed(299792458.0);
        ring.SetLightSpeed(299792458.0);
        for (double ut = 0; ut <= tL + 400; ut += dtRing)
            plain.Record(key, ring.TryStateAt(pid, ut, out var st) ? st : default, ut == 0 ? HistoryReason.Start : HistoryReason.Departure);
        double worstReal = 0, worstRealS = 0;
        foreach (double t in new[] { tL - 1, tL + 50, tL + 105, tL + 300 })
        {
            var lensOffset = Rot(lensB, w * t);
            var a = PastInstant.Craft(ring, pid, t, Kerbin, lensOffset, 299792458.0, ref tree);
            var b = PastInstant.Craft(plain, pid, t, Kerbin, lensOffset, 299792458.0, ref tree);
            worstReal = Math.Max(worstReal, (a.About - b.About).Magnitude);
            worstRealS = Math.Max(worstRealS, Math.Abs(a.Ut - b.Ut));
        }
        Check(worstReal < 1 && worstRealS < 1e-6, $"at real light speed the drawn craft moves {worstReal:F3} m and its instant {worstRealS:E1} s");
    }
}

/* ---- the band on small bodies ---- */
{
    var rate = new DVec3(0, 0, 1e-4);
    var low = BodySpin.Of(Mun, rate, 200000, 5000);
    Check(low.FromAltitudeM == 5000 && low.BandM == 20000 && PastInstant.TurningWeight(low, 5000) == 1
        && PastInstant.TurningWeight(low, 25000) == 0 && PastInstant.TurningSlope(low) == 1.5 / 20000,
        "a low threshold turns over a band no narrower than a tenth of the radius");
    var tight = BodySpin.Of(Mun, rate, 200000, 100000, 300000);
    var out1 = new DVec3(300000 + 1, 0, 0);
    Check(Math.Abs(tight.TopRadiusM - 300000) < 1e-6 && Math.Abs(tight.FromAltitudeM - 50000) < 1e-6 && Math.Abs(tight.BandM - 50000) < 1e-6
        && PastInstant.Turn(tight, Mun, out1, false, 0, 100).Magnitude == 0
        && PastInstant.Turn(tight, Mun, new DVec3(299900, 0, 0), false, 0, 100).Magnitude < 1e-4 * 100 * 1e-3,
        "a band reaching past the sphere of influence shrinks to end there, so nothing jumps at the crossing");
    Check(!BodySpin.Of(Mun, rate, 200000, 100000, 150000).Turns && BodySpin.Of(Mun, rate, 200000, 100000, double.NaN).TopRadiusM == 400000,
        "a sphere of influence inside the body turns nothing, and an unknown one clamps nothing");
    var spun = (HistoryBody[])bodies.Clone();
    spun[Mun].Spin = rate;
    spun[Mun].RotThresholdM = 2e6;
    var ring = NewRing(60);
    ring.SetBodies(spun);
    Check(Math.Abs(ring.SpinOf(Mun).TopRadiusM - bodies[Mun].SoiRadius) < 1e-3, "the ring's spin ends at the body's own sphere of influence");
}

/* ---- the weight's span, capped by the light time across the band ---- */
{
    /*
     * A craft in a 100 km orbit of Kerbin, inside the band, for a day, seen
     * from the ground at 3e5 m/s. Over its whole past the weight's part of
     * the bound passes c; over the light time across the band, what a solve
     * can need, it is tens of metres a second, so the solve still brackets.
     */
    const double c = 3e5, r = 700000, t = 86400;
    const uint pid = 41;
    double w = 2 * Math.PI / 21549.425, v = Math.Sqrt(MuKerbin / r);
    var spun = (HistoryBody[])bodies.Clone();
    spun[Kerbin].Spin = new DVec3(0, 0, w);
    spun[Kerbin].RotThresholdM = 70000;
    var ring = new LightHistory { SystemDiameterM = 2.4e11 };
    ring.SetBodies(spun);
    ring.SetLightSpeed(c);
    ring.Record(LightHistory.CraftKey(pid), State(0, Kerbin, new DVec3(r, 0, 0), new DVec3(0, v, 0)), HistoryReason.Start);
    var spin = ring.SpinOf(Kerbin);
    var track = ring.Track(LightHistory.CraftKey(pid));
    var lensOffset = new DVec3(0, 600000, 0);
    double whole = ring.MaxSpeedAbout(track, Kerbin, spin, t, double.NaN, c);
    double capped = ring.MaxSpeedAbout(track, Kerbin, spin, t, lensOffset.Magnitude, c);
    Check(whole >= c && capped * PastInstant.SpeedMargin < c && capped < v + 400,
        $"over its whole past the bound reaches {whole:E1} m/s, past c; capped at the light time across the band it is {capped:F0} m/s");
    Check(PastInstant.TurnSpan(spin, t, 600000, c) == (600000 + spin.TopRadiusM) / c && PastInstant.TurnSpan(spin, 1, 600000, c) == 1
        && PastInstant.TurnSpan(spin, double.PositiveInfinity, 600000, c) == (600000 + spin.TopRadiusM) / c
        && PastInstant.TurnSpan(default, t, 600000, c) == t,
        "the span is capped at the lens's distance plus the band's top over c, and only where something turns");
    var tree = new Tree();
    var sight = PastInstant.Craft(ring, pid, t, Kerbin, lensOffset, c, ref tree);
    double g = sight.Found ? sight.Ut + (lensOffset - sight.About).Magnitude / c - t : double.NaN;
    Check(sight.Found && g <= 0 && g > -1e-5, $"and the solve finds the craft's light arriving now ({g:E1} s)");
}

/*
 * The fastest a ring path moves where a solve relies on its bound: |dP/du|
 * sampled every `step` seconds over [lo, t] wherever light from P(u) has not
 * reached `lens` by t (g > 0). `latestBand` is the largest t - u there with
 * the craft within `top` of `body` and about it, the reach TurnSpan caps.
 */
double SampledRate<TB>(ref RingTurned<TB> path, DVec3 lens, double lo, double t, double c, double step, double top, int body,
    out double latestBand) where TB : IBodyTree
{
    double best = 0;
    latestBand = 0;
    const double h = 0.01;
    for (double u = lo + h; u < t - h; u += step)
    {
        if (!path.TryAt(u, out var p) || G(lens, p, u, t, c) <= 0) continue;
        if (path.Last.Body == body && path.Last.Position.Magnitude < top) latestBand = Math.Max(latestBand, t - u);
        if (!path.TryAt(u - h, out var a) || !path.TryAt(u + h, out var b)) continue;
        best = Math.Max(best, (b - a).Magnitude / (2 * h));
    }
    return best;
}

/* ---- the weight's span cap binding for a craft climbing through the band ---- */
{
    /*
     * A craft waits on Kerbin's equator for 3000 s, then climbs straight up
     * at 300 m/s through the band from 70 to 140 km, seen from 1000 km out
     * on the far side at c = 1e3. Its past outlasts the cap of (L + Top) / c,
     * 1740 s, so the cap binds; the weight's part is still needed, so a cap
     * of nothing would leave the bound below the path's own rate. Kerbin's
     * Mu is 0 here so each record bounds its own speed, as a powered climb's
     * samples would, rather than by a conic's periapsis.
     */
    const double c = 1e3, radius = 600000, tL = 3000, climb = 300;
    const uint pid = 61;
    double w = 2 * Math.PI / 21549.425;
    DVec3 Rot(DVec3 v, double a) => new DVec3(v.X * Math.Cos(a) - v.Y * Math.Sin(a), v.X * Math.Sin(a) + v.Y * Math.Cos(a), v.Z);
    double H(double ut) => ut > tL ? climb * (ut - tL) : 0;
    DVec3 CraftI(double ut) => Rot(new DVec3(radius + H(ut), 0, 0), w * ut);
    var lensB = new DVec3(-1e6, 0, 0);
    var rate = new DVec3(0, 0, w);
    var spun = (HistoryBody[])bodies.Clone();
    spun[Kerbin].Spin = rate;
    spun[Kerbin].RotThresholdM = 70000;
    spun[Kerbin].Mu = 0;
    var ring = new LightHistory { SystemDiameterM = 2.4e11 };
    ring.SetBodies(spun);
    ring.SetLightSpeed(c);
    var key = LightHistory.CraftKey(pid);
    for (double ut = 0; ut <= tL + 2000; ut += 1)
    {
        var s = new HistoryState { Ut = ut, Body = Kerbin, Attitude = DQuat.Identity, Position = CraftI(ut) };
        if (ut <= tL)
        {
            s.Kind = HistoryKind.Surface;
            s.AngularVelocity = rate;
            s.Velocity = DVec3.Cross(rate, s.Position);
        }
        else
        {
            s.Kind = HistoryKind.Conic;
            s.Velocity = (CraftI(ut + 1e-4) - CraftI(ut - 1e-4)) * (1 / 2e-4);
        }
        ring.Record(key, s, ut == 0 ? HistoryReason.Start : HistoryReason.Departure);
    }
    var spin = ring.SpinOf(Kerbin);
    var track = ring.Track(key);
    var tree = new Tree();
    double lo = Math.Max(track.StartUt, ring.FloorUt);
    bool binds = true, covered = true, needed = false, latest = true;
    double worstRatio = 0, mostOver = 0;
    for (double t = tL + 300; t <= tL + 1500; t += 100)
    {
        var lensOffset = Rot(lensB, w * t);
        double L = lensOffset.Magnitude;
        binds &= PastInstant.TurnSpan(spin, t - lo, L, c) < t - lo;
        double bound = ring.MaxSpeedAbout(track, Kerbin, spin, t - lo, L, c);
        double none = ring.MaxSpeedAbout(track, Kerbin, spin, 0, L, c);
        var path = new RingTurned<Tree> { Ring = ring, Track = track, Tree = tree, Frame = Kerbin, Spin = spin, T = t };
        double sampled = SampledRate(ref path, lensOffset, lo, t, c, 0.5, spin.TopRadiusM, Kerbin, out _);
        covered &= sampled <= bound;
        worstRatio = Math.Max(worstRatio, sampled / bound);
        if (sampled > none) needed = true;
        mostOver = Math.Max(mostOver, sampled - none);
        var sight = PastInstant.Craft(ring, pid, t, Kerbin, lensOffset, c, ref tree);
        latest &= sight.Found && IsLatest(ref path, lensOffset, t, c, 1 + sampled * 1.1 / c, sight.Ut, LatestS);
    }
    Check(binds, "climbing through the band after a long wait on the pad, the span is capped at the light time across the band");
    Check(covered && needed,
        $"the capped bound covers the path's sampled rate where light has not arrived (at most {worstRatio:P0} of it), "
            + $"which outruns a bound with no weight's part by {mostOver:F0} m/s");
    Check(latest, "and the solve draws the latest root at every instant through the climb");
}

/* ---- the weight's span cap in a frame above the lens's body ---- */
{
    /*
     * A lens on the Mun's leading side, c = 700, the Mun circling Kerbin at
     * 540 m/s and spinning at 1e-3 rad/s, its band from 100 to 200 km. A
     * craft hovers in the band on the trailing side, then climbs out at
     * 1 km/s through the band's top and on out of the Mun's sphere, so the
     * light time is solved in Kerbin's frame. There the lens's body runs
     * from the band's old light, which lingers for up to (L + Top) / (c - v)
     * rather than (L + Top) / c. Mu is 0 so each record bounds its own speed.
     */
    const double c = 700, munR = 1.2e7, munV = 540, hover = 350000, climb = 1000, tClimb = 1000;
    const uint pid = 62;
    var circle = new MunCircle { R = munR, W = munV / munR };
    var trailing = new DVec3(0, -1, 0);
    DVec3 AboutMun(double ut) => trailing * (ut > tClimb ? hover + climb * (ut - tClimb) : hover);
    DVec3 VelMun(double ut) => ut > tClimb ? trailing * climb : default;
    double soi = bodies[Mun].SoiRadius;
    double exitUt = tClimb + (soi - hover) / climb;
    var spun = (HistoryBody[])bodies.Clone();
    spun[Mun].Spin = new DVec3(0, 0, 1e-3);
    spun[Mun].RotThresholdM = 100000;
    spun[Mun].Mu = 0;
    spun[Kerbin].Mu = 0;
    var ring = new LightHistory { SystemDiameterM = 2.4e11 };
    ring.SetBodies(spun);
    ring.SetLightSpeed(c);
    var key = LightHistory.CraftKey(pid);
    for (double ut = 0; ut <= 5000; ut += 1)
    {
        bool inMun = ut < exitUt;
        circle.TryOffset(Mun, ut, out var munAt);
        var munVel = circle.Velocity(ut);
        var s = new HistoryState
        {
            Ut = ut, Body = inMun ? Mun : Kerbin, Kind = HistoryKind.Conic, Attitude = DQuat.Identity,
            Position = inMun ? AboutMun(ut) : munAt + AboutMun(ut), Velocity = inMun ? VelMun(ut) : munVel + VelMun(ut),
        };
        ring.Record(key, s, ut == 0 ? HistoryReason.Start : HistoryReason.Departure);
    }
    var spin = ring.SpinOf(Mun);
    var track = ring.Track(key);
    var lensOffset = new DVec3(0, 200000, 0);
    double L = lensOffset.Magnitude;
    double lo = Math.Max(track.StartUt, ring.FloorUt);
    double vMun = ring.BodySpeedBound(Mun, Kerbin);
    double oldCap = (L + spin.TopRadiusM) / c, newCap = (L + spin.TopRadiusM) / (c - vMun);
    bool frames = true, covered = true, outran = false, beyondOld = false, withinNew = true, latest = true;
    double worstRatio = 0, longest = 0;
    for (double t = exitUt + 100; t <= exitUt + 1300; t += 100)
    {
        circle.TryOffset(Mun, t, out var munAt);
        var lens = munAt + lensOffset;
        frames &= ring.TryStateAt(track, t, out var now) && PastInstant.CommonAncestor(ref circle, Mun, now.Body) == Kerbin;
        double bound = ring.MaxSpeedAbout(track, Kerbin, spin, t - lo, L, c);
        /* The bound as it was, with the cap over c alone. */
        double old = 0;
        foreach (var sp in track.Spheres)
            old = Math.Max(old, sp.SpeedMs + ring.BodySpeedBound(sp.Body, Kerbin)
                + PastInstant.TurnSpeedBoundAbout(spin, sp.Body, sp.SpeedMs, t - lo, sp.RadiusM, L, c));
        var path = new RingTurned<MunCircle> { Ring = ring, Track = track, Tree = circle, Frame = Kerbin, Spin = spin, T = t };
        double sampled = SampledRate(ref path, lens, lo, t, c, 0.5, spin.TopRadiusM, Mun, out double band);
        longest = Math.Max(longest, band);
        beyondOld |= band > oldCap;
        withinNew &= band <= newCap;
        covered &= sampled <= bound;
        worstRatio = Math.Max(worstRatio, sampled / bound);
        outran |= sampled > old;
        var status = PastInstant.Solve(ref path, lens, t, c, bound, lo, t, out double tr);
        latest &= status == PastStatus.Found && IsLatest(ref path, lens, t, c, 1 + sampled * 1.1 / c, tr, LatestS);
    }
    Check(frames, "a craft climbed out of the Mun's sphere is solved in Kerbin's frame");
    Check(beyondOld && withinNew && PastInstant.TurnSpan(spin, double.PositiveInfinity, L, c, vMun) == newCap,
        $"there the band's light reaches the lens up to {longest:F0} s after it left, past (L + Top) / c = {oldCap:F0} s "
            + $"and within (L + Top) / (c - v) = {newCap:F0} s");
    Check(covered && outran,
        $"the bound with the Mun's speed taken off c covers the path's sampled rate (at most {worstRatio:P0} of it), which the cap over c alone does not");
    Check(latest, "and the solve in Kerbin's frame draws the latest root");
    Check(PastInstant.TurnSpan(spin, 5000, L, c, c) == 5000 && PastInstant.TurnSpan(spin, 5000, L, c, 2 * c) == 5000,
        "a lens's body that can keep up with light leaves the span uncapped");
}

/* ---- truth knows a loaded craft only as it is now ---- */
{
    /*
     * A Mun lens 30 m up at c = 300 m/s and a probe on a circular 60 km Mun
     * orbit, about 500 m/s, faster than light. Truth gives a loaded craft's
     * place now whatever instant is asked, so a solve on it stands still at
     * the craft now; the picture draws the ring's latest root, hundreds of
     * km back along the orbit. The aim takes the picture's place instead.
     */
    const double c = 300;
    const uint pid = 71;
    double r = 260000, v = Math.Sqrt(MuMun / r);
    var tree = new MunCircle { R = 1.2e7, W = 543.0 / 1.2e7 };
    var ring = NewRing(c);
    ring.Record(LightHistory.CraftKey(pid), State(37, Mun, new DVec3(r, 0, 0), new DVec3(0, v, 0)), HistoryReason.Start);
    var lens = new DVec3(-200030, 0, 0);
    var track = ring.Track(LightHistory.CraftKey(pid));
    bool stillNow = true, latest = true;
    int found = 0, several = 0;
    double widest = 0;
    for (double t = 600; t < 6000; t += 37)
    {
        var sight = PastInstant.Craft(ring, pid, t, Mun, lens, c, ref tree);
        if (!sight.Found) continue;
        found++;
        ring.TryStateAt(track, t, out var now);
        var live = new Known { Body = Mun, T = t, Live = true, F = _ => now.Position };
        var st = PastInstant.Solve(ref live, lens, t, c, 0, double.NegativeInfinity, t, out double liveTr);
        stillNow &= st == PastStatus.Found && live.TryAt(liveTr, out var aimed) && (aimed - now.Position).Magnitude < 1e-6;
        widest = Math.Max(widest, (sight.About - now.Position).Magnitude);
        var path = new RingTurned<MunCircle> { Ring = ring, Track = track, Tree = tree, Frame = Mun, T = t };
        latest &= IsLatest(ref path, lens, t, c, 1 + v * PastInstant.SpeedMargin / c, sight.Ut, LatestS);
        int roots = 0;
        double prev = double.NaN;
        for (double u = 37; u <= t; u += 0.5)
        {
            path.TryAt(u, out var p);
            double g = G(lens, p, u, t, c);
            if (prev <= 0 && g > 0) roots++;
            prev = g;
        }
        if (roots > 1) several++;
    }
    Check(found > 100 && several > 0, $"the probe is drawn at {found} instants, {several} of them with several roots");
    Check(latest, "the picture draws the latest root throughout");
    Check(stillNow && widest > 1e5, $"a live place solved as a path is the craft now, up to {widest / 1000:F0} km from where the picture draws it");
    Check(CommandCameraMath.TruthAimPlaceFor(true, true, true, true, true) == TruthAimPlace.Drawn,
        "so on truth with a light speed the aim takes the place and instant the picture draws");
    Check(CommandCameraMath.TruthAimPlaceFor(true, true, true, false, true) == TruthAimPlace.Source,
        "a craft drawn live is where truth puts it now");
    Check(CommandCameraMath.TruthAimPlaceFor(true, true, false, false, true) == TruthAimPlace.NoFix,
        "a craft whose light the picture does not draw, with the history reaching back past it, has no fix");
    Check(CommandCameraMath.TruthAimPlaceFor(true, true, false, false, false) == TruthAimPlace.Source,
        "a craft the picture cannot judge, its history younger than its light time, keeps truth's place");
    Check(CommandCameraMath.TruthAimPlaceFor(false, true, false, false, true) == TruthAimPlace.Source,
        "an aim at a point keeps truth's range whatever the picture draws");
    Check(CommandCameraMath.TruthAimPlaceFor(true, false, true, true, true) == TruthAimPlace.Source,
        "an aim at a craft the picture is not drawing keeps truth's place");
}

/* ---- truth reads an unloaded craft's present conic at every instant ---- */
{
    /*
     * A Mun lens at c = 30 m/s and a hyperbolic Mun escape (periapsis
     * 300 km from the centre, e 1.5) from UT 37, into Kerbin's sphere.
     * Truth gives an unloaded craft on the conic it is on now, about the body
     * it is at now, at any instant asked, with no start: read back for its
     * light time, the Kerbin conic lands before the craft's history began.
     * The ring never solves before a track's start.
     */
    const double c = 30;
    const uint pid = 72;
    double rp = 300000, e = 1.5, vp = Math.Sqrt(MuMun * (1 + e) / rp);
    var tree = new MunCircle { R = 1.2e7, W = 543.0 / 1.2e7 };
    var ring = NewRing(c);
    var key = LightHistory.CraftKey(pid);
    ring.Record(key, State(37, Mun, new DVec3(rp, 0, 0), new DVec3(0, vp, 0)), HistoryReason.Start);
    double exitUt = 37;
    DVec3 rx = default, vx = default;
    for (double ut = 37; ut < 1e5; ut += 1)
    {
        LightHistoryMath.Kepler(new DVec3(rp, 0, 0), new DVec3(0, vp, 0), MuMun, ut - 37, out rx, out vx);
        if (rx.Magnitude > bodies[Mun].SoiRadius) { exitUt = ut; break; }
    }
    tree.TryOffset(Mun, exitUt, out var munAtExit);
    ring.Record(key, State(exitUt, Kerbin, munAtExit + rx, tree.Velocity(exitUt) + vx), HistoryReason.SoiChange);
    var track = ring.Track(key);
    var lensOffset = new DVec3(-200030, 0, 0);
    bool ringAfterStart = true;
    int beforeStart = 0, solved = 0;
    double earliest = double.PositiveInfinity;
    for (double t = 20000; t <= 245000; t += 5000)
    {
        var sight = PastInstant.Craft(ring, pid, t, Mun, lensOffset, c, ref tree);
        if (sight.Found && sight.Ut < track.StartUt) ringAfterStart = false;
        ring.TryStateAt(track, t, out var now);
        tree.TryOffset(Mun, t, out var munAt);
        var lens = munAt + lensOffset;
        var conic = new Known { Body = Kerbin, T = t, F = ut =>
        {
            LightHistoryMath.Kepler(now.Position, now.Velocity, MuKerbin, ut - t, out var p, out _);
            return p;
        } };
        LightHistoryMath.ConicBound(now.Position, now.Velocity, MuKerbin, bodies[Kerbin].SoiRadius, out _, out double bound);
        if (PastInstant.Solve(ref conic, lens, t, c, bound, double.NegativeInfinity, t, out double tr) != PastStatus.Found) continue;
        solved++;
        if (tr < track.StartUt) beforeStart++;
        earliest = Math.Min(earliest, tr);
    }
    Check(beforeStart > 0,
        $"read on its present conic the craft solves to before its history starts at UT 37 ({beforeStart} of {solved} instants, as early as UT {earliest:F0})");
    Check(ringAfterStart, "while the picture's ring never draws it before its start");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

/* A point on the x axis whose light reaches the origin at x + B(x) = 100 + x / 2 + 0.05 sin(20 x), at speeds up to 1.5. */
struct Wiggle : IPastPath
{
    public bool TryAt(double ut, out DVec3 position)
    {
        position = new DVec3(100 - 0.5 * ut + 0.05 * Math.Sin(20 * ut), 0, 0);
        return true;
    }
}

/* Inner, but standing at At over [From, To). */
struct Shifted : IPastPath
{
    public Wobble Inner;
    public double From, To;
    public DVec3 At;
    public bool TryAt(double ut, out DVec3 position)
    {
        if (ut >= From && ut < To) position = At;
        else Inner.TryAt(ut, out position);
        return true;
    }
}

/* A point swinging along the x axis about D, A either side, at angular rate W. */
struct Wobble : IPastPath
{
    public double D, A, W;
    public bool TryAt(double ut, out DVec3 position)
    {
        position = new DVec3(D + A * Math.Sin(W * ut), 0, 0);
        return true;
    }
}

struct Line : IPastPath
{
    public double X0, V;
    public bool TryAt(double ut, out DVec3 position)
    {
        position = new DVec3(X0 + V * ut, 0, 0);
        return true;
    }
}

/* Inner, but placed only from From on, as a world model's record before its ValidFromUt. */
struct FromUt : IPastPath
{
    public Line Inner;
    public double From;
    public bool TryAt(double ut, out DVec3 position)
    {
        position = default;
        return ut >= From && Inner.TryAt(ut, out position);
    }
}

/* A point fixed on the equator of a body turning at W about z, seen inertially, placed only from From on. */
struct Spun : IPastPath
{
    public double Radius, Angle, W, From;
    public bool TryAt(double ut, out DVec3 position)
    {
        position = new DVec3(Radius * Math.Cos(Angle + W * ut), Radius * Math.Sin(Angle + W * ut), 0);
        return ut >= From;
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

/* Bodies on circles in one plane, parents before children, with stock-like radii and periods. */
struct Circles : IBodyTree
{
    public int[] Parent;
    public double[] Radius, Period, Phase, Mu;

    public int Count => Parent.Length;

    public static Circles Stock()
    {
        /* Sun, Kerbin, Mun, Minmus, Duna, Ike, Jool, Laythe, Vall, Tylo, Bop, Pol, Moho, Eve, Dres, Eeloo */
        var parent = new[] { -1, 0, 1, 1, 0, 4, 0, 6, 6, 6, 6, 6, 0, 0, 0, 0 };
        var radius = new[] { 0, 1.3599840256e10, 1.2e7, 4.7e7, 2.0726155264e10, 3.2e6, 6.8773560320e10, 2.7184e7, 4.3152e7, 6.85e7, 1.2875e8, 1.79890e8,
            5.263138304e9, 9.832684544e9, 4.0839348203e10, 9.0118820000e10 };
        var mu = new[] { 1.1723328e18, 3.5316e12, 6.5138398e10, 1.7658e9, 3.0136321e11, 1.8568369e10, 2.82528e14, 1.962e12, 2.0748e11,
            2.8252800e12, 2.4868349e9, 7.2170208e8, 1.6860938e11, 8.1717302e12, 2.1484489e10, 7.4410815e10 };
        int n = parent.Length;
        var period = new double[n];
        var phase = new double[n];
        for (int i = 1; i < n; i++)
        {
            double r = radius[i];
            period[i] = 2 * Math.PI * Math.Sqrt(r * r * r / mu[parent[i]]);
            phase[i] = i * 0.7;
        }
        return new Circles { Parent = parent, Radius = radius, Period = period, Phase = phase, Mu = mu };
    }

    public HistoryBody[] Bodies()
    {
        var b = new HistoryBody[Count];
        for (int i = 0; i < Count; i++)
            b[i] = new HistoryBody
            {
                Index = i, Parent = Parent[i], Mu = Mu[i], SoiRadius = double.PositiveInfinity,
                MaxOrbitalSpeed = i == 0 ? 0 : 2 * Math.PI * Radius[i] / Period[i], PeriapsisM = Radius[i],
            };
        return b;
    }

    public bool TryParent(int body, out int parent)
    {
        parent = Parent[body];
        return parent >= 0;
    }

    public bool TryOffset(int body, double ut, out DVec3 offset)
    {
        offset = default;
        if (Parent[body] < 0) return false;
        double a = Phase[body] + 2 * Math.PI * ut / Period[body];
        offset = new DVec3(Radius[body] * Math.Cos(a), Radius[body] * Math.Sin(a), 0);
        return true;
    }
}

/* Stock orbits with their eccentricities and inclinations, read by Kepler's equation. */
struct Orbits : IBodyTree
{
    public const int Kerbin = 1, Mun = 2;
    public int[] Parent;
    public double[] Sma, Ecc, Inc, Lan, ArgPe, Anomaly, Mu;

    public int Count => Parent.Length;

    public static Orbits Stock() => new Orbits
    {
        /* Sun, Kerbin, Mun, Minmus, Moho, Eve, Duna, Ike, Jool, Laythe, Vall, Bop, Tylo, Gilly, Pol, Dres, Eeloo */
        Parent = new[] { -1, 0, 1, 1, 0, 0, 0, 6, 0, 8, 8, 8, 8, 5, 8, 0, 0 },
        Sma = new[] { 0, 13599840256, 12e6, 47e6, 5263138304, 9832684544, 20726155264, 3.2e6, 68773560320, 27184000, 43152000,
            128500000, 68500000, 31500000, 179890000, 40839348203, 90118820000 },
        Ecc = new[] { 0, 0, 0, 0, 0.2, 0.01, 0.051, 0.03, 0.05, 0, 0, 0.235, 0, 0.55, 0.171, 0.145, 0.26 },
        Inc = new[] { 0, 0, 0, 6, 7, 2.1, 0.06, 0.2, 1.304, 0, 0, 15, 0.025, 12, 4.25, 5, 6.15 },
        Lan = new double[] { 0, 0, 0, 78, 70, 15, 135.5, 0, 52, 0, 0, 10, 0, 80, 2, 280, 50 },
        ArgPe = new double[] { 0, 0, 0, 38, 15, 0, 0, 0, 0, 0, 0, 25, 0, 10, 15, 90, 260 },
        Anomaly = new[] { 0, 3.14, 1.7, 0.9, 3.14, 3.14, 3.14, 1.7, 0.1, 3.14, 0.9, 0.9, 3.14, 0.9, 0.9, 3.14, 3.14 },
        Mu = new[] { 1.1723328e18, 3.5316e12, 6.5138398e10, 1.7658e9, 1.6860938e11, 8.1717302e12, 3.0136321e11, 1.8568369e10,
            2.82528e14, 1.962e12, 2.0748e11, 2.4868349e9, 2.82528e12, 8.289449e6, 7.2170208e8, 2.1484489e10, 7.4410815e10 },
    };

    public HistoryBody[] Bodies()
    {
        var b = new HistoryBody[Count];
        for (int i = 0; i < Count; i++)
        {
            b[i] = new HistoryBody { Index = i, Parent = Parent[i], Mu = Mu[i], SoiRadius = double.PositiveInfinity };
            if (Parent[i] < 0) continue;
            double pe = Sma[i] * (1 - Ecc[i]);
            b[i].MaxOrbitalSpeed = Math.Sqrt(Mu[Parent[i]] * (1 + Ecc[i]) / pe);
            b[i].PeriapsisM = pe;
        }
        return b;
    }

    /* The most a body moves about an ancestor, from the orbits alone: each periapsis speed up its parents. */
    public double SpeedBound(int body, int frame)
    {
        double sum = 0;
        for (int b = body; b != frame && Parent[b] >= 0; b = Parent[b])
            sum += Math.Sqrt(Mu[Parent[b]] * (1 + Ecc[b]) / (Sma[b] * (1 - Ecc[b])));
        return sum;
    }

    public bool TryParent(int body, out int parent)
    {
        parent = Parent[body];
        return parent >= 0;
    }

    public bool TryOffset(int body, double ut, out DVec3 offset)
    {
        offset = default;
        if (Parent[body] < 0) return false;
        double a = Sma[body], e = Ecc[body];
        double m = (Anomaly[body] + Math.Sqrt(Mu[Parent[body]] / (a * a * a)) * ut) % (2 * Math.PI);
        double ea = m;
        for (int i = 0; i < 30; i++) ea -= (ea - e * Math.Sin(ea) - m) / (1 - e * Math.Cos(ea));
        double x = a * (Math.Cos(ea) - e), y = a * Math.Sqrt(1 - e * e) * Math.Sin(ea);
        double w = ArgPe[body] * Math.PI / 180, inc = Inc[body] * Math.PI / 180, lan = Lan[body] * Math.PI / 180;
        double xp = Math.Cos(w) * x - Math.Sin(w) * y, yp = Math.Sin(w) * x + Math.Cos(w) * y;
        offset = new DVec3(Math.Cos(lan) * xp - Math.Sin(lan) * Math.Cos(inc) * yp, Math.Sin(lan) * xp + Math.Cos(lan) * Math.Cos(inc) * yp,
            Math.Sin(inc) * yp);
        return true;
    }
}

/* A craft crossing the lens's view at X, moving along y at V. */
struct Crossing : IPastPath
{
    public double X, V;
    public bool TryAt(double ut, out DVec3 position)
    {
        position = new DVec3(X, V * ut, 0);
        return true;
    }
}

/* Inner, but placed only from From on. */
struct CrossingFrom : IPastPath
{
    public Crossing Inner;
    public double From;
    public bool TryAt(double ut, out DVec3 position)
    {
        position = default;
        return ut >= From && Inner.TryAt(ut, out position);
    }
}

/* A record's quadratic path from T0, placed from From on. */
struct Launch : IPastPath
{
    public DVec3 P, V, A;
    public double T0, From;
    public bool TryAt(double ut, out DVec3 position)
    {
        double dt = ut - T0;
        position = P + V * dt + A * (0.5 * dt * dt);
        return ut >= From;
    }
}

/* A path known everywhere. */
struct Truth : IPastPath
{
    public Func<double, DVec3> F;
    public bool TryAt(double ut, out DVec3 position)
    {
        position = F(ut);
        return true;
    }
}

/* A path about `Body` carried round to `T` by PastInstant.Turned, as the rig's KnownPath and the anchors' KinematicPath carry it. */
struct Turning<TP> : IPastPath where TP : IPastPath
{
    public TP Inner;
    public BodySpin Spin;
    public int Body;
    public double T;
    public bool TryAt(double ut, out DVec3 position)
    {
        if (!Inner.TryAt(ut, out var p))
        {
            position = default;
            return false;
        }
        position = PastInstant.Turned(Spin, Body, p, false, ut, T);
        return true;
    }
}

/* A craft's ring path in `Frame`, turned about the lens's body as PastInstant's own ring path turns it. */
struct RingTurned<TB> : IPastPath where TB : IBodyTree
{
    public LightHistory Ring;
    public HistoryTrack Track;
    public TB Tree;
    public int Frame;
    public BodySpin Spin;
    public double T;
    public HistoryState Last;
    public bool TryAt(double ut, out DVec3 position)
    {
        position = default;
        if (!Ring.TryStateAt(Track, ut, out var s) || !PastInstant.TryAbout(ref Tree, s.Body, Frame, ut, out var body)) return false;
        Last = s;
        position = body + PastInstant.Turned(Spin, s.Body, s.Position, s.Kind == HistoryKind.Surface, ut, T);
        return true;
    }
}

/* Kerbin still about the Sun, the Mun on a circle of radius R about Kerbin at angular rate W. */
struct MunCircle : IBodyTree
{
    public double R, W;
    public bool TryParent(int body, out int parent)
    {
        parent = body == 2 ? 1 : 0;
        return body == 1 || body == 2;
    }
    public bool TryOffset(int body, double ut, out DVec3 offset)
    {
        offset = body == 2 ? new DVec3(R * Math.Cos(W * ut), R * Math.Sin(W * ut), 0) : default;
        return body == 1 || body == 2;
    }
    public DVec3 Velocity(double ut) => new DVec3(-R * W * Math.Sin(W * ut), R * W * Math.Cos(W * ut), 0);
}

/* A source's known place about `Body`, solved as the rig's KnownPath solves it: a live place is never turned. */
struct Known : IPastPath
{
    public Func<double, DVec3> F;
    public BodySpin Spin;
    public int Body;
    public double T;
    public bool Live;
    public bool TryAt(double ut, out DVec3 position)
    {
        position = PastInstant.KnownTurned(Spin, Body, F(ut), Live, ut, T);
        return true;
    }
}
