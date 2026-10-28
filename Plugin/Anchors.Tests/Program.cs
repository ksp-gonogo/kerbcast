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
Check(!reg.Set("mission-control", 10, new List<AnchorSpec> { AnchorSpec.ForVessel("a", 1), AnchorSpec.ForVessel("a", 2) }, out var err)
    && err.Contains("twice"), "ids are unique per owner and camera");
var seventeen = new List<AnchorSpec>();
for (int i = 0; i < 17; i++) seventeen.Add(AnchorSpec.ForVessel("a" + i, 1));
Check(!reg.Set("mission-control", 10, seventeen, out _), "at most 16 per owner per camera");
Check(!reg.Set("peer:1:1", 10, new List<AnchorSpec>(), out _), "a game owner can not pose as a browser client");
Check(!reg.Set("kerbcast", 10, new List<AnchorSpec>(), out _), "nor as Kerbcast");
Check(reg.Set("mission-control", 10, new List<AnchorSpec> { AnchorSpec.ForVessel("target", 3) }, out _), "a game owner sets anchors");
var onCam = new List<KeyValuePair<string, AnchorSpec>>();
reg.ForCamera(10, onCam);
Check(onCam.Count == 4, "the camera has the browser's three and the game's one");
var framed = new List<KeyValuePair<string, AnchorSpec>> { new KeyValuePair<string, AnchorSpec>(AnchorRegistry.KerbcastOwner, AnchorSpec.ForVessel("subject", 7)) };
reg.AddForCamera(10, framed);
Check(framed.Count == 5 && framed[0].Key == AnchorRegistry.KerbcastOwner, "Kerbcast's own anchor stays first, ahead of every other owner's");
reg.ReplaceRemote(new List<RemoteAnchorSet>(), null);
reg.ForCamera(10, onCam);
Check(onCam.Count == 1 && onCam[0].Key == "mission-control", "an empty browser table clears only browser anchors");
reg.Set("mission-control", 10, null, out _);
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
    new AnchorResult { Id = "pro \"q\"", Owner = "mission-control", X = 1, Y = 2, Behind = true, DistanceM = double.NaN },
};
var sb = new StringBuilder();
byte[] buf = null;
int len = FrameMetaJson.Encode(double.NaN, null, results, sb, ref buf, 4096);
string json = Encoding.UTF8.GetString(buf, 0, len);
Check(json.StartsWith("{\"anchors\":[{\"id\":\"port\",\"owner\":\"peer:7:1\",\"x\":640.1235,\"y\":360,\"inFrame\":true,\"behind\":false,\"distanceM\":52,"),
    "fields in wire names, rounded: " + json);
Check(json.Contains("\"forward\":[0,0,-1],\"up\":[0,1,0],\"occluded\":false}"), "orientation and occlusion when asked for");
Check(json.Contains("\"id\":\"pro \\\"q\\\"\"") && !json.Contains("NaN"), "strings escaped, a direction has no distance");
var parsed = MiniJson.Parse(json) as Dictionary<string, object>;
Check(parsed != null && (parsed["anchors"] as List<object>)?.Count == 2 && parsed.Count == 1, "the object parses, anchors only");
int small = FrameMetaJson.Encode(double.NaN, null, results, sb, ref buf, 300);
var first = MiniJson.Parse(Encoding.UTF8.GetString(buf, 0, small)) as Dictionary<string, object>;
Check(first != null && (first["anchors"] as List<object>)?.Count == 1, "past the capacity whole results are left out and the rest still parses");
Check(FrameMetaJson.Encode(double.NaN, null, new List<AnchorResult>(), sb, ref buf, 4096) == 0, "nothing to say, no metadata");

/* A command camera's frame carries its state and the boundary beside its anchors. */
var cmd = CommandFrameState.Empty;
cmd.HasFix = true;
cmd.RangeM = 1500.25;
cmd.BracketHalfPx = 12.5;
cmd.FrameHeightPx = 720;
cmd.Occluder = "";
cmd.Sunlit = true;
cmd.AuthorityOwner = "mission";
cmd.SubjectPersistentId = 42;
cmd.AimKind = "vessel";
cmd.SubjectUt = 86398.123456789;
cmd.WorldModel = "model";
cmd.WorldModelSeq = 9;
cmd.KnowledgeUt = 86390;
cmd.Scope = "centre";
len = FrameMetaJson.Encode(0.05, cmd, results, sb, ref buf, 4096);
var meta = MiniJson.Parse(Encoding.UTF8.GetString(buf, 0, len)) as Dictionary<string, object>;
var c = meta?["command"] as Dictionary<string, object>;
Check(meta != null && (double)meta["boundaryM"] == 0.05 && (meta["anchors"] as List<object>)?.Count == 2, "boundary and anchors beside the command state");
Check(c != null && (bool)c["hasFix"] && (double)c["rangeM"] == 1500.25 && (double)c["bracketHalfPx"] == 12.5
    && (double)c["frameHeightPx"] == 720 && (bool)c["sunlit"] && !(bool)c["occluded"], "the bracket state reads back");
Check(c != null && (double)c["subjectUt"] == 86398.123456789, "UTs keep every digit");
Check(c != null && ((Dictionary<string, object>)c["authority"])["owner"] as string == "mission" && (double)c["worldModelSeq"] == 9
    && c["scope"] as string == "centre" && c["worldModel"] as string == "model", "the lock and the snapshot read");
Check(c != null && !c.ContainsKey("trueOffsetPx") && !c.ContainsKey("blank") && !c.ContainsKey("refusal"), "what does not apply is left out");
foreach (var key in c.Keys) Check(Array.IndexOf(CommandFrameState.Keys, key) >= 0, $"frame key '{key}' is a known one");
small = FrameMetaJson.Encode(0.05, cmd, results, sb, ref buf, 600);
var trimmed = MiniJson.Parse(Encoding.UTF8.GetString(buf, 0, small)) as Dictionary<string, object>;
Check(trimmed != null && trimmed.ContainsKey("command") && (trimmed["anchors"] as List<object>)?.Count == 1,
    "anchors give way to the command state, which is never cut");

/* A subject's refusal from its fix. */
Check(CommandFrameState.RefusalFor(42, CraftFixState.Positioned, null) == null, "refusal: a positioned subject has none");
Check(CommandFrameState.RefusalFor(0, CraftFixState.Unknown, null) == null, "refusal: no subject, none");
Check(CommandFrameState.RefusalFor(42, CraftFixState.Unknown, null) == "unknown", "refusal: unknown");
Check(CommandFrameState.RefusalFor(42, CraftFixState.Listed, null) == "notPositioned", "refusal: listed is notPositioned");
Check(CommandFrameState.RefusalFor(42, CraftFixState.Refused, "stale") == "stale", "refusal: stale");
Check(CommandFrameState.RefusalFor(42, CraftFixState.Refused, "noSignal") == "refused", "refusal: the provider's own code is refused");

/* Every field of a frame's state set to something other than its default, by reflection, so a new field is covered without editing this test. */
CommandFrameState Filled()
{
    object boxed = CommandFrameState.Empty;
    int n = 0;
    foreach (var f in typeof(CommandFrameState).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
    {
        n++;
        if (f.FieldType == typeof(bool)) f.SetValue(boxed, !(bool)f.GetValue(boxed));
        else if (f.FieldType == typeof(double)) f.SetValue(boxed, 1000.0 + n + 0.25);
        else if (f.FieldType == typeof(int)) f.SetValue(boxed, 700 + n);
        else if (f.FieldType == typeof(uint)) f.SetValue(boxed, (uint)(40 + n));
        else if (f.FieldType == typeof(long)) f.SetValue(boxed, (long)n);
        else if (f.FieldType == typeof(string)) f.SetValue(boxed, "v" + n);
        else throw new InvalidOperationException($"no test value for {f.FieldType} {f.Name}");
    }
    return (CommandFrameState)boxed;
}
var filled = Filled();
Check(filled.AuthorityOwner != null, "a filled state names an authority, so it reads as locked");

/* A filled frame writes every key it knows, and no other. */
{
    sb.Length = 0;
    filled.AppendJson(sb);
    var keys = (MiniJson.Parse(sb.ToString()) as Dictionary<string, object>).Keys;
    foreach (var key in CommandFrameState.Keys) Check(keys.Contains(key), $"a filled frame writes '{key}'");
    Check(keys.Count == CommandFrameState.Keys.Length, "and nothing else");
    var zeroSeq = CommandFrameState.Empty;
    zeroSeq.WorldModelSeq = 0;
    sb.Length = 0;
    zeroSeq.AppendJson(sb);
    Check(sb.ToString().Contains("\"worldModelSeq\":0"), "snapshot sequence 0 is still sent");
    sb.Length = 0;
    CommandFrameState.Empty.AppendJson(sb);
    Check(!sb.ToString().Contains("worldModelSeq"), "no snapshot read, no sequence");
    var wordy = CommandFrameState.Empty;
    wordy.RefusalText = new string('x', 5000);
    sb.Length = 0;
    wordy.AppendJson(sb);
    var text = (MiniJson.Parse(sb.ToString()) as Dictionary<string, object>)["refusalText"] as string;
    Check(text != null && text.Length == CommandFrameState.MaxRefusalText, "refusal text is capped");
}

/* Multi-byte anchor text past the limit gives way whole, and the state still goes. */
{
    var wide = new List<AnchorResult> { new AnchorResult { Id = new string('\u00e9', 300), Owner = "o", X = 1, Y = 2 } };
    int n = FrameMetaJson.Encode(0.05, CommandFrameState.Empty, wide, sb, ref buf, 500);
    var m = n > 0 ? MiniJson.Parse(Encoding.UTF8.GetString(buf, 0, n)) as Dictionary<string, object> : null;
    Check(m != null && m.ContainsKey("command") && !m.ContainsKey("anchors"), "an overflow retries without anchors");
}

/*
 * The status file's command fields are the 1.11 bracket contract, sent whole
 * with or without a lock so an older client keeps its brackets and range.
 * The key sets are frozen: a new field fails here, and belongs on the frames.
 */
{
    var events = new List<CommandTrackEvent>
    {
        new CommandTrackEvent(100, false, "", true, null),
        new CommandTrackEvent(101.5, true, "Mun", false, "Held"),
        new CommandTrackEvent(104, false, "", true, "Held", true),
    };

    void TrackKeys(Dictionary<string, object> track, string when)
    {
        foreach (var key in track.Keys) Check(Array.IndexOf(CommandTrackJson.TrackKeys, key) >= 0, $"{when}: track key '{key}' is a 1.11 key");
        foreach (var key in CommandTrackJson.TrackKeys) Check(track.ContainsKey(key), $"{when}: the 1.11 field '{key}' is sent");
        foreach (var e in (List<object>)track["events"])
        {
            var ev = (Dictionary<string, object>)e;
            Check(ev.ContainsKey("ut"), "every event carries its UT");
            foreach (var key in ev.Keys) Check(Array.IndexOf(CommandTrackJson.EventKeys, key) >= 0, $"event key '{key}' is known");
        }
    }

    void CameraKeys(Dictionary<string, object> cam, string when)
    {
        foreach (var key in cam.Keys) Check(key == "end" || Array.IndexOf(CommandTrackJson.CameraKeys, key) >= 0, $"{when}: camera key '{key}' is a 1.11 key");
        foreach (var key in CommandTrackJson.CameraKeys) Check(cam.ContainsKey(key), $"{when}: the camera field '{key}' is sent");
    }

    sb.Length = 0;
    CommandTrackJson.Append(sb, 101.75, filled, events);
    var locked = MiniJson.Parse(sb.ToString()) as Dictionary<string, object>;
    Check(locked != null, "locked track parses");
    TrackKeys(locked, "locked");
    Check((double)locked["ut"] == 101.75 && ((List<object>)locked["events"]).Count == 3, "the lock keeps the UT and the events");
    Check(locked["hasFix"] is bool fix && fix && locked["rangeM"] != null && locked["bracketHalfPx"] != null,
        "a locked track still carries the fix, range and bracket");
    Check(locked["authority"] is Dictionary<string, object> auth && auth["owner"] as string == filled.AuthorityOwner, "a locked track names its owner");

    sb.Length = 0;
    sb.Append('{');
    CommandTrackJson.AppendCameraFields(sb, filled, 12.5, "Probe");
    sb.Append("\"end\": 0}");
    CameraKeys(MiniJson.Parse(sb.ToString()) as Dictionary<string, object>, "locked");

    var open = filled;
    open.AuthorityOwner = null;
    open.Blank = "Held";
    sb.Length = 0;
    CommandTrackJson.Append(sb, 101.75, open, events);
    var unlocked = MiniJson.Parse(sb.ToString()) as Dictionary<string, object>;
    TrackKeys(unlocked, "open");
    Check(unlocked["authority"] == null && unlocked["blank"] as string == "Held", "with no lock, no owner");
    sb.Length = 0;
    sb.Append('{');
    CommandTrackJson.AppendCameraFields(sb, open, 12.5, "Probe");
    sb.Append("\"end\": 0}");
    CameraKeys(MiniJson.Parse(sb.ToString()) as Dictionary<string, object>, "open");

    var noFix = CommandFrameState.Empty;
    sb.Length = 0;
    CommandTrackJson.Append(sb, 5, noFix, null);
    var none = MiniJson.Parse(sb.ToString()) as Dictionary<string, object>;
    Check(none != null && none["rangeM"] == null && none["bracketHalfPx"] == null && none["authority"] == null
        && ((List<object>)none["events"]).Count == 0, "no fix sends nulls, as 1.11 did");
}

/* Observed-only: Kerbcast's own always, browsers' under a lock unless relaxed, the game's never. */
Check(AnchorRules.Observed("kerbcast", false, AnchorPolicy.Full), "the subject anchor is observed with no lock");
Check(!AnchorRules.Observed("peer:1:2", false, AnchorPolicy.ObservedOnly), "a browser's anchors are full with no lock");
Check(AnchorRules.Observed("peer:1:2", true, AnchorPolicy.ObservedOnly), "and observed under a lock by default");
Check(!AnchorRules.Observed("peer:1:2", true, AnchorPolicy.Full), "unless the owner relaxes it");
Check(!AnchorRules.Observed("mission-control", true, AnchorPolicy.ObservedOnly), "anchors set in game are never limited");
Check(!AnchorRules.Observed("peer:1:2", true, AnchorPolicy.ObservedOnly, AnchorKind.Kinematic)
    && !AnchorRules.Observed("kerbcast", false, AnchorPolicy.Full, AnchorKind.Kinematic),
    "a kinematic anchor is caller-supplied knowledge, never limited to what the picture shows");
Check(AnchorRules.Observed("peer:1:2", true, AnchorPolicy.ObservedOnly, AnchorKind.Vessel), "other kinds keep the owner's rule");

/* Kinematic anchors: validation mirrors the world model's records, and the wire parses into them. */
Check(AnchorSpec.ForConic("c", "Kerbin", 5, 0.1, 700000, 0, 0, 0, 0).Invalid() == null, "kinematic: a conic is valid");
Check(AnchorSpec.ForConic("c", "Kerbin", 5, 1.5, -2e6, 0, 0, 0, 0).Invalid() == null, "kinematic: a hyperbola with a negative semi-major axis is valid");
Check(AnchorSpec.ForConic("c", "Kerbin", 5, 1.5, 2e6, 0, 0, 0, 0).Invalid() != null, "kinematic: a hyperbola with a positive semi-major axis is not");
Check(AnchorSpec.ForConic("c", "Kerbin", 5, 1.0, 1e6, 0, 0, 0, 0).Invalid() != null, "kinematic: a parabola is not");
Check(AnchorSpec.ForConic("c", "", 5, 0.1, 700000, 0, 0, 0, 0).Invalid() != null, "kinematic: a body is required");
Check(AnchorSpec.ForConic("c", "Kerbin", double.NaN, 0.1, 700000, 0, 0, 0, 0).Invalid() != null, "kinematic: non-finite elements are not");
Check(AnchorSpec.ForStateVector("s", "Kerbin", 100, new DVec3(7e5, 0, 0), new DVec3(0, 2200, 0)).Invalid() == null, "kinematic: a state vector is valid");
Check(AnchorSpec.ForStateVector("s", "Kerbin", 100, new DVec3(0, 0, 0), new DVec3(0, 2200, 0)).Invalid() != null, "kinematic: a state at the body's centre is not");
Check(AnchorSpec.ForSurfaceMotion("g", "Mun", 10, 20).Invalid() == null && AnchorSpec.ForSurfaceMotion("g", "Mun", 91, 20).Invalid() != null,
    "kinematic: a surface point needs a latitude within -90..90");
Check(new AnchorSpec { Id = "k", Kind = AnchorKind.Kinematic, Body = "Kerbin" }.Invalid() != null, "kinematic: an anchor with no kinematics is not");
Check(AnchorSpec.ForSurfaceMotion("g", new string('b', AnchorSpec.MaxBodyLength + 1), 0, 0).Invalid() != null
    && AnchorSpec.ForSurfaceMotion("g", new string('b', AnchorSpec.MaxBodyLength), 0, 0).Invalid() == null,
    "kinematic: the body name is held to the sidecar's length");
Check(AnchorSpec.ForStateVector("s", "Kerbin", 1, new DVec3(1e-200, 0, 0), new DVec3(0, 1, 0)).Invalid() == null,
    "kinematic: a tiny position whose magnitude underflows is not the centre, as the sidecar compares components");

/* A surface point under a light speed stays on the body as drawn; conics and state vectors go to their light instant. */
Check(!AnchorRules.PlacedAtLightInstant(AnchorMotion.Surface) && AnchorRules.PlacedAtLightInstant(AnchorMotion.Conic)
    && AnchorRules.PlacedAtLightInstant(AnchorMotion.StateVector), "kinematic: only paths are placed at the light instant");

/* Under ObservedOnly a kinematic anchor keeps its knowledge but not the lens's pose. */
Check(AnchorRules.PoseWithheld("peer:1:2", true, AnchorPolicy.ObservedOnly, AnchorKind.Kinematic)
    && !AnchorRules.PoseWithheld("peer:1:2", true, AnchorPolicy.Full, AnchorKind.Kinematic)
    && !AnchorRules.PoseWithheld("peer:1:2", false, AnchorPolicy.ObservedOnly, AnchorKind.Kinematic)
    && !AnchorRules.PoseWithheld("peer:1:2", true, AnchorPolicy.ObservedOnly, AnchorKind.Vessel),
    "kinematic: the pose is withheld only where the anchor would be observed");
{
    string Enc(AnchorResult r)
    {
        var b = new StringBuilder();
        r.AppendJson(b);
        return b.ToString();
    }
    var inView = new AnchorResult { Id = "ghost", Owner = "peer:1:2", X = 100, Y = 50, InFrame = true, DistanceM = 4.2e5, ApparentRadiusPx = 3, AngleOffAxisDeg = 1.5, PoseWithheld = true };
    var offFrame = inView;
    offFrame.Id = "hypothesis";
    offFrame.InFrame = false;
    offFrame.X = 4000;
    var back = offFrame;
    back.Id = "impact";
    back.Behind = true;
    bool noPose = true;
    foreach (var r in new[] { inView, offFrame, back })
    {
        var j = Enc(r);
        noPose &= !j.Contains("distanceM") && !j.Contains("behind") && !j.Contains("angleOffAxis") && !j.Contains("observed");
    }
    Check(noPose, "kinematic under ObservedOnly: no range, no behind and no angle off axis on any of three results");
    Check(Enc(inView).Contains("\"x\":100") && Enc(inView).Contains("\"apparentRadiusPx\":3"), "kinematic under ObservedOnly: in frame it is placed and sized");
    Check(!Enc(offFrame).Contains("\"x\"") && !Enc(back).Contains("\"x\"") && !Enc(back).Contains("apparentRadiusPx"),
        "kinematic under ObservedOnly: out of frame it gives no bearing");
}

/* The registry keeps copies, so a caller's later change can not leave a path built from the old spec. */
{
    var kreg = new AnchorRegistry();
    var spec = AnchorSpec.ForConic("c", "Kerbin", 5, 0.1, 700000, 0, 0, 0, 0);
    Check(kreg.Set("mission-control", 3, new List<AnchorSpec> { spec }, out _), "kinematic: a conic anchor is set");
    spec.SemiMajorAxis = 900000;
    var held = new List<KeyValuePair<string, AnchorSpec>>();
    kreg.ForCamera(3, held);
    Check(held.Count == 1 && !ReferenceEquals(held[0].Value, spec) && held[0].Value.SemiMajorAxis == 700000 && !held[0].Value.PathTried,
        "kinematic: the registry holds its own copy, with no path built");
}
{
    var o = MiniJson.Parse("{\"id\":\"ghost\",\"kind\":\"kinematic\",\"marginM\":50,\"kinematics\":{\"kind\":\"stateVector\",\"body\":\"Kerbin\","
        + "\"ut\":123.5,\"position\":[700000,1,2],\"velocity\":[3,2200,4]}}") as Dictionary<string, object>;
    var a = AnchorSpec.FromJson(o, out string why);
    Check(a != null && a.Kind == AnchorKind.Kinematic && a.Motion == AnchorMotion.StateVector && a.Body == "Kerbin" && a.Ut == 123.5
        && a.Position.X == 700000 && a.Position.Z == 2 && a.Velocity.Y == 2200 && a.MarginM == 50, "kinematic: a state vector parses " + why);
    var conic = AnchorSpec.FromJson(MiniJson.Parse("{\"id\":\"hyp\",\"kind\":\"kinematic\",\"kinematics\":{\"kind\":\"conic\",\"body\":\"Mun\","
        + "\"inclination\":5,\"eccentricity\":0.2,\"semiMajorAxis\":300000,\"lan\":10,\"argumentOfPeriapsis\":20,\"meanAnomalyAtEpoch\":1,\"epoch\":50}}")
        as Dictionary<string, object>, out why);
    Check(conic != null && conic.Motion == AnchorMotion.Conic && conic.Body == "Mun" && conic.SemiMajorAxis == 300000 && conic.Epoch == 50, "kinematic: a conic parses " + why);
    var bad = AnchorSpec.FromJson(MiniJson.Parse("{\"id\":\"x\",\"kind\":\"kinematic\",\"kinematics\":{\"kind\":\"stateVector\",\"body\":\"Kerbin\",\"ut\":1}}")
        as Dictionary<string, object>, out why);
    Check(bad == null && why.Contains("position"), "kinematic: a state vector without its vectors is refused: " + why);
    var none = AnchorSpec.FromJson(MiniJson.Parse("{\"id\":\"x\",\"kind\":\"kinematic\"}") as Dictionary<string, object>, out why);
    Check(none == null && why != null, "kinematic: no kinematics is refused: " + why);
    var stray = AnchorSpec.FromJson(MiniJson.Parse("{\"id\":\"pad\",\"kind\":\"surface\",\"body\":\"Kerbin\",\"latitude\":1,\"longitude\":2,"
        + "\"kinematics\":{\"kind\":\"surface\",\"body\":\"Mun\",\"latitude\":3,\"longitude\":4}}") as Dictionary<string, object>, out why);
    Check(stray == null && why.Contains("kinematic anchor only"), "kinematics on another kind is refused, as the sidecar refuses it: " + why);
}

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
var deadA = CommandCameraMath.Project(eye, fwd, up, m00, m11, 0, 0, 1280, 720, new DVec3(1e-9, -1e-9, -1e6));
var deadB = CommandCameraMath.Project(eye, fwd, up, m00, m11, 0, 0, 1280, 720, new DVec3(-1e-9, 1e-9, -1e6));
Check(deadA.Behind && Near(deadA.X, 640, 1e-6) && deadA.Y > 720, "dead behind: pinned below the frame");
Check(Near(deadA.X, deadB.X, 1e-6) && Near(deadA.Y, deadB.Y, 1e-6), "dead behind: rounding noise does not pick the side");
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
