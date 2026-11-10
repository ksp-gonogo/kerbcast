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

{
    /* KSC at Kerbin looking at Duna: 20 Gm away against a 750 km far plane. */
    const double DunaR = 320000, DunaMax = 327000, FarClip = 750000;
    Check(!PictureBoundary.TerrainInReach(2e10, DunaMax, FarClip), "terrain reach: a lens a planet away cannot draw the terrain");
    Check(PictureBoundary.TerrainInReach(DunaR + 1000, DunaMax, FarClip), "terrain reach: a lens on the ground can");
    Check(PictureBoundary.TerrainInReach(DunaMax + FarClip, DunaMax, FarClip), "terrain reach: at the far plane exactly, the highest terrain can still draw");
    Check(!PictureBoundary.TerrainInReach(DunaMax + FarClip + 1, DunaMax, FarClip), "terrain reach: a metre past it, none can");
    Check(PictureBoundary.TerrainInReach(2e10, DunaMax, 3e10), "terrain reach: a far plane stretched past the planet reaches it, so the camera must hide it");
    Check(!PictureBoundary.TerrainInReach(double.NaN, DunaMax, FarClip), "terrain reach: an unreadable distance is out of reach");
}

{
    Check(Math.Abs(PictureBoundary.SpinBackS(1.2e7, 6e5, 3e8) + 1.14e7 / 3e8) < 1e-12, "spin back: from the Mun, the distance to the near surface over c");
    Check(PictureBoundary.SpinBackS(6e5, 6e5, 3e8) == 0, "spin back: a lens on the ground sees the ground under it unmoved");
    Check(PictureBoundary.SpinBackS(5e5, 6e5, 3e8) == 0, "spin back: a lens below the surface does not spin the body forward");
    Check(PictureBoundary.SpinBackS(1e9, 6e5, 3e8) < PictureBoundary.SpinBackS(1e8, 6e5, 3e8), "spin back: farther, further back");
    Check(PictureBoundary.SpinBackS(1e9, 6e5, 0) == 0, "spin back: no spin back with no light speed");
    Check(PictureBoundary.SpinBackS(1e7, 6e5, 3e5) == 1000 * PictureBoundary.SpinBackS(1e7, 6e5, 3e8), "spin back: grows as the light speed drops");
}

{
    var sun = new DVec3(0.6, 0, 0.8);
    var spin = new DVec3(0, -2.9e-4, 0);
    var present = DQuat.FromRotationVector(new DVec3(0.3, 1.1, -0.4));
    var turnedBack = (DQuat.FromRotationVector(spin * -480) * present).Normalized;
    var stored = PictureBoundary.LightInBodyAxes(present, sun);
    var fresh = PictureBoundary.LightInBodyAxes(turnedBack, sun);
    Check(Near(PictureBoundary.LightInBodyAxes(DQuat.Identity, sun), sun, 1e-12), "light: unturned axes keep the direction");
    Check(Near(turnedBack.Rotate(fresh), sun, 1e-9), "light: written for the turned axes it lands back on the Sun's direction in world space");
    Check((turnedBack.Rotate(stored) - sun).Magnitude > 0.1, "light: the value left from the present rotation misses it by the spin");
    Check(Math.Abs(stored.Magnitude - 1) < 1e-12 && Math.Abs(fresh.Magnitude - 1) < 1e-12, "light: stays a unit direction");
}

/* ---- far terrain: one walk a frame, shared by every camera ---- */
{
    var walk = new SharedWalk<string, int>();
    int next = 0;
    Action<string, List<int>> fill = (k, l) => l.Add(++next);
    var first = walk.For("kerbin", 10, 500, fill);
    var again = walk.For("kerbin", 10, 500, fill);
    Check(walk.Walks == 1 && ReferenceEquals(first, again) && again[0] == 1, "walk: a second camera in the frame shares the first camera's walk");
    walk.For("mun", 10, 500, fill);
    Check(walk.Walks == 2, "walk: each sphere is walked for itself");
    Check(walk.For("kerbin", 10, 500, fill)[0] == 1 && walk.Walks == 2, "walk: walking another sphere leaves the first one's list alone");
    Check(walk.For("kerbin", 11, 500, fill)[0] == 3 && walk.Walks == 3, "walk: a new frame walks again");
    Check(walk.For("kerbin", 11, 501, fill)[0] == 4 && walk.Walks == 4, "walk: quads changing inside the frame walks again");
    Check(walk.For("mun", 11, 501, fill)[0] == 5 && walk.Walks == 5, "walk: a change anywhere walks every sphere again, the count being shared");
    var list = walk.For("kerbin", 12, 501, fill);
    Check(list.Count == 1, "walk: a new walk replaces the old items rather than adding to them");
    walk.For("duna", 13, 501, (k, l) => { });
    Check(walk.For("kerbin", 14, 501, (k, l) => { }).Count == 0, "walk: nothing is kept from an earlier frame");
}

/* ---- far terrain: which bodies have their terrain left out ---- */
{
    /* A network lens on the Mun near side watching a craft at KSC: the far plane is stretched to the craft, 11.26 Mm out. StockTo stands for a stock terrain altitude a little above Kerbin's air. */
    const double KerbinR = 600000, KerbinMax = 606000, NativeFar = 750000, ToKsc = 11262210, StockTo = 160000;
    bool LeftOut(double distance, double reach) => FarTerrain.LeftOut(distance, KerbinMax, NativeFar, reach, distance - KerbinR, StockTo);
    double munToKerbin = ToKsc + KerbinR;
    Check(LeftOut(munToKerbin, ToKsc), "far terrain: Kerbin's ground reached only by a plane stretched to KSC from the Mun is left out");
    Check(!LeftOut(munToKerbin, NativeFar), "far terrain: with the plane native it is out of reach and nothing is switched");
    Check(!LeftOut(KerbinR + 100000, ToKsc), "far terrain: a lens within native reach and below stock's terrain altitude keeps its ground, however far the plane is stretched");
    Check(!LeftOut(KerbinR + 70, NativeFar), "far terrain: a lens on the ground keeps it");
    Check(LeftOut(KerbinMax + NativeFar + 1, NativeFar + 2), "far terrain: a metre past native reach, a stretched plane that reaches it leaves it out");
    Check(!FarTerrain.LeftOut(KerbinMax + NativeFar, KerbinMax, NativeFar, 3e7, KerbinMax + NativeFar - KerbinR, double.PositiveInfinity), "far terrain: at native reach exactly, with no stock altitude, it is kept");
    Check(!LeftOut(double.NaN, 3e7), "far terrain: an unreadable distance switches nothing");

    /* A probe 300 km over Kerbin watching a craft landed near KSC, 376 km off: in native reach, but higher than stock ever draws terrain from. */
    Check(LeftOut(KerbinR + 300000, NativeFar), "far terrain: a lens above stock's terrain altitude leaves it out, though within native reach");
    Check(LeftOut(KerbinR + 300000, 376700), "far terrain: and so with the plane stretched to its subject");
    Check(!LeftOut(KerbinR + StockTo, NativeFar), "far terrain: at stock's terrain altitude exactly it is kept");
    Check(LeftOut(KerbinR + StockTo + 1, NativeFar), "far terrain: a metre above it, left out");
    Check(!FarTerrain.LeftOut(KerbinR + 300000, KerbinMax, NativeFar, NativeFar, 300000, double.PositiveInfinity), "far terrain: a sphere stock never switches by altitude keeps its ground in native reach");
    Check(!FarTerrain.LeftOut(KerbinR + 300000, KerbinMax, NativeFar, NativeFar, double.NaN, StockTo), "far terrain: an unreadable altitude switches nothing");

    Check(FarTerrain.SeenAsStock(KerbinR + 70, KerbinMax, NativeFar, 70, StockTo), "seen as stock: a lens on the ground");
    Check(!FarTerrain.SeenAsStock(KerbinR + 300000, KerbinMax, NativeFar, 300000, StockTo), "seen as stock: not from above stock's terrain altitude");
    Check(!FarTerrain.SeenAsStock(munToKerbin, KerbinMax, NativeFar, munToKerbin - KerbinR, double.PositiveInfinity), "seen as stock: not from beyond native reach");
}

/* ---- far terrain: the altitude stock switches terrain off above ---- */
{
    Check(FarTerrain.StockTerrainTo(90500, -1, 3) == 90500, "stock terrain to: no high-quality altitude, the plain one");
    Check(FarTerrain.StockTerrainTo(90500, 160000, 1) == 90500, "stock terrain to: below shader quality 2, the plain one");
    Check(FarTerrain.StockTerrainTo(90500, 160000, 2) == 160000, "stock terrain to: from shader quality 2, the high-quality one");
    Check(double.IsPositiveInfinity(FarTerrain.StockTerrainTo(90500, 0, 2)), "stock terrain to: a high-quality altitude of 0 never switches off");
}

/* ---- far terrain: the quads in storage every sphere shares ---- */
{
    var terrain = new object();
    var ocean = new object();
    var storage = new List<(string Name, object Root)> { ("a", terrain), ("b", ocean), ("c", new object()), ("d", null), ("e", terrain) };
    var own = new List<(string Name, object Root)> { ("stale", terrain) };
    FarTerrain.OwnedQuads(storage, terrain, q => q.Root, own);
    Check(own.Count == 2 && own[0].Name == "a" && own[1].Name == "e", "storage: only the sphere's own quads are kept, and the list is refilled");
    FarTerrain.OwnedQuads(storage, ocean, q => q.Root, own);
    Check(own.Count == 1 && own[0].Name == "b", "storage: a child sphere such as the ocean finds its own quads there");
    FarTerrain.OwnedQuads(new List<(string Name, object Root)>(), terrain, q => q.Root, own);
    Check(own.Count == 0, "storage: empty storage gives nothing");
}

/* ---- body light: the Sun's direction per body, and which body gets the camera's one light ---- */
{
    Check(BodyLight.Applies(true, false), "body light: always on under a light speed");
    Check(BodyLight.Applies(false, true), "body light: on without a light speed when the setting is on");
    Check(!BodyLight.Applies(false, false), "body light: off without a light speed by default");
    var sun = new DVec3(10, 0, 0);
    var d = BodyLight.Direction(sun, new DVec3(10, 5e9, 0), new DVec3(0, 0, 1));
    Check(Math.Abs(d.Y - 1) < 1e-12 && Math.Abs(d.X) < 1e-12, "body light: the direction runs from the Sun to the drawn centre");
    var f = BodyLight.Direction(sun, sun, new DVec3(0, 0, 1));
    Check(f.Z == 1 && f.X == 0, "body light: a star's direction to itself falls back");
    var rim = PictureBoundary.LightInBodyAxes(new DQuat(0, Math.Sin(Math.PI / 4), 0, Math.Cos(Math.PI / 4)), new DVec3(1, 0, 0));
    Check(Math.Abs(rim.Z - 1) < 1e-12, "body light: the rim direction is stored in the turned mesh's axes");

    Check(Math.Abs(BodyLight.DiscRadians(2, 1) - Math.PI / 6) < 1e-12, "disc: from twice the radius a sphere spans 30 degrees to its limb");
    Check(BodyLight.DiscRadians(1, 1) == Math.PI / 2 && BodyLight.DiscRadians(0.5, 1) == Math.PI / 2, "disc: at or inside the sphere it fills half the sky");
    double half = BodyLight.HalfDiagonalRadians(90, 1);
    Check(Math.Abs(half - Math.Atan(Math.Sqrt(2))) < 1e-12, "frame: the half diagonal of a square 90 degree frame");

    var eye = new DVec3(0, 0, 0);
    var ahead = new DVec3(0, 0, 1);
    double narrow = BodyLight.HalfDiagonalRadians(1, 16.0 / 9);
    Check(BodyLight.DiscInFrame(eye, new DVec3(0, 0, 1e10), 1e6, ahead, narrow) > 0, "frame: a body ahead is in frame");
    Check(BodyLight.DiscInFrame(eye, new DVec3(0, 0, -1e10), 1e6, ahead, narrow) == 0, "frame: a body behind is not");
    Check(BodyLight.DiscInFrame(eye, new DVec3(1e9, 0, 1e10), 1e6, ahead, narrow) == 0, "frame: a body off to the side of a narrow frame is not");
    Check(BodyLight.DiscInFrame(eye, new DVec3(0, -6.1e5, 0), 6e5, new DVec3(0, 1, 0), narrow) == 0, "frame: a telescope looking straight up does not count the ground under it");
    Check(BodyLight.DiscInFrame(eye, new DVec3(0, -6.0001e5, 0), 6e5, new DVec3(1, 0, 0), BodyLight.HalfDiagonalRadians(40, 16.0 / 9)) > 0, "frame: a horizon in frame counts its own planet");
    Check(BodyLight.DiscInFrame(eye, new DVec3(0, -5e5, 0), 6e5, new DVec3(0, 1, 0), narrow) == Math.PI / 2, "frame: a lens inside the sphere always counts it");
    {
        /* A probe 5,000 km over Kerbin aims a 0.2 degree frame at Eve, its view grazing the limb: Kerbin's sea level has left the frame, its air and clouds have not. */
        var probe = new DVec3(0, 5.6e6, 0);
        double down = -83.62 * Math.PI / 180;
        var past = new DVec3(Math.Cos(down), Math.Sin(down), 0);
        double slit = BodyLight.ConeFor(BodyLight.HalfDiagonalRadians(0.2, 800.0 / 448), true);
        double kerbinReach = BodyLight.ReachRadius(6e5, 6.0676e5, true, 7e4);
        Check(BodyLight.DiscInFrame(probe, default, 6e5, past, slit) == 0, "reach: a grazing limb's sea level is out of the frame");
        Check(BodyLight.DiscInFrame(probe, default, 6e5, kerbinReach, past, slit) == BodyLight.DiscRadians(5.6e6, 6e5), "reach: its air keeps it in, with its sea-level disc");
        double clear = -82.5 * Math.PI / 180;
        Check(BodyLight.DiscInFrame(probe, default, 6e5, kerbinReach, new DVec3(Math.Cos(clear), Math.Sin(clear), 0), slit) == 0, "reach: a view clear of the air's top leaves it out");
        Check(BodyLight.ReachRadius(2e5, 2.07e5, false, 0) == 2.07e5, "reach: an airless body reaches its highest ground");
        Check(BodyLight.ReachRadius(6e5, 6.0676e5, true, 7e4) == 6.7e5, "reach: a body with air reaches its top");
        Check(BodyLight.ReachRadius(6e5, 0, false, 0) == 6e5, "reach: a body with no terrain reaches sea level");
        double eveRange = 3.972e9, eveRadius = 7e5;
        double[] limbAndEve =
        {
            BodyLight.DiscInFrame(probe, default, 6e5, kerbinReach, past, slit),
            BodyLight.DiscInFrame(probe, past * eveRange, eveRadius, past, slit)
        };
        bool fills = BodyLight.FillsFrame(BodyLight.AngleFrom(probe, default, past), limbAndEve[0], slit);
        Check(!fills, "fills: a limb crossing only the frame's edge does not fill it");
        Check(BodyLight.Pick(limbAndEve, 2, -1, fills ? 0 : -1) == 0, "fills: so the planet whose limb is in frame keeps the light from the small one it frames");
        Check(BodyLight.FillsFrame(BodyLight.AngleFrom(probe, default, new DVec3(0, -1, 0)), limbAndEve[0], slit), "fills: the same planet straight down does");
    }

    Check(BodyLight.Pick(new double[] { 0, 0, 0 }, 3, 1) == -1, "pick: nothing in frame gives none");
    Check(BodyLight.Pick(new double[] { 0.1, 0.3, 0.2 }, 3, -1) == 1, "pick: with none held the largest disc wins");
    Check(BodyLight.Pick(new double[] { 0.2, 0.2 }, 2, -1) == 0, "pick: an exact tie goes to the lower slot");
    Check(BodyLight.Pick(new double[] { 0.24, 0.2 }, 2, 1) == 1, "pick: a held body keeps the light against a near-equal disc");
    Check(BodyLight.Pick(new double[] { 0.26, 0.2 }, 2, 1) == 0, "pick: a clearly wider disc takes the light");
    Check(BodyLight.Pick(new double[] { 0.1, 0 }, 2, 1) == 0, "pick: a held body that left the frame gives up the light");
    Check(BodyLight.Pick(new double[] { 0.1, 0.2 }, 2, 5) == 1, "pick: a held slot past the list is ignored");
    double[] ownAndDuna = { Math.PI / 2, 0.002 };
    Check(BodyLight.Pick(ownAndDuna, 2, -1, 0) == 1, "underfoot: a small Duna takes the light from the huge planet under the lens");
    Check(BodyLight.Pick(ownAndDuna, 2, 0, 0) == 1, "underfoot: the planet under the lens cannot hold the light against another body");
    Check(BodyLight.Pick(new double[] { Math.PI / 2, 0.002, 0.0021 }, 3, 1, 0) == 1, "underfoot: the hysteresis still holds between the other bodies");
    Check(BodyLight.Pick(new double[] { Math.PI / 2, 0 }, 2, -1, 0) == 0, "underfoot: alone in frame it keeps the light");
    Check(BodyLight.Pick(new double[] { 0, 0 }, 2, -1, 0) == -1, "underfoot: out of frame, nothing takes the light");
    Check(BodyLight.Pick(new double[] { 0.3, 0.2 }, 2, -1) == 0, "underfoot: with none named the widest disc wins");
    double wide = BodyLight.HalfDiagonalRadians(60, 16.0 / 9);
    Check(BodyLight.FillsFrame(0, BodyLight.DiscRadians(6.0001e5, 6e5), wide), "fills: a planet under a ground lens fills even a wide frame");
    Check(BodyLight.FillsFrame(0, BodyLight.DiscRadians(6.0001e5, 6e5), BodyLight.HalfDiagonalRadians(1, 16.0 / 9)), "fills: and a telescope's narrow frame");
    Check(!BodyLight.FillsFrame(0, BodyLight.DiscRadians(2e7, 6e5), wide), "fills: a planet well below a craft in high orbit does not, so it can take the light");
    var groundEye = new DVec3(0, 0, 0);
    var ownCentre = new DVec3(0, -6.0001e5, 0);
    const double ownRadius = 6e5;
    const double dunaRadius = 3.2e5, dunaRange = 6.8e9;
    double wideFov = 60, teleFov = 2 * Math.Atan(Math.Tan(Math.PI / 180) / Math.Sqrt(1 + 16.0 * 16 / 81)) * 180 / Math.PI;
    int PickFromGround(DVec3 aim, DVec3 centre, double fov, int heldSlot)
    {
        double halfD = BodyLight.HalfDiagonalRadians(fov, 16.0 / 9);
        var discs = new[]
        {
            BodyLight.DiscInFrame(groundEye, ownCentre, ownRadius, aim, halfD),
            BodyLight.DiscInFrame(groundEye, centre, dunaRadius, aim, halfD)
        };
        int own = BodyLight.FillsFrame(BodyLight.AngleFrom(groundEye, ownCentre, aim), discs[0], halfD) ? 0 : -1;
        if (own == 0 && discs[1] > 0 && !BodyLight.OutranksOwn(groundEye, ownCentre, ownRadius, centre, discs[1], fov, 1080, heldSlot == 1)) discs[1] = 0;
        return BodyLight.Pick(discs, 2, heldSlot, own);
    }
    double horizon = 3 * Math.PI / 180;
    var overHorizon = new DVec3(Math.Cos(horizon), Math.Sin(horizon), 0);
    double dunaDisc = BodyLight.DiscRadians(dunaRange, dunaRadius);
    Check(Math.Abs(BodyLight.DiscPixels(dunaDisc, teleFov, 1080) - 3) < 0.3, "outrank: Duna is about three pixels on a one degree telescope at 1080p");
    Check(BodyLight.DiscPixels(dunaDisc, wideFov, 1080) < 0.1, "outrank: and a small fraction of one on a wide camera");
    Check(PickFromGround(overHorizon, overHorizon * dunaRange, wideFov, -1) == 0, "outrank: a tiny planet in a wide camera's cone leaves the light on the own planet");
    Check(PickFromGround(overHorizon, overHorizon * dunaRange, wideFov, 1) == 0, "outrank: and cannot go on holding it");
    Check(PickFromGround(overHorizon, overHorizon * dunaRange, teleFov, -1) == 1, "outrank: Duna at three pixels in a telescope takes the light");
    Check(PickFromGround(overHorizon, overHorizon * dunaRange, teleFov, 0) == 1, "outrank: from the own planet that held it");
    var belowGround = new DVec3(0, -1, 0);
    Check(BodyLight.BehindLimb(groundEye, ownCentre, ownRadius, belowGround * dunaRange), "limb: a planet straight through the ground is behind it");
    Check(!BodyLight.BehindLimb(groundEye, ownCentre, ownRadius, overHorizon * dunaRange), "limb: a planet above the horizon is not");
    Check(!BodyLight.BehindLimb(groundEye, ownCentre, ownRadius, belowGround * 5), "limb: a body nearer than the limb is not hidden by it");
    var offNadir = new DVec3(Math.Cos(-1.2), Math.Sin(-1.2), 0);
    var bigBehind = offNadir * 1e9;
    Check(!BodyLight.OutranksOwn(groundEye, ownCentre, ownRadius, bigBehind, BodyLight.DiscRadians(1e9, 6e5), teleFov, 1080, false), "limb: a planet well over two pixels behind the own planet cannot outrank it");
    Check(BodyLight.OutranksOwn(groundEye, ownCentre, ownRadius, overHorizon * 1e9, BodyLight.DiscRadians(1e9, 6e5), teleFov, 1080, false), "limb: the same planet above the horizon can");
    double teleHalf = BodyLight.HalfDiagonalRadians(teleFov, 16.0 / 9);
    var aimEast = new DVec3(1, 0, 0);
    DVec3 Off(double angle) => new DVec3(Math.Cos(angle), Math.Sin(angle), 0) * dunaRange;
    double EdgeDisc(double angle, bool widen) => BodyLight.DiscInFrame(groundEye, Off(angle), dunaRadius, aimEast, BodyLight.ConeFor(teleHalf, widen));
    Check(BodyLight.ConeFor(1, false) == 1 && Math.Abs(BodyLight.ConeFor(1, true) - 1.1) < 1e-12, "edge: only the held body's cone is widened");
    Check(EdgeDisc(teleHalf * 1.05, false) == 0 && EdgeDisc(teleHalf * 1.05, true) > 0, "edge: just outside the frame only the held body still counts");
    Check(EdgeDisc(teleHalf * 1.2, true) == 0, "edge: clearly outside the frame even the held body does not");
    int EdgeSwitches(bool hold)
    {
        int heldSlot = 1, flips = 0;
        var pair = new double[2];
        for (int i = 0; i < 200; i++)
        {
            pair[0] = Math.PI / 2;
            pair[1] = EdgeDisc(teleHalf * (1 + 0.04 * Math.Sin(i * 0.7)), hold && heldSlot == 1);
            int now = BodyLight.Pick(pair, 2, heldSlot, 0);
            if (now != heldSlot) flips++;
            heldSlot = now;
        }
        return flips;
    }
    Check(EdgeSwitches(true) == 0, "edge: a lens jittering at the frame's edge keeps the light where it is");
    Check(EdgeSwitches(false) > 0, "edge: without the widened cone the same jitter flips it");
    double FovFor(double px) => 2 * Math.Atan(540 * Math.Tan(dunaDisc) / px) * 180 / Math.PI;
    Check(!BodyLight.OutranksOwn(groundEye, ownCentre, ownRadius, overHorizon * dunaRange, dunaDisc, FovFor(1.8), 1080, false), "edge: a newcomer under two pixels does not take the light");
    Check(BodyLight.OutranksOwn(groundEye, ownCentre, ownRadius, overHorizon * dunaRange, dunaDisc, FovFor(1.8), 1080, true), "edge: the held body keeps it just under two pixels");
    Check(!BodyLight.OutranksOwn(groundEye, ownCentre, ownRadius, overHorizon * dunaRange, dunaDisc, FovFor(1.4), 1080, true), "edge: and gives it up when clearly smaller");
    int held = -1;
    double[] sweep = new double[2];
    int switches = 0;
    for (int i = 0; i < 200; i++)
    {
        sweep[0] = 0.2 + 0.01 * Math.Sin(i * 0.3);
        sweep[1] = 0.2 + 0.01 * Math.Cos(i * 0.3);
        int now = BodyLight.Pick(sweep, 2, held);
        if (held >= 0 && now != held) switches++;
        held = now;
    }
    Check(switches == 0, "pick: two discs wobbling round each other do not swap the light");
}

/* ---- one light per planetary system ---- */
{
    /* Slots: 0 Sun, 1 Kerbin, 2 Mun, 3 Duna, 4 Ike, 5 Jool, 6 Laythe, 7 a moon of Laythe, 8 Eeloo, 9 a rogue with no parent. */
    int[] parent = { -1, 0, 1, 0, 3, 0, 5, 6, 0, -1 };
    bool[] star = { true, false, false, false, false, false, false, false, false, false };
    Check(BodyLight.SystemRoot(parent, star, 0) == -1, "system: a star belongs to no system");
    Check(BodyLight.SystemRoot(parent, star, 1) == 1, "system: a planet is its own system");
    Check(BodyLight.SystemRoot(parent, star, 2) == 1, "system: a moon belongs to its planet's");
    Check(BodyLight.SystemRoot(parent, star, 7) == 5, "system: a moon's moon belongs to the planet at the top");
    Check(BodyLight.SystemRoot(parent, star, 9) == 9, "system: a body with no parent is its own system");
    int[] loop = { 1, 0 };
    Check(BodyLight.SystemRoot(loop, new bool[2], 0) >= 0, "system: a parent loop ends");

    int[] spares = { 3, 6, 7 };
    int[] none = { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
    int[] layer = new int[10];
    double[] best = new double[10];
    best[1] = 0.9; best[3] = 0.002; best[5] = 0.01;
    int n = BodyLight.AssignLayers(best, 10, 3, none, spares, 3, layer);
    Check(n == 2 && layer[3] == -1, "assign: the main light's system takes no spare layer");
    Check(layer[1] == 3 && layer[5] == 6, "assign: the others take spare layers, widest first");
    Check(layer[8] == -1 && layer[0] == -1, "assign: a system out of frame takes none");

    best[8] = 0.005; best[9] = 0.0045;
    BodyLight.AssignLayers(best, 10, 3, none, spares, 3, layer);
    Check(layer[1] == 3 && layer[5] == 6 && layer[8] == 7 && layer[9] == -1, "assign: past the spare layers the narrowest share the main light");

    int[] held = (int[])none.Clone();
    held[9] = 7; held[5] = 3;
    BodyLight.AssignLayers(best, 10, 3, held, spares, 3, layer);
    Check(layer[9] == 7, "assign: a held system keeps its place against a near-equal disc");
    Check(layer[5] == 3, "assign: and keeps the layer it had");
    Check(layer[8] == -1 && layer[1] == 6, "assign: a newcomer takes a free layer, the narrowest newcomer shares");

    best[8] = 0.02;
    BodyLight.AssignLayers(best, 10, 3, held, spares, 3, layer);
    Check(layer[8] >= 0 && layer[9] == -1, "assign: a clearly wider disc takes the place");

    BodyLight.AssignLayers(best, 10, 3, none, spares, 0, layer);
    Check(Array.TrueForAll(layer, l => l == -1), "assign: with no spare layers every system shares the main light");

    double[] tie = new double[10];
    tie[1] = 0.1; tie[5] = 0.1;
    BodyLight.AssignLayers(tie, 10, -1, none, spares, 1, layer);
    Check(layer[1] == 3 && layer[5] == -1, "assign: an exact tie goes to the lower slot");

    int heldNow = -1, swaps = 0;
    double[] wobble = new double[10];
    int[] h = (int[])none.Clone();
    for (int i = 0; i < 200; i++)
    {
        wobble[1] = 0.2 + 0.01 * Math.Sin(i * 0.3);
        wobble[5] = 0.2 + 0.01 * Math.Cos(i * 0.3);
        BodyLight.AssignLayers(wobble, 10, -1, h, spares, 1, layer);
        int now = layer[1] >= 0 ? 1 : 5;
        if (heldNow >= 0 && now != heldNow) swaps++;
        heldNow = now;
        Array.Copy(layer, h, 10);
    }
    Check(swaps == 0, "assign: two systems wobbling round each other do not swap the spare layer");

    const int scaled = 10;
    int spareMask = (1 << 3) | (1 << 6) | (1 << 7);
    Check(BodyLight.MirrorMask(~(1 << scaled), scaled, spareMask) == (~(1 << scaled) & ~spareMask), "mirror: the local sun light, which skips the scaled layer, skips the spare layers too");
    Check(BodyLight.MirrorMask(-1, scaled, spareMask) == -1, "mirror: a light on every layer stays on every layer");
    Check(BodyLight.MirrorMask(1 << scaled, scaled, spareMask) == ((1 << scaled) | spareMask), "mirror: a light on the scaled layer alone reaches the spare layers");
    Check(BodyLight.MirrorMask(1, scaled, spareMask) == 1, "mirror: a light off the scaled layer is left alone");

    Check(BodyLight.FreeLayers(spares, 0) == spareMask, "free: an unused spare layer is free");
    Check(BodyLight.FreeLayers(spares, 1 << 6) == ((1 << 3) | (1 << 7)), "free: a spare layer something draws on is not");
    int[] got = new int[3];
    Check(BodyLight.LayersIn(spareMask & ~(1 << 6), got) == 2 && got[0] == 3 && got[1] == 7, "free: the layers of a mask, lowest first");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
