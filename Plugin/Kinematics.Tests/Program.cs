using System;
using System.Diagnostics;
using Kerbcast;

/*
 * Kerbcast's kinematics against KSP's own maths (KspReference.cs), and the
 * uncertainty and tooUncertain rules. The golden fixtures run each case with
 * the planetarium still and inverse-rotating, the frame question a low craft
 * raises: a record is in KSP's fixed frame, so it lands where KSP's Orbit
 * puts the craft whichever way the planetarium has turned since.
 */

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

const double KerbinMu = 3.5316e12;
const double KerbinRadius = 600000;
var bodyPos = new DVec3(-13599840256.0, 1234.5, 52.25);

KspBody Kerbin(bool inverse, double directRot = 0) => new KspBody
{
    Mu = KerbinMu,
    Radius = KerbinRadius,
    InitialRotation = 90,
    RotationPeriod = 21549.425,
    InverseRotation = inverse,
    DirectRotAngle = directRot,
    Position = bodyPos,
};

ZupFrame ZupOf(KspPlanetarium p)
{
    var z = p.Zup;
    return new ZupFrame(z.X, z.Y, z.Z);
}

/* Kerbcast's world position for a fixed-frame offset, as ProviderFixSource builds it. */
DVec3 World(KspPlanetarium p, KspBody body, DVec3 fixedOffset) => body.Position + ZupOf(p).ToWorld(fixedOffset);

double Tol(DVec3 v) => 1e-3 + 1e-9 * v.Magnitude;

/* The planetarium still (angle 0), turned (inverse rotation at an odd angle), and turned the other way. */
double[] angles = { 0.0, 237.5, -61.25 };

/* Elements fixtures: low Kerbin orbit, polar retrograde, an extreme ellipse (KSP's other solver), a hyperbola, equatorial. */
var conics = new (string name, double inc, double e, double sma, double lan, double argPe, double mEp, double epoch)[]
{
    ("low orbit", 5.0, 0.001, 700000, 30, 45, 1.2, 1e6),
    ("polar retrograde", 135.0, 0.2, 1.5e6, 300, 200, 4.0, 2.5e6),
    ("extreme ellipse", 12.0, 0.92, 4e7, 80, 10, 0.3, 5e5),
    ("escape hyperbola", 20.0, 1.4, -2e6, 10, 250, 0.5, 3e6),
    ("equatorial", 0.0, 0.05, 900000, 0, 0, 0.0, 0.0),
};

foreach (var c in conics)
{
    foreach (double angle in angles)
    {
        var p = new KspPlanetarium { InverseRotAngle = angle };
        var body = Kerbin(false);
        var ksp = KspOrbit.FromElements(p, c.inc, c.e, c.sma, c.lan, c.argPe, c.mEp, c.epoch, body);
        bool built = KeplerPath.TryFromElements(c.inc, c.e, c.sma, c.lan, c.argPe, c.mEp, c.epoch, KerbinMu, out var path);
        Check(built, $"elements {c.name}: the path builds");
        double worst = 0, worstV = 0;
        bool all = true;
        foreach (double dt in new[] { -7200.0, -1.0, 0.0, 0.5, 600.0, 3456.7, 86400.0 * 3 })
        {
            double ut = c.epoch + dt;
            if (!path.TryAt(ut, out var pos, out var vel)) { all = false; continue; }
            var want = ksp.getPositionAtUT(ut);
            var got = World(p, body, pos);
            double err = (got - want).Magnitude;
            all &= err <= Tol(want - body.Position);
            worst = Math.Max(worst, err);
            var wantV = KspPlanetarium.Xzy(ksp.getOrbitalVelocityAtUT(ut));
            double errV = (ZupOf(p).ToWorld(vel) - wantV).Magnitude;
            all &= errV <= 1e-6 + 1e-9 * wantV.Magnitude;
            worstV = Math.Max(worstV, errV);
        }
        Check(all, $"golden {c.name}, inverse rotation {angle}: Kerbcast's world position and velocity match Orbit.getPositionAtUT (worst {worst:G3} m, {worstV:G3} m/s)");
    }
}

/*
 * State vectors. A publisher reads a craft's state from KSP at t0, while the
 * planetarium is turned by one angle, and publishes it in the fixed frame
 * (Planetarium.Zup.LocalToWorld of getRelativePositionAtUT). Kerbcast renders
 * later, with the planetarium turned by another, and must land where KSP's
 * Orbit puts the craft then.
 */
foreach (var c in conics)
{
    foreach (double then in angles)
    foreach (double now in angles)
    {
        var body = Kerbin(false);
        var pThen = new KspPlanetarium { InverseRotAngle = then };
        var pNow = new KspPlanetarium { InverseRotAngle = now };
        var kspThen = KspOrbit.FromElements(pThen, c.inc, c.e, c.sma, c.lan, c.argPe, c.mEp, c.epoch, body);
        var kspNow = KspOrbit.FromElements(pNow, c.inc, c.e, c.sma, c.lan, c.argPe, c.mEp, c.epoch, body);
        double t0 = c.epoch + 123.25;
        var zupThen = pThen.Zup;
        var r = zupThen.LocalToWorld(kspThen.getRelativePositionAtUT(t0));
        var v = zupThen.LocalToWorld(kspThen.getOrbitalVelocityAtUT(t0));
        bool built = KeplerPath.TryFromState(r, v, t0, KerbinMu, out var path);
        double worst = 0;
        bool all = built;
        foreach (double dt in new[] { 0.0, 1.0, 30.0, 900.0, 5000.0 })
        {
            if (!path.TryAt(t0 + dt, out var pos, out _)) { all = false; continue; }
            var want = kspNow.getPositionAtUT(t0 + dt);
            double err = (World(pNow, body, pos) - want).Magnitude;
            all &= err <= Tol(want - body.Position);
            worst = Math.Max(worst, err);
        }
        if (then != 0 && c.name == "low orbit")
        {
            /* The same vector read as the turned frame lands elsewhere, so the convention is not moot. */
            var raw = kspThen.getRelativePositionAtUT(t0);
            var rawV = kspThen.getOrbitalVelocityAtUT(t0);
            KeplerPath.TryFromState(raw, rawV, t0, KerbinMu, out var wrong);
            wrong.TryAt(t0 + 30, out var wp, out _);
            Check((World(pNow, body, wp) - kspNow.getPositionAtUT(t0 + 30)).Magnitude > 1000,
                $"state vector {c.name}, {then} then {now}: Zup's own frame is not the contract (lands far off)");
        }
        Check(all, $"golden state vector {c.name}, inverse rotation {then} at publish and {now} at render: matches Orbit.getPositionAtUT (worst {worst:G3} m)");
    }
}

/* KSP's own UpdateFromStateVectors agrees with the fixed-frame reading when fed Zup's frame. */
{
    var body = Kerbin(false);
    var p = new KspPlanetarium { InverseRotAngle = 237.5 };
    var r = new DVec3(400000, 520000, 30000);
    var v = new DVec3(-1700, 1500, 90);
    var zup = p.Zup;
    var ksp = KspOrbit.FromStateVectors(p, zup.WorldToLocal(r), zup.WorldToLocal(v), body, 5000);
    KeplerPath.TryFromState(r, v, 5000, KerbinMu, out var path);
    path.TryAt(5600, out var pos, out _);
    var want = ksp.getPositionAtUT(5600);
    double err = (World(p, body, pos) - want).Magnitude;
    Check(err <= Tol(want - body.Position), $"golden: Orbit.UpdateFromStateVectors(Zup.WorldToLocal(fixed)) and Kerbcast's fixed-frame state agree ({err:G3} m)");
}

/*
 * Surface points, carried by the body's rotation. Kerbcast reads the body's
 * axes once (Zup.LocalToWorld of BodyFrame) and turns them by the spin; KSP
 * advances rotationAngle and rebuilds BodyFrame. Both inverse rotation off
 * (BodyFrame turns) and on (Zup turns, BodyFrame holds).
 */
foreach (bool inverse in new[] { false, true })
{
    var p = new KspPlanetarium { InverseRotAngle = inverse ? 0 : 41.0 };
    var body = Kerbin(inverse, directRot: 12.5);
    double ut0 = 1234567.0;
    body.Update(p, ut0);
    var zup0 = p.Zup;
    var kb = new KinematicsBody
    {
        Known = true,
        Mu = body.Mu,
        Radius = body.Radius,
        AxisX = zup0.LocalToWorld(body.BodyFrame.X),
        AxisY = zup0.LocalToWorld(body.BodyFrame.Y),
        AxisZ = zup0.LocalToWorld(body.BodyFrame.Z),
        SpinDegPerS = 360.0 / body.RotationPeriod,
        AxesUt = ut0,
    };
    bool all = true;
    double worst = 0;
    foreach (var (lat, lon, alt) in new[] { (-0.0972, -74.5577, 67.0), (45.0, 120.0, 1500.0), (-89.0, 10.0, 0.0), (0.0, 179.9, 3000.0) })
    {
        foreach (double dt in new[] { 0.0, 0.02, 10.0, 600.0, 21549.425 * 2.5 })
        {
            double ut = ut0 + dt;
            body.Update(p, ut);
            var want = body.GetWorldSurfacePosition(lat, lon, alt);
            var got = World(p, body, kb.SurfaceAt(lat, lon, alt, ut, out _));
            double err = (got - want).Magnitude;
            all &= err <= 1e-3;
            worst = Math.Max(worst, err);
        }
    }
    Check(all, $"golden surface, inverse rotation {(inverse ? "on" : "off")}: matches GetWorldSurfacePosition as the body turns (worst {worst:G3} m)");
}

/* A surface point's velocity is the spin's, so a numeric derivative agrees. */
{
    var kb = new KinematicsBody { Known = true, Radius = 600000, AxisX = new DVec3(1, 0, 0), AxisY = new DVec3(0, 1, 0), AxisZ = new DVec3(0, 0, 1), SpinDegPerS = 360.0 / 21549.425 };
    var a = kb.SurfaceAt(10, 20, 100, 50, out var v);
    var b = kb.SurfaceAt(10, 20, 100, 50.001, out _);
    Check(((b - a) * 1000.0 - v).Magnitude < 1e-3, "surface velocity is the spin's");
}

/* The propagator on its own: invariants, round trips and the awkward conics. */
{
    var cases = new (string name, DVec3 r, DVec3 v)[]
    {
        ("circular", new DVec3(700000, 0, 0), new DVec3(0, Math.Sqrt(KerbinMu / 700000), 0)),
        ("suborbital arc", new DVec3(610000, 0, 0), new DVec3(1200, 900, 0)),
        ("near parabolic below", new DVec3(700000, 0, 0), new DVec3(0, Math.Sqrt(2 * KerbinMu / 700000) * (1 - 1e-9), 1)),
        ("near parabolic above", new DVec3(700000, 0, 0), new DVec3(0, Math.Sqrt(2 * KerbinMu / 700000) * (1 + 1e-9), 1)),
        ("fast hyperbola", new DVec3(700000, 100000, 0), new DVec3(-500, 9000, 2000)),
        ("near radial", new DVec3(700000, 0, 0), new DVec3(2000, 1e-3, 0)),
    };
    foreach (var c in cases)
    {
        bool ok = KeplerPath.TryFromState(c.r, c.v, 100, KerbinMu, out var path);
        double e0 = DVec3.Dot(c.v, c.v) / 2 - KerbinMu / c.r.Magnitude;
        var h0 = DVec3.Cross(c.r, c.v);
        double worstE = 0, worstH = 0;
        foreach (double dt in new[] { -300.0, -1.0, 1e-3, 1.0, 60.0, 400.0 })
        {
            if (!path.TryAt(100 + dt, out var pos, out var vel)) { ok = false; continue; }
            double e = DVec3.Dot(vel, vel) / 2 - KerbinMu / pos.Magnitude;
            worstE = Math.Max(worstE, Math.Abs(e - e0) / (KerbinMu / c.r.Magnitude));
            worstH = Math.Max(worstH, (DVec3.Cross(pos, vel) - h0).Magnitude / Math.Max(1.0, h0.Magnitude));
            /* Back again from the propagated state. */
            KeplerPath.TryFromState(pos, vel, 100 + dt, KerbinMu, out var back);
            back.TryAt(100, out var home, out _);
            ok &= (home - c.r).Magnitude <= 1e-4 + 1e-9 * c.r.Magnitude;
        }
        Check(ok && worstE < 1e-9 && worstH < 1e-9, $"propagator {c.name}: energy and angular momentum hold, and it runs back to the start (dE {worstE:G2}, dh {worstH:G2})");
    }

    /* A thousand orbits on, an ellipse is where its period says. */
    KeplerPath.TryFromElements(30, 0.3, 2e6, 10, 20, 1.0, 0, KerbinMu, out var ell);
    double period = 2 * Math.PI * Math.Sqrt(2e6 * 2e6 * 2e6 / KerbinMu);
    ell.TryAt(500, out var p1, out _);
    ell.TryAt(500 + 1000 * period, out var p2, out _);
    Check((p1 - p2).Magnitude < 1e-2, "propagator: an ellipse a thousand periods on returns to the same point");

    /* Under gravity, not in a straight line: after a minute a suborbital arc has fallen. */
    KeplerPath.TryFromState(new DVec3(650000, 0, 0), new DVec3(0, 1000, 0), 0, KerbinMu, out var arc);
    arc.TryAt(60, out var fell, out _);
    double g = KerbinMu / (650000.0 * 650000.0);
    double linearR = Math.Sqrt(650000.0 * 650000.0 + 60000.0 * 60000.0);
    Check(Math.Abs(fell.Magnitude - (linearR - 0.5 * g * 3600)) < 200, $"propagator: a ballistic state falls under the body's gravity ({linearR - fell.Magnitude:F0} m below the straight line)");

    Check(!KeplerPath.TryFromElements(0, 1.0, 1e6, 0, 0, 0, 0, KerbinMu, out _), "elements: a parabola (e = 1) is refused");
    Check(!KeplerPath.TryFromElements(0, 0.5, -1e6, 0, 0, 0, 0, KerbinMu, out _), "elements: an ellipse with a negative semi-major axis is refused");
    Check(!KeplerPath.TryFromElements(0, 1.5, 1e6, 0, 0, 0, 0, KerbinMu, out _), "elements: a hyperbola with a positive semi-major axis is refused");
    Check(!KeplerPath.TryFromState(new DVec3(0, 0, 0), new DVec3(1, 0, 0), 0, KerbinMu, out _), "state: a craft at the body centre is refused");
    KeplerPath.TryFromElements(0, 1.4, -2e6, 0, 0, 0, 0, KerbinMu, out var hyp);
    Check(hyp.TryAt(1e15, out var far, out _) && far.Magnitude > 1e17, "propagator: a hyperbola a very long way on is still placed, far out");
    Check(!hyp.TryAt(double.PositiveInfinity, out _, out _) && !hyp.TryAt(1e305, out _, out _), "propagator: a UT past overflow reads as not placed, not as infinity");
}

/*
 * Stopping rule. A relative step test of 1e-13 asks for more than double
 * precision holds once the terms are large: an inbound e = 3 hyperbola at
 * 1.6e8 m read 1e7 s on converged in two steps, then bounced its residual
 * +-200 against a target of 1.9e13 and gave up after the cap.
 */
{
    KeplerPath.TryFromElements(30, 3.0, -1e7, 40, 50, 0, 0, KerbinMu, out var hyp);
    /* The inbound point at 1.6e8 m, by bisection on the elements' own path. */
    double lo = -1e7, hi = 0;
    for (int i = 0; i < 200; i++)
    {
        double mid = 0.5 * (lo + hi);
        hyp.TryAt(mid, out var p, out _);
        if (p.Magnitude > 1.6e8) lo = mid; else hi = mid;
    }
    hyp.TryAt(lo, out var r0, out var v0);
    DVec3 got = default;
    bool ok = KeplerPath.TryFromState(r0, v0, lo, KerbinMu, out var inbound) && inbound.TryAt(lo + 1e7, out got, out _);
    hyp.TryAt(lo + 1e7, out var want, out _);
    Check(ok && (got - want).Magnitude < 1e-6 * want.Magnitude,
        $"solver: the inbound e = 3 hyperbola at {r0.Magnitude:G3} m reads 1e7 s on ({(ok ? (got - want).Magnitude : double.NaN):G3} m from the elements' path)");

    /* Jool at 2.2e9 m and an escape from Kerbol: every read places. */
    const double JoolMu = 2.82528e14, KerbolMu = 1.1723328e18;
    KeplerPath.TryFromElements(3, 0.4, 2.2e9, 10, 20, 1, 0, JoolMu, out var jool);
    KeplerPath.TryFromElements(1, 1.3, -6e10, 10, 20, 0.2, 0, KerbolMu, out var escape);
    int placed = 0, tried = 0;
    for (double t = -3e8; t <= 3e8; t += 1.23e6)
    {
        tried += 2;
        if (jool.TryAt(t, out _, out _)) placed++;
        if (escape.TryAt(t, out _, out _)) placed++;
    }
    Check(placed == tried, $"solver: Jool's far orbits and a Kerbol escape place at every read ({placed} of {tried})");
}

/*
 * After the iteration cap the best iterate can be small beside its huge
 * terms and still be tens of gigametres off: a state read far out on a
 * hyperbola and propagated back through periapsis. Such a read must refuse
 * or be right, checked against an independent hyperbolic Kepler solve.
 */
foreach (var (e, a) in new[] { (30.0, -1e6), (3.0, -1e5) })
{
    double n = Math.Sqrt(KerbinMu / -(a * a * a));
    KeplerPath.TryFromElements(10, e, a, 20, 30, 0, 0, KerbinMu, out var far);
    Kinematics.OrbitalFrame(20 * Math.PI / 180, 10 * Math.PI / 180, 30 * Math.PI / 180, out var px, out var py);
    double H(double t)
    {
        double m = n * t, h = Math.Asinh(m / e);
        for (int i = 0; i < 300; i++) h -= (e * Math.Sinh(h) - h - m) / (e * Math.Cosh(h) - 1);
        return h;
    }
    DVec3 Truth(double t)
    {
        double h = H(t);
        return px * (-a * (e - Math.Cosh(h))) + py * (-a * Math.Sqrt(e * e - 1) * Math.Sinh(h));
    }
    DVec3 VTruth(double t)
    {
        double h = H(t);
        return (px * (a * Math.Sinh(h)) + py * (-a * Math.Sqrt(e * e - 1) * Math.Cosh(h))) * (n / (e * Math.Cosh(h) - 1));
    }
    int wrong = 0, refused = 0, right = 0;
    void Read(KeplerPath p, double ut, double tol)
    {
        var want = Truth(ut);
        if (!p.TryAt(ut, out var got, out _)) refused++;
        else if ((got - want).Magnitude <= tol * want.Magnitude + 1) right++;
        else
        {
            wrong++;
            Console.WriteLine($"     wrong: e {e} at {ut:G3} off {(got - want).Magnitude:G3} m");
        }
    }
    for (double t = 10; t < 1e11 / Math.Sqrt(KerbinMu / -a); t *= 1.37)
    {
        Read(far, t, 1e-6);
        Read(far, -t, 1e-6);
    }
    foreach (double t0 in new[] { 1.1e7, -1.1e7, 1.34e7, 2.87e7, 5.3e7 })
    {
        KeplerPath.TryFromState(Truth(t0), VTruth(t0), t0, KerbinMu, out var fromFar);
        foreach (double dt in new[] { -1e7, -1e5, 1, 1e5, 1e7 }) Read(fromFar, t0 + dt, 1e-5);
    }
    Check(wrong == 0, $"solver: an e = {e} hyperbola out to 1e11 m reads right or refuses ({right} right, {refused} refused, {wrong} wrong)");
}

/* Near a parabola the conic guesses mislead; the plain guess is used instead. */
{
    double vesc = Math.Sqrt(2 * KerbinMu / 700000.0);
    foreach (double v in new[] { vesc * (1 + 1e-16), vesc * (1 - 1e-16), vesc * (1 + 1e-12), vesc })
    {
        bool ok = KeplerPath.TryFromState(new DVec3(700000, 0, 0), new DVec3(0, v, 0), 0, KerbinMu, out var p)
            && p.TryAt(600, out var at, out var vel) && p.TryAt(-600, out _, out _) && p.TryAt(86400, out _, out _);
        Check(ok, $"solver: escape speed times (1 {(v >= vesc ? "+" : "-")} {Math.Abs(v / vesc - 1):G2}) places (alpha {p.Alpha:G2})");
    }
}

/* The speed bound of a fall straight down is by energy to the surface, not the speed now. */
{
    KeplerPath.TryFromState(new DVec3(700000, 0, 0), new DVec3(-10, 0, 0), 0, KerbinMu, out var drop, 600000);
    double want = Math.Sqrt(100 + 2 * KerbinMu * (1 / 600000.0 - 1 / 700000.0));
    Check(Math.Abs(drop.PeriapsisSpeed - want) < 1e-6, $"speed bound: a radial fall is bounded at the surface ({drop.PeriapsisSpeed:F1} m/s)");
    KeplerPath.TryFromState(new DVec3(700000, 0, 0), new DVec3(0, 2300, 0), 0, KerbinMu, out var orbit, 600000);
    double rp = 700000;
    Check(orbit.PeriapsisSpeed >= 2300 - 1e-9 && rp > 0, "speed bound: an orbit whose periapsis is above ground keeps its periapsis speed");
    KeplerPath.TryFromState(new DVec3(700000, 0, 0), new DVec3(-500, 300, 0), 0, KerbinMu, out var grazing, 600000);
    grazing.TryAt(100, out var plater, out var vlater);
    Check(plater.Magnitude > 600000 && grazing.PeriapsisSpeed >= vlater.Magnitude, "speed bound: holds for a craft falling in, while it is above ground");
}

/* The frame turn round trips. */
{
    var p = new KspPlanetarium { InverseRotAngle = 237.5 };
    var zup = ZupOf(p);
    var v = new DVec3(1.5, -2.25, 3.125);
    Check((zup.FromWorld(zup.ToWorld(v)) - v).Magnitude < 1e-12, "ZupFrame: FromWorld undoes ToWorld");
    Check((ZupFrame.Identity.ToWorld(v) - new DVec3(1.5, 3.125, -2.25)).Magnitude == 0, "ZupFrame: with the planetarium still it is the xzy swizzle");
}

/* The free-fall uncertainty form. */
{
    Check(Kinematics.UncertaintyAt(0, 0, 20, 2000, 1) == 10, "uncertainty: a = 20 m/s^2 one second after a fix is 10 m");
    Check(Kinematics.UncertaintyAt(5, 2, 20, 2000, 0) == 5 && Kinematics.UncertaintyAt(5, 2, 20, 2000, -3) == 5, "uncertainty: at or before ValidFromUt it is r0");
    double knee = 2000.0 / 20.0;
    double before = Kinematics.UncertaintyAt(0, 0, 20, 2000, knee - 1e-6), after = Kinematics.UncertaintyAt(0, 0, 20, 2000, knee + 1e-6);
    Check(Math.Abs(before - after) < 1e-2, "uncertainty: continuous at the speed cap");
    Check(Math.Abs(Kinematics.UncertaintyAt(0, 0, 20, 2000, 300) - (2000 * 300 - 2000.0 * 2000.0 / 40.0)) < 1e-6, "uncertainty: linear at the cap after it");
    Check(Kinematics.UncertaintyAt(10, 3, 0, 0, 7) == 31, "uncertainty: no free-fall term leaves r0 + rate * dt");
    Check(Kinematics.UncertaintyAt(0, 0, 20, 0, 5) == 0, "uncertainty: a zero speed cap adds nothing");
}

/* The tooUncertain range rule. */
{
    Check(!Kinematics.TooUncertain(0, 10, 0, 1000, 60), "tooUncertain: no uncertainty never is");
    Check(!Kinematics.TooUncertain(10, 5, 0, double.NaN, 60), "tooUncertain: no range never is");
    Check(Kinematics.TooUncertain(1000, 5, 0, 1000, 60), "tooUncertain: uncertainty reaching the lens is");
    Check(!Kinematics.TooUncertain(10, 5, 0, 100000, 60), "tooUncertain: 10 m at 100 km fits");
    Check(Kinematics.TooUncertain(8000, 5, 0, 10000, 60), "tooUncertain: 8 km at 10 km does not fit a 60 degree field at 2 km");
    Check(Kinematics.TooUncertain(400, 5, 2000, 5000, 30) && !Kinematics.TooUncertain(400, 5, 0, 5000, 30), "tooUncertain: the operator's margin counts");
}

/* Cost: the targets list's 500 conic reads, after the paths are built. */
{
    var paths = new KeplerPath[500];
    var rng = new Random(7);
    for (int i = 0; i < paths.Length; i++)
        KeplerPath.TryFromElements(rng.NextDouble() * 180, rng.NextDouble() * 0.9, 7e5 + rng.NextDouble() * 1e7, rng.NextDouble() * 360,
            rng.NextDouble() * 360, rng.NextDouble() * 6, 0, KerbinMu, out paths[i]);
    for (int i = 0; i < paths.Length; i++) paths[i].TryAt(1e6, out _, out _);
    var sw = Stopwatch.StartNew();
    int ok = 0;
    for (int round = 0; round < 20; round++)
        for (int i = 0; i < paths.Length; i++)
            if (paths[i].TryAt(1e6 + round * 17.0, out _, out _)) ok++;
    double ms = sw.Elapsed.TotalMilliseconds / 20;
    Check(ok == 20 * paths.Length, "cost: every one of 500 random conics places");
    Console.WriteLine($"     500 conic reads: {ms:F3} ms on this machine (the Deck's Mono is several times slower)");
    /* A loose bound, so a slow CI runner passes while an Orbit allocated per read would not. */
    Check(ms < 5, "cost: 500 conic reads stay near the 0.5 ms budget");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
