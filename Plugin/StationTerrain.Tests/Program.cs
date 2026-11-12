using System;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}
bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;

const double R = 600000.0; // Kerbin
const int Verts = 15; // PQS.cacheSideVertCount

/* The Low terrain preset the Deck runs: Kerbin minDistance 4, maxSubdivision 8, minLevel clamped up to 2. */
const double LowDetail = 4.0;
const int LowMin = 2, LowMax = 8;

Check(Near(StationTerrainMath.SubdivideThresholdM(R, LowDetail, 1.0, 0), 2400000, 1e-6), "level 0 threshold is radius times minDistance");
Check(Near(StationTerrainMath.SubdivideThresholdM(R, LowDetail, 1.0, 3), 300000, 1e-6), "each level halves the threshold");
Check(Near(StationTerrainMath.AngularIntervalM(R, 0), 2 * Math.PI, 1e-6), "stock's angular interval is a few metres at the root");
Check(StationTerrainMath.AngularIntervalM(R, 8) < 0.1, "and next to nothing at the finest level");
Check(Near(StationTerrainMath.GcDistM(0.1, R, -500, 0), 0.1 * R * 1.3 + 500, 1e-9), "gcDist stretches the arc by 1.3 and adds the height either side of the ground");

/* The 10 Nov setup: craft 100 km up, 351 km in a straight line from a KSC lens. */
double h = 100000, d = 351000;
double cos = (R * R + (R + h) * (R + h) - d * d) / (2 * R * (R + h));
double angle = Math.Acos(cos);
Check(Near(angle * R / 1000, 315, 2), "the setup puts the craft about 315 km round the sphere from the station");
int far = StationTerrainMath.StockLevel(angle, R, h, LowDetail, 1.0, LowMin, LowMax);
Check(far == 3, $"stock builds the station's ground at level 3 for that craft (got {far})");
Check(Near(StationTerrainMath.VertexSpacingM(R, 3, Verts), 8415, 5), "level 3 vertices are about 8.4 km apart");
int lens = StationTerrainMath.LevelForLens(R, Verts, 5.0, LowMax);
Check(lens == 5, $"a lens 5 m up needs level 5, vertices inside its 2.4 km horizon (got {lens})");
Check(StationTerrainMath.Coarse(far, true, lens), "so the setup's terrain is coarse for the lens");

int home = StationTerrainMath.StockLevel(0, R, 2, LowDetail, 1.0, LowMin, LowMax);
Check(home == LowMax, $"a craft at the station gets the finest level (got {home})");
Check(!StationTerrainMath.Coarse(home, true, lens), "and that is not coarse");
Check(StationTerrainMath.Coarse(home, false, lens), "ground not drawn at all counts as coarse");

/* Where the switch falls on the Low preset: level 4 below gcDist 150 km, a landed craft about 115 km off. */
int near = StationTerrainMath.StockLevel(40000 / R, R, 0, LowDetail, 1.0, LowMin, LowMax);
Check(StationTerrainMath.StockLevel(110000 / R, R, 0, LowDetail, 1.0, LowMin, LowMax) == lens, "a landed craft 110 km away still leaves level 5");
Check(near >= lens, $"a landed craft 40 km away keeps the station's ground fine enough (level {near})");
int hundred = StationTerrainMath.StockLevel(120000 / R, R, 0, LowDetail, 1.0, LowMin, LowMax);
Check(hundred < lens, $"a landed craft 120 km away leaves it coarse (level {hundred})");

/* The stock speed cap bounds the predicted level like maxLevel does. */
Check(StationTerrainMath.StockLevel(0, R, 0, LowDetail, 1.0, LowMin, 4) == 4, "the predicted level never passes the cap given");
Check(StationTerrainMath.StockLevel(Math.PI, R, 0, LowDetail, 1.0, LowMin, LowMax) == LowMin, "the far side of the sphere sits at minLevel");

/* A taller lens sees farther, so needs less. */
Check(StationTerrainMath.LevelForLens(R, Verts, 2000, LowMax) < lens, "a lens on a 2 km peak needs a coarser level than one on the ground");
Check(StationTerrainMath.LevelForLens(R, Verts, 0, LowMax) <= LowMax, "a lens at the ground never asks past the finest level");
Check(Near(StationTerrainMath.HorizonM(R, 5), Math.Sqrt(2 * R * 5 + 25), 1e-9), "horizon over a smooth sphere");
Check(StationTerrainMath.HorizonM(R, -3) == 0, "a lens below the ground sees no horizon");

/* Angles survive rounding past one. */
Check(StationTerrainMath.AngleRad(1, 0, 0, 1.0000000001, 0, 0) == 0, "a dot just over one is clamped");
Check(Near(StationTerrainMath.AngleRad(1, 0, 0, 0, 1, 0), Math.PI / 2, 1e-12), "perpendicular is a right angle");

/* Ring points sit the asked angle away from the station, spread round it. */
double ring = 20000 / R;
for (int k = 0; k < 8; k++)
{
    StationTerrainMath.RingPoint(0, 1, 0, ring, k, 8, out double x, out double y, out double z);
    Check(Near(Math.Sqrt(x * x + y * y + z * z), 1, 1e-12) && Near(StationTerrainMath.AngleRad(0, 1, 0, x, y, z), ring, 1e-9),
        $"ring point {k} is a unit vector 20 km round from the pole");
}
StationTerrainMath.RingPoint(1, 0, 0, ring, 0, 4, out double ax, out double ay, out double az);
StationTerrainMath.RingPoint(1, 0, 0, ring, 2, 4, out double bx, out double by, out double bz);
Check(StationTerrainMath.AngleRad(ax, ay, az, bx, by, bz) > 1.9 * ring, "opposite ring points lie on opposite sides");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
