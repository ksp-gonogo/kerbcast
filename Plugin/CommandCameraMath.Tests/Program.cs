using System;
using System.Collections.Generic;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}
bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;
bool VNear(DVec3 a, DVec3 b, double tol) => (a - b).Magnitude <= tol;

const double R = 600000.0; // Kerbin
var origin = new DVec3(0, 0, 0);
var ix = new DVec3(1, 0, 0);
var iy = new DVec3(0, 1, 0);
var iz = new DVec3(0, 0, 1);

// SurfacePosition with an identity body frame: KSP's z-up local swizzled to
// Unity's y-up, so the north pole is +Y and (0,0) is +X.
Check(VNear(CommandCameraMath.SurfacePosition(origin, ix, iy, iz, R, 0, 0, 0), new DVec3(R, 0, 0), 1e-6),
    "lat 0 lon 0 is +X at the radius");
Check(VNear(CommandCameraMath.SurfacePosition(origin, ix, iy, iz, R, 90, 0, 0), new DVec3(0, R, 0), 1e-6),
    "north pole is Unity +Y");
Check(VNear(CommandCameraMath.SurfacePosition(origin, ix, iy, iz, R, 0, 90, 0), new DVec3(0, 0, R), 1e-6),
    "lon 90 is Unity +Z (KSP local +Y swizzled)");
Check(VNear(CommandCameraMath.SurfacePosition(origin, ix, iy, iz, R, 0, 0, 70), new DVec3(R + 70, 0, 0), 1e-6),
    "altitude adds along the normal");
var centre = new DVec3(1000, -2000, 3000);
Check(VNear(CommandCameraMath.SurfacePosition(centre, ix, iy, iz, R, 0, 0, 0), new DVec3(R + 1000, -2000, 3000), 1e-6),
    "body centre offsets the result (floating origin moves the body, not the maths)");
// A body rotated 90 degrees about KSP's z (its spin axis): local +X now points
// along world +Y (z-up), which is Unity +Z.
var rx = new DVec3(0, 1, 0);
var ry = new DVec3(-1, 0, 0);
Check(VNear(CommandCameraMath.SurfacePosition(origin, rx, ry, iz, R, 0, 0, 0), new DVec3(0, 0, R), 1e-6),
    "body rotation carries the station round with it");
// KSC-ish coordinates land on the sphere at radius + alt.
var kscPos = CommandCameraMath.SurfacePosition(origin, ix, iy, iz, R, -0.0972, -74.5577, 70);
Check(Near(kscPos.Magnitude, R + 70, 1e-6), "KSC-ish point sits at radius + alt");

// AimBasis: forward points at the target, up stays in the vertical plane and
// orthogonal to forward.
var station = new DVec3(0, R, 0);
var stationUp = new DVec3(0, 1, 0);
var north = new DVec3(0, 0, 1);
CommandCameraMath.AimBasis(station, station + new DVec3(1000, 0, 0), stationUp, north, out var f1, out var u1);
Check(VNear(f1, new DVec3(1, 0, 0), 1e-9), "level target: forward is horizontal");
Check(VNear(u1, stationUp, 1e-9), "level target: up is local vertical (horizon level)");
CommandCameraMath.AimBasis(station, station + new DVec3(1000, 1000, 0), stationUp, north, out var f2, out var u2);
Check(Near(DVec3.Dot(f2, u2), 0, 1e-9), "climbing target: up orthogonal to forward");
Check(u2.Y > 0, "climbing target: up still points skyward");
Check(Near(CommandCameraMath.ElevationDeg(f2, stationUp), 45, 1e-9), "climbing target elevation is 45 degrees");
CommandCameraMath.AimBasis(station, station + new DVec3(0, 5000, 0), stationUp, north, out var f3, out var u3);
Check(VNear(f3, stationUp, 1e-9), "overhead target: forward is straight up");
Check(VNear(u3, north, 1e-9), "overhead target: up falls back to north instead of NaN");

// Angular size and FoV-for-fraction.
Check(Near(CommandCameraMath.AngularDiameterDeg(15, 600), 2.0 * Math.Asin(15.0 / 600.0) * 180 / Math.PI, 1e-9),
    "angular diameter is 2 asin(r/d)");
Check(CommandCameraMath.AngularDiameterDeg(15, 10) == 180.0, "inside the sphere subtends 180");
double padFov = CommandCameraMath.FovForFraction(15, 600, 0.3, 0.02, 30);
Check(padFov > 9 && padFov < 10, $"pad framing: 15 m craft at 600 m is about 9.5 degrees (got {padFov:F2})");
double screenFrac = Math.Tan(CommandCameraMath.AngularDiameterDeg(15, 600) * 0.5 * Math.PI / 180)
    / Math.Tan(padFov * 0.5 * Math.PI / 180);
Check(Near(screenFrac, 0.3, 1e-9), "the fraction is exact on screen (tan-ratio)");
double fov30k = CommandCameraMath.FovForFraction(15, 30000, 0.3, 0.02, 30);
Check(fov30k > 0.18 && fov30k < 0.2, $"30 km: about 0.19 degrees (got {fov30k:F3})");
Check(CommandCameraMath.FovForFraction(15, 5e6, 0.3, 0.02, 30) == 0.02, "deep range clamps to the floor");
Check(CommandCameraMath.FovForFraction(15, 20, 0.3, 0.02, 30) == 30, "inside the craft clamps to the ceiling");
Check(CommandCameraMath.FovForFraction(15, 10, 0.3, 0.02, 30) == 30, "distance under radius returns the ceiling");

// SmoothFov: log-space ease, snap on long gaps.
double s = CommandCameraMath.SmoothFov(10, 1, 0.4, 0.4, 1.0);
double expected = Math.Exp(Math.Log(10) + (1 - Math.Exp(-1)) * (Math.Log(1) - Math.Log(10)));
Check(Near(s, expected, 1e-12), "one tau covers 63% of the way in log space");
Check(CommandCameraMath.SmoothFov(10, 1, 2.0, 0.4, 1.0) == 1, "a gap past snapAfter snaps to target");
Check(CommandCameraMath.SmoothFov(0, 5, 0.01, 0.4, 1.0) == 5, "uninitialised current snaps");
Check(CommandCameraMath.SmoothFov(10, double.NaN, 0.016, 0.4, 1.0) == 10, "a NaN target holds the current field");
Check(CommandCameraMath.SmoothFov(10, double.PositiveInfinity, 0.016, 0.4, 1.0) == 10, "an infinite target holds the current field");
double down = CommandCameraMath.SmoothFov(20, 0.02, 0.016, 0.4, 1.0);
Check(down < 20 && down > 0.02, "one frame moves part of the way");

// Occlusion: the sphere rule.
var planet = new DVec3(0, 0, 0);
var onSurface = new DVec3(0, R + 70, 0);
Check(CommandCameraMath.SegmentClearOfSphere(onSurface, new DVec3(0, R + 80000, 0), planet, R),
    "craft overhead is visible");
Check(!CommandCameraMath.SegmentClearOfSphere(onSurface, new DVec3(0, -(R + 80000), 0), planet, R),
    "craft on the far side is hidden");
Check(!CommandCameraMath.SegmentClearOfSphere(onSurface, new DVec3(R + 80000, 0, 0), planet, R),
    "craft 90 degrees round at 80 km altitude is below the horizon");
Check(CommandCameraMath.SegmentClearOfSphere(onSurface, new DVec3(1000, R + 70, 0), planet, R),
    "craft on the pad 1 km away is visible (the segment barely dips)");
Check(CommandCameraMath.SegmentClearOfSphere(onSurface, onSurface, planet, R), "zero-length segment outside is clear");
Check(CommandCameraMath.SegmentClearOfSphere(onSurface, new DVec3(0, -1e9, 0), planet, 0),
    "a body with no radius never blocks");

// Terrain occlusion: a 7 km ridge on the limb hides a grazing sight line the sphere passes.
const double MunR = 200000;
var mun = new DVec3(0, 0, 0);
var scope = new DVec3(-1.1e7, MunR + 3000, 0);
var grazing = new DVec3(30000, MunR + 3000, 0);
Func<DVec3, double> ridge = p => Math.Abs(p.X) < 20000 ? MunR + 7000 : MunR;
Check(CommandCameraMath.SegmentClearOfSphere(scope, grazing, mun, MunR), "grazing line clears the sea-level sphere");
Check(!CommandCameraMath.SegmentClearOfTerrain(scope, grazing, mun, MunR, MunR + 7500, ridge, 64),
    "a ridge on the limb hides the grazing line");
Check(CommandCameraMath.SegmentClearOfTerrain(scope, new DVec3(30000, MunR + 9000, 0), mun, MunR, MunR + 7500, ridge, 64),
    "a line above the highest peak is clear");
Check(CommandCameraMath.SegmentClearOfTerrain(scope, grazing, mun, MunR, MunR + 7500, p => MunR, 64),
    "flat terrain leaves the grazing line clear");
Check(!CommandCameraMath.SegmentClearOfTerrain(scope, new DVec3(1e7, 0, 0), mun, MunR, MunR + 7500, p => MunR, 64),
    "a line through the body is still hidden");
Check(CommandCameraMath.SegmentClearOfTerrain(scope, grazing, mun, MunR, MunR, ridge, 64),
    "no terrain headroom falls back to the sphere");
Check(!CommandCameraMath.SegmentInsideSphere(scope, new DVec3(0, MunR + 9000, 0), mun, MunR + 7500, out _, out _),
    "a line above the shell never enters it");

// Bounding radius.
var parts = new List<DVec3> { new DVec3(0, 10, 0), new DVec3(0, -5, 0), new DVec3(2, 0, 0) };
Check(Near(CommandCameraMath.BoundingRadius(parts, origin, 1.5, 2), 11.5, 1e-9), "farthest part plus pad");
Check(CommandCameraMath.BoundingRadius(new List<DVec3>(), origin, 0.5, 2) == 2, "never below the minimum");
Check(CommandCameraMath.BoundingRadius(null, origin, 0.5, 2) == 2, "null part list gives the minimum");

/* Which chutes show a canopy, when one is unfolding, and when a craft's cached chutes are found again. */
Check(CommandCameraMath.CanopyOpen(CommandCameraMath.ChuteState.SemiDeployed) && CommandCameraMath.CanopyOpen(CommandCameraMath.ChuteState.Deployed),
    "canopy: semi and fully deployed chutes count");
Check(!CommandCameraMath.CanopyOpen(CommandCameraMath.ChuteState.Stowed) && !CommandCameraMath.CanopyOpen(CommandCameraMath.ChuteState.Active)
    && !CommandCameraMath.CanopyOpen(CommandCameraMath.ChuteState.Cut), "canopy: packed, armed and cut chutes do not");
Check(CommandCameraMath.CanopyUnfolding(0.4f, 0.2f) && CommandCameraMath.CanopyUnfolding(0f, -1f), "canopy: a moving deploy animation is unfolding, first look included");
Check(!CommandCameraMath.CanopyUnfolding(1f, 0.9f), "canopy: a finished animation is not");
Check(!CommandCameraMath.CanopyUnfolding(0f, 0f) && !CommandCameraMath.CanopyUnfolding(0.5f, 0.5f),
    "canopy: one held short of the end (in vacuum, or paused) is not, so the fast refresh stops");
Check(!CommandCameraMath.ChutesStale(12, 12, true, false, false), "chute cache: the same parts, root and objects keep it");
Check(CommandCameraMath.ChutesStale(12, 9, true, false, false), "chute cache: staging or docking rebuilds it");
Check(CommandCameraMath.ChutesStale(12, 12, false, false, false), "chute cache: a reload (same id and count, new root part) rebuilds it");
Check(CommandCameraMath.ChutesStale(12, 12, true, true, false), "chute cache: a destroyed module or renderer rebuilds it");
Check(CommandCameraMath.ChutesStale(12, 12, true, false, true), "chute cache: a chute that just opened rebuilds it, for a kerbal's chute fitted since");

/* BoxReach: a canopy's renderer bounds measured from the craft's centre of mass. */
Check(Near(CommandCameraMath.BoxReach(new DVec3(0, 15, 0), new DVec3(4, 2, 4), origin), Math.Sqrt(16 + 289 + 16), 1e-9),
    "a canopy 15 m above the centre of mass reaches its far corner");
Check(Near(CommandCameraMath.BoxReach(new DVec3(0, -15, 0), new DVec3(4, 2, 4), origin), Math.Sqrt(16 + 289 + 16), 1e-9),
    "the reach is the same on either side of the centre of mass");
Check(Near(CommandCameraMath.BoxReach(origin, new DVec3(1, 2, 2), origin), 3, 1e-9), "a box about the point reaches its half diagonal");
Check(Near(CommandCameraMath.BoxReach(new DVec3(10, 0, 0), new DVec3(-1, 0, 0), new DVec3(10, 0, 0)), 1, 1e-9), "negative extents count as their size");
{
    /* A canopy unfolding over one second from 3 m to 20 m, seen at 400 m and drawn at 60 fps. */
    const double Distance = 400, Fraction = 0.6, Dt = 1.0 / 60;
    double Target(double t) => 3 + 17 * Math.Min(1, Math.Max(0, t));
    double Run(double refresh, out double settled)
    {
        double radius = Target(0), fov = CommandCameraMath.FovForFraction(radius, Distance, Fraction, 0.02, 60);
        double measured = 0, worst = 0;
        for (double t = 0; t < 3; t += Dt)
        {
            if (t - measured >= refresh - 1e-9) { radius = Target(t); measured = t; }
            double next = CommandCameraMath.SmoothFov(fov, CommandCameraMath.FovForFraction(radius, Distance, Fraction, 0.02, 60), Dt, 0.4, 1.0);
            worst = Math.Max(worst, Math.Abs(Math.Log(next / fov)));
            fov = next;
        }
        settled = fov;
        return worst;
    }
    double fast = Run(0.1, out double fastFov), slow = Run(0.5, out _);
    double open = CommandCameraMath.FovForFraction(20, Distance, Fraction, 0.02, 60);
    Check(fast < slow, $"refreshing while a canopy unfolds steps the zoom less per frame ({fast:G3} vs {slow:G3} in log fov)");
    double every = Run(Dt, out _);
    Check(fast < every * 1.25, $"and steps it within a quarter of measuring every frame ({fast:G3} vs {every:G3})");
    Check(Near(fastFov, open, open * 0.01), $"the view settles framing the open canopy ({fastFov:F2} vs {open:F2} deg)");
}

{
    /*
     * A craft 12 km out framed with a world model's uncertainty, which starts
     * over at each record (every 0.53 s) and runs high for the last 0.1 s of
     * it, drawn at 60 fps into 576 rows. Sizing the bracket from the raw
     * framing over the eased field pumps it in and out with every record;
     * sized from the framing the field was eased to, it holds the fraction.
     */
    const double Distance = 12000, Fraction = 0.3, Dt = 1.0 / 60, FovMin = 0.02, FovMax = 60, Period = 0.53;
    const int Rows = 576;
    double Radius(double t)
    {
        double age = t % Period;
        return 5 + 40 * age + (age > Period - 0.1 ? 150 : 0);
    }
    (double lo, double hi) Old()
    {
        double fov = CommandCameraMath.FovForFraction(Radius(0), Distance, Fraction, FovMin, FovMax);
        double lo = double.MaxValue, hi = 0;
        for (double t = 0; t < 5; t += Dt)
        {
            fov = CommandCameraMath.SmoothFov(fov, CommandCameraMath.FovForFraction(Radius(t), Distance, Fraction, FovMin, FovMax), Dt, 0.4, 1.0);
            double b = CommandCameraMath.BracketHalfPx(Radius(t), Distance, fov, Rows);
            if (t > 2) { lo = Math.Min(lo, b); hi = Math.Max(hi, b); }
        }
        return (lo, hi);
    }
    (double lo, double hi, double worstStep) New(Func<double, double> radius, double fovMin)
    {
        var framing = new SubjectFraming();
        double fov = 30, lo = double.MaxValue, hi = 0, prev = double.NaN, worst = 0;
        for (double t = 0; t < 7; t += Dt)
        {
            fov = framing.Step(CommandCameraMath.FramingTan(radius(t), Distance), CommandCameraMath.FramingTan(5, Distance), fov,
                Fraction, fovMin, FovMax, Dt, 0.4, 1.0);
            double b = framing.BracketHalfPx(fov, Rows);
            if (!double.IsNaN(prev)) worst = Math.Max(worst, Math.Abs(Math.Log(b / prev)));
            prev = b;
            if (t > 4) { lo = Math.Min(lo, b); hi = Math.Max(hi, b); }
        }
        return (lo, hi, worst);
    }
    var (oldLo, oldHi) = Old();
    Check(oldHi / oldLo > 1.5, $"raw framing over the eased field: the bracket pumps with each record ({oldLo:F1} to {oldHi:F1} px)");
    var (newLo, newHi, _) = New(Radius, FovMin);
    double held = Fraction * Rows / 2;
    Check(Near(newLo, held, 0.05) && Near(newHi, held, 0.05),
        $"eased framing: once the zoom has settled the bracket holds the framing fraction through every record ({newLo:F2} to {newHi:F2} px, want {held:F1})");

    /* At the field's floor the box follows the craft below the fraction, eased rather than snapping. */
    double floor = 10.0;
    var (fLo, fHi, fWorst) = New(Radius, floor);
    double rawLo = CommandCameraMath.BracketHalfPx(5, Distance, floor, Rows);
    Check(fHi < held && fLo > rawLo, $"at the floor the box sits below the fraction and above the smallest raw size ({fLo:F1} to {fHi:F1} px)");
    double calm = 5 + 40 * (Period - 0.1);
    double rawStep = Math.Log(CommandCameraMath.BracketHalfPx(calm + 150, Distance, floor, Rows)
        / CommandCameraMath.BracketHalfPx(calm, Distance, floor, Rows));
    Check(fWorst < rawStep / 10, $"and steps far less per frame than the raw size jumps ({fWorst:G3} vs {rawStep:G3} in log px)");

    /* The first step starts from the field already shown, so a view off a placeholder zooms on rather than snapping. */
    var start = new SubjectFraming();
    double tan5 = CommandCameraMath.FramingTan(5, Distance);
    double first = start.Step(tan5, tan5, 30, Fraction, FovMin, FovMax, Dt, 0.4, 1.0);
    double wantFirst = CommandCameraMath.FovForTan(
        CommandCameraMath.SmoothFov(CommandCameraMath.FramingTanAt(30, Fraction), tan5, Dt, 0.4, 1.0), Fraction, FovMin, FovMax);
    Check(first < 30 && Near(first, wantFirst, 1e-9), $"the first step eases from the field shown ({first:F4} deg, want {wantFirst:F4})");
    Check(double.IsNaN(new SubjectFraming().BracketHalfPx(30, Rows)), "no bracket before the first step");
    var inside = new SubjectFraming();
    double insideFov = inside.Step(CommandCameraMath.FramingTan(50, 10), CommandCameraMath.FramingTan(50, 10), 10, Fraction, FovMin, FovMax, Dt, 0.4, 1.0);
    Check(Near(inside.BracketHalfPx(insideFov, Rows), Rows, 1e-9), "a lens inside the framing sphere fills the frame with the box");

    /* Acquired from a placeholder at the widest field, a 25 m craft 12 km out is boxed at its own size while the view zooms onto it. */
    {
        var acquire = new SubjectFraming();
        double tan25 = CommandCameraMath.FramingTan(25, Distance), fov = FovMax, worst = 0;
        for (int i = 0; i <= 120; i++)
        {
            double shown = fov;
            fov = acquire.Step(tan25, tan25, shown, Fraction, FovMin, FovMax, Dt, 0.4, 1.0);
            double ratio = acquire.BracketHalfPx(fov, Rows) / CommandCameraMath.BracketHalfPx(25, Distance, fov, Rows);
            worst = Math.Max(worst, Math.Abs(Math.Log(ratio)));
        }
        Check(worst < 1e-9, $"on acquisition the box is the craft's own size on every frame of the zoom (worst log ratio {worst:G3})");
        Check(fov < 10, $"while the view zooms in ({fov:F2} deg after 2 s)");

        /* A retarget resets the framing: the new craft is boxed at its size at once, and the view zooms on from where it was. */
        acquire.Reset();
        double tan80 = CommandCameraMath.FramingTan(80, 3000);
        double next = acquire.Step(tan80, tan80, fov, Fraction, FovMin, FovMax, Dt, 0.4, 1.0);
        Check(Near(acquire.BracketHalfPx(next, Rows), CommandCameraMath.BracketHalfPx(80, 3000, next, Rows), 1e-9),
            "a retarget boxes the new craft at its own size on its first frame");
        Check(next > fov * 1.01 && next < fov * 2, $"and the view eases out from the field shown ({fov:F2} to {next:F2} deg)");
    }

    /* One frame with no framing (a NaN radius) holds the view and the box rather than throwing the field wide open. */
    {
        var held2 = new SubjectFraming();
        double fov = 30;
        for (int i = 0; i < 300; i++) fov = held2.Step(tan5, tan5, fov, Fraction, FovMin, FovMax, Dt, 0.4, 1.0);
        double box = held2.BracketHalfPx(fov, Rows);
        double after = held2.Step(double.NaN, double.NaN, fov, Fraction, FovMin, FovMax, Dt, 0.4, 1.0);
        Check(held2.Started && Near(after, fov, 1e-12) && Near(held2.BracketHalfPx(after, Rows), box, 1e-9),
            $"a NaN framing holds the field and the box ({fov:F4} to {after:F4} deg)");
        Check(double.IsPositiveInfinity(CommandCameraMath.EaseFraming(0.01, double.PositiveInfinity, Dt, 0.4, 1.0)),
            "an infinite framing (the lens inside it) is still taken at once");
    }

    /* A 10 m craft passing at 300 m/s from 2 km to 200 m with the field on its floor: the box never draws inside the craft. */
    {
        var pass = new SubjectFraming();
        double fov = 10, worst = double.MaxValue;
        for (double t = 0; t < 6; t += Dt)
        {
            double range = 2000 - 300 * t, tan = CommandCameraMath.FramingTan(10, range);
            fov = pass.Step(tan, tan, fov, Fraction, 10, FovMax, Dt, 0.4, 1.0);
            worst = Math.Min(worst, pass.BracketHalfPx(fov, Rows) / CommandCameraMath.BracketHalfPx(10, range, fov, Rows));
        }
        Check(worst >= 1 - 1e-9, $"a fast pass at the floor keeps the box at least the craft's size (worst box over craft {worst:F3})");
    }

    /* The ease runs on time, not frames: 30 and 144 fps reach the same field and box at 0.5 s. */
    {
        double Field(double fps, out double box)
        {
            var f = new SubjectFraming();
            double fov = 30;
            int n = (int)Math.Round(0.5 * fps);
            for (int i = 0; i < n; i++) fov = f.Step(tan5, tan5, fov, Fraction, FovMin, FovMax, 1.0 / fps, 0.4, 1.0);
            box = f.BracketHalfPx(fov, Rows);
            return fov;
        }
        double at30 = Field(30, out double box30), at144 = Field(144, out double box144);
        Check(Near(at30, at144, at144 * 1e-9) && Near(box30, box144, box144 * 1e-9),
            $"the framing is frame-rate independent ({at30:F6} vs {at144:F6} deg, {box30:F4} vs {box144:F4} px)");
    }
}

// Clip plan on a split (OpenGL/Metal) stack: near 0.21..397, far 397..750 km.
const float nN = 0.21f, nF = 397f, fN = 397f, fF = 750000f;
var pad = CommandCameraMath.PlanClips(600, 15, false, true, false, nN, nF, fN, fF);
Check(!pad.Split && !pad.MidActive && pad.FarRender && pad.FarNear == fN && pad.FarFar == fF,
    "pad range: native split, no mid layer");
var low = CommandCameraMath.PlanClips(5000, 15, false, true, false, nN, nF, fN, fF);
Check(low.Split && low.MidActive && low.FarRender, "5 km: mid layer on");
Check(Near(low.FarNear, 5000 - 80, 1e-3) && low.MidNear == fN && Near(low.MidFar, low.FarNear, 1e-3),
    "5 km: far starts just short of the craft and mid meets it");
Check(low.FarFar == fF && low.NearFar == nF, "5 km: far keeps 750 km, near untouched");
double depthRatioBefore = fF / fN, depthRatioAfter = fF / low.FarNear;
Check(depthRatioAfter < depthRatioBefore / 10, "the craft's depth range gets over 10x the precision");
var deep = CommandCameraMath.PlanClips(2e6, 15, false, true, true, nN, nF, fN, fF);
Check(Near(deep.FarFar, 2e6 + 80, 1) && Near(deep.FarNear, 2e6 - 80, 1), "past 750 km the far plane follows the craft");
// Hysteresis: 1.5x..2x of the native near plane keeps whatever state it had.
double between = fN * 1.75 + 80;
Check(CommandCameraMath.PlanClips(between, 15, false, true, true, nN, nF, fN, fF).Split, "hysteresis keeps split on");
Check(!CommandCameraMath.PlanClips(between, 15, false, true, false, nN, nF, fN, fF).Split, "hysteresis keeps split off");
// Occluded: far skipped, mid stops short of the craft.
var hidden = CommandCameraMath.PlanClips(900000, 15, true, true, true, nN, nF, fN, fF);
Check(!hidden.FarRender && hidden.MidActive && hidden.MidFar == fF, "occluded beyond 750 km: terrain only, craft never drawn");
var hiddenNear = CommandCameraMath.PlanClips(100000, 15, true, true, true, nN, nF, fN, fF);
Check(!hiddenNear.FarRender && Near(hiddenNear.MidFar, 100000 - 80, 1e-3), "occluded: mid stops short of the craft");
// D3D11 single-camera stack.
var single = CommandCameraMath.PlanClips(5000, 15, false, false, false, nN, fF, 0, 0);
Check(!single.FarRender && !single.MidActive && single.NearFar == fF, "single stack: native range");
var singleDeep = CommandCameraMath.PlanClips(2e6, 15, false, false, false, nN, fF, 0, 0);
Check(Near(singleDeep.NearFar, 2e6 + 80, 1), "single stack: far plane follows past 750 km");
var singleHidden = CommandCameraMath.PlanClips(2e6, 15, true, false, false, nN, fF, 0, 0);
Check(Near(singleHidden.NearFar, 2e6 - 80, 1), "single stack occluded: stops short of the craft");

// Launch-site stand-off: 500 m on a Kerbin-sized sphere from the KSC pad.
const double kerbinR = 600000;
CommandCameraMath.Destination(-0.0972, -74.5577, 180, 500, kerbinR, out double sLat, out double sLon);
Check(Near(sLon, -74.5577, 1e-9) && Near((-0.0972 - sLat) * Math.PI / 180 * kerbinR, 500, 1e-6),
    "due south: same longitude, 500 m of latitude");
CommandCameraMath.Destination(0, -74.5577, 90, 500, kerbinR, out double eLat, out double eLon);
Check(Near(eLat, 0, 1e-12) && Near((eLon + 74.5577) * Math.PI / 180 * kerbinR, 500, 1e-6),
    "due east on the equator: 500 m of longitude");
CommandCameraMath.Destination(-0.0972, -74.5577, 37, 500, kerbinR, out double aLat, out double aLon);
var padPos = CommandCameraMath.SurfacePosition(new DVec3(0, 0, 0), new DVec3(1, 0, 0), new DVec3(0, 1, 0), new DVec3(0, 0, 1),
    kerbinR, -0.0972, -74.5577, 0);
var lensPos = CommandCameraMath.SurfacePosition(new DVec3(0, 0, 0), new DVec3(1, 0, 0), new DVec3(0, 1, 0), new DVec3(0, 0, 1),
    kerbinR, aLat, aLon, 0);
Check(Near((lensPos - padPos).Magnitude, 500, 0.01), "any bearing: the lens stands 500 m from the pad");
CommandCameraMath.Destination(10, 179.9999, 90, 500, kerbinR, out _, out double wrapLon);
Check(wrapLon > -180 && wrapLon < -179.9, "longitude wraps across the antimeridian");

// Impostor stand-off distance.
Check(CommandCameraMath.ImpostorDistance(2e6, 5000, 1e-5, 1000, 4) == 5000, "impostor: preferred distance when precision allows");
Check(CommandCameraMath.ImpostorDistance(3000, 5000, 1e-5, 1000, 4) == 3000, "impostor: never beyond the target");
double floorD = CommandCameraMath.ImpostorDistance(2e6, 5000, 1e-6, 3e5, 4);
Check(Near(floorD * 1e-6, 4 * CommandCameraMath.FloatEpsilon * 3e5, 1e-12), "impostor: pushed out until the dot spans 4 float steps");
Check(Near(CommandCameraMath.PixelSizeAtUnit(90, 2), 1.0, 1e-12), "pixel size: 90 degrees over 2 rows is 1 m per m");

// Bracket half-extent: a 10 m sphere at 10 km, 1 degree over 1000 rows.
double perPxB = CommandCameraMath.PixelSizeAtUnit(1.0, 1000);
double bracket = CommandCameraMath.BracketHalfPx(10, 10000, 1.0, 1000);
Check(Near(bracket, (10 / Math.Sqrt(10000.0 * 10000 - 100)) / perPxB, 1e-9), "bracket: tan of the half-angle over the pixel size");
Check(Near(bracket, 57.3, 0.1), $"bracket: 10 m at 10 km in a 1 degree, 1000-row frame is about 57 px (got {bracket:F2})");
Check(Near(CommandCameraMath.BracketHalfPx(1210, 10000, 10.0, 1000) / CommandCameraMath.BracketHalfPx(10, 10000, 10.0, 1000), 121.9, 0.05),
    "bracket: a framing margin grows the box with the radius");
Check(CommandCameraMath.BracketHalfPx(10, 5, 1.0, 576) == 576, "bracket: lens inside the sphere caps at the frame height");
Check(CommandCameraMath.BracketHalfPx(0, 1000, 1.0, 576) == 0, "bracket: no radius, no box");
Check(CommandCameraMath.BracketHalfPx(1, 1e9, 30, 576) > 0, "bracket: tiny but positive at deep range");

// An unloaded subject under the marker setting: a point while point-sized, its model once it fills more.
Check(CommandCameraMath.SubjectAsModel(false, true, false, 2.0, 447, 1.707, 406),
    "subject: a 2 m craft 447 m away in a 1.7 degree frame is a model, not a 3 px point");
Check(!CommandCameraMath.SubjectAsModel(false, true, false, 2.0, 268000, 1.707, 406), "subject: the same craft 268 km away stays a point");
Check(CommandCameraMath.SubjectAsModel(true, false, false, 2.0, 268000, 1.707, 406), "subject: the model setting always draws the model");
Check(!CommandCameraMath.SubjectAsModel(false, false, true, 2.0, 447, 1.707, 406), "subject: with neither setting there is nothing to draw");
double onePx = CommandCameraMath.PixelSizeAtUnit(1.0, 1000) * 10000;
Check(CommandCameraMath.SubjectAsModel(false, true, false, 3.03 * onePx, 10000, 1.0, 1000)
    && !CommandCameraMath.SubjectAsModel(false, true, false, 2.97 * onePx, 10000, 1.0, 1000),
    "subject: a point becomes a model at PointBelowPx across, where the picture's stand-ins switch");
Check(CommandCameraMath.SubjectAsModel(false, true, true, 2.5 * onePx, 10000, 1.0, 1000)
    && !CommandCameraMath.SubjectAsModel(false, true, true, 1.9 * onePx, 10000, 1.0, 1000),
    "subject: a model goes back to a point only under ModelLeaveBelowPx");
Check(CommandCameraMath.SubjectSizeM(20, 15, 100) == 20 && CommandCameraMath.SubjectSizeM(0, 15, 100) == 15
    && CommandCameraMath.SubjectSizeM(0, 0, 100) == 100, "subject size: the model, else the history's parts, else the guess");

/*
 * The rig's path frame by frame, as it measures: a 20 m station whose
 * history records its parts, closing at the field floor (1.707 degrees over
 * 406 rows). It turns into a model where 20 m spans 6 px, not where a
 * nominal 2 m craft would, and once built, judged by the model's own radius
 * at a field that widens with it, it never flips back while it closes.
 */
{
    double fov = 1.707, partsSize = 20, modelRadius = 0;
    bool shown = false, flipped = false;
    double switchedAt = double.NaN;
    for (double d = 3e6; d > 400; d *= 0.98)
    {
        double size = CommandCameraMath.SubjectSizeM(modelRadius, partsSize, 100);
        bool model = CommandCameraMath.SubjectAsModel(false, true, shown, size, d, fov, 406);
        if (model && !shown) { if (!double.IsNaN(switchedAt)) flipped = true; switchedAt = d; modelRadius = 21; }
        if (!model && shown) flipped = true;
        shown = model;
        fov = Math.Max(fov, CommandCameraMath.FovForFraction(modelRadius > 0 ? modelRadius : 2, d, 0.3, 1.707, 30));
    }
    double sixPxAt = 2 * 20 / (6 * CommandCameraMath.PixelSizeAtUnit(1.707, 406));
    Check(Near(switchedAt, sixPxAt, 0.03 * sixPxAt), $"rig path: a 20 m station turns model near {sixPxAt / 1000:F0} km (got {switchedAt / 1000:F0} km)");
    Check(!flipped, "rig path: once a model it stays one as it closes and the field widens");
}
{
    /* No size known: tried as a model at the generous guess, then judged by the 2.8 m model, which is a point again far off. */
    double modelRadius = 0;
    bool shown = false;
    double size = CommandCameraMath.SubjectSizeM(modelRadius, 0, 100);
    shown = CommandCameraMath.SubjectAsModel(false, true, shown, size, 268000, 1.707, 406);
    Check(shown, "rig path: a craft of unknown size 268 km away is tried as a model");
    modelRadius = 2.8;
    shown = CommandCameraMath.SubjectAsModel(false, true, shown, CommandCameraMath.SubjectSizeM(modelRadius, 0, 100), 268000, 1.707, 406);
    Check(!shown, "rig path: its 2.8 m model is under a pixel there, so it is a point again, and stays one");
    shown = CommandCameraMath.SubjectAsModel(false, true, shown, CommandCameraMath.SubjectSizeM(modelRadius, 0, 100), 268000, 1.707, 406);
    Check(!shown, "rig path: no flip back once judged by the model");
}

/*
 * Showing what was decided. B's build failed and A's model is still held:
 * it never stands in for B. A model built only to learn a guessed size waits
 * a frame behind the marker, then its own radius decides; the model setting
 * has no marker to wait behind.
 */
Check(!CommandCameraMath.ShowsModel(true, false, false, false), "shown: another craft's model never stands in for the subject");
Check(!CommandCameraMath.ShowsModel(true, false, true, false), "shown: not under the model setting either");
Check(!CommandCameraMath.ShowsModel(true, true, false, true), "shown: the probe frame keeps the marker");
Check(CommandCameraMath.ShowsModel(true, true, true, true), "shown: the model setting shows a model built on a guess at once");
Check(CommandCameraMath.ShowsModel(true, true, false, false) && !CommandCameraMath.ShowsModel(false, true, false, false),
    "shown: a matching model is shown exactly when decided");
{
    /* The probe, frame by frame: unknown size 20 km away, a 10 m model. The dot never blinks out. */
    double modelRadius = 0;
    bool shown = false;
    var seen = new List<string>();
    for (int frame = 0; frame < 3; frame++)
    {
        bool guessed = modelRadius == 0;
        double size = CommandCameraMath.SubjectSizeM(modelRadius, 0, 100);
        bool decided = CommandCameraMath.SubjectAsModel(false, true, shown, size, 20000, 1.707, 406);
        if (decided) modelRadius = 10;
        shown = CommandCameraMath.ShowsModel(decided, modelRadius > 0, false, guessed);
        seen.Add(shown ? "model" : "marker");
    }
    Check(string.Join(",", seen) == "marker,model,model", $"probe: marker on the frame the model is built, model after ({string.Join(",", seen)})");
}

// Framing fit at the widest field.
Check(CommandCameraMath.FramingFits(1000, 1e6, 30), "fit: 1 km at 1000 km fits a 30 degree view");
Check(!CommandCameraMath.FramingFits(400000, 1e6, 30), "fit: 400 km at 1000 km (23.6 degrees half-angle) does not fit 30 degrees");
Check(CommandCameraMath.FramingFits(Math.Sin(15 * Math.PI / 180) * 1e6 - 1, 1e6, 30), "fit: just inside the edge fits");
Check(!CommandCameraMath.FramingFits(10, 5, 30), "fit: lens inside the sphere never fits");
Check(CommandCameraMath.FramingFits(0, 5, 30), "fit: no radius always fits");

// Blank reasons: fail closed, in precedence order.
Check(CommandCameraMath.BlankReason(false, true, "x") == null, "blank: standalone never blanks");
Check(CommandCameraMath.BlankReason(true, true, "Custom") == CommandCameraMath.DefaultSilentText,
    "blank: a silent holder beats its own reason, with the default silence text");
Check(CommandCameraMath.BlankReason(true, true, "Custom", false, "Mission control silent") == "Mission control silent",
    "blank: a silent holder's own silence text shows");
Check(CommandCameraMath.DefaultSilentText == "Authority not responding", "blank: the default silence text is generic");
Check(CommandCameraMath.BlankReason(true, false, "Custom") == "Custom", "blank: the holder's reason shows");
Check(CommandCameraMath.BlankReason(true, false, "") == null, "blank: a held lock with no reason shows the feed");
Check(CommandCameraMath.BlankReason(false, false, null, vantageGone: true) == CommandCameraMath.BlankVantageGone,
    "blank: a lens whose vantage craft is gone blanks without a lock");
Check(CommandCameraMath.BlankReason(true, true, "Custom", vantageGone: true) == CommandCameraMath.BlankVantageGone,
    "blank: a gone vantage beats a silent holder");

// Merges: dock or claw grab folds oldPid into newPid; KSP announces it twice.
Check(CommandCameraMath.MergedPersistentId(11, 11, 22) == 22, "merge: an absorbed craft is followed into the survivor");
Check(CommandCameraMath.MergedPersistentId(22, 11, 22) == 22, "merge: the repeat announcement is a no-op");
Check(CommandCameraMath.MergedPersistentId(33, 11, 22) == 33, "merge: an uninvolved craft is untouched");
Check(CommandCameraMath.MergedPersistentId(22, 22, 11) == 11 && CommandCameraMath.MergedPersistentId(22, 11, 22) == 22,
    "merge: the survivor watched from the start stays");
Check(CommandCameraMath.MergedPersistentId(0, 0, 22) == 0, "merge: nothing watched follows nothing");
Check(CommandCameraMath.MergedPersistentId(11, 11, 0) == 11, "merge: no survivor id keeps the craft");

// Saved records: the save's list is a snapshot, so it outlives docked, crashed and recovered craft.
Check(!CommandCameraMath.SavedRecordCounts(true, true, true), "record: a merged-away craft's record is not a craft in flight");
Check(!CommandCameraMath.SavedRecordCounts(true, false, false), "record: in flight live vessels are the whole list");
Check(!CommandCameraMath.SavedRecordCounts(false, true, true), "record: a record whose Vessel was destroyed never counts");
Check(CommandCameraMath.SavedRecordCounts(false, false, false), "record: an unbuilt record counts where vessels are not built");
Check(CommandCameraMath.SavedRecordCounts(false, true, false), "record: a record with a live Vessel counts");

{
    /* Direction: up +Y, north +Z, east +X, with skewed inputs squared up. */
    var dirUp = new DVec3(0, 1, 0);
    var dirNorth = new DVec3(0, 0.3, 1);
    var dirEast = new DVec3(1, -0.2, 0.1);
    Check(VNear(CommandCameraMath.DirectionVector(dirUp, dirNorth, dirEast, 0, 0), new DVec3(0, 0, 1), 1e-9), "direction: az 0 el 0 is north");
    Check(VNear(CommandCameraMath.DirectionVector(dirUp, dirNorth, dirEast, 90, 0), new DVec3(1, 0, 0), 1e-9), "direction: az 90 is east");
    Check(VNear(CommandCameraMath.DirectionVector(dirUp, dirNorth, dirEast, 270, 0), new DVec3(-1, 0, 0), 1e-9), "direction: az 270 is west");
    Check(VNear(CommandCameraMath.DirectionVector(dirUp, dirNorth, dirEast, 123, 90), dirUp, 1e-9), "direction: el 90 is straight up");
    var d = CommandCameraMath.DirectionVector(dirUp, dirNorth, dirEast, 180, 30);
    Check(Math.Abs(d.Y - 0.5) < 1e-9 && d.Z < 0 && Math.Abs(d.X) < 1e-9, "direction: az 180 el 30 is south and 30 deg up");
}

// Tracking Station level: normalised level to 1-based.
Check(CommandCameraMath.TrackingLevel(0.0, 2) == 1, "level: 0 of stock is level 1");
Check(CommandCameraMath.TrackingLevel(0.5, 2) == 2, "level: 0.5 of stock is level 2");
Check(CommandCameraMath.TrackingLevel(1.0, 2) == 3, "level: 1 of stock is level 3");
Check(CommandCameraMath.TrackingLevel(1.0 / 3.0, 3) == 2, "level: a four-level facility maps a third to level 2");
Check(CommandCameraMath.TrackingLevel(1.0, -1) == 3, "level: an unregistered facility reads as stock");
Check(CommandCameraMath.TrackingLevel(1.7, 2) == 3 && CommandCameraMath.TrackingLevel(-1, 2) == 1, "level: out-of-range input clamps");
Check(Math.Abs(CommandCameraMath.LensFovMin(0.0, 1, 0.1, 0.02) - 1) < 1e-9, "lens: stock level 1 is the lowest lens");
Check(Math.Abs(CommandCameraMath.LensFovMin(0.5, 1, 0.1, 0.02) - 0.1) < 1e-9, "lens: stock level 2 is the middle lens");
Check(Math.Abs(CommandCameraMath.LensFovMin(1.0, 1, 0.1, 0.02) - 0.02) < 1e-9, "lens: stock level 3 is the top lens");
Check(Math.Abs(CommandCameraMath.LensFovMin(0.25, 1, 0.1, 0.02) - Math.Sqrt(0.1)) < 1e-9, "lens: between levels interpolates in log space");
Check(CommandCameraMath.LensFovMin(0.0, 0.01, 0.1, 0.02) >= CommandCameraMath.LensFovMin(1.0, 0.01, 0.1, 0.02), "lens: a lower level is never tighter than a higher one");
Check(CommandCameraMath.LensFovMin(double.NaN, 1, 0.1, 0.02) == 1 && CommandCameraMath.LensFovMin(2, 1, 0.1, 0.02) == 0.02, "lens: bad levels clamp");

// Eligibility: trackability first, then the career gates.
const double Dsn1 = 2e9;
Check(CommandCameraMath.Eligibility(true, true, false, 0, 1e6, Dsn1) == CommandCameraMath.EligibleOk, "eligible: own craft in range");
Check(CommandCameraMath.Eligibility(true, true, false, 0, 3e9, Dsn1) == CommandCameraMath.EligibleTooFar, "eligible: own craft past the range");
Check(CommandCameraMath.Eligibility(false, true, false, 0, 3e9, Dsn1) == CommandCameraMath.EligibleOk, "eligible: no range limit ungated");
Check(CommandCameraMath.Eligibility(true, true, true, 0, 1e6, Dsn1) == CommandCameraMath.EligibleLevelTooLow, "eligible: unowned object at a level that tracks none");
Check(CommandCameraMath.Eligibility(true, true, true, 3, 1e6, Dsn1) == CommandCameraMath.EligibleOk, "eligible: unowned object once the level tracks some");

// TargetEligibility: what the fix source holds, before the career rules.
string ok = CommandCameraMath.EligibleOk, tooFar = CommandCameraMath.EligibleTooFar;
Check(CommandCameraMath.TargetEligibility(CraftFixState.Unknown, null, true, ok) == null, "target: an unknown craft has no row");
Check(CommandCameraMath.TargetEligibility(CraftFixState.Positioned, null, false, tooFar) == tooFar, "target: a positioned craft takes the career rules");
Check(CommandCameraMath.TargetEligibility(CraftFixState.Positioned, null, false, ok, tooUncertain: true) == "tooUncertain",
    "target: a positioned craft the career rules pass can still be too uncertain");
Check(CommandCameraMath.TargetEligibility(CraftFixState.Positioned, null, false, tooFar, tooUncertain: true) == tooFar,
    "target: the career rules come before tooUncertain");
Check(CommandCameraMath.TargetEligibility(CraftFixState.Listed, null, false, ok) == CommandCameraMath.EligibleNotPositioned,
    "target: a listed craft stops at notPositioned before the career rules");
Check(CommandCameraMath.TargetEligibility(CraftFixState.Listed, null, true, ok) == ok,
    "target: under truth a craft that cannot be placed lists under the career rules, as 1.11 did");
Check(CommandCameraMath.TargetEligibility(CraftFixState.Refused, "beyond-horizon", false, ok) == CommandCameraMath.EligibleRefused,
    "target: a refused record reads refused");
Check(CommandCameraMath.TargetEligibility(CraftFixState.Refused, CommandCameraMath.RefusalStale, false, ok) == CommandCameraMath.EligibleStale,
    "target: a stale record reads stale");
Check(CommandCameraMath.EligibilityText(CommandCameraMath.EligibleNotPositioned).Length > 0
    && CommandCameraMath.EligibilityText(CommandCameraMath.EligibleRefused).Length > 0
    && CommandCameraMath.EligibilityText(CommandCameraMath.EligibleStale).Length > 0, "target: every new code has words");
Check(CommandCameraMath.Eligibility(true, true, true, 0, 3e9, Dsn1) == CommandCameraMath.EligibleLevelTooLow, "eligible: level beats range");
Check(CommandCameraMath.Eligibility(false, true, true, 0, 1e6, Dsn1) == CommandCameraMath.EligibleOk, "eligible: unowned object ungated");
Check(CommandCameraMath.Eligibility(false, false, true, 99, 1e6, Dsn1) == CommandCameraMath.EligibleNotTrackable, "eligible: untracked object never, even ungated");
Check(CommandCameraMath.Eligibility(true, false, true, 0, 1e6, Dsn1) == CommandCameraMath.EligibleLevelTooLow, "eligible: untracked object at a level that tracks none needs the upgrade");
Check(CommandCameraMath.Eligibility(true, false, true, 3, 1e6, Dsn1) == CommandCameraMath.EligibleNotTrackable, "eligible: untracked object once the level tracks some");
Check(CommandCameraMath.Eligibility(true, true, false, 0, double.NaN, Dsn1) == CommandCameraMath.EligibleOk, "eligible: no position passes the range test");
Check(CommandCameraMath.EligibilityText(CommandCameraMath.EligibleTooFar) == "Out of tracking range", "eligible: text for tooFar");
Check(CommandCameraMath.EligibilityText("bogus") == "", "eligible: unknown code has no text");

// Significant figures for listed ranges.
Check(CommandCameraMath.RoundSignificant(123456.0, 3) == 123000.0, "round: 3 sig figs");
Check(Near(CommandCameraMath.RoundSignificant(0.012345, 3), 0.0123, 1e-12), "round: small values");
Check(CommandCameraMath.RoundSignificant(-98765.0, 2) == -99000.0, "round: negative");
Check(double.IsNaN(CommandCameraMath.RoundSignificant(double.NaN, 3)) && CommandCameraMath.RoundSignificant(0, 3) == 0, "round: NaN and zero pass through");

// Event ring: only transitions, oldest first, capped, reset when time runs back.
var ring = new CommandTrackEventRing(3);
Check(ring.Record(new CommandTrackEvent(10, false, "", true, null)), "ring: first state recorded");
Check(!ring.Record(new CommandTrackEvent(11, false, null, true, "")), "ring: same state (null and empty equal) not recorded");
Check(ring.Record(new CommandTrackEvent(12, true, "Mun", true, null)), "ring: occlusion edge recorded");
Check(ring.Record(new CommandTrackEvent(13, true, "Minmus", true, null)), "ring: occluder change recorded");
Check(ring.Record(new CommandTrackEvent(14, true, "Minmus", false, null)), "ring: sunlight change recorded");
Check(ring.Count == 3 && ring[0].Ut == 12 && ring[2].Ut == 14, "ring: capped, oldest dropped, oldest first");
Check(ring.Record(new CommandTrackEvent(15, true, "Minmus", false, "Lost")) && ring[2].Blank == "Lost"
    && ring[0].Ut == 13, "ring: blank change recorded and wraps");
Check(ring.Record(new CommandTrackEvent(5, false, "", false, null)) && ring.Count == 1 && ring[0].Ut == 5,
    "ring: time running backwards starts again");

// Body rotation: Rodrigues turns in the Cross sense, matching KSP's getRFrmVel.
var spinAxis = new DVec3(0, -1, 0); // KSP's angularVelocity is Vector3d.down * w
var r0 = new DVec3(R, 0, 0);
var turned = CommandCameraMath.RotateAbout(r0, spinAxis, 1e-6);
var rate = (turned - r0) * (1.0 / 1e-6);
Check(VNear(rate, DVec3.Cross(spinAxis, r0), 1.0), "rotate: rate of turn is Cross(axis, v)");
Check(VNear(CommandCameraMath.RotateAbout(r0, spinAxis, Math.PI / 2), DVec3.Cross(spinAxis, r0).Normalized * R, 1e-6),
    "rotate: a quarter turn lands on Cross(axis, v)");
Check(VNear(CommandCameraMath.RotateAbout(new DVec3(0, 5, 0), spinAxis, 1.3), new DVec3(0, 5, 0), 1e-12),
    "rotate: a point on the axis stays put");
double w = 2 * Math.PI / 21549.425; // Kerbin's sidereal day
var spin = spinAxis * w;
var cNow = new DVec3(10, 20, 30);
var cLater = new DVec3(5000, -300, 12000);
var relNow = CommandCameraMath.SurfacePosition(origin, ix, iy, iz, R, -0.0972, -74.5577, 70);
var later = CommandCameraMath.CarriedByBody(relNow, cLater, spin, 3600);
Check(Near((later - cLater).Magnitude, R + 70, 1e-6), "carry: stays on the sphere about the moved centre");
Check(Near(CommandCameraMath.AngleBetween(CommandCameraMath.CarriedByBody(r0, cLater, spin, 3600) - cLater, r0), w * 3600, 1e-9),
    "carry: an equator point turns by w dt");
Check(VNear(CommandCameraMath.ToBodyFrameNow(later, cLater, cNow, spin, 3600), cNow + relNow, 1e-6),
    "rotate-back: a carried surface point maps to where it stands now");
var free = new DVec3(123456, -654321, 987654);
var back = CommandCameraMath.ToBodyFrameNow(free, cLater, cNow, spin, 777);
Check(VNear(CommandCameraMath.CarriedByBody(back - cNow, cLater, spin, 777), free, 1e-6),
    "rotate-back: round-trips through carry for any point");
Check(VNear(CommandCameraMath.CarriedByBody(relNow, cLater, new DVec3(0, 0, 0), 3600), cLater + relNow, 1e-9),
    "carry: a body that does not spin only moves its centre");
// Longitude drifts eastward by w dt: a fixed world point is seen further west later.
var east = CommandCameraMath.CarriedByBody(r0, origin, spin, 1);
Check(east.Z > 0, "carry: the spin moves lon 0 towards lon 90 (east, Unity +Z)");

// Angles in pixels.
Check(Near(CommandCameraMath.AngleBetween(new DVec3(1, 0, 0), new DVec3(0, 2, 0)), Math.PI / 2, 1e-12), "angle: right angle");
Check(Near(CommandCameraMath.AngleBetween(new DVec3(1, 0, 0), new DVec3(1, 1e-9, 0)), 1e-9, 1e-15), "angle: tiny angle exact");
Check(Near(CommandCameraMath.AngleInPixels(0.1 * Math.PI / 180, 1.0, 1000), 100, 1e-9), "pixels: 0.1 degrees in a 1 degree, 1000-row frame is 100 px");

// Edge finding: a step at t = 3.3337 between samples is bisected to the tolerance.
var edges = new List<KeyValuePair<double, bool>>();
int evals = 0;
CommandCameraMath.FindEdges(0, 4, 64, 10, 1e-3, t => { evals++; return t >= 3.3337; }, (a, b) => a == b, edges);
Check(edges.Count == 1 && edges[0].Value && edges[0].Key >= 3.3337 && edges[0].Key - 3.3337 < 1e-3,
    $"edges: one step found within 1 ms (got {(edges.Count > 0 ? edges[0].Key : double.NaN):F6})");
Check(evals <= 65 + 10, $"edges: bounded evaluations ({evals})");
CommandCameraMath.FindEdges(0, 3600, 64, 10, 1e-3, t => t >= 1000.123, (a, b) => a == b, edges);
Check(edges.Count == 1 && edges[0].Key >= 1000.123 && edges[0].Key - 1000.123 <= 3600.0 / 64 / 1024 + 1e-9,
    "edges: wide horizon stops after 10 halvings");
CommandCameraMath.FindEdges(0, 4, 64, 10, 1e-3, t => t >= 1 && t < 3, (a, b) => a == b, edges);
Check(edges.Count == 2 && edges[0].Value && !edges[1].Value && Near(edges[0].Key, 1, 1e-3) && Near(edges[1].Key, 3, 1e-3),
    "edges: on and off both found, in order");
CommandCameraMath.FindEdges(0, 4, 64, 10, 1e-3, t => false, (a, b) => a == b, edges);
Check(edges.Count == 0, "edges: no change, no edges");
CommandCameraMath.FindEdges(5, 5, 64, 10, 1e-3, t => t > 0, (a, b) => a == b, edges);
Check(edges.Count == 0, "edges: empty span, no edges");

// Predicted events appended after the recorded ones.
var history = new List<CommandTrackEvent> { new CommandTrackEvent(100, false, "", true, "Hold") };
var forecast = new List<KeyValuePair<double, CommandTrackEvent>>
{
    new KeyValuePair<double, CommandTrackEvent>(99.5, new CommandTrackEvent(99.5, true, "Mun", true, null)),
    new KeyValuePair<double, CommandTrackEvent>(101, new CommandTrackEvent(101, false, "", true, null)),
    new KeyValuePair<double, CommandTrackEvent>(102, new CommandTrackEvent(102, true, "Mun", false, null)),
};
CommandTrackEvent.AppendPredicted(history, forecast, 100, "Hold");
Check(history.Count == 2 && history[1].Ut == 102 && history[1].Predicted && history[1].Occluder == "Mun" && !history[1].Sunlit
    && history[1].Blank == "Hold" && !history[0].Predicted,
    "predicted: past and repeated states dropped, blank carried, flag set only on forecasts");
Check(history[1].SameStateAs(new CommandTrackEvent(0, true, "Mun", false, "Hold")), "predicted: the flag is not part of the state");

// Debug UT stripe payload.
ulong v = CommandCameraMath.UtStripeValue(637599435.8216);
Check(v == 637599435822UL, "stripe: UT rounds to whole milliseconds");
ulong decoded = 0;
for (int i = 0; i < CommandCameraMath.UtStripeBits; i++) decoded = (decoded << 1) | (CommandCameraMath.UtStripeBit(v, i) ? 1UL : 0UL);
Check(decoded == v, "stripe: bits read MSB first rebuild the value");
Check(CommandCameraMath.UtStripeBit(1UL << 47, 0) && !CommandCameraMath.UtStripeBit(1UL << 47, 47) && CommandCameraMath.UtStripeBit(1, 47),
    "stripe: block 0 is the top bit, block 47 the bottom");
Check(CommandCameraMath.UtStripeValue(-5) == 0 && CommandCameraMath.UtStripeValue(1e300) == (1UL << 48) - 1, "stripe: clamped to 48 bits");

/* Unit icosphere: 20 * 4^level triangles. */
(List<DVec3>, List<int>) Icosphere(int level, double radius)
{
    double g = (1 + Math.Sqrt(5)) / 2;
    var vs = new List<DVec3>
    {
        new DVec3(-1, g, 0), new DVec3(1, g, 0), new DVec3(-1, -g, 0), new DVec3(1, -g, 0),
        new DVec3(0, -1, g), new DVec3(0, 1, g), new DVec3(0, -1, -g), new DVec3(0, 1, -g),
        new DVec3(g, 0, -1), new DVec3(g, 0, 1), new DVec3(-g, 0, -1), new DVec3(-g, 0, 1),
    };
    for (int i = 0; i < vs.Count; i++) vs[i] = vs[i].Normalized * radius;
    var ts = new List<int> { 0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
        3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1 };
    for (int l = 0; l < level; l++)
    {
        var cache = new Dictionary<long, int>();
        int Mid(int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (cache.TryGetValue(key, out int m)) return m;
            vs.Add(((vs[a] + vs[b]) * 0.5).Normalized * radius);
            cache[key] = vs.Count - 1;
            return vs.Count - 1;
        }
        var next = new List<int>();
        for (int t = 0; t < ts.Count; t += 3)
        {
            int a = ts[t], b = ts[t + 1], c = ts[t + 2];
            int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
            next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
        }
        ts = next;
    }
    return (vs, ts);
}

// Body-local frame: a point fixed to a spinning body reads the same at any dt.
{
    var axX = new DVec3(0, 0, 1);
    var axY = new DVec3(1, 0, 0);
    var axZ = new DVec3(0, 1, 0);
    var fixedRel = new DVec3(120000, -5000, 33000);
    var localNow = CommandCameraMath.ToBodyLocal(cNow + fixedRel, cNow, spin, 0, axX, axY, axZ);
    Check(VNear(localNow, new DVec3(33000, 120000, -5000), 1e-9), "body-local: reads along the given axes");
    var carried = CommandCameraMath.CarriedByBody(fixedRel, cLater, spin, 5000);
    Check(VNear(CommandCameraMath.ToBodyLocal(carried, cLater, spin, 5000, axX, axY, axZ), localNow, 1e-6),
        "body-local: a carried point keeps its local coordinates");
    Check(Near(CommandCameraMath.DistanceToSegment(new DVec3(0, 5, 0), new DVec3(-1, 0, 0), new DVec3(1, 0, 0)), 5, 1e-12),
        "segment distance: perpendicular foot");
    Check(Near(CommandCameraMath.DistanceToSegment(new DVec3(4, 3, 0), new DVec3(-1, 0, 0), new DVec3(1, 0, 0)), Math.Sqrt(18), 1e-12),
        "segment distance: past the end");
    Check(!CommandCameraMath.MayReach(1000, 1000, 10, 4, 900), "reach: 40 m of motion cannot close a 100 m gap");
    Check(CommandCameraMath.MayReach(1000, 1000, 60, 4, 900), "reach: 240 m of motion can close it midway");
    Check(!CommandCameraMath.MayReach(1000, 1200, 100, 4, 850) && 1000 - 100 * 4 < 850, "reach: the far end bounds it too");
}

// Horizon mask on a bare sphere: the skyline is the geometric horizon.
{
    double seaR = 600000, stationAlt = 70, rMax = 606800;
    double rs = seaR + stationAlt;
    var axis = new DVec3(0, 1, 0);
    var relS = new DVec3(rs, 0, 0);
    int terrainCalls = 0;
    var bare = new HorizonMask(rs, seaR, rMax, 0.25, dir => { terrainCalls++; return seaR; });
    double limit = HorizonMask.ArcLimit(rs, seaR, rMax);
    Check(Near(limit, Math.Acos(seaR / rs) + Math.Acos(seaR / rMax), 1e-15), "mask: arc limit is touch plus leave");
    Check(limit * seaR > 95000 && limit * seaR < 105000, $"mask: Kerbin's limit from 70 m is about 100 km (got {limit * seaR:F0} m)");
    Check(bare.SampleCount > 100 && bare.SampleCount < 400, $"mask: a few hundred samples per bin ({bare.SampleCount})");
    double dip = -Math.Acos(seaR / rs);
    HorizonMask.Basis(relS, axis, out var mu, out var mn, out var me);
    var rng = new Random(7);
    int agree = 0, total = 0;
    for (int i = 0; i < 400; i++)
    {
        double az = rng.NextDouble() * 2 * Math.PI;
        double el = dip + (rng.NextDouble() - 0.5) * 0.02;
        double range = 1000 + rng.NextDouble() * 4e6;
        var dir = mu * Math.Sin(el) + (mn * Math.Cos(az) + me * Math.Sin(az)) * Math.Cos(el);
        var target = relS + dir * range;
        bool sphere = !CommandCameraMath.SegmentClearOfSphere(relS, target, new DVec3(0, 0, 0), seaR);
        double margin = Math.Abs(el - dip);
        if (margin < 2e-4) continue;
        total++;
        if (bare.Blocks(relS, target, axis) == sphere) agree++;
    }
    Check(agree == total && total > 300, $"mask: bare sphere agrees with the sphere test away from the edge ({agree}/{total})");
    Check(!bare.Blocks(relS, relS + mu * 5000, axis), "mask: straight up is clear");
    Check(bare.Blocks(relS, new DVec3(-rs, 0, 0), axis), "mask: the far side is hidden");
    int built = bare.BinsBuilt;
    bare.Blocks(relS, relS + mn * 100000 + mu * -2000, axis);
    Check(bare.BinsBuilt == built, "mask: a bin is built once and kept");
    Check(terrainCalls == bare.BinsBuilt * bare.SampleCount, "mask: terrain read once per sample per bin");

    // A 1 km ridge 20 km north hides what lies just above the horizon behind it, not what stands in front.
    var ridgeDir = (mu * Math.Cos(20000 / seaR) + mn * Math.Sin(20000 / seaR)).Normalized;
    var ridged = new HorizonMask(rs, seaR, rMax, 0.25, d =>
        CommandCameraMath.AngleBetween(d, ridgeDir) < 1500 / seaR ? seaR + 1000 : seaR);
    double ridgeEl = HorizonMask.ElevationOf(rs, seaR + 1000, 20000 / seaR);
    var behind = relS + (mu * Math.Sin(ridgeEl - 0.005) + mn * Math.Cos(ridgeEl - 0.005)) * 2e6;
    var above = relS + (mu * Math.Sin(ridgeEl + 0.005) + mn * Math.Cos(ridgeEl + 0.005)) * 2e6;
    var before = relS + (mu * Math.Sin(ridgeEl - 0.005) + mn * Math.Cos(ridgeEl - 0.005)) * 10000;
    Check(ridged.Blocks(relS, behind, axis), "mask: the ridge hides a target just below its crest");
    Check(!ridged.Blocks(relS, above, axis), "mask: a target over the crest is clear");
    Check(!ridged.Blocks(relS, before, axis), "mask: a target in front of the ridge is clear");
    var eastward = relS + (mu * Math.Sin(ridgeEl - 0.005) + me * Math.Cos(ridgeEl - 0.005)) * 2e6;
    Check(!ridged.Blocks(relS, eastward, axis), "mask: the ridge only hides its own azimuth");

    // A craft whose centre sits just under the crest still shows its top over it.
    double skyline = ridged.MaskAt(0, 30000 / seaR, mu, mn, me);
    var underCrest = relS + (mu * Math.Sin(skyline - 1e-5) + mn * Math.Cos(skyline - 1e-5)) * 30000;
    Check(ridged.Blocks(relS, underCrest, axis), "mask: a centre just under the crest is hidden as a point");
    Check(!ridged.Blocks(relS, underCrest, axis, 3.0), "mask: a 3 m craft with its centre just under the crest is not hidden");
    Check(ridged.Blocks(relS, behind, axis, 3.0), "mask: a craft wholly under the crest is still hidden");

    // Interpolation between bins, and the budget fallback.
    Check(HorizonMask.CountBelow(new[] { 1.0, 2.0, 3.0 }, 2.0) == 1 && HorizonMask.CountBelow(new[] { 1.0, 2.0, 3.0 }, 9) == 3
        && HorizonMask.CountBelow(new[] { 1.0, 2.0, 3.0 }, 0.5) == 0, "mask: strictly-closer sample count");
    var tilted = new HorizonMask(rs, seaR, rMax, 1.0, d => seaR + 2000 * Math.Max(0, DVec3.Dot(d, me)) * 100);
    double m0 = tilted.MaskAt(0, 1, mu, mn, me);
    double m1 = tilted.MaskAt(1 * Math.PI / 180, 1, mu, mn, me);
    double mh = tilted.MaskAt(0.5 * Math.PI / 180, 1, mu, mn, me);
    Check(Near(mh, 0.5 * (m0 + m1), 1e-12) && m1 > m0, "mask: halfway between bins is the mean of the two");
    var budgeted = new HorizonMask(rs, seaR, rMax, 0.25, d => seaR + 5000) { Budget = 0 };
    double fb = budgeted.MaskAt(1.0, 1, mu, mn, me);
    Check(budgeted.BinsBuilt == 0 && budgeted.Fallbacks == 2 && Near(fb, dip, 1e-5),
        $"mask: out of budget, the sea-level skyline stands in and nothing is kept (got {fb:G6}, dip {dip:G6})");
}

// Scaled mesh occluder on an icosphere.
{
    var (verts, tris) = Icosphere(4, 1.0);
    double meshR = 200000;
    for (int i = 0; i < verts.Count; i++) verts[i] = verts[i] * meshR;
    var occ = new ScaledMeshOccluder(verts, tris);
    Check(occ.TriangleCount == 20 * 256, $"mesh: icosphere level 4 has 5120 triangles ({occ.TriangleCount})");
    Check(Near(occ.RMax, meshR, 1e-6) && occ.RInner < meshR && occ.RInner > meshR * 0.995, $"mesh: inner {occ.RInner:F0} and outer radii");
    var far = new DVec3(1.15e7, 0, 0);
    Check(!occ.Blocks(new DVec3(1.15e7, meshR * 1.001, 0), new DVec3(-1e6, meshR * 1.001, 0), 0), "mesh: a line just over the top is clear");
    Check(occ.Blocks(far, new DVec3(-1e5, occ.RInner * 0.999, 0), 0), "mesh: a line through the inner sphere is blocked");
    Check(occ.Blocks(far, new DVec3(-3e5, 0, 0), 0), "mesh: straight through the middle is blocked");
    Check(!occ.Blocks(far, new DVec3(3e5, 0, 0), 0), "mesh: short of the body is clear");
    var rnd = new Random(11);
    int same = 0, shell = 0, n = 3000;
    for (int i = 0; i < n; i++)
    {
        double y = occ.RInner + rnd.NextDouble() * (occ.RMax - occ.RInner) * 1.02;
        double ang = rnd.NextDouble() * 2 * Math.PI;
        var offset = new DVec3(0, Math.Cos(ang) * y, Math.Sin(ang) * y);
        var a = far + offset;
        var b = new DVec3(-2e5 - rnd.NextDouble() * 1e6, 0, 0) + offset;
        bool bvh = occ.Blocks(a, b, 0), brute = occ.BlocksBruteForce(a, b);
        if (bvh == brute) same++;
        if (bvh) shell++;
    }
    Check(same == n, $"mesh: the hierarchy matches every triangle on grazing lines ({same}/{n})");
    Check(shell > n / 10 && shell < n * 9 / 10, $"mesh: the grazing sample mixes hits and misses ({shell}/{n})");

    // A target on the surface, under the mesh by a little, is seen from overhead with slack.
    var underMesh = new DVec3(0, meshR * 0.998, 0);
    var overhead = new DVec3(0, 1e7, 0);
    Check(occ.Blocks(overhead, underMesh, 0), "mesh: a target just under the mesh reads as blocked without slack");
    Check(!occ.Blocks(overhead, underMesh, 2 * (occ.RMax - occ.RMin) + 1000), "mesh: slack lets it through from overhead");
    Check(occ.Blocks(new DVec3(0, -1e7, 0), underMesh, 2 * (occ.RMax - occ.RMin) + 1000), "mesh: slack never sees through the body");

    // Axis-parallel segments (zero direction components).
    Check(occ.Blocks(new DVec3(0, 0, 1e7), new DVec3(0, 0, -1e7), 0), "mesh: an axis-aligned line through the centre is blocked");
    Check(!occ.Blocks(new DVec3(0, meshR * 1.01, 1e7), new DVec3(0, meshR * 1.01, -1e7), 0), "mesh: an axis-aligned line over the top is clear");
}

/* The live bound: tau, or the lens's own farthest part, at the holder's light speed. */
{
    double tau = CommandCameraMath.BoundaryLightTimeS;
    Check(CommandCameraMath.LiveLightTimeS(null, 100) == 0, "live bound: no light speed, delay off, 0");
    Check(CommandCameraMath.LiveLightTimeS(0, 100) == 0, "live bound: a zero light speed is no light speed");
    Check(CommandCameraMath.LiveLightTimeS(299792458, 30) == tau, "live bound: at light speed a 30 m craft is inside tau");
    Check(CommandCameraMath.LiveLightTimeS(299792458, 0) == tau, "live bound: a lens with no craft of its own is tau");
    Check(Math.Abs(CommandCameraMath.LiveLightTimeS(3, 30) - 10) < 1e-12, "live bound: at 3 m/s a part 30 m away is 10 s");
    Check(Math.Abs(CommandCameraMath.BoundaryM(3) - 0.05) < 1e-12 && double.IsNaN(CommandCameraMath.BoundaryM(null)),
        "boundary: c times tau, NaN with no light speed");

    /* A partner docks: the farthest own part jumps from 30 m to 100 m, so at 3 m/s the bound steps by 23 s on that frame. */
    var lens = new DVec3(1000, 0, 0);
    var own = new List<DVec3> { lens + new DVec3(0, 10, 0), lens + new DVec3(0, 28, 0) };
    var radii = new List<double> { 1, 2 };
    double before = CommandCameraMath.LiveLightTimeS(3, CommandCameraMath.FarthestPartM(lens, own, radii));
    own.Add(lens + new DVec3(0, -98, 0));
    radii.Add(2);
    double after = CommandCameraMath.LiveLightTimeS(3, CommandCameraMath.FarthestPartM(lens, own, radii));
    Check(Math.Abs(before - 10) < 1e-9, "dock: before, the farthest reach 28 + 2 m is 10 s");
    Check(Math.Abs(after - before - 70.0 / 3) < 1e-9, "dock: the partner's far part steps the bound by 23.3 s");
    own.RemoveAt(2);
    radii.RemoveAt(2);
    Check(CommandCameraMath.LiveLightTimeS(3, CommandCameraMath.FarthestPartM(lens, own, radii)) == before, "undock: the bound steps back down");
    Check(CommandCameraMath.FarthestPartM(lens, new List<DVec3>(), null) == 0, "no parts reach nowhere");
}

/* Part reach only grows: a canopy that opens raises it at once, and nothing lowers it. */
{
    var cache = new ReachCache();
    Func<double, double> same = v => v;
    Check(cache.Reach(1, 0, false, same, 2.0) == 2.0, "reach: first sight measures");
    Check(cache.Reach(1, 1, false, same, 25.0) == 2.0, "reach: a still part is not measured again before its refresh");
    Check(cache.Reach(1, 1, true, same, 25.0) == 25.0, "reach: a moving part is measured on the frame, and the canopy counts");
    Check(cache.Reach(1, 2, true, same, 3.0) == 25.0, "reach: a packed canopy never lowers it");
    Check(cache.Reach(1, 2 + ReachCache.RefreshFrames, false, same, 1.0) == 25.0, "reach: a refresh keeps the larger");
    Check(cache.Reach(1, 3 + 2 * ReachCache.RefreshFrames, false, same, 40.0) == 40.0, "reach: a still part's refresh can raise it");
    int calls = 0;
    Func<double, double> counted = v => { calls++; return v; };
    cache.Reach(1, 5000, true, counted, 1.0);
    cache.Reach(1, 5000, true, counted, 1.0);
    Check(calls == 1, "reach: measured once a frame however many cameras ask");
    Check(cache.Reach(2, 0, false, same, double.NaN) == 0, "reach: nothing drawn yet reads as no reach");
    Check(cache.Reach(2, 1, false, same, 6.0) == 6.0, "reach: and is measured again on the next frame, once it shows");
}

// AimSlew: the view follows the aim exactly between jumps, and turns out a jump eased and rate-limited.
{
    const double Dt = 1.0 / 60.0, D2R = Math.PI / 180.0, Fov = 1.0;
    DVec3 Dir(double deg) => new DVec3(Math.Cos(deg * D2R), Math.Sin(deg * D2R), 0);
    double Deg(DVec3 a, DVec3 b) => CommandCameraMath.AngleBetween(a, b) / D2R;

    /* Fast aims from a fresh start, at 60 and 30 fps: no lag at all, and never a slew. */
    foreach (var (aimRate, dt) in new[] { (8.0, 1.0 / 60), (20.0, 1.0 / 30), (200.0, 1.0 / 30) })
    {
        var fresh = new AimSlew();
        double lag = 0;
        bool slewed = false;
        for (int i = 0; i < 300; i++)
        {
            var aim = Dir(aimRate * i * dt);
            lag = Math.Max(lag, Deg(fresh.Step(aim, dt, dt, false, Fov), aim));
            slewed |= fresh.Slewing;
        }
        Check(lag < 1e-9 && !slewed, $"slew: a {aimRate} deg/s aim at {1 / dt:F0} fps from a fresh start is followed with no lag ({lag:G2} deg)");
    }

    /* Publishes that agree with the motion, once a second at 60 and 30 fps: no offset, no slew, no lag. */
    foreach (var (aimRate, dt, fov) in new[] { (8.0, 1.0 / 60, 1.0), (20.0, 1.0 / 30, 5.0) })
    {
        var held = new AimSlew();
        double lag = 0;
        bool slewed = false;
        int perSecond = (int)Math.Round(1 / dt);
        for (int i = 0; i < 10 * perSecond; i++)
        {
            var aim = Dir(aimRate * i * dt);
            lag = Math.Max(lag, Deg(held.Step(aim, dt, dt, i % perSecond == 0, fov), aim));
            slewed |= held.Slewing;
        }
        Check(lag < 1e-6 && !slewed, $"slew: {aimRate} deg/s with 1 Hz publishes that agree stays on the aim with no slew ({lag:G2} deg)");
    }

    /* A world model publishing on every solve: no smooth step at all. Steady and accelerating aims stay on. */
    foreach (var (name, at) in new (string, Func<double, double>)[] { ("8 deg/s", t => 8 * t), ("accelerating at 8 deg/s^2", t => 4 * t * t) })
    {
        var every = new AimSlew();
        double lag = 0;
        for (int i = 0; i < 600; i++)
        {
            var aim = Dir(at(i * Dt));
            lag = Math.Max(lag, Deg(every.Step(aim, Dt, Dt, true, 1.0), aim));
        }
        Check(lag < 0.02, $"slew: {name} with a publish every frame stays within 2% of a 1 degree field ({lag:G2} deg)");
    }

    /* A 30 degree jump. */
    var slew = new AimSlew();
    slew.Step(Dir(0), Dt, Dt, false, Fov);
    var first = slew.Step(Dir(30), Dt, Dt, true, Fov);
    double firstStep = Deg(first, Dir(0));
    Check(firstStep <= AimSlew.AccelDegPerS2 * Dt * Dt + 1e-9, $"slew: a jump starts from where the view was, easing in ({firstStep:G3} deg in the first frame)");
    var last = first;
    double maxRate = 0, prevOff = Deg(first, Dir(30)), lastStep = double.NaN;
    bool monotonic = true;
    int frames = 1;
    while (slew.Slewing && frames < 2000)
    {
        var view = slew.Step(Dir(30), Dt, Dt, false, Fov);
        double step = Deg(view, last);
        maxRate = Math.Max(maxRate, step / Dt);
        double off = Deg(view, Dir(30));
        monotonic &= off <= prevOff + 1e-12;
        prevOff = off;
        if (slew.Slewing) lastStep = step;
        last = view;
        frames++;
    }
    double seconds = frames * Dt;
    Check(!slew.Slewing && Deg(last, Dir(30)) < 1e-9, $"slew: a jump ends exactly on the aim ({seconds:F2} s)");
    Check(maxRate <= AimSlew.MaxRateDegPerS + 1e-6, $"slew: never faster than the cap ({maxRate:F1} deg/s)");
    Check(monotonic, "slew: closes on the aim without overshooting");
    Check(lastStep < AimSlew.MaxRateDegPerS * Dt * 0.5, $"slew: eases out onto the aim ({lastStep:G3} deg in the last moving frame)");
    double ideal = 30.0 / AimSlew.MaxRateDegPerS + AimSlew.MaxRateDegPerS / AimSlew.AccelDegPerS2;
    Check(seconds < ideal + 0.2, $"slew: takes about the time its profile allows ({seconds:F2} s against {ideal:F2} s)");

    /* From a fast aim to a slower one elsewhere: the jump is turned out, then the new aim is followed exactly. */
    slew = new AimSlew();
    int k = 0;
    for (; k < 120; k++) slew.Step(Dir(20.0 * k * Dt), Dt, Dt, false, Fov);
    DVec3 Slow(int i) => Dir(90 + 2.0 * i * Dt);
    slew.Step(Slow(k), Dt, Dt, true, Fov);
    bool slewedOnto = slew.Slewing;
    for (k++; k < 2000 && slew.Slewing; k++) slew.Step(Slow(k), Dt, Dt, false, Fov);
    double after = 0;
    for (int j = 0; j < 120; j++, k++) after = Math.Max(after, Deg(slew.Step(Slow(k), Dt, Dt, false, Fov), Slow(k)));
    Check(slewedOnto && !slew.Slewing && after < 1e-9, $"slew: a jump from a fast aim to a slower one turns, settles, then follows it ({after:G2} deg)");

    /* Too long idle snaps; a half turn completes; no jump, no slew. */
    slew = new AimSlew();
    slew.Step(Dir(0), Dt, Dt, false, Fov);
    Check(Deg(slew.Step(Dir(90), AimSlew.SnapAfterS + 0.1, AimSlew.SnapAfterS + 0.1, true, Fov), Dir(90)) == 0 && !slew.Slewing, "slew: a view idle past SnapAfterS starts on its aim");
    slew.Step(Dir(270), Dt, Dt, true, Fov);
    for (int j = 0; j < 2000 && slew.Slewing; j++) slew.Step(Dir(270), Dt, Dt, false, Fov);
    Check(Deg(slew.Step(Dir(270), Dt, Dt, false, Fov), Dir(270)) < 1e-9, "slew: a half turn completes");
}

/*
 * As the rig drives it: UT moves only on a 50 Hz physics tick, frames render
 * at 60 or 144 fps, so some frames see no tick and some see two, and a world
 * model publishes every few ticks. A narrow field on a subject crossing it
 * at 2 deg/s shows any jump at a publish; the view stays on the aim.
 */
{
    const double Tick = 0.02, D2R = Math.PI / 180.0, Fov = 0.1, AimRate = 2.0;
    DVec3 Dir(double deg) => new DVec3(Math.Cos(deg * D2R), Math.Sin(deg * D2R), 0);
    double Deg(DVec3 a, DVec3 b) => CommandCameraMath.AngleBetween(a, b) / D2R;

    /*
     * The worst the view strays from the aim over 10 s, as a fraction of the
     * field. `pausedFrom` stops UT for a second there, with a publish halfway
     * through the pause and another on the first tick after it.
     */
    double Worst(double fps, int ticksPerPublish, double pausedFrom = double.NaN)
    {
        var slew = new AimSlew();
        double dt = 1.0 / fps, real = 0, ut = 0, lastUt = 0, worst = 0, paused = 0;
        int ticks = 0, published = 0;
        bool awaitTick = false;
        for (int i = 0; i < 10 * fps; i++)
        {
            real += dt;
            bool forced = false;
            if (real >= pausedFrom && paused < 1.0)
            {
                forced = paused < 0.5 && paused + dt >= 0.5;
                paused += dt;
                awaitTick = true;
            }
            else
            {
                int before = ticks;
                while ((ticks + 1) * Tick <= real - paused) ticks++;
                ut = ticks * Tick;
                forced = awaitTick && ticks > before;
                awaitTick &= !forced;
            }
            int publish = ticks / ticksPerPublish;
            var aim = Dir(AimRate * ut);
            var view = slew.Step(aim, i == 0 ? double.MaxValue : dt, ut - lastUt, i == 0 || forced || publish != published, Fov);
            published = publish;
            lastUt = ut;
            if (i > 0) worst = Math.Max(worst, Deg(view, aim) / Fov);
        }
        return worst;
    }

    foreach (double fps in new[] { 60.0, 144.0 })
        foreach (int every in new[] { 1, 5, 12, 25 })
        {
            double worst = Worst(fps, every);
            Check(worst < 0.01, $"slew on ticks: {fps:F0} fps, 50 Hz physics, a publish every {every} ticks stays within 1% of the field ({worst:P2})");
        }
    foreach (double fps in new[] { 60.0, 144.0 })
    {
        double worst = Worst(fps, 12, 4.0);
        Check(worst < 0.01, $"slew on ticks: {fps:F0} fps through a 1 s pause, publishing in it and on the tick after, stays within 1% of the field ({worst:P2})");
    }

    /* UT stepping back (a revert, a quickload) while the aim moves on teaches no reversed rate: the publish on the next tick is carried as before. */
    {
        var slew = new AimSlew();
        double ut = 0, worst = 0;
        slew.Step(Dir(0), double.MaxValue, 0, true, Fov);
        for (int i = 0; i < 50; i++)
        {
            ut += Tick;
            slew.Step(Dir(AimRate * ut), Tick, Tick, false, Fov);
        }
        double deg = AimRate * (ut + Tick);
        slew.Step(Dir(deg), Tick, -Tick, false, Fov);
        for (int i = 0; i < 10; i++)
        {
            deg += AimRate * Tick;
            worst = Math.Max(worst, Deg(slew.Step(Dir(deg), Tick, Tick, i == 0, Fov), Dir(deg)) / Fov);
        }
        Check(worst < 0.01, $"slew on ticks: a step back in UT is not learned from, so the next publish stays within 1% of the field ({worst:P2})");
    }
}

/* The placeholder a camera holds with no subject to aim at. */
{
    Check(CommandCameraMath.PlaceholderFor(false, true) == PlaceholderAim.LaunchSite, "placeholder: a launch site lens looks at the site");
    Check(CommandCameraMath.PlaceholderFor(false, false) == PlaceholderAim.Horizon, "placeholder: a station or surface lens looks at the horizon");
    Check(CommandCameraMath.PlaceholderFor(true, false) == PlaceholderAim.MountForward, "placeholder: a riding lens looks along its craft");

    var wait = new SubjectWait();
    Check(wait.Step(false, false) == SubjectAimMode.Placeholder && wait.Step(false, false) == SubjectAimMode.Placeholder,
        "wait: a subject never positioned waits at the placeholder");
    Check(wait.Step(false, true) == SubjectAimMode.Subject, "wait: once positioned the view follows the subject");
    Check(wait.Step(false, false) == SubjectAimMode.Hold && wait.Step(false, false) == SubjectAimMode.Hold,
        "wait: a subject lost after that holds the last aim, never the placeholder");
    Check(wait.Step(false, true) == SubjectAimMode.Subject && wait.Step(false, false) == SubjectAimMode.Hold
        && wait.Step(false, true) == SubjectAimMode.Subject, "wait: a flickering fix holds between fixes");
    Check(wait.Step(true, false) == SubjectAimMode.Placeholder && wait.Step(false, false) == SubjectAimMode.Placeholder,
        "wait: a changed subject, aim or vantage waits again until positioned");
    Check(wait.Step(true, true) == SubjectAimMode.Subject, "wait: a change to a positioned subject follows it at once");

    /* Toward the site, from the default Launchpad vantage, the field takes in the pad and tower at about 9 degrees. */
    double fov = CommandCameraMath.FovForFraction(CommandCameraMath.PlaceholderSiteRadiusM, 500.0,
        CommandCameraMath.PlaceholderSiteFraction, 0.02, 30);
    Check(Math.Abs(fov - 9.0) < 0.5, $"placeholder: a launch site is framed at about 9 degrees from 500 m ({fov:F2} deg)");
}

/* FrameStep: a hitch advances an ease by a usual frame; a lasting slowdown becomes the usual frame; a gap past snapAfter is the ease's own. */
{
    var steps = new FrameStep(1.0);
    for (int i = 0; i < 50; i++) steps.Next(1.0 / 24);
    Check(Math.Abs(steps.Next(0.3) - 1.0 / 24) < 1e-9, "frame step: a 0.3 s hitch at 24 fps advances one usual frame");
    Check(Math.Abs(steps.Next(1.0 / 24) - 1.0 / 24) < 1e-9, "frame step: the frame after a hitch advances as itself");
    double usual = steps.Clip(0.3);
    Check(usual < 0.06 && steps.Clip(0.9) == usual, $"frame step: a 0.9 s hitch, under snapAfter, still advances one usual frame ({usual:F3} s)");
    Check(steps.Next(1.5) == 1.5 && steps.Clip(0.3) == usual, "frame step: a gap past snapAfter passes through and is not learned");
    double last = 0;
    for (int i = 0; i < 60; i++) last = steps.Next(0.1);
    Check(Math.Abs(last - 0.1) < 1e-9, "frame step: a lasting drop to 10 fps is followed, not clipped");
    Check(new FrameStep(1.0).Next(0.2) == 0.2 && steps.Next(0) == 0, "frame step: a first frame and a zero gap pass through");

    var huge = new FrameStep(1.0);
    Check(huge.Next(double.MaxValue) == double.MaxValue && huge.Next(1e6) == 1e6, "frame step: huge first gaps pass through");
    huge.Next(1.0 / 24);
    Check(Math.Abs(huge.Next(0.3) - 1.0 / 24) < 1e-9, "frame step: and are not learned, so the next hitch is still caught");

    /* With the zoom: a hitch under snapAfter eases one usual frame, a gap past it snaps as before. */
    var zoom = new FrameStep(1.0);
    for (int i = 0; i < 50; i++) zoom.Next(1.0 / 24);
    double eased = CommandCameraMath.SmoothFov(10, 30, zoom.Next(0.6), 0.4, 1.0);
    double snapped = CommandCameraMath.SmoothFov(10, 30, zoom.Next(1.5), 0.4, 1.0);
    Check(eased > 10 && eased < 11.5 && snapped == 30, $"frame step: a 0.6 s hitch eases the zoom ({eased:F2} deg), a 1.5 s gap snaps it ({snapped:F0} deg)");

    /* The slew learns only where it is told: a shared step it only clips by. */
    var shared = new FrameStep(1.0);
    shared.Learn(0.1);
    var clipped = new AimSlew(shared);
    clipped.Step(new DVec3(1, 0, 0), double.MaxValue, double.MaxValue, false, 10);
    for (int i = 0; i < 40; i++) clipped.Step(new DVec3(1, 0, 0), i % 2 == 0 ? 0.095 : 0.005, i % 2 == 0 ? 0.095 : 0.005, false, 10);
    Check(Math.Abs(shared.Clip(0.15) - 0.15) < 1e-9, "frame step: a slew sharing the rendered frame's step does not learn polls between frames");
}

/*
 * The hand-off from a placeholder (or a held aim) to the subject, as the rig
 * drives it: SubjectWait picks what the view follows, and a change of that,
 * or of the subject, is a jump for the slew. The first fix arrives on a long
 * frame, as it did on the Deck (a 0.3 s hitch at 24 fps).
 */
{
    const double Dt = 1.0 / 24, Hitch = 0.3, D2R = Math.PI / 180.0, PlaceholderFov = 9.15, SubjectFov = 30.0;
    DVec3 At(double azDeg, double elDeg) => new DVec3(
        Math.Cos(elDeg * D2R) * Math.Cos(azDeg * D2R), Math.Cos(elDeg * D2R) * Math.Sin(azDeg * D2R), Math.Sin(elDeg * D2R));
    double Deg(DVec3 a, DVec3 b) => CommandCameraMath.AngleBetween(a, b) / D2R;
    double oneStep = AimSlew.AccelDegPerS2 * Dt * Dt;

    foreach (var (name, subjectAz, subjectEl) in new[] { ("the Deck's 0.56 degree drop", 0.0, 0.88), ("a 20 degree turn", 20.0, 5.0) })
    {
        var wait = new SubjectWait();
        var slew = new AimSlew();
        var fovStep = new FrameStep(1.0);
        var placeholder = At(0, 1.44);
        var subject = At(subjectAz, subjectEl);
        double fov = PlaceholderFov;
        var lastMode = SubjectAimMode.Placeholder;
        DVec3 view = default;
        for (int i = 0; i < 48; i++)
        {
            lastMode = wait.Step(i == 0, false);
            view = slew.Step(placeholder, i == 0 ? double.MaxValue : Dt, i == 0 ? double.MaxValue : Dt, i == 0, fov);
            fov = CommandCameraMath.SmoothFov(fov, PlaceholderFov, fovStep.Next(Dt), 0.4, 1.0);
        }
        var mode = wait.Step(false, true);
        bool jumped = mode != lastMode;
        var first = slew.Step(subject, Hitch, Hitch, jumped, fov);
        double firstFov = CommandCameraMath.SmoothFov(fov, SubjectFov, fovStep.Next(Hitch), 0.4, 1.0);
        double usualZoom = Math.Log(CommandCameraMath.SmoothFov(fov, SubjectFov, Dt, 0.4, 1.0) / fov);
        Check(mode == SubjectAimMode.Subject && jumped, $"hand-off ({name}): the first fix is a jump for the slew");
        Check(Deg(first, view) <= oneStep + 1e-9 && slew.Slewing,
            $"hand-off ({name}): the frame of the first fix stays within one slew step of the placeholder ({Deg(first, view):G3} deg, step {oneStep:G3}) and a slew starts");
        Check(firstFov > fov && Math.Log(firstFov / fov) <= usualZoom + 1e-9, $"hand-off ({name}): the zoom moves, one usual frame, on the hitch ({fov:F2} -> {firstFov:F2} deg)");

        double maxStep = 0, prevOff = Deg(first, subject);
        bool closes = true;
        var last = first;
        int frames = 0;
        for (; frames < 2000 && slew.Slewing; frames++)
        {
            var next = slew.Step(subject, Dt, Dt, false, fov);
            maxStep = Math.Max(maxStep, Deg(next, last));
            double off = Deg(next, subject);
            closes &= off <= prevOff + 1e-12;
            prevOff = off;
            last = next;
        }
        Check(!slew.Slewing && Deg(last, subject) < 1e-9 && closes && frames >= 2 && maxStep <= AimSlew.MaxRateDegPerS * Dt + 1e-9,
            $"hand-off ({name}): turns onto the subject over {frames + 1} frames, never faster than the cap");
    }

    /* Onto a subject moving at 3 deg/s, on a hitch: the turn closes, then the view follows it with no lag, a hitch while following included. */
    {
        var wait = new SubjectWait();
        var slew = new AimSlew();
        var placeholder = At(0, 1.44);
        DVec3 Moving(double t) => At(5 + 3 * t, 2);
        var lastMode = SubjectAimMode.Placeholder;
        for (int i = 0; i < 48; i++)
        {
            lastMode = wait.Step(i == 0, false);
            slew.Step(placeholder, i == 0 ? double.MaxValue : Dt, i == 0 ? double.MaxValue : Dt, i == 0, PlaceholderFov);
        }
        double t = 0;
        var mode = wait.Step(false, true);
        slew.Step(Moving(t += Hitch), Hitch, Hitch, mode != lastMode, PlaceholderFov);
        bool slewed = slew.Slewing;
        for (int i = 0; i < 2000 && slew.Slewing; i++) slew.Step(Moving(t += Dt), Dt, Dt, false, PlaceholderFov);
        double lag = 0;
        for (int i = 0; i < 48; i++)
        {
            double dt = i == 24 ? Hitch : Dt;
            var aim = Moving(t += dt);
            lag = Math.Max(lag, Deg(slew.Step(aim, dt, dt, i % 6 == 0, PlaceholderFov), aim));
        }
        Check(slewed && !slew.Slewing && lag < 1e-6, $"hand-off: onto a moving subject on a hitch, it turns, then follows with no lag ({lag:G2} deg)");
    }

    /* A held view (subject lost) keeps its aim; a new subject from there slews too. */
    {
        var wait = new SubjectWait();
        var slew = new AimSlew();
        var held = At(10, 2);
        var mode = wait.Step(true, true);
        slew.Step(held, double.MaxValue, double.MaxValue, true, SubjectFov);
        for (int i = 0; i < 24; i++) slew.Step(held, Dt, Dt, false, SubjectFov);
        var lastMode = mode;
        mode = wait.Step(false, false);
        var view = slew.Step(held, Dt, Dt, mode != lastMode, SubjectFov);
        Check(mode == SubjectAimMode.Hold && Deg(view, held) < 1e-9 && !slew.Slewing, "hand-off: a lost subject holds the view where it was");
        /* The new subject is a jump by itself, as AimJumped sees it, whatever the mode does. */
        mode = wait.Step(true, true);
        var next = At(-15, 4);
        var first = slew.Step(next, Hitch, Hitch, true, SubjectFov);
        Check(mode == SubjectAimMode.Subject && slew.Slewing && Deg(first, held) <= oneStep + 1e-9,
            $"hand-off: a new subject from a held view slews from where the view was ({Deg(first, held):G3} deg in the first frame)");
    }
}

/* CraftExtent.Enclose: a craft framed by a sphere it fits in, off its centre of mass, rather than a sphere about its centre of mass. */
{
    const double Pad = 1.5, Min = 2.0;
    DVec3 Roll(DVec3 p, double deg)
    {
        double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return new DVec3(p.X * c - p.Y * s, p.X * s + p.Y * c, p.Z);
    }
    CraftExtent Shape(IEnumerable<DVec3> points, double deg)
    {
        var e = new CraftExtent();
        foreach (var p in points) e.Add(Roll(p, deg), Pad);
        return e;
    }

    /* A capsule and its parts, with an open canopy 20 m above the centre of mass. */
    var chuted = new CraftExtent();
    chuted.Add(origin, Pad);
    chuted.Add(new DVec3(0, -2, 0), Pad);
    chuted.AddBox(new DVec3(0, 20, 0), new DVec3(3, 2, 3));
    chuted.Enclose(Min, out var cc, out double ch);
    double sphereR = chuted.ReachFrom(origin);
    Check(cc.Y > 8 && cc.Y < 11 && Near(cc.X, 0, 0.05) && Near(cc.Z, 0, 0.05), $"enclose: centred between the capsule and the canopy ({cc})");
    Check(Near(ch, chuted.ReachFrom(cc), 1e-9) && ch < sphereR * 0.65, $"enclose: a chuted craft frames at about half today's sphere ({ch:F1} m against {sphereR:F1} m)");

    /* A plain rocket, its centre of mass mid stack: today's sphere, at any attitude. */
    var stack = new List<DVec3>();
    for (int i = -5; i <= 5; i++) stack.Add(new DVec3(0, i, 0));
    double today = CommandCameraMath.BoundingRadius(stack, origin, Pad, Min);
    Shape(stack, 0).Enclose(Min, out var ec, out double eh);
    Check(Near(eh, today, 1e-9) && VNear(ec, origin, 1e-9), "enclose: a symmetric rocket frames as today's sphere, on its centre of mass");
    Shape(stack, 90).Enclose(Min, out _, out double eh90);
    Shape(stack, 37).Enclose(Min, out _, out double eh37);
    Check(Near(eh90, eh, 1e-9) && Near(eh37, eh, 1e-9), "enclose: end on, side on or pitched, it frames the same");

    /* The same rocket with its centre of mass lowStack: framed by its half length about its middle. */
    var lowStack = new List<DVec3>();
    for (int i = -2; i <= 8; i++) lowStack.Add(new DVec3(0, i, 0));
    Shape(lowStack, 0).Enclose(Min, out var lc, out double lh);
    Check(Near(lh, 6.5, 0.05) && Near(lc.Y, 3, 0.05), $"enclose: a rocket heavy at one end frames by its half length about its middle ({lh:F3} m at {lc})");
    Check(lh < CommandCameraMath.BoundingRadius(lowStack, origin, Pad, Min), "enclose: tighter than its sphere about the centre of mass");

    /* A T shape (a lander, radial chutes, a plane) rolled about the view: the same centre, turned with it, and the same size. */
    var tee = new[] { new DVec3(-10, 0, 0), new DVec3(10, 0, 0), new DVec3(0, 10, 0), origin };
    Shape(tee, 0).Enclose(Min, out var tc0, out double th0);
    Check(th0 <= Shape(tee, 0).ReachFrom(origin) + 1e-9, $"enclose: a T shape is never framed looser than its centre of mass sphere ({th0:F3} m)");
    bool steady = true;
    foreach (double deg in new[] { 15.0, 30, 45, 60, 90, 137, 200 })
    {
        Shape(tee, deg).Enclose(Min, out var tc, out double th);
        steady &= Near(th, th0, 1e-6) && VNear(tc, Roll(tc0, deg), 1e-6);
    }
    Check(steady, "enclose: a T shape rolled through 15 to 200 deg keeps its size, its centre rolling with it");

    /* An L shape, where the smallest sphere sits off the centre of mass. */
    var ell = new[] { new DVec3(0, 0, 0), new DVec3(12, 0, 0), new DVec3(0, 12, 0) };
    Shape(ell, 0).Enclose(Min, out var lcc, out double lhh);
    Check(Near(lhh, 6 * Math.Sqrt(2) + Pad, 0.1) && VNear(lcc, new DVec3(6, 6, 0), 0.1), $"enclose: an L shape frames by its hypotenuse ({lhh:F3} m at {lcc})");

    var onePart = new CraftExtent();
    onePart.Add(origin, Pad);
    onePart.Enclose(Min, out var oc, out double oh);
    Check(Near(oh, Min, 1e-9) && VNear(oc, origin, 1e-9), "enclose: a single small part is held at the minimum size, as the sphere was");

    /* The parts' own sphere off the centre of mass, with a canopy above: what the picture encloses each frame. */
    var partsSphere = new CraftExtent();
    partsSphere.Add(new DVec3(0, -1, 0), 2.5);
    partsSphere.Enclose(Min, out var pc, out double ph);
    Check(VNear(pc, new DVec3(0, -1, 0), 1e-9) && Near(ph, 2.5, 1e-9), "enclose: a single sphere off the centre of mass is its own framing");
    partsSphere.AddBox(new DVec3(0, 20, 0), new DVec3(3, 2, 3));
    partsSphere.Enclose(Min, out var pc2, out double ph2);
    Check(ph2 < partsSphere.ReachFrom(origin) * 0.65 && pc2.Y > 8, $"enclose: the parts' sphere and a canopy frame together ({ph2:F1} m at {pc2})");

    var a = new DVec3(0, 10, 0);
    var eased = CommandCameraMath.EaseToward(origin, a, 0.4, 0.4, 1.0);
    Check(Near(eased.Y, 10 * (1 - Math.Exp(-1)), 1e-9), "ease: the centre offset closes 63% of the way in one time constant");
    Check(VNear(CommandCameraMath.EaseToward(origin, a, 2.0, 0.4, 1.0), a, 1e-12), "ease: after a gap it snaps");
    var nanTarget = new DVec3(double.NaN, 1, 0);
    Check(VNear(CommandCameraMath.EaseToward(a, nanTarget, 0.4, 0.4, 1.0), a, 0), "ease: a NaN target holds the current offset");
    Check(VNear(CommandCameraMath.EaseToward(a, nanTarget, 2.0, 0.4, 1.0), a, 0), "ease: a NaN target holds even after a gap");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
