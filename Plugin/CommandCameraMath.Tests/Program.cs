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
double down = CommandCameraMath.SmoothFov(20, 0.02, 0.016, 0.4, 1.0);
Check(down < 20 && down > 0.02, "one frame moves part of the way");

// Occlusion: gonogo's sphere rule.
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

// Framing fit at the widest field.
Check(CommandCameraMath.FramingFits(1000, 1e6, 30), "fit: 1 km at 1000 km fits a 30 degree view");
Check(!CommandCameraMath.FramingFits(400000, 1e6, 30), "fit: 400 km at 1000 km (23.6 degrees half-angle) does not fit 30 degrees");
Check(CommandCameraMath.FramingFits(Math.Sin(15 * Math.PI / 180) * 1e6 - 1, 1e6, 30), "fit: just inside the edge fits");
Check(!CommandCameraMath.FramingFits(10, 5, 30), "fit: lens inside the sphere never fits");
Check(CommandCameraMath.FramingFits(0, 5, 30), "fit: no radius always fits");

// Blank reasons: fail closed, in precedence order.
Check(CommandCameraMath.BlankReason(false, true, "x") == null, "blank: standalone never blanks");
Check(CommandCameraMath.BlankReason(true, true, "Custom") == CommandCameraMath.BlankNotResponding,
    "blank: a silent authority beats everything");
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

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
