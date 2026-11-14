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
// A landed craft seen from straight above: its centre sits a few metres under the sampled ground, as the collider and TerrainAltitude disagree.
var orbiter = new DVec3(0, MunR + 200000, 0);
var landed = new DVec3(0, MunR + 2527, 0);
Func<DVec3, double> plateau = p => MunR + 2530;
Check(!CommandCameraMath.SegmentClearOfTerrain(orbiter, landed, mun, MunR, MunR + 7500, plateau, 64),
    "with no craft radius the ground under a landed craft hides it");
Check(CommandCameraMath.SegmentClearOfTerrain(orbiter, landed, mun, MunR, MunR + 7500, plateau, 64, 13.3),
    "the ground within the craft's radius does not hide a landed craft from overhead");
var slant = new DVec3(100000, MunR + 2527 + 100000, 0);
Check(!CommandCameraMath.SegmentClearOfTerrain(slant, landed, mun, MunR, MunR + 7500, plateau, 4096),
    "with no craft radius a landed craft is hidden by its own ground at 45 degrees");
Check(CommandCameraMath.SegmentClearOfTerrain(slant, landed, mun, MunR, MunR + 7500, plateau, 4096, 13.3),
    "a landed craft is seen at 45 degrees");
Check(!CommandCameraMath.SegmentClearOfTerrain(scope, new DVec3(30000, MunR + 3000, 0), mun, MunR, MunR + 7500, ridge, 64, 13.3),
    "a ridge far from the craft still hides it with a craft radius");
var sunk = new DVec3(0, MunR - 5, 0);
Check(CommandCameraMath.SegmentClearOfTerrain(orbiter, sunk, mun, MunR, MunR + 7500, p => MunR, 64, 13.3),
    "a craft sunk under the sea-level sphere by less than its radius is seen from overhead");
Check(!CommandCameraMath.SegmentClearOfTerrain(new DVec3(0, -(MunR + 200000), 0), landed, mun, MunR, MunR + 7500, plateau, 64, 13.3),
    "a craft radius never sees through the body from the far side");
/*
 * The rc.19 Deck run: an orbiter 197483 m from a lander, 89.46 degrees below
 * its horizon, before the lander's size is known (the nominal radius). The
 * lander's centre sits 3 m under the sampled ground, more than that radius.
 */
double nominal = CommandCameraMath.NominalCraftRadiusM;
double nadirTilt = (90 - 89.46) * Math.PI / 180;
var nadirOrbiter = landed + new DVec3(Math.Sin(nadirTilt), Math.Cos(nadirTilt), 0) * 197483;
Check(CommandCameraMath.SegmentClearOfTerrain(nadirOrbiter, landed, mun, MunR, MunR + 7500, plateau, 64, nominal),
    "a lander under its sampled ground by more than the nominal radius is seen from an orbiter overhead");
double kerbinElev = 44.28 * Math.PI / 180;
var kerbinward = landed + new DVec3(Math.Cos(kerbinElev), Math.Sin(kerbinElev), 0) * 11437113;
Check(CommandCameraMath.SegmentClearOfTerrain(kerbinward, landed, mun, MunR, MunR + 7500, plateau, 4096, nominal),
    "a lander under its sampled ground is seen 44 degrees up from Kerbin with the nominal radius");
Func<DVec3, double> slope = p => MunR + 2530 + Math.Max(0, p.X) * 0.1;
Check(CommandCameraMath.SegmentClearOfTerrain(kerbinward, landed, mun, MunR, MunR + 7500, slope, 4096, nominal),
    "a gentle slope rising toward the viewer does not hide a lander resting on it");
var lowSun = landed + new DVec3(Math.Cos(Math.PI / 180), Math.Sin(Math.PI / 180), 0) * 1e6;
Func<DVec3, double> ridgeNear = p => p.X > 4000 && p.X < 6000 ? MunR + 2830 : MunR + 2530;
Check(CommandCameraMath.SegmentClearOfTerrain(lowSun, landed, mun, MunR, MunR + 7500, plateau, 4096, nominal),
    "a line 1 degree up over flat ground reaches a lander under its sampled ground");
Check(!CommandCameraMath.SegmentClearOfTerrain(lowSun, landed, mun, MunR, MunR + 7500, ridgeNear, 4096, nominal),
    "a 300 m ridge 5 km away still hides a lander under its sampled ground");
var deepCraft = new DVec3(0, MunR + 2430, 0);
Check(!CommandCameraMath.SegmentClearOfTerrain(nadirOrbiter, deepCraft, mun, MunR, MunR + 7500, plateau, 64, nominal),
    "a craft 100 m under the ground stays hidden from overhead");
var hovering = new DVec3(0, MunR + 2600, 0);
Check(!CommandCameraMath.SegmentClearOfTerrain(lowSun, hovering, mun, MunR, MunR + 7500, ridgeNear, 4096, nominal),
    "a craft above the ground lowers nothing, so the ridge still hides it");
/* A lander 20 m under its sampled plain, seen along a line that clears a 25 m ridge 3 km off only if the ridge is lowered with the lander's own ground. */
var sunkLander = new DVec3(0, MunR + 2510, 0);
var grazer = sunkLander + new DVec3(1, 0.004, 0).Normalized * 1e6;
Func<DVec3, double> lowRidge = p => p.X > 2900 && p.X < 3100 ? MunR + 2555 : MunR + 2530;
Check(!CommandCameraMath.SegmentClearOfTerrain(grazer, sunkLander, mun, MunR, MunR + 7500, lowRidge, 4096, nominal),
    "a 25 m ridge 3 km away still hides a lander 20 m under its sampled ground");
Func<DVec3, double> lowRidgeOnly = p => p.X > 2900 && p.X < 3100 ? MunR + 2555 : p.X < 150 ? MunR + 2530 : MunR;
Check(!CommandCameraMath.SegmentClearOfTerrain(grazer, sunkLander, mun, MunR, MunR + 7500, lowRidgeOnly, 4096, nominal),
    "the ridge alone, with no plain beyond the lander's patch, still hides it");
Check(CommandCameraMath.SegmentClearOfTerrain(grazer, sunkLander, mun, MunR, MunR + 7500, p => p.X < 150 ? MunR + 2530 : MunR, 4096, nominal),
    "without the ridge the same line reaches the lander through its own lowered patch");
/*
 * The rc.20 ridge run: debris landed near the lander, each a lens. Each
 * craft's centre sits 3 m under its own sampled ground, and the ground
 * between them falls away 10 m below the line, so only the lens's own patch
 * can hide the lander: 264 m to it at 5.5 degrees up, 905 m at 4.4 down.
 */
Func<DVec3, DVec3, Func<DVec3, double>> sunkEnds = (lens, craft) => p =>
{
    double s = (p.X - lens.X) / (craft.X - lens.X);
    double lineY = lens.Y + (craft.Y - lens.Y) * s;
    double fromEnd = Math.Min(Math.Abs(p.X - lens.X), Math.Abs(p.X - craft.X));
    return lineY + (fromEnd < 50 ? 3 : -10);
};
var debrisUp = landed + new DVec3(-Math.Cos(5.55 * Math.PI / 180), -Math.Sin(5.55 * Math.PI / 180), 0) * 264;
Check(CommandCameraMath.SegmentClearOfTerrain(debrisUp, landed, mun, MunR, MunR + 7500, sunkEnds(debrisUp, landed), 4096, nominal, nominal),
    "a lens landed below the lander sees it up the slope past its own ground");
var debrisDown = landed + new DVec3(-Math.Cos(4.37 * Math.PI / 180), Math.Sin(4.37 * Math.PI / 180), 0) * 905;
Check(!CommandCameraMath.SegmentClearOfTerrain(debrisDown, landed, mun, MunR, MunR + 7500, sunkEnds(debrisDown, landed), 4096, nominal),
    "with no lens allowance a landed lens beyond the lander's own patch is hidden by its own ground");
Check(CommandCameraMath.SegmentClearOfTerrain(debrisDown, landed, mun, MunR, MunR + 7500, sunkEnds(debrisDown, landed), 4096, nominal, nominal),
    "a lens landed above the lander sees it down the slope past its own ground");
Func<DVec3, double> hump = p => p.X > -500 && p.X < -400 ? sunkEnds(debrisDown, landed)(p) + 30 : sunkEnds(debrisDown, landed)(p);
Check(!CommandCameraMath.SegmentClearOfTerrain(debrisDown, landed, mun, MunR, MunR + 7500, hump, 4096, nominal, nominal),
    "a 20 m hump halfway down the slope still hides the lander from a landed lens");
Check(CommandCameraMath.SegmentClearOfTerrain(nadirOrbiter, landed, mun, MunR, MunR + 7500, plateau, 64, nominal, nominal),
    "a lens allowance changes nothing for an orbiter above the terrain shell");
var buriedLens = new DVec3(0, MunR + 2400, 0);
Check(!CommandCameraMath.SegmentClearOfTerrain(buriedLens, buriedLens + new DVec3(-1000, 200, 0), mun, MunR, MunR + 7500, plateau, 4096, 0, nominal),
    "a lens 130 m under the ground stays buried");
Check(!CommandCameraMath.SegmentInsideSphere(scope, new DVec3(0, MunR + 9000, 0), mun, MunR + 7500, out _, out _),
    "a line above the shell never enters it");

/* A Surface aim at sea level on a world whose land stands above it, seen from another planet. */
{
    const double DunaR = 320000, DunaTop = 330500;
    var duna = new DVec3(0, 0, 0);
    var far = new DVec3(-3.2e10, 1e9, 0);
    Func<DVec3, double> dunaLand = p => DunaR + 500;
    var facing = new DVec3(-DunaR, 0, 0);
    var facingOblique = new DVec3(-DunaR * Math.Cos(1.0), DunaR * Math.Sin(1.0), 0);
    var behind = new DVec3(DunaR, 0, 0);
    Check(!CommandCameraMath.SegmentClearOfTerrain(far, facing, duna, DunaR, DunaTop, dunaLand, 64),
        "tested to the point itself, a sea-level point under land reads hidden");
    Check(CommandCameraMath.SegmentClearOfTerrain(far, CommandCameraMath.SurfaceSightEnd(far, facing, duna, DunaR, DunaTop),
        duna, DunaR, DunaTop, dunaLand, 64), "a sea-level point on the facing side is not hidden by its own ground");
    Check(CommandCameraMath.SegmentClearOfTerrain(far, CommandCameraMath.SurfaceSightEnd(far, facingOblique, duna, DunaR, DunaTop),
        duna, DunaR, DunaTop, dunaLand, 64), "nor one 57 degrees off the disc's centre");
    Check(!CommandCameraMath.SegmentClearOfTerrain(far, CommandCameraMath.SurfaceSightEnd(far, behind, duna, DunaR, DunaTop),
        duna, DunaR, DunaTop, dunaLand, 64), "a point on the far side is still hidden by the planet");
    var craft = new DVec3(-DunaR - 50000, 0, 0);
    Check(VNear(CommandCameraMath.SurfaceSightEnd(far, craft, duna, DunaR, DunaTop), craft, 0), "a target above the shell is tested to its place");
    Check(VNear(CommandCameraMath.SurfaceSightEnd(new DVec3(-DunaR - 10, 0, 0), facing, duna, DunaR, DunaTop), new DVec3(-DunaR - 10, 0, 0), 0),
        "a sight line shorter than the stretch left out collapses to its start");

    /* The Sun: no terrain, a sphere at its radius, a point on it in double precision. */
    const double SunR = 261600000;
    var sun = new DVec3(1e9, 2e9, 3e9);
    var kerbin = sun + new DVec3(1.36e10, 0, 0);
    var sunFacing = sun + new DVec3(SunR, 0, 0);
    var sunEdge = sun + new DVec3(SunR * Math.Cos(1.5), SunR * Math.Sin(1.5), 0);
    var sunBehind = sun + new DVec3(-SunR, 0, 0);
    Check(CommandCameraMath.SegmentClearOfSphere(kerbin, CommandCameraMath.SurfaceSightEnd(kerbin, sunFacing, sun, SunR, SunR), sun, SunR),
        "a point on the Sun's facing side is seen");
    Check(CommandCameraMath.SegmentClearOfSphere(kerbin, CommandCameraMath.SurfaceSightEnd(kerbin, sunEdge, sun, SunR, SunR), sun, SunR),
        "a point near the Sun's limb on the facing side is seen");
    Check(!CommandCameraMath.SegmentClearOfSphere(kerbin, CommandCameraMath.SurfaceSightEnd(kerbin, sunBehind, sun, SunR, SunR), sun, SunR),
        "a point on the Sun's far side is hidden by the Sun");
}

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

// Line of sight at one instant: bodies where they are then, the skyline turned with its body.
{
    double seaR = 600000, rMax = 606800, rs = seaR + 30;
    var up = new DVec3(1, 0, 0);
    var axis = new DVec3(0, 1, 0);
    var losEast = new DVec3(0, 0, 1);
    var losCentre = new DVec3(0, 0, 0);
    var relS = up * rs;

    /* A moon on a circular orbit crosses the line to a far craft and moves on. */
    double moonOrbit = 1.2e7, moonR = 200000, period = 138984;
    DVec3 MoonAt(double ut)
    {
        double a = 2 * Math.PI * ut / period;
        return new DVec3(Math.Cos(a), 0, Math.Sin(a)) * moonOrbit;
    }
    var craft = up * 3e7;
    string FirstBlocker(double ut) =>
        !CommandCameraMath.SegmentClearOfSphere(relS, craft, MoonAt(ut), moonR) ? "Mun"
        : !CommandCameraMath.SegmentClearOfSphere(relS, craft, losCentre, seaR) ? "Kerbin" : null;
    var blocked = LineOfSightMath.Answer(relS, craft, FirstBlocker(0), "Kerbin");
    var clear = LineOfSightMath.Answer(relS, craft, FirstBlocker(period / 8), "Kerbin");
    Check(blocked.Known && !blocked.Visible && blocked.Occluder == "Mun" && !blocked.BelowHorizon,
        "sight: a moon on the line at one instant hides the craft");
    Check(clear.Known && clear.Visible && clear.Occluder == null && Near(clear.RangeM, 3e7 - rs, 1e-6),
        "sight: an eighth of an orbit later it has moved clear, with the range to the craft");
    var under = LineOfSightMath.Answer(relS, up * -3e7, "Kerbin", "Kerbin");
    Check(!under.Visible && under.BelowHorizon, "sight: the vantage's own body in the way is below the horizon");
    Check(!LineOfSightMath.Unknown.Known && double.IsNaN(LineOfSightMath.Unknown.RangeM), "sight: unknown says nothing");
    var off = LineOfSightMath.SwitchedOff(relS, craft);
    Check(off.Known && off.Disabled && !off.Visible && off.Occluder == null && Near(off.RangeM, 3e7 - rs, 1e-6),
        "sight: a switched-off station is known to see nothing, with its range");

    /* A 1 km ridge 20 km east; a far craft fixed in space just under its crest rises over it as the body turns. */
    var ridgeDir = (up * Math.Cos(20000 / seaR) + losEast * Math.Sin(20000 / seaR)).Normalized;
    var mask = new HorizonMask(rs, seaR, rMax, 0.25, d =>
        CommandCameraMath.AngleBetween(d, ridgeDir) < 1500 / seaR ? seaR + 1000 : seaR);
    HorizonMask.Basis(relS, axis, out var mu, out var mn, out var me);
    double crest = mask.MaskAt(Math.PI / 2, Math.PI / 4, mu, mn, me);
    var target = relS + (up * Math.Sin(crest - 0.002) + losEast * Math.Cos(crest - 0.002)) * 2e6;
    double losW = 2 * Math.PI / 21549.425;
    var losSpin = new DVec3(0, -losW, 0);
    double dt = 0.006 / losW;
    var stationLater = CommandCameraMath.CarriedByBody(relS, losCentre, losSpin, dt);
    Check(DVec3.Dot(stationLater - relS, losEast) > 0, "sight: the body turns its stations eastward");
    Check(LineOfSightMath.SkylineBlocks(mask, relS, target, losCentre, losCentre, losSpin, 0, axis, 0),
        "sight: at the first instant the ridge hides the craft");
    Check(!LineOfSightMath.SkylineBlocks(mask, stationLater, target, losCentre, losCentre, losSpin, dt, axis, 0),
        "sight: once the body has turned 0.006 rad the craft has risen over the ridge");
    var stationEarlier = CommandCameraMath.CarriedByBody(relS, losCentre, losSpin, -dt);
    Check(LineOfSightMath.SkylineBlocks(mask, stationEarlier, target, losCentre, losCentre, losSpin, -dt, axis, 0),
        "sight: as long before, it was lower still and hidden");
}

// A station stands on its own ground, whatever altitude the game stored.
{
    Check(CommandCameraMath.StationGroundAltitude(-230, 64.8) == 64.8, "station: an altitude under the ground is raised to it");
    Check(CommandCameraMath.StationGroundAltitude(620, 64.8) == 64.8, "station: an altitude far over the ground (the 620 m Duna case) comes down to it");
    Check(CommandCameraMath.StationGroundAltitude(-230, double.NaN) == -230, "station: no ground to read keeps the stored altitude");
    Check(CommandCameraMath.StationGroundAltitude(620, double.NaN) == 620, "station: no ground to read keeps a high stored altitude too");
    Check(CommandCameraMath.StationGroundAltitude(35, 0) == 0, "station: over the sea the clamped sea surface is used");

    /* The Deck: KSC's home read -230 m with Kerbin's terrain unloaded, the lens 30 m over that, ground 64.8 m. A craft 77 degrees up was hidden by the lens's own skyline. */
    double seaR = 600000, rMax = 606800, ground = 64.8, offset = 30;
    var axis = new DVec3(0, 1, 0);
    Func<DVec3, double> flat = d => seaR + ground;
    var up = new DVec3(1, 0, 0);
    DVec3 Toward(DVec3 relS, double elDeg) =>
        relS + (up * Math.Sin(elDeg * Math.PI / 180) + new DVec3(0, 0, 1) * Math.Cos(elDeg * Math.PI / 180)) * 3.3e10;

    double buried = seaR + (-230 + offset);
    var buriedMask = new HorizonMask(buried, seaR, rMax, 0.25, flat);
    Check(buriedMask.Blocks(new DVec3(buried, 0, 0), Toward(new DVec3(buried, 0, 0), 77), axis),
        "station: a buried lens hides a craft 77 degrees up (the false occlusion)");
    HorizonMask.Basis(new DVec3(buried, 0, 0), axis, out var bu, out var bn, out var be);
    double wall = buriedMask.MaskAt(1.0, 0.2, bu, bn, be) * 180 / Math.PI;
    Check(Near(wall, 85.68, 0.05), $"station: the buried skyline stands where the Deck's occlusion cleared, 85.68 degrees (got {wall:F2})");

    double placed = seaR + CommandCameraMath.StationGroundAltitude(-230, ground) + offset;
    var relS = new DVec3(placed, 0, 0);
    var mask = new HorizonMask(placed, seaR, rMax, 0.25, flat);
    Check(!mask.Blocks(relS, Toward(relS, 77), axis), "station: on the ground, a craft 77 degrees up is clear");
    Check(!mask.Blocks(relS, Toward(relS, 1), axis), "station: on the ground, a craft 1 degree up is clear");
    Check(mask.Blocks(relS, Toward(relS, -1), axis), "station: on the ground, a craft 1 degree down is hidden");
}

// A lens's depth under the atmosphere's surface, from metres rather than rounded scaled positions.
{
    double k = 6000, pRadius = 100, lensM = R + 95;
    Check(Near(CommandCameraMath.LensDepthM(lensM, pRadius, k), -95, 1e-6), "lens depth: 95 m up is 95 m above the surface");
    Check(Near(CommandCameraMath.LensDepthM(R - 10, pRadius, k), 10, 1e-6), "lens depth: 10 m down is 10 m under it");

    /* Kerbin 1.7e6 scaled units from the origin (the active craft at Duna): float positions are 0.125 units (750 m) apart there. */
    var bodyAt = new DVec3(1.7e6, 2.1e5, -3.3e5);
    double worst = 0;
    for (int i = 0; i < 64; i++)
    {
        double a = i * 0.1, b = i * 0.37;
        var dir = new DVec3(Math.Cos(a) * Math.Cos(b), Math.Sin(b), Math.Sin(a) * Math.Cos(b));
        var cam = bodyAt + dir * (lensM / k);
        float dx = (float)cam.X - (float)bodyAt.X, dy = (float)cam.Y - (float)bodyAt.Y, dz = (float)cam.Z - (float)bodyAt.Z;
        double floatDepth = (pRadius - Math.Sqrt(dx * dx + dy * dy + dz * dz)) * k;
        worst = Math.Max(worst, Math.Abs(floatDepth - -95));
    }
    Check(worst > 95, $"lens depth: read from float scaled positions it can be off by more than the lens's height ({worst:F0} m)");
}

// A command camera's field when no craft sizes it.
{
    Check(CommandCameraMath.CarriedFov(2.5) == 2.5, "carried fov: a rebuild opens on the field its camera showed");
    Check(double.IsNaN(CommandCameraMath.CarriedFov(double.NaN)), "carried fov: nothing carried leaves the lens's default");
    Check(double.IsNaN(CommandCameraMath.CarriedFov(0)) && double.IsNaN(CommandCameraMath.CarriedFov(double.PositiveInfinity)),
        "carried fov: an unusable field is not carried");
    Check(CommandCameraMath.PointFov(true, 500, 1e4, 0.5, 0.02, 30) == 30, "point fov: a direction opens to the widest view");
    Check(CommandCameraMath.PointFov(false, 0, 1e4, 0.5, 0.02, 30) == 30, "point fov: a surface point without a margin does too");
    double f = CommandCameraMath.PointFov(false, 100, 1e4, 0.5, 0.02, 30);
    Check(Near(f, CommandCameraMath.FovForFraction(100, 1e4, 0.5, 0.02, 30), 1e-12) && f < 30,
        "point fov: a surface point frames its margin, whatever craft is the subject");
}

// A direction aim straight down from a craft vantage has a pose.
{
    var up = new DVec3(0.3, 0.9, 0.1).Normalized;
    var nNorth = new DVec3(0, 0, 1);
    var nEast = DVec3.Cross(nNorth, up);
    var nDown = CommandCameraMath.DirectionVector(up, nNorth, nEast, 0, -90);
    Check(Near(DVec3.Dot(nDown, up), -1, 1e-12), "nadir: el -90 points straight down");
    var nStation = new DVec3(7e5, 1e5, 3e4);
    CommandCameraMath.AimBasis(nStation, nStation + nDown * 50, up, nNorth, out var fwd, out var lensUp);
    Check(Near(DVec3.Dot(fwd, up), -1, 1e-12) && Near(lensUp.Magnitude, 1, 1e-9) && Math.Abs(DVec3.Dot(lensUp, fwd)) < 1e-9,
        "nadir: the lens keeps a finite up, from north, when local up is its axis");
}

// Stars faded at a lens as KSP fades them at the main camera (stock limits 0.6, 0.9, 1.1, 0.6).
{
    double Fade(double p, double zen, bool vis, double off, double fov) => CommandCameraMath.GalaxyFade(p, zen, vis, off, fov, 0.6, 0.9, 1.1, 0.6);
    Check(Fade(0.99, 40, true, 120, 30) >= 1, "galaxy fade: sea level by day hides the stars");
    Check(Fade(0.99, 130, false, 120, 30) == 0.6, "galaxy fade: sea level at night keeps the air's own haze only");
    double dusk = Fade(0.99, 96, true, 120, 30);
    Check(dusk > 0.6 && dusk < 1.5, "galaxy fade: dusk fades partway");
    Check(Fade(0, 40, true, 120, 30) == 0, "galaxy fade: in vacuum with the Sun out of view the stars are full");
    Check(Near(Fade(0, 40, true, 5, 30), 0.6, 1e-12), "galaxy fade: the Sun in the frame glares them away");
    Check(Fade(0, 40, false, 5, 30) == 0, "galaxy fade: a Sun below the horizon gives no glare");
}

// With the optics drawn the zoom stops where the blur spans two pixels of a 576-row frame.
{
    double Floor(double lensArcsec, double air, double elev) =>
        OpticsModel.OpticsFovFloor(0.02, OpticsModel.Assess(new OpticsSite
        {
            ResolutionArcsec = lensArcsec, ZenithSeeingArcsec = 1.0, SeeingAirmassPower = 0.6, AirFactor = air, ElevationDeg = elev,
            SunElevationDeg = double.NaN,
        }, double.NaN).BlurArcsec, 576);
    double l1Ground = Floor(15, 1, 45), l3Ground = Floor(1, 1, 45), l3Space = Floor(1, 0, 45);
    Check(l1Ground > 1.15 && l1Ground < 1.3, $"optics floor: level 1 from the ground stops near 1.2 deg ({l1Ground:F3})");
    Check(l3Ground > 0.09 && l3Ground < 0.13, $"optics floor: level 3 from the ground stops near 0.1 deg ({l3Ground:F3})");
    Check(l3Space < l3Ground && Near(l3Space, OpticsModel.FovForBlurPx(1, 576, 2), 1e-12),
        $"optics floor: with no air only the lens holds it, narrower ({l3Space:F3})");
    Check(Floor(1, 1, 5) > l3Ground, "optics floor: seeing low in the sky holds the zoom wider");
    Check(OpticsModel.OpticsFovFloor(0.02, 0.01, 576) == 0.02, "optics floor: a lens sharper than the zoom limit leaves the limit");
    Check(OpticsModel.OpticsFovFloor(1.0, double.NaN, 576) == 1.0, "optics floor: no blur leaves the zoom limit");
    double pix = OpticsModel.PixelArcsec(l1Ground, 576);
    double blur = OpticsModel.CombinedBlur(15, OpticsModel.Seeing(1, OpticsModel.Airmass(45), 0.6, 1));
    Check(Near(blur / pix, OpticsModel.FloorBlurPx, 1e-9), "optics floor: at the floor the blur spans FloorBlurPx pixels");
    double floorSpan = OpticsModel.BlurSpanPx(blur, l1Ground, 576);
    Check(Near(floorSpan, OpticsModel.FloorBlurPx, 1e-9) && OpticsModel.Worthwhile(floorSpan) && OpticsModel.Ramp(floorSpan) > 0,
        "optics floor: measured on the floor's own rows the effect shows at the floor");
    Check(OpticsModel.Ramp(OpticsModel.BlurSpanPx(blur, l1Ground, 288)) < 1e-6,
        "optics floor: measured on half-height rows it would vanish, which is why the rig measures on full rows");
    var lens = new LensLimits
    {
        Known = true, WidthPx = 1024, HeightPx = 576, OpticsOn = true,
        Knobs = new OpticsKnobs { ResolutionArcsec = 15, SeeingArcsec = 1, SeeingAirmassPower = 0.6 },
    };
    lens.FovMinDeg = OpticsModel.OpticsFovFloor(0.02, 15, 576);
    Check(Near(lens.PixelArcsec, 7.5, 1e-6), "optics floor: a vantage's pixel at the floor is half the lens's blur");
    Check(Near(lens.ResolutionArcsec(1, 45), blur, 1e-9), "optics floor: the resolution toward a point is still the blur, not the pixel");
}

// The body a lens stands on, and the quad that adds a layer under seeing's shift.
{
    Check(Near(OpticsModel.HomeAngularRadius(2000, 1000), Math.PI / 6, 1e-12), "home body: the angular radius is the disc's");
    double justAbove = OpticsModel.HomeAngularRadius(600030, 600000);
    Check(justAbove > 1.5 && justAbove < Math.PI * 0.5, "home body: a lens 30 m up still has the body filling almost half the sky");
    Check(OpticsModel.HomeAngularRadius(599000, 600000) == Math.PI * 0.5, "home body: a lens under the datum has it filling half the sky");

    OpticsModel.OverscanAxis(0f, 1024f, out float qx0, out float qx1, out float qu0, out float qu1);
    Check(qx0 == 0f && qx1 == 1024f && qu0 == 0f && qu1 == 1f, "overscan: no shift draws exactly over the frame");
    foreach (float shift in new[] { 3.5f, -3.5f, 0.25f, -40f })
    {
        OpticsModel.OverscanAxis(shift, 1024f, out qx0, out qx1, out qu0, out qu1);
        Check(qx0 <= 0f && qx1 >= 1024f, $"overscan: a shift of {shift} leaves no bare strip");
        double perPx = (qu1 - qu0) / (qx1 - qx0);
        Check(Near(qu0 + (0f - qx0) * perPx, -shift / 1024f, 1e-5) && Near(perPx, 1.0 / 1024, 1e-7), $"overscan: a shift of {shift} moves the picture by that much, unstretched");
    }
}

// The Sun's light turned to face a lens far from the main camera.
{
    /* The Sun at the origin, Kerbin and Duna on their orbits 137 degrees apart: stock lights from the Sun toward the main camera at Duna. */
    double a = 137 * Math.PI / 180;
    var kerbin = new DVec3(13.6e9, 0, 0);
    var duna = new DVec3(20.7e9 * Math.Cos(a), 0, 20.7e9 * Math.Sin(a));
    var stockForward = duna.Normalized;
    Check(Near(CommandCameraMath.SunTurnDeg(stockForward, kerbin), 137, 1e-6), "sun turn: a lens on Kerbin with the craft at Duna turns the light 137 degrees");
    var nearCraft = kerbin + new DVec3(0, 0, 100e3);
    Check(CommandCameraMath.SunTurnDeg(kerbin.Normalized, nearCraft) < CommandCameraMath.SunTurnMinDeg,
        "sun turn: a lens 100 km from the main camera keeps stock's light");
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

/* ---- aim handover across world model records ---- */
{
    /* A capsule under a chute 2.5 km out, falling at a steady 8 m/s, read through records republished every second. */
    const double range = 2500, fall = 8, g = 9.81, publish = 1.0, frame = 1.0 / 60, widest = 2 * Math.PI / 180;
    DVec3 Truth(double t) => new DVec3(range, 1000 - fall * t, 0);
    /* A record from its publish UT: ballistic (gravity, no drag), or with the craft's own net acceleration. */
    DVec3 Predict(double from, bool ballistic, double t)
    {
        double d = t - from;
        var at = Truth(from) + new DVec3(0, -fall * d, 0);
        return ballistic ? at + new DVec3(0, -0.5 * g * d * d, 0) : at;
    }
    double Angle(DVec3 a, DVec3 b) => Math.Acos(Math.Min(1, DVec3.Dot(a.Normalized, b.Normalized)));

    void Run(bool ballistic, bool eased, out double worstStepRad, out double worstLagM)
    {
        var handover = new AimHandover();
        worstStepRad = 0;
        worstLagM = 0;
        double from = 0, lastT = double.NaN;
        DVec3 last = default, lastRaw = default;
        for (double t = 0; t < 10; t += frame)
        {
            double now = Math.Floor(t / publish) * publish;
            bool published = now != from;
            if (published && eased && !double.IsNaN(lastT)) handover.Handover(lastRaw, Predict(now, ballistic, lastT), range, widest);
            from = now;
            var raw = Predict(from, ballistic, t);
            var offset = eased ? handover.Step(frame, AimHandover.TauS, 1.0, 0.001) : default;
            var aim = raw + offset;
            if (!double.IsNaN(lastT)) worstStepRad = Math.Max(worstStepRad, Angle(aim, last));
            worstLagM = Math.Max(worstLagM, (aim - raw).Magnitude);
            last = aim;
            lastRaw = raw;
            lastT = t;
        }
    }
    double fallStep = fall * frame / range;
    Run(true, false, out double snapRad, out _);
    Run(true, true, out double easedRad, out _);
    Check(snapRad > 10 * fallStep, $"handover: ballistic records under a chute snap the raw aim ({snapRad * 180 / Math.PI:F3} deg in a frame)");
    Check(easedRad < 3 * fallStep, $"handover: eased, the aim never moves more than a little over its own fall in a frame ({easedRad / fallStep:F2} falls)");
    Run(false, true, out double rightRad, out double rightLag);
    Check(rightLag == 0 && rightRad <= fallStep * 1.0001, $"handover: records that predict right leave no offset, so the aim never lags ({rightLag} m)");

    var one = new AimHandover();
    Check(one.Handover(new DVec3(0, 20, 0), default, 1000, 0.5), "handover: a 20 m correction at 1 km is eased");
    Check(VNear(one.Offset, new DVec3(0, 20, 0), 0), "handover: the offset starts on the old prediction");
    double t1 = 0;
    while (t1 < 1.0 - 1e-9) { one.Step(frame, AimHandover.TauS, 1.0, 0.001); t1 += frame; }
    Check(one.Offset.Magnitude < 0.02 * 20, $"handover: a 20 m correction is under 2 % within a second ({one.Offset.Magnitude:F3} m)");
    while (one.Easing && t1 < 10) { one.Step(frame, AimHandover.TauS, 1.0, 0.001); t1 += frame; }
    Check(!one.Easing && t1 < 3.5, $"handover: and gone once under the floor ({t1:F2} s)");

    var held = new AimHandover();
    held.Handover(new DVec3(0, 5, 0), default, 1000, 0.5);
    Check(!held.Handover(new DVec3(double.NaN, 0, 0), default, 1000, 0.5), "handover: a place that is not finite is not eased");
    Check(VNear(held.Offset, new DVec3(0, 5, 0), 0), "handover: a place that is not finite holds the offset");
    held.Step(0, AimHandover.TauS, 1.0, 0.001);
    Check(VNear(held.Offset, new DVec3(0, 5, 0), 0), "handover: a step of no time holds it");
    held.Step(1.5, AimHandover.TauS, 1.0, 0.001);
    Check(!held.Easing, "handover: a gap longer than the snap drops it");
    held.Handover(new DVec3(0, 5, 0), default, 1000, 0.5);
    held.Reset();
    Check(!held.Easing, "handover: a reset (a new subject, aim or vantage) starts on the aim, not eased from the last");
}

/* ---- aim handover: who leads, and what it leaves to the slew ---- */
{
    const double frame = 1.0 / 60, slew = AimSlew.MaxRateDegPerS * Math.PI / 180;
    double Widest(double fieldRad) => AimHandover.WidestRad(fieldRad, slew);
    double width = Widest(10 * Math.PI / 180);
    /* The old record put the craft 20 m off; the new one is right. The aim leaves the old place at once and closes on the right one as the ease allows. */
    var lag = new AimHandover();
    Check(lag.Handover(new DVec3(20, 0, 0), default, 2000, width), "handover: an old wrong record's 20 m at 2 km is eased");
    double t = 0, worstOver = 0;
    while (lag.Easing && t < 5)
    {
        double off = lag.Step(frame, AimHandover.TauS, 1.0, 0.02).Magnitude;
        t += frame;
        worstOver = Math.Max(worstOver, off - 20 * Math.Exp(-t / AimHandover.TauS));
    }
    Check(worstOver <= 1e-9, $"handover: off a right new record by no more than 20 m e^(-t/tau) ({worstOver:G3} m over)");
    Check(!lag.Easing && t < 2.0, $"handover: and on it within 2 s ({t:F2} s)");

    /* Too wide to ease at a turn the slew allows: 50 m at 100 m, about 27 degrees. */
    var wide = new AimHandover();
    wide.Handover(new DVec3(0, 0, 1), default, 100, Widest(1.0));
    Check(!wide.Handover(new DVec3(50, 0, 0), default, 100, Widest(1.0)) && !wide.Easing,
        "handover: a 50 m correction at 100 m is refused, and what was easing with it is reset, for the slew to turn");
    Check(Widest(10.0) / AimHandover.TauS <= slew * 1.0000001,
        "handover: the widest ease starts turning no faster than the slew's top rate, however wide the field");
    var narrowField = new AimHandover();
    Check(!narrowField.Handover(new DVec3(10, 0, 0), default, 1000, Widest(0.01)),
        "handover: a correction wider than half a narrow field goes to the slew too (0.57 deg in a 0.57 deg field)");
    var noRange = new AimHandover();
    Check(!noRange.Handover(new DVec3(1, 0, 0), default, 0, width), "handover: no range, no ease");
    Check(Math.Abs(Widest(1.0) - 45 * Math.PI / 180 * AimHandover.TauS) < 1e-12, "handover: in a wide field the slew's rate sets the widest ease (11.25 deg)");
}

/* ---- aim handover: a publish that also steps the instant read back to the cap ---- */
{
    /*
     * A craft crossing 500 km out at 200 m/s, records exact and republished
     * every 2 s, read back no more than 4 s (PastInstant.MaxReadBackS). With
     * a light time between 4 and 6 s each publish moves the placed instant
     * on by L - 4 s at once.
     */
    const double range = 500000, speed = 200, publish = 2.0, cap = 4.0, frame = 1.0 / 60;
    double widest = AimHandover.WidestRad(2 * Math.PI / 180, AimSlew.MaxRateDegPerS * Math.PI / 180);
    DVec3 Truth(double t) => new DVec3(range, speed * t, 0);
    double travel = speed * frame;

    double WorstStep(double light, bool stepAware)
    {
        var handover = new AimHandover();
        double from = 0, lastTr = double.NaN, worst = 0;
        DVec3 lastRaw = default, last = default;
        for (double t = 10; t < 30; t += frame)
        {
            double now = Math.Floor(t / publish) * publish;
            bool published = now != from;
            from = now;
            double tr = Math.Max(t - light, from - cap);
            var raw = Truth(tr);
            if (published && !double.IsNaN(lastTr))
            {
                var after = stepAware && AimHandover.InstantStepped(tr - lastTr, frame) ? raw : Truth(lastTr);
                handover.Handover(lastRaw, after, range, widest);
            }
            var aim = raw + handover.Step(frame, AimHandover.TauS, 1.0, 0.001);
            if (!double.IsNaN(lastTr)) worst = Math.Max(worst, (aim - last).Magnitude);
            last = aim;
            lastRaw = raw;
            lastTr = tr;
        }
        return worst;
    }
    foreach (double light in new[] { 4.5, 5.0, 5.5 })
    {
        double snapped = WorstStep(light, false), eased = WorstStep(light, true);
        /* Eased, a frame moves its own travel plus the step's share of an e-fold over TauS, never the step at once. */
        double easedBound = travel * (1 + (light - cap) / AimHandover.TauS) * 1.001;
        Check(snapped >= speed * (light - cap) * 0.999, $"handover at the cap: L = {light} s, compared at one instant the step is left in ({snapped / travel:F1} frames of travel at once)");
        Check(eased <= easedBound, $"handover at the cap: L = {light} s, the step eases ({eased / travel:F2} frames of travel at most, bound {easedBound / travel:F2})");
    }
    Check(!AimHandover.InstantStepped(frame, frame) && !AimHandover.InstantStepped(0.8 * frame, frame)
        && AimHandover.InstantStepped(1.0 + frame, frame) && AimHandover.InstantStepped(-0.5, frame)
        && !AimHandover.InstantStepped(double.NaN, frame) && !AimHandover.InstantStepped(1.0, double.NaN),
        "instant stepped: a frame's own step, or a light time changing slowly, is none; a jump either way is; unknowns are none");
}

/* ---- truth with a light speed: when the picture can say a craft has no fix ---- */
{
    TruthAimPlace Undrawn(bool lightGone) => CommandCameraMath.TruthAimPlaceFor(true, true, false, false, lightGone);
    Check(Undrawn(true) == TruthAimPlace.NoFix, "a craft whose last light has already arrived has no fix");
    Check(Undrawn(false) == TruthAimPlace.Source,
        "one the picture cannot judge (light older than the history, a craft just created, a solve that did not settle) keeps truth's place");
    Check(CommandCameraMath.TruthAimPlaceFor(false, true, false, false, true) == TruthAimPlace.Source,
        "an aim at a point keeps truth's range even then");
}

/* ---- truth with a light speed: the forecast for an aim at a drawn place ---- */
{
    /* At 1x warp the 4 s horizon has 32 steps of 0.125 s: about 375 km of light at 1 % of real light speed. */
    Check(CommandCameraMath.ForecastHoldsAtLightTime(60000 / 2.998e6, 4, 32), "a craft drawn 60 km away at 1 % c keeps truth's forecast");
    Check(!CommandCameraMath.ForecastHoldsAtLightTime(1.2e7 / 2.998e6, 4, 32), "one drawn at the Mun's distance has none");
    Check(CommandCameraMath.ForecastHoldsAtLightTime(1.2e7 / 2.998e6, 3600, 32), "unless the warp stretches a step past its light time");
    Check(CommandCameraMath.ForecastHoldsAtLightTime(double.NaN, 4, 32), "an unknown instant keeps the forecast");
}

/* ---- truth with a light speed: a flicker in the picture does not move the aim ---- */
{
    const int N = TruthPlaceFilter.Solves;
    var f = new TruthPlaceFilter();
    Check(f.Step(TruthAimPlace.Drawn, true) == TruthAimPlace.Drawn, "a drawn place is taken at once");
    bool steady = true;
    for (int i = 0; i < 20; i++)
    {
        var lost = f.Step(i % 2 == 0 ? TruthAimPlace.NoFix : TruthAimPlace.Source, false);
        var found = f.Step(TruthAimPlace.Drawn, true);
        steady &= lost == TruthAimPlace.Kept && found == TruthAimPlace.Drawn;
    }
    Check(steady, "a picture that finds and loses the craft on alternate solves keeps the aim on its drawn place, whether the loss reads as no fix or as truth's place");

    var steps = new List<TruthAimPlace>();
    for (int i = 0; i < N; i++) steps.Add(f.Step(TruthAimPlace.NoFix, false));
    Check(steps.GetRange(0, N - 1).TrueForAll(p => p == TruthAimPlace.Kept) && steps[N - 1] == TruthAimPlace.NoFix,
        $"a loss is taken on the {N}th solve in a row, the drawn place kept until then");
    Check(f.Step(TruthAimPlace.NoFix, false) == TruthAimPlace.NoFix, "and stays taken");
    Check(f.Step(TruthAimPlace.Drawn, true) == TruthAimPlace.Drawn, "a return is taken at once");

    var g = new TruthPlaceFilter();
    bool held = true;
    for (int i = 0; i < 40; i++)
    {
        var p = g.Step(i % 4 == 3 ? TruthAimPlace.NoFix : TruthAimPlace.Drawn, i % 4 != 3);
        held &= p == TruthAimPlace.Drawn || p == TruthAimPlace.Kept;
    }
    Check(held, "a craft drawn on 3 solves in 4 never leaves its drawn place");
    held = true;
    for (int i = 0; i < 40; i++)
    {
        var p = g.Step(i % 4 == 0 ? TruthAimPlace.Drawn : TruthAimPlace.NoFix, i % 4 == 0);
        held &= p == TruthAimPlace.Drawn || p == TruthAimPlace.Kept;
    }
    Check(held, "nor one drawn on 1 solve in 4");

    var h = new TruthPlaceFilter();
    Check(h.Step(TruthAimPlace.NoFix, false) == TruthAimPlace.NoFix && h.Step(TruthAimPlace.Source, false) == TruthAimPlace.Source,
        "with no drawn place before it, a loss is taken at once: nothing is kept that a Drawn solve did not place");
    h.Step(TruthAimPlace.Drawn, true);
    h.Reset();
    Check(h.Step(TruthAimPlace.NoFix, false) == TruthAimPlace.NoFix,
        "nor after a reset, which the rig makes on a new aim, subject or vantage and on a solve that places no subject");
    h.Step(TruthAimPlace.Drawn, true);
    Check(h.Step(TruthAimPlace.Source, true) == TruthAimPlace.Source,
        "a craft the picture now draws live is placed by truth at once, not kept at its past place");
    Check(h.Step(TruthAimPlace.NoFix, false) == TruthAimPlace.NoFix, "and a loss after it has nothing drawn to keep");
}

// OccludedSince: the raw edge's UT, held while occluded, NaN once clear.
{
    double since = double.NaN;
    since = CommandCameraMath.OccludedSince(false, since, 100);
    Check(double.IsNaN(since), "not occluded has no since time");
    since = CommandCameraMath.OccludedSince(true, since, 200);
    Check(since == 200, "the solve that turns occlusion on stamps its UT");
    since = CommandCameraMath.OccludedSince(true, since, 260);
    Check(since == 200, "later occluded solves keep the first UT, whatever blocks the view now");
    since = CommandCameraMath.OccludedSince(false, since, 300);
    Check(double.IsNaN(since), "clearing forgets it, with no hold");
    since = CommandCameraMath.OccludedSince(true, since, 301);
    Check(since == 301, "a fresh occlusion stamps a fresh UT");
    Check(CommandCameraMath.OccludedSince(true, double.NaN, 0) == 0, "UT 0 is a real time, distinct from not occluded");
}

// LowestViewElevationDeg: the lowest ray of a rectangular frustum against the local horizontal.
{
    var up = iy;
    var level = iz;
    Check(Near(CommandCameraMath.LowestViewElevationDeg(level, up, up, 20, 1), -10, 1e-9),
        "a level view's lowest ray is half the vertical field down");
    Check(Near(CommandCameraMath.LowestViewElevationDeg(iy * -1.0, iz, up, 10, 1), -90, 1e-9),
        "a frustum holding the nadir bottoms out at -90");
    var raised = new DVec3(0, Math.Sin(30 * Math.PI / 180), Math.Cos(30 * Math.PI / 180));
    var raisedUp = new DVec3(0, Math.Cos(30 * Math.PI / 180), -Math.Sin(30 * Math.PI / 180));
    double wide = CommandCameraMath.LowestViewElevationDeg(raised, raisedUp, up, 20, 1.5);
    Check(wide < 20 && wide > 18.5, "at a raised view the bottom corners sit a little lower than the bottom edge's middle");
    // Rolled 90: the frustum's long axis is now vertical, so the half width sets the lowest ray.
    double tanV = Math.Tan(10 * Math.PI / 180);
    double halfWidthDeg = Math.Atan(tanV * 2) * 180 / Math.PI;
    Check(Near(CommandCameraMath.LowestViewElevationDeg(level, ix, up, 20, 2), -halfWidthDeg, 1e-9),
        "rolled onto its side, the half width sets the lowest ray");
    // An edge can dip below both its corners when the view looks steeply down past the nadir side.
    var steep = new DVec3(0, -Math.Sin(70 * Math.PI / 180), Math.Cos(70 * Math.PI / 180));
    var steepUp = new DVec3(0, Math.Cos(70 * Math.PI / 180), Math.Sin(70 * Math.PI / 180));
    double lowest = CommandCameraMath.LowestViewElevationDeg(steep, steepUp, up, 30, 3);
    double brute = 90;
    for (double a = -1; a <= 1; a += 0.002)
        for (double b = -1; b <= 1; b += 0.002)
        {
            var right = DVec3.Cross(steepUp, steep).Normalized;
            var d = (steep + right * (a * 3 * Math.Tan(15 * Math.PI / 180)) + steepUp * (b * Math.Tan(15 * Math.PI / 180))).Normalized;
            double el = Math.Asin(DVec3.Dot(d, up)) * 180 / Math.PI;
            if (el < brute) brute = el;
        }
    Check(Near(lowest, brute, 0.02), $"the analytic lowest ray matches a brute search ({lowest:F3} against {brute:F3})");
}

// PlanStationGround: when the body's own ground has to be drawn for a lens standing on it.
{
    const double SF = 6000.0;
    var nadirUp = iy;
    var eyeR = R + 70;
    var below = new DVec3(0, -Math.Sin(39 * Math.PI / 180), Math.Cos(39 * Math.PI / 180));
    var downUp = new DVec3(0, Math.Cos(39 * Math.PI / 180), Math.Sin(39 * Math.PI / 180));
    var plan = CommandCameraMath.PlanStationGround(eyeR, R, SF, below, downUp, nadirUp, 20, 1.5, false);
    Check(plan.Draw, "a view 39 down from KSC draws the ground");
    Check(Near(plan.GroundRadiusM, R, 1e-6), "a station above the datum stands on the sphere at the datum");
    Check(Near(plan.HorizonDipDeg, Math.Acos(R / eyeR) * 180 / Math.PI, 1e-9), "the horizon dips by the sphere's own geometry");
    double eyeToCentre = eyeR * plan.EyeToCentreScale;
    Check(Near(eyeToCentre - plan.SphereRadiusUnits, 2.0, 1e-9), "the magnified ground lies two scaled units under the lens");
    Check(plan.SphereRadiusUnits > 0.2, "well past the scaled layer's 0.2 near plane");
    Check(Near(Math.Asin(plan.SphereRadiusUnits / eyeToCentre), Math.Asin(R / eyeR), 1e-12),
        "magnifying about the lens leaves the horizon where it was");

    var level = iz;
    var clear = CommandCameraMath.PlanStationGround(eyeR, R, SF, new DVec3(0, Math.Sin(30 * Math.PI / 180), Math.Cos(30 * Math.PI / 180)),
        new DVec3(0, Math.Cos(30 * Math.PI / 180), -Math.Sin(30 * Math.PI / 180)), nadirUp, 20, 1.5, false);
    Check(!clear.Draw, "a view wholly above the horizon draws nothing");

    var straddle = CommandCameraMath.PlanStationGround(eyeR, R, SF, level, nadirUp, nadirUp, 20, 1.5, false);
    Check(straddle.Draw, "a level view with the horizon in frame draws the ground under it");

    double dip = Math.Acos(R / eyeR) * 180 / Math.PI;
    double fov = 0.1;
    double aimEl = -dip + 0.10 + fov / 2; // the frame's lowest ray sits between the on and off thresholds
    var just = new DVec3(0, Math.Sin(aimEl * Math.PI / 180), Math.Cos(aimEl * Math.PI / 180));
    var justUp = new DVec3(0, Math.Cos(aimEl * Math.PI / 180), -Math.Sin(aimEl * Math.PI / 180));
    Check(!CommandCameraMath.PlanStationGround(eyeR, R, SF, just, justUp, nadirUp, fov, 1, false).Draw,
        "just above the horizon a resting view stays off");
    Check(CommandCameraMath.PlanStationGround(eyeR, R, SF, just, justUp, nadirUp, fov, 1, true).Draw,
        "and a drawing view holds on through the same small margin");

    var pit = CommandCameraMath.PlanStationGround(R - 100, R, SF, below, downUp, nadirUp, 20, 1.5, false);
    Check(pit.Draw && Near(pit.GroundRadiusM, R - 105, 1e-6), "a station below the datum keeps the sphere just under its lens");
    Check((R - 100) * pit.EyeToCentreScale > pit.SphereRadiusUnits, "so the lens is never inside the drawn sphere");

    var high = CommandCameraMath.PlanStationGround(R + 80000, R, SF, below, downUp, nadirUp, 20, 1.5, false);
    Check(!high.Draw, "a lens high enough to clear the near plane leaves the real mesh alone");
}

// DaylightAtLens: what dims the scaled planet's rim and reflection for a lens on its ground.
{
    Check(CommandCameraMath.DaylightAtLens(-40) == 0.0, "night at the lens: no daylight");
    Check(CommandCameraMath.DaylightAtLens(CommandCameraMath.LensTwilightFromDeg) == 0.0, "none at the end of civil twilight");
    Check(CommandCameraMath.DaylightAtLens(30) == 1.0, "day at the lens: full daylight, the stock look");
    Check(CommandCameraMath.DaylightAtLens(CommandCameraMath.LensTwilightToDeg) == 1.0, "full a few degrees up");
    Check(Near(CommandCameraMath.DaylightAtLens(0), 0.5, 1e-12), "half with the Sun on the horizon");
    double last = 0;
    bool rises = true;
    for (double e = -10; e <= 10; e += 0.25)
    {
        double d = CommandCameraMath.DaylightAtLens(e);
        if (d < last) rises = false;
        last = d;
    }
    Check(rises, "dusk to day never dims");
    Check(CommandCameraMath.DaylightAtLens(double.NaN) == 1.0, "an unknown Sun leaves the stock look");
}

// Deep-space optics.
{
    const double KerbinPressure = 101.325;
    Check(OpticsModel.AirFactor(KerbinPressure) == 1.0 && OpticsModel.AirFactor(0) == 0.0 && OpticsModel.AirFactor(500) == 1.0,
        "air factor: 1 at Kerbin sea level, 0 in vacuum, capped in thick air");
    Check(OpticsModel.AirFactor(KerbinPressure / 4) > 0.49 && OpticsModel.AirFactor(KerbinPressure / 4) < 0.51,
        "thin air sees better (square root of the pressure)");

    Check(Near(OpticsModel.Airmass(90), 1.0, 1e-3), "airmass 1 at the zenith");
    Check(Near(OpticsModel.Airmass(30), 2.0, 0.01), "about 2 at 30 degrees");
    Check(Near(OpticsModel.Airmass(4), 12.4, 0.5), "about 12 at 4 degrees");
    Check(OpticsModel.Airmass(0) > 30 && OpticsModel.Airmass(-5) == OpticsModel.Airmass(0), "finite at and below the horizon");

    Check(Near(OpticsModel.Seeing(1.0, OpticsModel.Airmass(90), 0.6, 1.0), 1.0, 1e-3), "seeing at the zenith is the knob");
    Check(Near(OpticsModel.Seeing(1.0, OpticsModel.Airmass(35), 0.6, 1.0), 1.4, 0.05), "about 1.4 arcsec at 35 degrees");
    Check(Near(OpticsModel.Seeing(1.0, OpticsModel.Airmass(4), 0.6, 1.0), 4.5, 0.2), "about 4.5 arcsec at 4 degrees");
    Check(OpticsModel.Seeing(1.0, 5, 0.6, 0) == 0, "no air, no seeing");

    Check(Near(OpticsModel.CombinedBlur(15, 1.4), Math.Sqrt(15 * 15 + 1.4 * 1.4), 1e-12), "lens and seeing add in quadrature");
    Check(OpticsModel.CombinedBlur(15, 1.4) < 15.1, "level 1: the lens dominates, the air barely adds");
    Check(OpticsModel.CombinedBlur(1, 1.4) > 1.7, "level 3: the air is the limit");

    Check(Near(OpticsModel.ResolutionForLevel(0, 15, 4, 1), 15, 1e-9) && Near(OpticsModel.ResolutionForLevel(0.5, 15, 4, 1), 4, 1e-9)
        && Near(OpticsModel.ResolutionForLevel(1, 15, 4, 1), 1, 1e-9), "stock's three levels land on their lenses");
    double betweenRes = OpticsModel.ResolutionForLevel(0.25, 15, 4, 1);
    Check(betweenRes < 15 && betweenRes > 4, "a modded level falls between");

    var site = new OpticsSite { ResolutionArcsec = 15, ZenithSeeingArcsec = 1, SeeingAirmassPower = 0.6, AirFactor = 1, ElevationDeg = 35, SunElevationDeg = -30 };
    var r = OpticsModel.Assess(site, 10_197_207_000);
    Check(Near(r.BlurOnTargetM, 10_197_207_000 * r.BlurArcsec / OpticsModel.ArcsecPerRadian, 1e-3), "blur on the target is range times the blur angle");
    Check(r.BlurOnTargetM > 700_000, "level 1 at Duna's range blurs over 700 km, wider than Duna itself");
    Check(r.DaySky == 0, "night: no day sky");
    site.SunElevationDeg = 40;
    Check(Near(OpticsModel.Assess(site, 1e6).DaySky, 1, 1e-12), "full day under full air");
    site.AirFactor = 0;
    var space = OpticsModel.Assess(site, 1e6);
    Check(space.SeeingArcsec == 0 && space.DaySky == 0 && space.ExtinctionStops == 0 && space.Airmass == 1,
        "a lens with no air around it pays only for its lens and range");

    // Symmetry from real geometry: a lens on Kerbin and one on Duna, placed point-symmetrically about the midpoint.
    var kerbinC = new DVec3(0, 0, 0);
    var dunaC = new DVec3(2.0e10, 0, 0);
    double tilt = 40 * Math.PI / 180;
    var upA = new DVec3(Math.Cos(tilt), Math.Sin(tilt), 0);
    var upB = new DVec3(-Math.Cos(tilt), -Math.Sin(tilt), 0);
    var lensA = kerbinC + upA * 600_000;
    var lensB = dunaC + upB * 320_000;
    double elevAB = CommandCameraMath.ElevationDeg((lensB - lensA).Normalized, upA);
    double elevBA = CommandCameraMath.ElevationDeg((lensA - lensB).Normalized, upB);
    double sameAir = OpticsModel.AirFactor(6.75);
    OpticsReading Look(double elev, double air, double range) => OpticsModel.Assess(new OpticsSite
        { ResolutionArcsec = 4, ZenithSeeingArcsec = 1, SeeingAirmassPower = 0.6, AirFactor = air, ElevationDeg = elev, SunElevationDeg = -20 }, range);
    var fromKerbin = Look(elevAB, sameAir, (lensB - lensA).Magnitude);
    var fromDuna = Look(elevBA, sameAir, (lensA - lensB).Magnitude);
    Check(elevAB > 40 && elevAB < 60 && Near(elevAB, elevBA, 1e-6), "mirrored lenses see each other at the same elevation");
    Check(Near(fromKerbin.BlurArcsec, fromDuna.BlurArcsec, 1e-9) && Near(fromKerbin.BlurOnTargetM, fromDuna.BlurOnTargetM, 1e-3)
        && Near(fromKerbin.ClarityStops, fromDuna.ClarityStops, 1e-9), "a Kerbin lens and a Duna lens in the same air read each other the same");
    var kerbinAir = Look(elevAB, OpticsModel.AirFactor(101.325), (lensB - lensA).Magnitude);
    var dunaAir = Look(elevBA, OpticsModel.AirFactor(6.75), (lensA - lensB).Magnitude);
    Check(kerbinAir.SeeingArcsec > dunaAir.SeeingArcsec && kerbinAir.ClarityStops < dunaAir.ClarityStops,
        "each in its own air, Kerbin's thicker air makes its view of Duna the worse one");
    var kscAtDuna = new OpticsSite { ResolutionArcsec = 4, ZenithSeeingArcsec = 1, SeeingAirmassPower = 0.6, AirFactor = 1, ElevationDeg = 50, SunElevationDeg = -20 };
    var a1 = OpticsModel.Assess(kscAtDuna, 2.0e10);
    var thinAir = kscAtDuna; thinAir.AirFactor = OpticsModel.AirFactor(6.75);
    Check(OpticsModel.Assess(thinAir, 2.0e10).SeeingArcsec < a1.SeeingArcsec, "Duna's thin air blurs less than Kerbin's");
    Check(OpticsModel.Assess(kscAtDuna, 1000).BlurOnTargetM < 0.03, "a near object stays sharp: 1 km away the blur is a couple of centimetres");

    // The network note's worked example (level 3): night at 45 degrees beats overhead in full day.
    double Score(double res, double range, double elev, double sun) => OpticsModel.Assess(
        new OpticsSite { ResolutionArcsec = res, ZenithSeeingArcsec = 1, SeeingAirmassPower = 0.6, AirFactor = 1, ElevationDeg = elev, SunElevationDeg = sun }, range).ClarityStops;
    Check(Score(1, 133_000, 45, -30) > Score(1, 100_000, 90, 40), "night at 45 degrees beats overhead by day");
    Check(Near(Score(1, 100_000, 90, 40), -0.96, 0.05), "overhead by day scores about -0.96 stops at level 3");

    // Pixels and the skip.
    double pix = OpticsModel.PixelArcsec(30, 576);
    Check(pix > 180 && pix < 200, "a 30 degree view is about 190 arcsec a pixel");
    Check(!OpticsModel.Worthwhile(15 / pix), "a wide view skips the blur entirely");
    Check(OpticsModel.Worthwhile(15 / OpticsModel.PixelArcsec(0.02, 576)), "a zoomed view at level 1 blurs");
    Check(!OpticsModel.Worthwhile(0.99) && OpticsModel.Worthwhile(1.0), "under a pixel of blur does nothing");
    Check(OpticsModel.Ramp(1.0) == 0 && OpticsModel.Ramp(0.5) == 0 && OpticsModel.Ramp(1.0 + OpticsModel.RampPx) == 1 && OpticsModel.Ramp(50) == 1,
        "the effect fades in from nothing at the threshold");
    double rprev = 0; bool rises = true;
    for (double bp = 1.0; bp < 3.0; bp += 0.01) { double rv = OpticsModel.Ramp(bp); rises &= rv >= rprev && rv - rprev < 0.03; rprev = rv; }
    Check(rises, "and rises smoothly, with no step");
    Check(OpticsModel.MaxLevelFor(1024, 576) == 8 && OpticsModel.MaxLevelFor(64, 36) == 4 && OpticsModel.MaxLevelFor(2, 2) == 1,
        "the pyramid goes as deep as the frame allows");

    // Discs and keeping the light.
    Check(Near(OpticsModel.DiscPeak(10, 1), 1, 1e-9), "a disc far wider than the blur keeps its centre");
    Check(OpticsModel.DiscPeak(1, 10) < 0.01, "a speck under a wide blur keeps under a hundredth of its peak");
    Check(Near(OpticsModel.DiscCoverage(0, 3, 2), OpticsModel.DiscPeak(3, 2), 1e-4), "coverage at the centre is the disc's peak");
    Check(Near(OpticsModel.DiscCoverage(100, 100, 1), 0.5, 0.01), "a big disc's blurred edge is half covered");
    Check(OpticsModel.DiscCoverage(110, 100, 1) < 1e-6 && OpticsModel.DiscCoverage(90, 100, 1) > 0.999999, "and falls off within a few sigma");
    double prev = 2;
    bool falls = true;
    for (double rr = 0; rr < 30; rr += 0.5) { double c = OpticsModel.DiscCoverage(rr, 5, 3); falls &= c <= prev + 1e-9; prev = c; }
    Check(falls, "blurred coverage only falls outward");
    // Energy kept: the blurred disc's coverage integrates to the disc's area.
    double area = 0;
    for (double rr = 0.05; rr < 40; rr += 0.1) area += OpticsModel.DiscCoverage(rr, 5, 3) * 2 * Math.PI * rr * 0.1;
    Check(Near(area, Math.PI * 25, Math.PI * 25 * 0.01), "the blur keeps the disc's light");

    Check(OpticsModel.Gain(1, 1, 2.2, 10) == 1, "a resolved body gets no gain");
    Check(Near(OpticsModel.Gain(0.2, 1, 0, 10), 5, 1e-9), "hold 1 restores the surface brightness");
    Check(OpticsModel.Gain(0.2, 0, 0, 10) == 1, "hold 0 keeps the light strictly");
    Check(OpticsModel.Gain(1e-4, 1, 2.2, 10) == 10, "the cap stops a speck becoming a beacon");
    Check(OpticsModel.DayGain(10, 1) == 1 && OpticsModel.DayGain(10, 0) == 10, "no gain in full daylight, where the sky would brighten with it");

    // The blur pyramid matches a target width.
    Check(Near(OpticsModel.PyramidSigma(1), Math.Sqrt(0.75), 1e-12) && Near(OpticsModel.PyramidSigma(2), Math.Sqrt(3.75), 1e-12),
        "pyramid widths match the measured box-down, bilinear-up chain");
    OpticsModel.PyramidPlan(0.4, 8, out int lv0, out double w0);
    Check(lv0 == 0 && w0 > 0 && w0 < 1, "a small blur mixes the sharp layer with level 1");
    foreach (double target in new[] { 0.5, 1.3, 5.0, 17.0, 40.0 })
    {
        OpticsModel.PyramidPlan(target, 8, out int lv, out double share);
        double mixVar = (1 - share) * Math.Pow(OpticsModel.PyramidSigma(lv), 2) + share * Math.Pow(OpticsModel.PyramidSigma(lv + 1), 2);
        Check(Near(Math.Sqrt(mixVar), target, 1e-9), $"pyramid plan reproduces a {target}px blur");
    }
    OpticsModel.PyramidPlan(1e6, 8, out int lvMax, out double wMax);
    Check(lvMax == 7 && wMax == 1, "a blur past the deepest level is capped there");

    // The craft's blur: Airy rings without air, washed out with it.
    var airy = OpticsModel.PsfProfile(1, 0, 4, 401);
    Check(Near(airy[0], 1, 1e-12) && Near(airy[50], 0.5, 0.01), "the Airy core falls to half at half its FWHM");
    int ringAt = 0;
    for (int i = 2; i < airy.Length - 1; i++) if (airy[i] > airy[i - 1] && airy[i] > airy[i + 1]) { ringAt = i; break; }
    Check(ringAt > 0 && Near(ringAt / 100.0, 1.58, 0.05), "a first ring near 1.6 FWHM out");
    var washed = OpticsModel.PsfProfile(1, 3, 4, 401);
    bool monotone = true;
    for (int i = 1; i < washed.Length; i++) monotone &= washed[i] <= washed[i - 1] + 1e-9;
    Check(monotone, "seeing far wider than the lens washes the rings out");
    Check(Near(OpticsModel.BesselJ1(1.0), 0.4400505857, 1e-7) && Near(OpticsModel.BesselJ1(10.0), 0.0434727462, 1e-7), "J1 matches tables");
    Check(Near(OpticsModel.BesselI0e(1.0), 1.2660658778 * Math.Exp(-1), 1e-6) && Near(OpticsModel.BesselI0e(10.0), 2815.716628 * Math.Exp(-10), 1e-6),
        "I0e matches tables");

    OpticsModel.SeeingMotion(15, out double wob1, out _, out double tw1);
    OpticsModel.SeeingMotion(1, out double wob3, out _, out double tw3);
    Check(wob1 > wob3 && tw1 > tw3, "small lenses dance and twinkle more");

    var noise = new SeeingNoise(7);
    double ss = 0;
    int n = 0;
    for (double t = 0; t < 2; t += 0.001) { ss += noise.At(t, 0) * noise.At(t, 0); n++; }
    Check(Near(Math.Sqrt(ss / n), 1, 0.05), "seeing noise has unit RMS");
    Check(Near(noise.At(0.3, 1), noise.At(2.3, 1), 1e-9), "and loops every 2 s");

    var knobs = new OpticsKnobs { ResolutionArcsec = 30, SeeingArcsec = double.NaN, SeeingAirmassPower = double.NaN, BrightnessHold = double.NaN, DaySkyStrength = double.NaN };
    var merged = knobs.Over(OpticsModel.Defaults);
    Check(merged.ResolutionArcsec == 30 && merged.SeeingArcsec == OpticsModel.Defaults.SeeingArcsec, "a host's knob wins; the rest stay default");
    Check(OpticsKnobs.Unset.IsUnset && !knobs.IsUnset && knobs.Valid, "unset knobs read as unset");
    var bad = OpticsKnobs.Unset; bad.BrightnessHold = 2;
    var bad2 = OpticsKnobs.Unset; bad2.ResolutionArcsec = double.PositiveInfinity;
    Check(!bad.Valid && !bad2.Valid, "out of range or infinite knobs are refused");
}

// How fine a detail a lens resolves at its tightest zoom.
{
    var off = new LensLimits { Known = true, FovMinDeg = 0.02, WidthPx = 1024, HeightPx = 576, Knobs = OpticsKnobs.Unset };
    double pixel = 2 * Math.Tan(0.01 * Math.PI / 180) / 576 * OpticsModel.ArcsecPerRadian;
    Check(Near(off.PixelArcsec, pixel, 1e-9) && Near(off.PixelArcsec, 0.125, 1e-3), "one pixel at 0.02 degrees over 576 rows is about 0.125 arcsec");
    Check(off.ResolutionArcsec(0, 90) == off.PixelArcsec && off.ResolutionArcsec(1, 3) == off.PixelArcsec,
        "with the optics off the pixel is the limit, whatever the air");
    Check(double.IsNaN(off.LensArcsec), "with the optics off there is no lens blur");

    var knobs = OpticsModel.Defaults;
    knobs.ResolutionArcsec = 4;
    var on = off;
    on.OpticsOn = true;
    on.Knobs = knobs;
    Check(on.LensArcsec == 4, "the lens blur is the knob's");
    Check(Near(on.ResolutionArcsec(0, 10), 4, 1e-12), "in space the lens alone limits, toward any elevation");
    Check(Near(on.ResolutionArcsec(1, 90), Math.Sqrt(16 + 1), 1e-9), "from Kerbin's sea level at the zenith lens and seeing add in quadrature");
    double lowBlur = OpticsModel.Assess(new OpticsSite
    {
        ResolutionArcsec = 4, ZenithSeeingArcsec = knobs.SeeingArcsec, SeeingAirmassPower = knobs.SeeingAirmassPower,
        AirFactor = 1, ElevationDeg = 5,
    }, 1e6).BlurArcsec;
    Check(on.ResolutionArcsec(1, 5) == lowBlur && lowBlur > on.ResolutionArcsec(1, 90), "low in the sky it is the blur the optics draw, and worse");
    Check(on.ResolutionArcsec(0.5, 5) < lowBlur, "thinner air blurs less");

    var wide = on;
    wide.FovMinDeg = 1;
    wide.Knobs.ResolutionArcsec = 1;
    Check(Near(wide.ResolutionArcsec(0, 90), wide.PixelArcsec, 1e-12) && wide.PixelArcsec > 6,
        "a lens finer than its widest pixel is held to the pixel");
    var sharp = on;
    sharp.Knobs.ResolutionArcsec = 0;
    Check(sharp.ResolutionArcsec(0, 90) == sharp.PixelArcsec, "a perfect lens in space is held to the pixel");

    var unknown = LensLimits.Unknown;
    unknown.OpticsOn = true;
    Check(double.IsNaN(unknown.PixelArcsec) && double.IsNaN(unknown.ResolutionArcsec(1, 45)) && double.IsNaN(unknown.LensArcsec),
        "an unknown lens resolves nothing");
}

{
    CommandCameraMath.SplitSpawnPath("Facility/End09/SpawnPoint", out string model, out string inModel);
    Check(model == "Facility" && inModel == "End09/SpawnPoint", "runway spawn path splits into its model and the path inside it");
    CommandCameraMath.SplitSpawnPath("Facility/LaunchPad_spawn", out model, out inModel);
    Check(model == "Facility" && inModel == "LaunchPad_spawn", "launch pad spawn path splits at the first slash");
    CommandCameraMath.SplitSpawnPath("Facility", out model, out inModel);
    Check(model == "Facility" && inModel == "", "a path naming only the model has nothing inside it");
    CommandCameraMath.SplitSpawnPath(null, out model, out inModel);
    Check(model == "" && inModel == "", "no path splits into nothing");

    Check(CommandCameraMath.FacilityLevelIndex(0, 3) == 0, "level 0 is the first model");
    Check(CommandCameraMath.FacilityLevelIndex(0.5, 3) == 1, "level 0.5 of three is the middle model");
    Check(CommandCameraMath.FacilityLevelIndex(1, 3) == 2, "level 1 is the last model, not past it");
    Check(CommandCameraMath.FacilityLevelIndex(1.7, 3) == 2 && CommandCameraMath.FacilityLevelIndex(-1, 3) == 0, "out of range levels clamp");
    Check(CommandCameraMath.FacilityLevelIndex(double.NaN, 3) == 0 && CommandCameraMath.FacilityLevelIndex(0.5, 0) == 0, "no level or no models is the first");
}

{
    var steps = new[]
    {
        new SpaceLensStep("scienceTech", 0.5),
        new SpaceLensStep("advScienceTech", 0.2),
        new SpaceLensStep("experimentalScience", 0.05),
    };
    Check(SpaceLens.BestStep(steps, id => false) == -1, "space lens: nothing researched gives no step");
    Check(SpaceLens.BestStep(steps, id => true) == 2, "space lens: everything researched gives the finest step");
    Check(SpaceLens.BestStep(steps, id => id == "scienceTech") == 0, "space lens: one node researched gives its step");
    Check(SpaceLens.BestStep(steps, id => id != "advScienceTech") == 2, "space lens: a later node researched past a skipped one still counts");
    var dropped = (SpaceLensStep[])steps.Clone();
    dropped[2].Arcsec = 0;
    Check(SpaceLens.BestStep(dropped, id => true) == 1, "space lens: a step with a lens of 0 is dropped");
    dropped[1].TechId = "";
    Check(SpaceLens.BestStep(dropped, id => true) == 0, "space lens: a step with no node is dropped");
    Check(SpaceLens.BestStep(null, id => true) == -1 && SpaceLens.BestStep(steps, null) == -1, "space lens: no steps or no research gives nothing");

    /* Kerbin: 600 km radius, 70 km of air. A craft on an elliptical orbit, placed at two times. */
    const double airR = 600000, airDepth = 70000;
    double periapsisAt = airR + 60000, apoapsisAt = airR + 250000;
    Check(!SpaceLens.OutsideAir(periapsisAt, airR, true, airDepth), "space lens: a craft dipping to 60 km is inside Kerbin's air at that time");
    Check(SpaceLens.OutsideAir(apoapsisAt, airR, true, airDepth), "space lens: the same craft at 250 km later is outside it");
    Check(SpaceLens.OutsideAir(airR + airDepth, airR, true, airDepth), "space lens: the edge of the air counts as outside");
    Check(SpaceLens.OutsideAir(200000 + 10, 200000, false, 0), "space lens: a craft landed on an airless body is outside any air");

    Check(SpaceLens.Beats(0.5, 1.0) && !SpaceLens.Beats(1.0, 1.0) && !SpaceLens.Beats(2.0, 1.0), "space lens: taken only when finer than the ground lens");
    Check(!SpaceLens.Beats(double.NaN, 1.0) && !SpaceLens.Beats(0, 1.0) && SpaceLens.Beats(0.5, double.NaN), "space lens: no lens never wins, any lens beats none");

    double fov = SpaceLens.FovMin(0.02, 0.05, 576);
    double pix = OpticsModel.PixelArcsec(fov, 576);
    Check(Near(0.05 / pix, OpticsModel.FloorBlurPx, 1e-6), "space lens: the zoom stops where the lens spans FloorBlurPx");
    Check(Near(fov, 0.004, 1e-4), "space lens: the top step zooms to about 0.004 degrees");
    Check(SpaceLens.FovMin(0.02, 1.0, 576) == 0.02, "space lens: a coarse lens never widens the usual limit");
    Check(SpaceLens.FovMin(0.02, 0.001, 576) == SpaceLens.NarrowestFovDeg, "space lens: a near-perfect lens stops at the narrowest field");
    Check(SpaceLens.FovMin(0.02, double.NaN, 576) == 0.02 && SpaceLens.FovMin(0.02, 0.05, 0) == 0.02, "space lens: no lens or no frame keeps the usual limit");

    double firstStep = SpaceLens.ShownFovMin(0.02, 0.5, 576);
    Check(SpaceLens.FovMin(0.02, 0.5, 576) == 0.02 && firstStep > 0.039 && firstStep < 0.041,
        $"space lens: the first step shows where the optics floor really stops it, near 0.04 degrees ({firstStep:F4})");
    Check(Near(firstStep, Math.Max(SpaceLens.FovMin(0.02, 0.5, 576), OpticsModel.OpticsFovFloor(0, 0.5, 576)), 1e-12),
        "space lens: the shown limit is the rig's, the lens limit or the optics floor whichever is wider");
    Check(Near(SpaceLens.ShownFovMin(0.02, 0.05, 576), SpaceLens.FovMin(0.02, 0.05, 576), 1e-12), "space lens: a fine step's shown limit is its own");
    Check(SpaceLens.ShownFovMin(0.02, 0.001, 576) == SpaceLens.NarrowestFovDeg, "space lens: a near-perfect lens shows the narrowest field");
}

{
    /* LookRotation: Unity's convention (z along forward, y the up squared to it), worked in double. */
    DVec3 Turn(double qx, double qy, double qz, double qw, DVec3 v)
    {
        var u = new DVec3(qx, qy, qz);
        var t = DVec3.Cross(u, v) * 2;
        return v + t * qw + DVec3.Cross(u, t);
    }
    var rng = new Random(7);
    bool allAxes = true, allUnit = true;
    double worstFloat = 0;
    for (int i = 0; i < 2000; i++)
    {
        var f = new DVec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1).Normalized;
        var up0 = new DVec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
        CommandCameraMath.LookRotation(f, up0, out double qx, out double qy, out double qz, out double qw);
        var upSquared = (up0 - f * DVec3.Dot(up0, f)).Normalized;
        if (!VNear(Turn(qx, qy, qz, qw, new DVec3(0, 0, 1)), f, 1e-12) || !VNear(Turn(qx, qy, qz, qw, new DVec3(0, 1, 0)), upSquared, 1e-12)
            || !VNear(Turn(qx, qy, qz, qw, new DVec3(1, 0, 0)), DVec3.Cross(upSquared, f), 1e-12))
            allAxes = false;
        if (!Near(qx * qx + qy * qy + qz * qz + qw * qw, 1, 1e-12)) allUnit = false;
        var fq = Turn((float)qx, (float)qy, (float)qz, (float)qw, new DVec3(0, 0, 1));
        double err = Math.Atan2(DVec3.Cross(fq, f).Magnitude, DVec3.Dot(fq, f));
        if (err > worstFloat) worstFloat = err;
    }
    Check(allAxes, "look rotation: forward, up and right land on Unity's axes");
    Check(allUnit, "look rotation: a unit quaternion");
    Check(worstFloat < 1.5e-7, $"look rotation: one float rounding turns the view by at most a pixel at 0.004 degrees (worst {worstFloat:G3} rad)");
    CommandCameraMath.LookRotation(new DVec3(0, 1, 0), new DVec3(0, 1, 0), out double dx, out double dy, out double dz, out double dw);
    Check(VNear(Turn(dx, dy, dz, dw, new DVec3(0, 0, 1)), new DVec3(0, 1, 0), 1e-12), "look rotation: an up along the view still looks along it");
    CommandCameraMath.LookRotation(new DVec3(0, 0, 1), new DVec3(0, 1, 0), out double ix0, out double iy0, out double iz0, out double iw0);
    Check(Near(iw0, 1, 1e-15) && ix0 == 0 && iy0 == 0 && iz0 == 0, "look rotation: looking along z with y up is no turn");
    CommandCameraMath.LookRotation(new DVec3(0, 0, -1), new DVec3(0, 1, 0), out double bx, out double by, out double bz, out double bw);
    Check(Near(Math.Abs(by), 1, 1e-15), "look rotation: looking back is a half turn about y");

    /* The stand-in model's distance: 50 km near the origin, farther for a lens far from it at a narrow field. */
    double perPx = CommandCameraMath.PixelSizeAtUnit(0.004, 576);
    Check(CommandCameraMath.ImpostorDistance(1e9, 50000, perPx, 10, 4) == 50000, "proxy: a lens at the origin keeps 50 km");
    double far = CommandCameraMath.ImpostorDistance(1e9, 50000, perPx, 1e6, 4);
    Check(CommandCameraMath.FloatEpsilon * 1e6 / far <= perPx / 4 * 1.0001, "proxy: a lens 1000 km out stands it where its float step is a quarter pixel");
    Check(CommandCameraMath.ImpostorDistance(2e5, 50000, perPx, 1e6, 4) == 2e5, "proxy: never beyond the craft itself");
    Check(CommandCameraMath.ImpostorDistance(1e9, 50000, CommandCameraMath.PixelSizeAtUnit(5, 576), 1e6, 4) == 50000, "proxy: a wide field keeps 50 km");
}

{
    /* A Surface aim at a body drawn at its past instant: on the drawn body, 8 minutes of spin back, at the same height. */
    var aimSpin = new DVec3(0, -2.9e-4, 0);
    var liveCentre = new DVec3(5e9, 1e8, -3e9);
    var drawnCentre = liveCentre + new DVec3(-3.6e9, 0, 2e6);
    var surface = new DVec3(320000, 0, 0);
    var aimed = CommandCameraMath.CarriedByBody(surface, drawnCentre, aimSpin, -480);
    Check(Near((aimed - drawnCentre).Magnitude, 320000, 1e-6), "surface aim: stays on the drawn body at its height");
    Check(Near(CommandCameraMath.AngleBetween(aimed - drawnCentre, surface), 2.9e-4 * 480, 1e-9), "surface aim: turned back by the body's spin over its light time");
    Check(VNear(CommandCameraMath.ToBodyFrameNow(aimed, drawnCentre, liveCentre, aimSpin, -480), liveCentre + surface, 1e-6), "surface aim: undoing the carry lands on the live ground point");
    Check(VNear(CommandCameraMath.CarriedByBody(surface, drawnCentre, aimSpin, 0), drawnCentre + surface, 1e-9), "surface aim: a body with no light time keeps the ground point");
}

/* The drawn-light line, for a craft and for an aim at no craft. */
Check(CommandCameraMath.DrawnLightState(false, true, 45000) == -1, "no light speed reads off");
Check(CommandCameraMath.DrawnLightState(true, false, 45000) == 0, "a place not drawn reads none");
Check(CommandCameraMath.DrawnLightState(true, true, double.NaN) == 0, "a place with no light time reads none");
Check(CommandCameraMath.DrawnLightState(true, true, 0) == 1, "a place drawn as it is now reads live");
Check(CommandCameraMath.DrawnLightState(true, true, 45000) == 2, "a Sun aim 45000 s of light away reads past");
Check(CommandCameraMath.DrawnLightName(2) == "past" && CommandCameraMath.DrawnLightName(1) == "live"
    && CommandCameraMath.DrawnLightName(0) == "none" && CommandCameraMath.DrawnLightName(-1) == "off", "states name as the craft line does");
Check(CommandCameraMath.AimLightLogDue(2, -1, true), "the first aim under a light speed is said");
Check(CommandCameraMath.AimLightLogDue(2, 2, true), "a new aim in the same state is said again, with its own light time");
Check(!CommandCameraMath.AimLightLogDue(2, 2, false), "the same aim in the same state is said once");
Check(CommandCameraMath.AimLightLogDue(1, 2, false), "a change of state is said");
Check(!CommandCameraMath.AimLightLogDue(-1, -1, true), "a new aim with no light speed says nothing");
Check(CommandCameraMath.AimLightLogDue(-1, 2, false), "a light speed going away is said");

/* How far a station looks for buildings along its sight line. */
Check(Near(CommandCameraMath.SceneryRayM(430000, -0.1, 5000, 300), 5000, 1e-9), "a far craft below the skyline: the whole reach is tested");
Check(Near(CommandCameraMath.SceneryRayM(330000, Math.Sin(3 * Math.PI / 180), 5000, 300), 5000, 1e-9), "a craft rising 3 degrees: the line is under the ceiling across the reach");
Check(Near(CommandCameraMath.SceneryRayM(150000, Math.Sin(30 * Math.PI / 180), 5000, 300), 600, 1e-9), "a craft 30 degrees up: the line clears every roof after 600 m");
Check(Near(CommandCameraMath.SceneryRayM(500, 0.01, 5000, 300), 250, 1e-9), "a craft on the pad 500 m off: only the near half counts");
Check(CommandCameraMath.SceneryRayM(1.5, 0, 5000, 300) == 0, "a craft at the lens: nothing to test");
Check(CommandCameraMath.SceneryRayM(double.NaN, 0, 5000, 300) == 0, "no range: nothing to test");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
