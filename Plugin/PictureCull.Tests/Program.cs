using System;
using System.Collections.Generic;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

const int Sun = 0, Kerbin = 1, Mun = 2;
const double MuKerbin = 3.5316e12, MuMun = 6.5138398e10;
var bodies = new[]
{
    new HistoryBody { Index = Sun, Parent = -1, Mu = 1.1723328e18, Radius = 2.616e8, SoiRadius = double.PositiveInfinity },
    new HistoryBody { Index = Kerbin, Parent = Sun, Mu = MuKerbin, Radius = 600000, SoiRadius = 84159286, MaxOrbitalSpeed = 9285 },
    new HistoryBody { Index = Mun, Parent = Kerbin, Mu = MuMun, Radius = 200000, SoiRadius = 2429559, MaxOrbitalSpeed = 543 },
};
var tree = new Tree { KerbinAt = new DVec3(1.36e10, 0, 0), KerbinVel = new DVec3(0, 9285, 0), MunAt = new DVec3(1.2e7, 0, 0), MunVel = new DVec3(500, 0, 0) };
DVec3[] BodiesAt(double ut) => new[] { new DVec3(0, 0, 0), tree.KerbinAt + tree.KerbinVel * ut, tree.KerbinAt + tree.KerbinVel * ut + tree.MunAt + tree.MunVel * ut };

HistoryState Conic(double ut, int body, DVec3 r, DVec3 v) => new HistoryState
{
    Ut = ut, Body = body, Kind = HistoryKind.Conic, Position = r, Velocity = v, Attitude = DQuat.Identity,
};
/* One part `sizeM` in radius; a craft that changes shape gets another part, as staging leaves a different set. */
PartRecord[] Parts(double sizeM, uint part = 1) => new[] { new PartRecord { Pid = part, Px = 0, Py = 0, Pz = 0, RadiusM = (float)sizeM } };

const double r0 = 700000;
double vc = Math.Sqrt(MuKerbin / r0);
const double TE = 300;

/* Every craft's life as the ring would have heard it, in UT order; the ring at
   any render UT holds only what had happened by then. */
var events = new List<(double Ut, Action<LightHistory> Do)>();
void Start(uint pid, double ut, int body, DVec3 r, DVec3 v, double sizeM)
{
    events.Add((ut, h => h.Record(LightHistory.CraftKey(pid), Conic(ut, body, r, v), HistoryReason.Start)));
    events.Add((ut, h => h.SetComposition(pid, ut, Parts(sizeM), 0, 0, 0)));
}
DVec3 Around(double angle, double r) => new DVec3(r * Math.Cos(angle), r * Math.Sin(angle), 0);
DVec3 Along(double angle, double v) => new DVec3(-v * Math.Sin(angle), v * Math.Cos(angle), 0);

Start(10, 0, Kerbin, Around(0, r0), Along(0, vc), 5);
Start(11, 0, Kerbin, Around(1, r0), Along(1, vc), 8);
events.Add((TE, h => h.Destroyed(11, TE, h.Generation)));
/* Its id comes back in a relaunch: the destroyed life becomes a ghost track under the same pid. */
Start(11, TE + 150, Kerbin, Around(1.5, r0), Along(1.5, vc), 3);
Start(12, 0, Kerbin, Around(2, r0), Along(2, vc), 8);
events.Add((TE, h => h.Recovered(12, TE)));
Start(13, 0, Kerbin, Around(3, r0), Along(3, vc), 6);
Start(14, 0, Kerbin, Around(3.0001, r0), Along(3.0001, vc), 6);
events.Add((TE, h => h.Merge(13, 14, TE)));
events.Add((TE, h => h.SetComposition(14, TE, Parts(14, 2), 0, 0, 0)));
Start(15, 0, Kerbin, Around(4, r0), Along(4, vc), 40);
events.Add((TE, h => h.SetComposition(15, TE, Parts(5, 2), 0, 0, 0)));
events.Add((TE, h =>
{
    h.Split(15, 16, TE);
    h.Record(LightHistory.CraftKey(16), Conic(TE, Kerbin, Around(4 + vc * TE / r0, r0 + 50), Along(4 + vc * TE / r0, vc + 30)), HistoryReason.Split);
    h.SetComposition(16, TE, Parts(30), 0, 0, 0);
}));
/* Leaves Kerbin for the Mun at TE: a state about the Mun from then. */
/* Coasts from Kerbin into the Mun's sphere of influence, crossing at TE: its
   state about the Mun then is its own Kerbin conic less the Mun's. */
Start(17, 0, Kerbin, new DVec3(tree.MunAt.X + tree.MunVel.X * TE - 2.40e6 - 300 * TE, 0, 0), new DVec3(300, 0, 0), 10);
events.Add((TE, h =>
{
    h.TryStateAt(17u, TE, out var k);
    var mun = tree.MunAt + tree.MunVel * TE;
    h.BodyChanged(LightHistory.CraftKey(17), Conic(TE, Mun, k.Position - mun, k.Velocity - tree.MunVel), TE);
}));
/* Still bound to Kerbin, it coasts sideways into the Mun's sphere of influence
   at TE. Seen from the far side of Kerbin at the narrowest field, the places
   its light shows it at before then are in view while the Mun's sphere, and
   the craft in it now, are not. */
const double Early = -1000;
Start(19, Early, Kerbin, new DVec3(tree.MunAt.X + tree.MunVel.X * TE, -2.40e6 - 750 * (TE - Early), 0), new DVec3(0, 750, 0), 10);
events.Add((TE, h =>
{
    h.TryStateAt(19u, TE, out var k);
    var mun = tree.MunAt + tree.MunVel * TE;
    h.BodyChanged(LightHistory.CraftKey(19), Conic(TE, Mun, k.Position - mun, k.Velocity - tree.MunVel), TE);
}));
/* Landed on Kerbin, turning with it. */
const double Spin = 2 * Math.PI / 21549.425;
events.Add((0, h => h.Record(LightHistory.CraftKey(18), new HistoryState
{
    Ut = 0, Body = Kerbin, Kind = HistoryKind.Surface, Position = new DVec3(0, 6e5, 0), AngularVelocity = new DVec3(0, 0, Spin),
    Velocity = DVec3.Cross(new DVec3(0, 0, Spin), new DVec3(0, 6e5, 0)), Attitude = DQuat.Identity,
}, HistoryReason.Start)));
events.Add((0, h => h.SetComposition(18, 0, Parts(12), 0, 0, 0)));
events.Sort((a, b) => a.Ut.CompareTo(b.Ut));

LightHistory RingAt(double t, double c)
{
    var h = new LightHistory { SystemDiameterM = 2.4e11 };
    h.SetBodies(bodies);
    h.SetLightSpeed(c);
    foreach (var e in events) if (e.Ut <= t) e.Do(h);
    return h;
}

/* What the picture pass would draw of `pid`: where, and how large. */
/* `pastBodies` places it as the pass does against a body drawn at its own past
   instant: its past position in the frame it was solved in, about that frame's
   body as drawn now. Otherwise it sits on its body as drawn now. */
bool Drawn(LightHistory h, uint pid, double t, DVec3 lensOffset, double c, out DVec3 world, out double size, bool pastBodies = false)
{
    world = default;
    size = 0;
    var tr = tree;
    var sight = PastInstant.Craft(h, pid, t, Kerbin, lensOffset, c, ref tr);
    if (!sight.Found) return false;
    world = pastBodies && sight.Frame >= 0
        ? BodiesAt(t)[sight.Frame] + sight.About
        : BodiesAt(t)[sight.State.Body] + PastInstant.AboutDrawnBody(sight.State, t);
    var comp = h.CompositionAt(sight.Track, sight.Ut);
    size = comp != null && comp.SizeM > 0 ? comp.SizeM : 2.0;
    return true;
}

var rng = new Random(11);
DVec3 RandomUnit()
{
    while (true)
    {
        var v = new DVec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
        double m = v.Magnitude;
        if (m > 0.1 && m <= 1) return v * (1 / m);
    }
}
var fovs = new[] { 60.0, 10.0, 3.0, 1.0, 0.3, 0.1, 0.01 };
/* Near the craft, on the far side of Kerbin, out by the Mun, and far enough off that size decides what shows. */
var lenses = new[] { new DVec3(6e5, 0, 0), new DVec3(0, -6e5, 0), new DVec3(-4e5, 4e5, 2e5), new DVec3(1.15e7, 0, 0), new DVec3(4e7, 0, 0), new DVec3(-2e7, 1e7, 0),
    new DVec3(0, 4e7, 0), new DVec3(-3e7, 0, 0) };
var pids = new uint[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };

/* ---- no skipped craft would have been drawn ---- */
{
    const double c = 1e5;
    int checks = 0, skips = 0, drawnSeen = 0, leaks = 0;
    var leaked = new HashSet<uint>();
    var views = new List<CullView>();
    var allSkip = new Dictionary<uint, bool>();
    for (double t = TE - 60; t <= TE + 700; t += 5.3)
    {
        var h = RingAt(t, c);
        var at = BodiesAt(t);
        foreach (var lensOffset in lenses)
        {
            var eye = at[Kerbin] + lensOffset;
            views.Clear();
            /* Each craft's true drawn place at every field, a little off it, and random views. */
            foreach (uint aim in pids)
            {
                if (!Drawn(h, aim, t, lensOffset, c, out var aimAt, out _)) continue;
                foreach (double fov in fovs)
                {
                    var forward = ((aimAt - eye).Normalized + RandomUnit() * (rng.NextDouble() * fov * 0.004)).Normalized;
                    var up = Math.Abs(forward.Z) < 0.9 ? new DVec3(0, 0, 1) : new DVec3(1, 0, 0);
                    views.Add(CullView.Of(eye, forward, up, fov, 16.0 / 9.0, 1080));
                }
            }
            for (int k = 0; k < 10; k++)
            {
                var forward = RandomUnit();
                var up = Math.Abs(forward.Z) < 0.9 ? new DVec3(0, 0, 1) : new DVec3(1, 0, 0);
                views.Add(CullView.Of(eye, forward, up, fovs[rng.Next(fovs.Length)], 16.0 / 9.0, 1080));
            }
            foreach (var view in views)
            {
                /* As the pass walks the ring: a craft is dropped only when every track under its id skips. */
                allSkip.Clear();
                foreach (var track in h.Tracks)
                {
                    if (track.IsBody) continue;
                    checks++;
                    bool skip = PictureCull.Skip(h, track, t, view, DQuat.Identity, at, c, Kerbin);
                    if (skip) skips++;
                    allSkip[track.Pid] = (!allSkip.TryGetValue(track.Pid, out bool all) || all) && skip;
                }
                foreach (var kv in allSkip)
                {
                    /* Either way the pass may place it: on its body as drawn, or at its past position with bodies drawn at theirs. */
                    bool visible = false;
                    foreach (bool pastBodies in new[] { false, true })
                    {
                        if (!Drawn(h, kv.Key, t, lensOffset, c, out var world, out double size, pastBodies)) continue;
                        double d = (world - eye).Magnitude;
                        visible |= !PictureCull.OutOfView(view, world, size)
                            && PictureCull.RadiusPx(size, d, view.RadPerPxToward(world - eye)) >= PictureCull.CutoffPx;
                    }
                    if (visible) drawnSeen++;
                    if (kv.Value && visible)
                    {
                        leaks++;
                        leaked.Add(kv.Key);
                    }
                }
            }
        }
    }
    Check(leaks == 0, $"no craft the cull skipped would have been drawn: destroy, recover, dock, break-up, an SOI change, a relaunched id and a landed craft within the horizon ({leaks}; craft {string.Join(",", leaked)})");
    Check(drawnSeen > 1000, $"the matrix has visible craft to get wrong ({drawnSeen})");
    Check(skips > checks / 4, $"and the cull skips a real share of the work ({skips} of {checks})");
}

/* ---- no cull once the speed bound reaches 0.9 c ---- */
{
    /* The speed in the lens's and the craft's common frame: about 2.2 km/s in low Kerbin orbit, over 0.9 of 2.4e3. */
    const double c = 2.4e3;
    var h = RingAt(TE + 10, c);
    var at = BodiesAt(TE + 10);
    var eye = at[Kerbin] + lenses[0];
    var away = CullView.Of(eye, new DVec3(0, 0, 1), new DVec3(1, 0, 0), 0.01, 16.0 / 9.0, 1080);
    int fast = 0, culledFast = 0;
    foreach (var track in h.Tracks)
    {
        if (track.IsBody || h.MaxSpeedAbout(track, Kerbin) < 0.9 * c) continue;
        fast++;
        if (PictureCull.Skip(h, track, TE + 10, away, DQuat.Identity, at, c, Kerbin)) culledFast++;
    }
    Check(fast >= 5 && culledFast == 0, $"a craft whose speed bound reaches 0.9 c is never culled, even looking away at 0.01 degrees ({culledFast} of {fast})");

    /* Slower light than before, but still well above orbital speeds in the frame: the cull works. */
    const double slow = 3e4;
    var hs = RingAt(TE + 10, slow);
    int skipped = 0, tracks = 0;
    foreach (var track in hs.Tracks)
    {
        if (track.IsBody) continue;
        tracks++;
        if (PictureCull.Skip(hs, track, TE + 10, away, DQuat.Identity, at, slow, Kerbin)) skipped++;
    }
    Check(skipped > tracks / 2, $"at 3e4 m/s, below Kerbin's own orbital speed plus twice it, looking away still skips most craft ({skipped} of {tracks})");
}

/* ---- the pieces ---- */
{
    Check(PictureCull.LightTimeBound(3e5, 3e5, 0, 100) == 1, "light time bound at rest is D / c");
    Check(PictureCull.LightTimeBound(3e5, 3e5, 1.5e5, 100) == 2, "and D / (c - v) when moving");
    Check(PictureCull.LightTimeBound(3e5, 3e5, 2.8e5, 100) == 100, "and the horizon once v reaches 0.9 c");
    Check(PictureCull.LightTimeBound(3e9, 3e5, 0, 100) == 100, "and never more than the horizon");

    var v = CullView.Of(new DVec3(0, 0, 0), new DVec3(0, 0, 1), new DVec3(0, 1, 0), 60, 1, 1000);
    Check(!PictureCull.OutOfView(v, new DVec3(0, 0, 100), 1), "a point dead ahead is in view");
    Check(PictureCull.OutOfView(v, new DVec3(0, 0, -100), 1), "a point behind is out");
    Check(PictureCull.OutOfView(v, new DVec3(100, 0, 10), 1), "a point well off the side is out");
    Check(!PictureCull.OutOfView(v, new DVec3(100, 0, 10), 100), "but not a sphere that reaches into the view");

    /* A 10 m radius at the centre of 1080 rows: about 470 km at 60 degrees and 31,000 km at 1 degree (section 3.9). */
    var wide = CullView.Of(default, new DVec3(0, 0, 1), new DVec3(0, 1, 0), 60, 16.0 / 9.0, 1080);
    var tight = CullView.Of(default, new DVec3(0, 0, 1), new DVec3(0, 1, 0), 1, 16.0 / 9.0, 1080);
    Check(!PictureCull.TooFaint(10, 4.5e5, wide.RadPerPx) && PictureCull.TooFaint(10, 4.9e5, wide.RadPerPx), "a 10 m craft at 60 degrees fades out near 470 km");
    var corner = new DVec3(wide.TanHalfH, wide.TanHalfV, 1);
    double ratio = PictureCull.RadiusPx(10, 1e6, wide.RadPerPxToward(corner)) / PictureCull.RadiusPx(10, 1e6, wide.RadPerPx);
    double expect = 1 + wide.TanHalfH * wide.TanHalfH + wide.TanHalfV * wide.TanHalfV;
    Check(Math.Abs(ratio - expect) < 1e-6 && Math.Abs(wide.MinRadPerPx - wide.RadPerPxToward(corner)) < 1e-15,
        $"in the corner of a 60 degree 16:9 frame a craft spans 1 + tan²h + tan²v times the pixels it does at the centre ({ratio:F2})");
    Check(!PictureCull.TooFaint(10, 2.9e7, tight.RadPerPx) && PictureCull.TooFaint(10, 3.3e7, tight.RadPerPx), "and at 1 degree near 31,000 km");
    double cutLight = PictureCull.PointBrightness(PictureCull.CutoffPx, 1, 2) * PictureCull.DotArea * 4;
    Check(cutLight < 1.5 / 255 && cutLight > 0.5 / 255, $"at the cutoff a full-phase dot gives about one 8-bit level in all ({cutLight * 255:F2})");
    Check(Math.Abs(PictureCull.PointBrightness(0.1, 1, 2) - 4 * PictureCull.PointBrightness(0.1, 1, 4)) < 1e-12,
        "a faint point drawn twice as wide is drawn a quarter as bright, the same light in all");
    Check(PictureCull.PointBrightness(1, -1, 2) == 0, "a craft seen against the Sun shows its dark side");
}

{
    /* A planet drawn at its past place: it hides what its drawn disc covers, not what it covers now. */
    var centres = new[] { new DVec3(0, 0, -1e9), new DVec3(0, 0, 1000) };
    var radii = new[] { 1.0, 100.0 };
    var eye = default(DVec3);
    var craft = new DVec3(0, 0, 2000);
    Check(DrawnSpheres.Blocked(eye, craft, centres, radii, 2), "a body drawn across the line of sight hides the craft");
    centres[1] = new DVec3(500, 0, 1000);
    Check(!DrawnSpheres.Blocked(eye, craft, centres, radii, 2), "the same body drawn off the line hides nothing, wherever it is now");
    centres[1] = new DVec3(double.NaN, double.NaN, double.NaN);
    Check(!DrawnSpheres.Blocked(eye, craft, centres, radii, 2), "a body the picture leaves out hides nothing");
    centres[1] = new DVec3(0, 0, 1000);
    Check(DrawnSpheres.Blocked(eye, craft, centres, radii, 1) == false, "only the counted bodies are tested");
    var sunCentres = new[] { new DVec3(0, 0, 1e6), new DVec3(0, 0, 10000) };
    var sunRadii = new[] { 10.0, 100.0 };
    Check(!DrawnSpheres.Shown(eye, new DVec3(0, 0, 20000), sunCentres, sunRadii, 2, 0), "a craft behind a drawn body is not shown");
    Check(!DrawnSpheres.Shown(eye, new DVec3(0, 0, 5000), sunCentres, sunRadii, 2, 0), "nor one in a drawn body's shadow");
    sunCentres[1] = new DVec3(5000, 0, 10000);
    Check(DrawnSpheres.Shown(eye, new DVec3(0, 0, 5000), sunCentres, sunRadii, 2, 0), "but is once the body is drawn clear of both lines");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

/* The Sun at the root, Kerbin drifting about it at one velocity, the Mun drifting about Kerbin. */
struct Tree : IBodyTree
{
    public DVec3 KerbinAt, KerbinVel, MunAt, MunVel;
    public bool TryParent(int body, out int parent)
    {
        parent = body == 2 ? 1 : 0;
        return body == 1 || body == 2;
    }
    public bool TryOffset(int body, double ut, out DVec3 offset)
    {
        offset = body == 1 ? KerbinAt + KerbinVel * ut : body == 2 ? MunAt + MunVel * ut : default;
        return body == 1 || body == 2;
    }
}
