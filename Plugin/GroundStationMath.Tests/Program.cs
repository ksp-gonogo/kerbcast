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
Check(VNear(GroundStationMath.SurfacePosition(origin, ix, iy, iz, R, 0, 0, 0), new DVec3(R, 0, 0), 1e-6),
    "lat 0 lon 0 is +X at the radius");
Check(VNear(GroundStationMath.SurfacePosition(origin, ix, iy, iz, R, 90, 0, 0), new DVec3(0, R, 0), 1e-6),
    "north pole is Unity +Y");
Check(VNear(GroundStationMath.SurfacePosition(origin, ix, iy, iz, R, 0, 90, 0), new DVec3(0, 0, R), 1e-6),
    "lon 90 is Unity +Z (KSP local +Y swizzled)");
Check(VNear(GroundStationMath.SurfacePosition(origin, ix, iy, iz, R, 0, 0, 70), new DVec3(R + 70, 0, 0), 1e-6),
    "altitude adds along the normal");
var centre = new DVec3(1000, -2000, 3000);
Check(VNear(GroundStationMath.SurfacePosition(centre, ix, iy, iz, R, 0, 0, 0), new DVec3(R + 1000, -2000, 3000), 1e-6),
    "body centre offsets the result (floating origin moves the body, not the maths)");
// A body rotated 90 degrees about KSP's z (its spin axis): local +X now points
// along world +Y (z-up), which is Unity +Z.
var rx = new DVec3(0, 1, 0);
var ry = new DVec3(-1, 0, 0);
Check(VNear(GroundStationMath.SurfacePosition(origin, rx, ry, iz, R, 0, 0, 0), new DVec3(0, 0, R), 1e-6),
    "body rotation carries the station round with it");
// KSC-ish coordinates land on the sphere at radius + alt.
var kscPos = GroundStationMath.SurfacePosition(origin, ix, iy, iz, R, -0.0972, -74.5577, 70);
Check(Near(kscPos.Magnitude, R + 70, 1e-6), "KSC-ish point sits at radius + alt");

// AimBasis: forward points at the target, up stays in the vertical plane and
// orthogonal to forward.
var station = new DVec3(0, R, 0);
var stationUp = new DVec3(0, 1, 0);
var north = new DVec3(0, 0, 1);
GroundStationMath.AimBasis(station, station + new DVec3(1000, 0, 0), stationUp, north, out var f1, out var u1);
Check(VNear(f1, new DVec3(1, 0, 0), 1e-9), "level target: forward is horizontal");
Check(VNear(u1, stationUp, 1e-9), "level target: up is local vertical (horizon level)");
GroundStationMath.AimBasis(station, station + new DVec3(1000, 1000, 0), stationUp, north, out var f2, out var u2);
Check(Near(DVec3.Dot(f2, u2), 0, 1e-9), "climbing target: up orthogonal to forward");
Check(u2.Y > 0, "climbing target: up still points skyward");
Check(Near(GroundStationMath.ElevationDeg(f2, stationUp), 45, 1e-9), "climbing target elevation is 45 degrees");
GroundStationMath.AimBasis(station, station + new DVec3(0, 5000, 0), stationUp, north, out var f3, out var u3);
Check(VNear(f3, stationUp, 1e-9), "overhead target: forward is straight up");
Check(VNear(u3, north, 1e-9), "overhead target: up falls back to north instead of NaN");

// Angular size and FoV-for-fraction.
Check(Near(GroundStationMath.AngularDiameterDeg(15, 600), 2.0 * Math.Asin(15.0 / 600.0) * 180 / Math.PI, 1e-9),
    "angular diameter is 2 asin(r/d)");
Check(GroundStationMath.AngularDiameterDeg(15, 10) == 180.0, "inside the sphere subtends 180");
double padFov = GroundStationMath.FovForFraction(15, 600, 0.3, 0.02, 30);
Check(padFov > 9 && padFov < 10, $"pad framing: 15 m craft at 600 m is about 9.5 degrees (got {padFov:F2})");
double screenFrac = Math.Tan(GroundStationMath.AngularDiameterDeg(15, 600) * 0.5 * Math.PI / 180)
    / Math.Tan(padFov * 0.5 * Math.PI / 180);
Check(Near(screenFrac, 0.3, 1e-9), "the fraction is exact on screen (tan-ratio)");
double fov30k = GroundStationMath.FovForFraction(15, 30000, 0.3, 0.02, 30);
Check(fov30k > 0.18 && fov30k < 0.2, $"30 km: about 0.19 degrees (got {fov30k:F3})");
Check(GroundStationMath.FovForFraction(15, 5e6, 0.3, 0.02, 30) == 0.02, "deep range clamps to the floor");
Check(GroundStationMath.FovForFraction(15, 20, 0.3, 0.02, 30) == 30, "inside the craft clamps to the ceiling");
Check(GroundStationMath.FovForFraction(15, 10, 0.3, 0.02, 30) == 30, "distance under radius returns the ceiling");

// SmoothFov: log-space ease, snap on long gaps.
double s = GroundStationMath.SmoothFov(10, 1, 0.4, 0.4, 1.0);
double expected = Math.Exp(Math.Log(10) + (1 - Math.Exp(-1)) * (Math.Log(1) - Math.Log(10)));
Check(Near(s, expected, 1e-12), "one tau covers 63% of the way in log space");
Check(GroundStationMath.SmoothFov(10, 1, 2.0, 0.4, 1.0) == 1, "a gap past snapAfter snaps to target");
Check(GroundStationMath.SmoothFov(0, 5, 0.01, 0.4, 1.0) == 5, "uninitialised current snaps");
double down = GroundStationMath.SmoothFov(20, 0.02, 0.016, 0.4, 1.0);
Check(down < 20 && down > 0.02, "one frame moves part of the way");

// Occlusion: gonogo's sphere rule.
var planet = new DVec3(0, 0, 0);
var onSurface = new DVec3(0, R + 70, 0);
Check(GroundStationMath.SegmentClearOfSphere(onSurface, new DVec3(0, R + 80000, 0), planet, R),
    "craft overhead is visible");
Check(!GroundStationMath.SegmentClearOfSphere(onSurface, new DVec3(0, -(R + 80000), 0), planet, R),
    "craft on the far side is hidden");
Check(!GroundStationMath.SegmentClearOfSphere(onSurface, new DVec3(R + 80000, 0, 0), planet, R),
    "craft 90 degrees round at 80 km altitude is below the horizon");
Check(GroundStationMath.SegmentClearOfSphere(onSurface, new DVec3(1000, R + 70, 0), planet, R),
    "craft on the pad 1 km away is visible (the segment barely dips)");
Check(GroundStationMath.SegmentClearOfSphere(onSurface, onSurface, planet, R), "zero-length segment outside is clear");
Check(GroundStationMath.SegmentClearOfSphere(onSurface, new DVec3(0, -1e9, 0), planet, 0),
    "a body with no radius never blocks");

// Bounding radius.
var parts = new List<DVec3> { new DVec3(0, 10, 0), new DVec3(0, -5, 0), new DVec3(2, 0, 0) };
Check(Near(GroundStationMath.BoundingRadius(parts, origin, 1.5, 2), 11.5, 1e-9), "farthest part plus pad");
Check(GroundStationMath.BoundingRadius(new List<DVec3>(), origin, 0.5, 2) == 2, "never below the minimum");
Check(GroundStationMath.BoundingRadius(null, origin, 0.5, 2) == 2, "null part list gives the minimum");

// Clip plan on a split (OpenGL/Metal) stack: near 0.21..397, far 397..750 km.
const float nN = 0.21f, nF = 397f, fN = 397f, fF = 750000f;
var pad = GroundStationMath.PlanClips(600, 15, false, true, false, nN, nF, fN, fF);
Check(!pad.Split && !pad.MidActive && pad.FarRender && pad.FarNear == fN && pad.FarFar == fF,
    "pad range: native split, no mid layer");
var low = GroundStationMath.PlanClips(5000, 15, false, true, false, nN, nF, fN, fF);
Check(low.Split && low.MidActive && low.FarRender, "5 km: mid layer on");
Check(Near(low.FarNear, 5000 - 80, 1e-3) && low.MidNear == fN && Near(low.MidFar, low.FarNear, 1e-3),
    "5 km: far starts just short of the craft and mid meets it");
Check(low.FarFar == fF && low.NearFar == nF, "5 km: far keeps 750 km, near untouched");
double depthRatioBefore = fF / fN, depthRatioAfter = fF / low.FarNear;
Check(depthRatioAfter < depthRatioBefore / 10, "the craft's depth range gets over 10x the precision");
var deep = GroundStationMath.PlanClips(2e6, 15, false, true, true, nN, nF, fN, fF);
Check(Near(deep.FarFar, 2e6 + 80, 1) && Near(deep.FarNear, 2e6 - 80, 1), "past 750 km the far plane follows the craft");
// Hysteresis: 1.5x..2x of the native near plane keeps whatever state it had.
double between = fN * 1.75 + 80;
Check(GroundStationMath.PlanClips(between, 15, false, true, true, nN, nF, fN, fF).Split, "hysteresis keeps split on");
Check(!GroundStationMath.PlanClips(between, 15, false, true, false, nN, nF, fN, fF).Split, "hysteresis keeps split off");
// Occluded: far skipped, mid stops short of the craft.
var hidden = GroundStationMath.PlanClips(900000, 15, true, true, true, nN, nF, fN, fF);
Check(!hidden.FarRender && hidden.MidActive && hidden.MidFar == fF, "occluded beyond 750 km: terrain only, craft never drawn");
var hiddenNear = GroundStationMath.PlanClips(100000, 15, true, true, true, nN, nF, fN, fF);
Check(!hiddenNear.FarRender && Near(hiddenNear.MidFar, 100000 - 80, 1e-3), "occluded: mid stops short of the craft");
// D3D11 single-camera stack.
var single = GroundStationMath.PlanClips(5000, 15, false, false, false, nN, fF, 0, 0);
Check(!single.FarRender && !single.MidActive && single.NearFar == fF, "single stack: native range");
var singleDeep = GroundStationMath.PlanClips(2e6, 15, false, false, false, nN, fF, 0, 0);
Check(Near(singleDeep.NearFar, 2e6 + 80, 1), "single stack: far plane follows past 750 km");
var singleHidden = GroundStationMath.PlanClips(2e6, 15, true, false, false, nN, fF, 0, 0);
Check(Near(singleHidden.NearFar, 2e6 - 80, 1), "single stack occluded: stops short of the craft");

// Launch-site stand-off: 500 m on a Kerbin-sized sphere from the KSC pad.
const double kerbinR = 600000;
GroundStationMath.Destination(-0.0972, -74.5577, 180, 500, kerbinR, out double sLat, out double sLon);
Check(Near(sLon, -74.5577, 1e-9) && Near((-0.0972 - sLat) * Math.PI / 180 * kerbinR, 500, 1e-6),
    "due south: same longitude, 500 m of latitude");
GroundStationMath.Destination(0, -74.5577, 90, 500, kerbinR, out double eLat, out double eLon);
Check(Near(eLat, 0, 1e-12) && Near((eLon + 74.5577) * Math.PI / 180 * kerbinR, 500, 1e-6),
    "due east on the equator: 500 m of longitude");
GroundStationMath.Destination(-0.0972, -74.5577, 37, 500, kerbinR, out double aLat, out double aLon);
var padPos = GroundStationMath.SurfacePosition(new DVec3(0, 0, 0), new DVec3(1, 0, 0), new DVec3(0, 1, 0), new DVec3(0, 0, 1),
    kerbinR, -0.0972, -74.5577, 0);
var lensPos = GroundStationMath.SurfacePosition(new DVec3(0, 0, 0), new DVec3(1, 0, 0), new DVec3(0, 1, 0), new DVec3(0, 0, 1),
    kerbinR, aLat, aLon, 0);
Check(Near((lensPos - padPos).Magnitude, 500, 0.01), "any bearing: the lens stands 500 m from the pad");
GroundStationMath.Destination(10, 179.9999, 90, 500, kerbinR, out _, out double wrapLon);
Check(wrapLon > -180 && wrapLon < -179.9, "longitude wraps across the antimeridian");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
