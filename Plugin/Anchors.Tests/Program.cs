using System;
using System.Collections.Generic;
using System.Text;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}
bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;

/* Validation mirrors the sidecar's validate_anchor. */
Check(AnchorSpec.ForPart("port", 1, 42).Invalid() == null, "a part anchor with a part id is valid");
Check(AnchorSpec.ForPart("port", 1, 0).Invalid() != null, "a part anchor needs a part id");
Check(AnchorSpec.ForVessel("ship", 0).Invalid() != null, "a vessel anchor needs a craft");
Check(AnchorSpec.ForSurface("site", "Kerbin", 91, 0).Invalid() != null, "latitude must be within -90..90");
Check(AnchorSpec.ForSurface("site", "", 0, 0).Invalid() != null, "a surface anchor needs a body");
Check(AnchorSpec.ForDirection("pro", AnchorDirection.Prograde).Invalid() == null, "a named direction is valid");
Check(AnchorSpec.ForVector("v", 0, 0, 0).Invalid() != null, "a zero vector has no direction");
var both = AnchorSpec.ForVector("v", 1, 0, 0);
both.Direction = AnchorDirection.Sun;
Check(both.Invalid() != null, "a direction anchor takes one of a name and a vector, not both");
Check(AnchorSpec.ForVessel(new string('x', 65), 1).Invalid() != null, "ids are at most 64 characters");
var negMargin = AnchorSpec.ForVessel("m", 1);
negMargin.MarginM = -1;
Check(negMargin.Invalid() != null, "a margin is never negative");

/* The commands file the sidecar writes: requests beside the anchor table. */
string file = "{\"requests\":[],\"anchors\":["
    + "{\"owner\":\"peer:7:1\",\"flightId\":10,\"anchors\":["
    + "{\"id\":\"port\",\"kind\":\"part\",\"vessel\":5,\"part\":99,\"offset\":[0,0.5,0],\"orientation\":true},"
    + "{\"id\":\"pro\",\"kind\":\"direction\",\"direction\":\"targetRelativeVelocity\"},"
    + "{\"id\":\"site\",\"kind\":\"surface\",\"body\":\"Mun\",\"latitude\":1.5,\"longitude\":-20,\"occlusion\":true}]},"
    + "{\"owner\":\"peer:7:2\",\"flightId\":10,\"anchors\":[{\"id\":\"bad\",\"kind\":\"part\"}]}]}";
var skipped = new List<string>();
var sets = AnchorRegistry.ParseRemote(file, skipped);
Check(sets.Count == 1, "the valid set parses, the malformed one is skipped");
Check(skipped.Count == 1 && skipped[0].Contains("peer:7:2"), "the skip names the owner");
var port = sets[0].Anchors[0];
Check(port.Kind == AnchorKind.Part && port.Vessel == 5 && port.Part == 99 && port.Orientation, "part fields read");
Check(port.OffsetY == 0.5, "offset read");
Check(sets[0].Anchors[1].Direction == AnchorDirection.TargetRelativeVelocity, "named directions read");
var site = sets[0].Anchors[2];
Check(site.Body == "Mun" && site.Latitude == 1.5 && site.Longitude == -20 && site.Occlusion, "surface fields read");
Check(AnchorRegistry.ParseRemote("{\"requests\":[]}", null).Count == 0, "a file from an older sidecar has no anchors");
Check(AnchorRegistry.ParseRemote("not json", null).Count == 0, "an unreadable file has no anchors");

/* The registry: browser tables replace each other whole; game owners persist. */
var reg = new AnchorRegistry();
reg.ReplaceRemote(sets, null);
Check(!reg.Set("gonogo", 10, new List<AnchorSpec> { AnchorSpec.ForVessel("a", 1), AnchorSpec.ForVessel("a", 2) }, out var err)
    && err.Contains("twice"), "ids are unique per owner and camera");
var seventeen = new List<AnchorSpec>();
for (int i = 0; i < 17; i++) seventeen.Add(AnchorSpec.ForVessel("a" + i, 1));
Check(!reg.Set("gonogo", 10, seventeen, out _), "at most 16 per owner per camera");
Check(!reg.Set("peer:1:1", 10, new List<AnchorSpec>(), out _), "a game owner can not pose as a browser client");
Check(!reg.Set("kerbcast", 10, new List<AnchorSpec>(), out _), "nor as Kerbcast");
Check(reg.Set("gonogo", 10, new List<AnchorSpec> { AnchorSpec.ForVessel("target", 3) }, out _), "a game owner sets anchors");
var onCam = new List<KeyValuePair<string, AnchorSpec>>();
reg.ForCamera(10, onCam);
Check(onCam.Count == 4, "the camera has the browser's three and the game's one");
reg.ReplaceRemote(new List<RemoteAnchorSet>(), null);
reg.ForCamera(10, onCam);
Check(onCam.Count == 1 && onCam[0].Key == "gonogo", "an empty browser table clears only browser anchors");
reg.Set("gonogo", 10, null, out _);
Check(reg.Count == 0, "null clears a game owner's anchors");

/* Dock merges: a craft absorbed lives on as the survivor, through chains. */
reg.Merged(5, 8);
reg.Merged(8, 9);
Check(reg.Resolve(5) == 9 && reg.Resolve(8) == 9 && reg.Resolve(9) == 9, "merges chain to the survivor");
reg.Merged(9, 5);
Check(reg.Resolve(9) == 5 && reg.Resolve(5) == 5, "a merge back into an absorbed id does not loop");

/* Frame results: JSON the sidecar's FrameAnchor reads, never cut mid-object. */
var results = new List<AnchorResult>
{
    new AnchorResult
    {
        Id = "port", Owner = "peer:7:1", X = 640.123456, Y = 360, InFrame = true, DistanceM = 52,
        ApparentRadiusPx = 12, AngleOffAxisDeg = 0.25, HasOrientation = true,
        Forward = new DVec3(0, 0, -1), Up = new DVec3(0, 1, 0), HasOcclusion = true, Occluded = false,
    },
    new AnchorResult { Id = "pro \"q\"", Owner = "gonogo", X = 1, Y = 2, Behind = true, DistanceM = double.NaN },
};
var sb = new StringBuilder();
byte[] buf = null;
int len = AnchorJson.Encode(results, sb, ref buf, 4096);
string json = Encoding.UTF8.GetString(buf, 0, len);
Check(json.StartsWith("[{\"id\":\"port\",\"owner\":\"peer:7:1\",\"x\":640.1235,\"y\":360,\"inFrame\":true,\"behind\":false,\"distanceM\":52,"),
    "fields in wire names, rounded: " + json);
Check(json.Contains("\"forward\":[0,0,-1],\"up\":[0,1,0],\"occluded\":false}"), "orientation and occlusion when asked for");
Check(json.Contains("\"id\":\"pro \\\"q\\\"\"") && !json.Contains("NaN"), "strings escaped, a direction has no distance");
var parsed = MiniJson.Parse(json) as List<object>;
Check(parsed != null && parsed.Count == 2, "the array parses");
int small = AnchorJson.Encode(results, sb, ref buf, 300);
var first = MiniJson.Parse(Encoding.UTF8.GetString(buf, 0, small)) as List<object>;
Check(first != null && first.Count == 1, "past the capacity whole results are left out and the rest still parses");
Check(AnchorJson.Encode(new List<AnchorResult>(), sb, ref buf, 4096) == 0, "no results, no metadata");

/* Observed-only: Kerbcast's own always, browsers' under a lock unless relaxed, the game's never. */
Check(AnchorRules.Observed("kerbcast", false, AnchorPolicy.Full), "the subject anchor is observed with no lock");
Check(!AnchorRules.Observed("peer:1:2", false, AnchorPolicy.ObservedOnly), "a browser's anchors are full with no lock");
Check(AnchorRules.Observed("peer:1:2", true, AnchorPolicy.ObservedOnly), "and observed under a lock by default");
Check(!AnchorRules.Observed("peer:1:2", true, AnchorPolicy.Full), "unless the owner relaxes it");
Check(!AnchorRules.Observed("gonogo", true, AnchorPolicy.ObservedOnly), "anchors set in game are never limited");

string Json(AnchorResult r)
{
    var b = new StringBuilder();
    r.AppendJson(b);
    return b.ToString();
}
var seen = new AnchorResult
{
    Id = "subject", Owner = "kerbcast", X = 10, Y = 20, InFrame = true, DistanceM = 9e6,
    ApparentRadiusPx = 5, AngleOffAxisDeg = 3, HasOcclusion = true, Occluded = false, Observed = true,
    HasOrientation = true, Forward = new DVec3(0, 0, 1), Up = new DVec3(0, 1, 0),
};
Check(Json(seen) == "{\"id\":\"subject\",\"owner\":\"kerbcast\",\"observed\":true,\"x\":10,\"y\":20,\"inFrame\":true,"
    + "\"apparentRadiusPx\":5,\"forward\":[0,0,1],\"up\":[0,1,0],\"occluded\":false}",
    "observed and visible: position and size, never range, angle off axis or behind: " + Json(seen));
var hidden = seen;
hidden.Occluded = true;
Check(Json(hidden) == "{\"id\":\"subject\",\"owner\":\"kerbcast\",\"observed\":true,\"inFrame\":true,\"occluded\":true}",
    "observed and occluded: no coordinates at all: " + Json(hidden));
var gone = seen;
gone.InFrame = false;
gone.Behind = true;
Check(Json(gone) == "{\"id\":\"subject\",\"owner\":\"kerbcast\",\"observed\":true,\"inFrame\":false,\"occluded\":false}",
    "observed and out of frame: no coordinates, nothing about behind: " + Json(gone));
var untested = seen;
untested.HasOcclusion = false;
Check(!untested.ShowsPosition, "an observed result with no occlusion test shows nothing");
var full = seen;
full.Observed = false;
Check(Json(full).Contains("\"distanceM\":9000000") && Json(full).Contains("\"angleOffAxis\":3") && !Json(full).Contains("observed"),
    "a full result keeps everything");

/* Projection: a 90 degree lens looking down +Z with +Y up. */
var eye = new DVec3(0, 0, 0);
var fwd = new DVec3(0, 0, 1);
var up = new DVec3(0, 1, 0);
CommandCameraMath.PerspectiveTerms(90, 16.0 / 9.0, out double m00, out double m11);
Check(Near(m11, 1, 1e-12) && Near(m00, 9.0 / 16.0, 1e-12), "perspective terms for 90 degrees at 16:9");
var centre = CommandCameraMath.Project(eye, fwd, up, m00, m11, 0, 0, 1280, 720, new DVec3(0, 0, 100));
Check(Near(centre.X, 640, 1e-9) && Near(centre.Y, 360, 1e-9) && centre.InFrame && !centre.Behind, "on the axis lands at frame centre");
Check(Near(centre.Distance, 100, 1e-9) && Near(centre.AngleOffAxisDeg, 0, 1e-9), "distance and angle off axis");
var top = CommandCameraMath.Project(eye, fwd, up, m00, m11, 0, 0, 1280, 720, new DVec3(0, 100, 100));
Check(Near(top.Y, 0, 1e-9) && Near(top.X, 640, 1e-9), "45 degrees up is the top edge (y grows downward)");
Check(Near(top.AngleOffAxisDeg, 45, 1e-9), "and 45 degrees off axis");
var right = CommandCameraMath.Project(eye, fwd, up, m00, m11, 0, 0, 1280, 720, new DVec3(100 * 16.0 / 9.0, 0, 100));
Check(Near(right.X, 1280, 1e-6), "right is +x, at the right edge for the horizontal half-field");
var outside = CommandCameraMath.Project(eye, fwd, up, m00, m11, 0, 0, 1280, 720, new DVec3(0, 200, 100));
Check(!outside.InFrame && !outside.Behind && outside.Y < 0, "above the field: off the top, not behind");
var behind = CommandCameraMath.Project(eye, fwd, up, m00, m11, 0, 0, 1280, 720, new DVec3(10, 0, -100));
Check(behind.Behind && !behind.InFrame && behind.X > 1280, "behind and to the right: off the right edge");
Check(behind.AngleOffAxisDeg > 90, "behind is more than 90 degrees off axis");
var shifted = CommandCameraMath.Project(eye, fwd, up, m00, m11, 0.1, 0, 1280, 720, new DVec3(0, 0, 100));
Check(Near(shifted.X, 640 - 0.1 * 640, 1e-9), "an off-centre projection term moves the centre");
/* Far from the floating origin at double precision: a station 600 km out, a craft 3 km beyond. */
var far = new DVec3(600000.25, 1.5, -3.75);
var craft = far + new DVec3(0, 0, 3000);
var farP = CommandCameraMath.Project(far, fwd, up, m00, m11, 0, 0, 1280, 720, craft);
Check(Near(farP.X, 640, 1e-6) && Near(farP.Y, 360, 1e-6), "a lens far from the origin projects without float error");
Check(Near(CommandCameraMath.ApparentRadiusPx(10, 1000, 2, 1080), CommandCameraMath.BracketHalfPx(10, 1000, 2, 1080), 1e-12),
    "apparent radius is the command bracket's half-size");
Check(CommandCameraMath.ApparentRadiusPx(0, 1000, 2, 1080) == 0, "no radius, no size");
var cs = CommandCameraMath.ToCameraSpace(new DVec3(1, 0, 0), fwd, up);
Check(Near(cs.X, 1, 1e-12) && Near(cs.Y, 0, 1e-12) && Near(cs.Z, 0, 1e-12), "world right is camera x");
var yawed = CommandCameraMath.ToCameraSpace(new DVec3(0, 0, 1), new DVec3(1, 0, 0), up);
Check(Near(yawed.X, -1, 1e-12), "a lens turned right sees the old forward on its left");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
