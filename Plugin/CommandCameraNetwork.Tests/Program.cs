using System;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}
bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;

VantageInput Seeing(double rangeM, double elevationDeg, double sunElevationDeg, double resolution = 1.0, double air = 1.0) => new VantageInput
{
    Resolved = true,
    Placed = true,
    HasSubject = true,
    Fix = CraftFixState.Positioned,
    RangeM = rangeM,
    Optics = new OpticsSite
    {
        ResolutionArcsec = resolution,
        ZenithSeeingArcsec = 1.0,
        SeeingAirmassPower = 0.6,
        AirFactor = air,
        ElevationDeg = elevationDeg,
        SunElevationDeg = sunElevationDeg,
    },
};

// Scoring: the design's worked example, a craft at 100 km over Kerbin.
{
    var a = Seeing(100000, 90, 60);
    var b = Seeing(133000, 45, -30);
    var c = Seeing(237000, 15, -30);
    double sa = NetworkMath.Score(a, OpticsModel.DaySkyStops);
    double sb = NetworkMath.Score(b, OpticsModel.DaySkyStops);
    double sc = NetworkMath.Score(c, OpticsModel.DaySkyStops);
    Check(Near(sa, -0.96, 0.02) && Near(sb, -0.13, 0.03) && Near(sc, -2.17, 0.05), $"level 3 lens scores the worked example ({sa:F2}, {sb:F2}, {sc:F2})");
    Check(sb > sa && sa > sc, "night at a moderate angle beats overhead in daylight, which beats low at night");
    double la = NetworkMath.Score(Seeing(100000, 90, 60, 15), OpticsModel.DaySkyStops);
    double lb = NetworkMath.Score(Seeing(133000, 45, -30, 15), OpticsModel.DaySkyStops);
    double lc = NetworkMath.Score(Seeing(237000, 15, -30, 15), OpticsModel.DaySkyStops);
    Check(Near(la, -4.37, 0.03) && Near(lb, -3.37, 0.05) && Near(lc, -4.80, 0.05), $"level 1 lens scores the worked example ({la:F2}, {lb:F2}, {lc:F2})");
    Check(Near(NetworkMath.Score(a, 0), sa + OpticsModel.DaySkyStops, 1e-9), "the daylight weight is the one tunable cost of full day");
    Check(NetworkMath.Score(a, 3.0) < sb - 2, "a heavier daylight weight pushes a daylit station further down");
    var space = Seeing(500000, 10, 60, 1, 0);
    Check(Near(NetworkMath.Score(space, OpticsModel.DaySkyStops), -Math.Log(500000 * 1.0 / OpticsModel.ArcsecPerRadian, 2), 1e-9),
        "a lens with no air pays only for range and lens");
    Check(NetworkMath.Score(Seeing(50000, 30, -30), 1.5) > NetworkMath.Score(Seeing(100000, 30, -30), 1.5), "nearer is clearer, all else equal");
}

// Gate: each vantage either sees the craft or says why not.
{
    var v = Seeing(100000, 45, -30);
    Check(NetworkMath.Gate(v) == VantageState.Ok, "a placed, clear vantage sees");
    var t = v; t.Resolved = false; Check(NetworkMath.Gate(t) == VantageState.Unresolved, "an unplaced site is unresolved");
    t = v; t.Disabled = true; Check(NetworkMath.Gate(t) == VantageState.Disabled, "a station the game turned off is disabled");
    t = v; t.Gone = true; Check(NetworkMath.Gate(t) == VantageState.Gone, "a ridden craft that is gone is gone");
    t = v; t.IsSubject = true; Check(NetworkMath.Gate(t) == VantageState.IsSubject, "a lens on the watched craft is out");
    t = v; t.HasSubject = false; Check(NetworkMath.Gate(t) == VantageState.NoSubject, "with no craft to follow nothing sees");
    t = v; t.TooFar = true; Check(NetworkMath.Gate(t) == VantageState.TooFar, "beyond the tracking range is too far");
    t = v; t.BelowHorizon = true; Check(NetworkMath.Gate(t) == VantageState.BelowHorizon, "under the lens's own skyline is below the horizon");
    t = v; t.Occluded = true; Check(NetworkMath.Gate(t) == VantageState.Occluded, "behind another body is occluded");
    t = v; t.Placed = false; t.RangeM = 0; Check(NetworkMath.Gate(t) == VantageState.Unplaced, "a positioned craft seen from a lens with no position is no view, never a perfect one");
    Check(!NetworkMath.Placeable(VantageState.Unplaced) && !NetworkMath.Placeable(VantageState.Pending), "an unplaced or unscored vantage cannot stand in");
    t = v; t.Disabled = true; t.Occluded = true; Check(NetworkMath.Gate(t) == VantageState.Disabled, "a disabled station reports that, not what it would see");
    Check(NetworkMath.Code(VantageState.BelowHorizon) == "belowHorizon" && NetworkMath.Code(VantageState.Disabled) == "disabled"
        && NetworkMath.Code(VantageState.Ok) == "ok" && NetworkMath.Code(VantageState.TooFar) == "tooFar", "state codes are the wire's");
    Check(!NetworkMath.Placeable(VantageState.Disabled) && !NetworkMath.Placeable(VantageState.Unresolved) && !NetworkMath.Placeable(VantageState.Gone)
        && NetworkMath.Placeable(VantageState.Occluded), "only a placed, enabled vantage can stand in");
    Check(!NetworkMath.Placeable(VantageState.IsSubject), "never one whose lens rides the craft it watches");
}

// World-model fairness: each vantage gates only on what its own site knows.
{
    var knows = Seeing(100000, 45, -30);
    var blind = knows; blind.Fix = CraftFixState.Unknown;
    var refused = knows; refused.Fix = CraftFixState.Refused;
    var listed = knows; listed.Fix = CraftFixState.Listed;
    Check(NetworkMath.Gate(blind) == VantageState.Unknown && NetworkMath.Gate(refused) == VantageState.Unknown
        && NetworkMath.Gate(listed) == VantageState.Unknown, "a site that cannot place the craft fails the gate, whatever another site knows");
    var states = new[] { NetworkMath.Gate(blind), NetworkMath.Gate(knows) };
    var scores = new[] { 99.0, -3.0 };
    Check(NetworkMath.Best(states, scores, 2, 0) == 1, "an unknowing site's score never counts, however good its geometry");
    var sw = new NetworkSwitch();
    var start = sw.Decide(new[] { VantageState.Unknown, VantageState.Unknown }, new[] { 5.0, 1.0 }, 2, -1, false, null, 0, 0, NetworkTuning.Default);
    Check(start.Cuts && start.Index == 0 && sw.NoView, "when no site knows the craft the camera starts at the first placed vantage with no view, not where truth says");
}

// Labels.
{
    Check(NetworkMath.Label("North dish", "Crater Rim Station", "Crater Rim") == "North dish", "a host's label replaces the vantage's own name");
    Check(NetworkMath.Label(" ", "Crater Rim Station", "Crater Rim") == "Crater Rim Station", "a blank label keeps the site's name");
    Check(NetworkMath.Label(null, "", "vessel:42") == "vessel:42", "a site with no name reads as its key");
    Check(NetworkMath.Label(new string('x', 500), "KSC", "KSC").Length == NetworkMath.MaxLabelLength, "a long label is cut, since it rides every frame");
}

// Disabled stations follow stock's own rule for building a station's node.
{
    Check(NetworkMath.StationEnabled(true, false, false, false), "every station with the extra ground stations on");
    Check(!NetworkMath.StationEnabled(false, false, false, true), "an extra station is off with them off");
    Check(NetworkMath.StationEnabled(false, true, false, false), "the home station is always on");
    Check(NetworkMath.StationEnabled(false, false, true, true) && !NetworkMath.StationEnabled(false, false, true, false),
        "a permanent station only where other launch sites are allowed");
}

// Hysteresis.
{
    var t = NetworkTuning.Default;
    var ok = new[] { VantageState.Ok, VantageState.Ok, VantageState.Ok };
    var sw = new NetworkSwitch();
    var p = sw.Decide(ok, new[] { -2.0, -1.0, -3.0 }, 3, -1, false, null, 100, 1000, t);
    Check(p.Index == 1 && p.Reason == CutReason.Start, "the camera starts at the clearest vantage");
    sw.CutTo(p.Index, 100, 1000);
    p = sw.Decide(ok, new[] { 0.0, -1.0, -3.0 }, 3, -1, false, null, 105, 1010, t);
    Check(!p.Cuts, "within 10 game seconds of a cut it holds, however much better another is");
    p = sw.Decide(ok, new[] { 0.0, -1.0, -3.0 }, 3, -1, false, null, 1000, 1002, t);
    Check(!p.Cuts, "at high warp it holds for 3 real seconds too");
    p = sw.Decide(ok, new[] { -0.6, -1.0, -3.0 }, 3, -1, false, null, 110, 1003, t);
    Check(!p.Cuts, "after the hold, 0.4 stops better is not clearly better");
    p = sw.Decide(ok, new[] { -0.5, -1.0, -3.0 }, 3, -1, false, null, 110, 1003, t);
    Check(p.Index == 0 && p.Reason == CutReason.Better, "0.5 stops better is");
    sw.CutTo(0, 110, 1003);
    p = sw.Decide(ok, new[] { -0.5, -0.5, -3.0 }, 3, -1, false, null, 200, 1100, t);
    Check(!p.Cuts, "a tie keeps the current vantage");
    Check(Near(t.HoldUtAt(1000), 3000, 1e-9) && Near(t.HoldUtAt(1), 10, 1e-9), "the hold lasts the longer of the two floors in game time");
    var back = new NetworkSwitch();
    back.CutTo(0, 5000, 1000);
    var reverted = back.Decide(ok, new[] { -2.0, 0.0, -3.0 }, 3, -1, false, null, 4000, 1010, t);
    Check(reverted.Index == 1 && reverted.Reason == CutReason.Better, "game time run back by a revert ends the hold instead of stretching it");
    Check(!new NetworkTuning { MinHoldUt = -1, MinHoldRealSeconds = 3, MarginStops = 0.5 }.Valid
        && !new NetworkTuning { MinHoldUt = 10, MinHoldRealSeconds = double.NaN, MarginStops = 0.5 }.Valid && t.Valid, "negative or NaN tuning is refused");
}

// Loss of sight and the forecast override the hold.
{
    var t = NetworkTuning.Default;
    var sw = new NetworkSwitch();
    sw.CutTo(0, 100, 1000);
    var p = sw.Decide(new[] { VantageState.BelowHorizon, VantageState.Ok, VantageState.Ok }, new[] { 0.0, -1.0, -2.0 }, 3, -1, false, null, 101, 1000.1, t);
    Check(p.Index == 1 && p.Reason == CutReason.Lost, "a vantage that has lost sight is left at once, inside the hold, for the best that can see");
    var ok = new[] { VantageState.Ok, VantageState.Ok, VantageState.Ok };
    p = sw.Decide(ok, new[] { 0.0, -1.0, -2.0 }, 3, -1, true, null, 101, 1000.1, t);
    Check(p.Index == 1 && p.Reason == CutReason.Forecast, "a forecast loss leaves inside the hold, even for a worse vantage");
    p = sw.Decide(ok, new[] { 0.0, -1.0, -2.0 }, 3, -1, true, new[] { true, false, true }, 101, 1000.1, t);
    Check(p.Index == 2, "but never for a vantage that would itself lose the craft during its hold");
    p = sw.Decide(ok, new[] { 0.0, -1.0, -2.0 }, 3, -1, true, new[] { true, false, false }, 101, 1000.1, t);
    Check(!p.Cuts, "with nowhere clear ahead it stays and reacts when the loss comes");
}

// No vantage can see.
{
    var t = NetworkTuning.Default;
    var none = new[] { VantageState.Occluded, VantageState.BelowHorizon, VantageState.Occluded };
    var sw = new NetworkSwitch();
    sw.CutTo(1, 0, 0);
    var p = sw.Decide(none, new[] { 0.0, 0.0, 0.0 }, 3, -1, false, null, 1, 1, t);
    Check(!p.Cuts && sw.NoView, "it stays on the last vantage with no view, not hopping between blind ones");
    p = sw.Decide(none, new[] { 0.0, 0.0, 0.0 }, 3, 2, false, null, 1, 1, t);
    Check(p.Index == 2 && p.Reason == CutReason.Fallback && sw.NoView, "or moves to the host's fallback");
    sw.CutTo(2, 1, 1);
    p = sw.Decide(none, new[] { 0.0, 0.0, 0.0 }, 3, 2, false, null, 2, 2, t);
    Check(!p.Cuts, "and stays there");
    var stuck = new NetworkSwitch();
    stuck.CutTo(0, 0, 0);
    p = stuck.Decide(new[] { VantageState.Gone, VantageState.Occluded, VantageState.Occluded }, new[] { 0.0, 0.0, 0.0 }, 3, -1, false, null, 1, 1, t);
    Check(p.Index == 1 && p.Reason == CutReason.Unplaceable, "with no view it still leaves a vantage it may not stand at");
    stuck.CutTo(1, 1, 1);
    p = stuck.Decide(new[] { VantageState.Occluded, VantageState.IsSubject, VantageState.Occluded }, new[] { 0.0, 0.0, 0.0 }, 3, -1, false, null, 2, 2, t);
    Check(p.Index == 0 && p.Reason == CutReason.Unplaceable, "a lens now riding the craft it watches moves too");
    var nowhere = new NetworkSwitch();
    p = nowhere.Decide(new[] { VantageState.Disabled, VantageState.Unresolved }, new[] { 0.0, 0.0 }, 2, -1, false, null, 1, 1, t);
    Check(!p.Cuts && nowhere.NothingPlaceable, "with nowhere to stand nothing is picked, and the camera says so (the feed blanks)");
    Check(!stuck.NothingPlaceable, "while somewhere can stand in, it is not nothing");
    var disabledFallback = new[] { VantageState.Occluded, VantageState.Occluded, VantageState.Disabled };
    var sw2 = new NetworkSwitch();
    sw2.CutTo(0, 0, 0);
    Check(!sw2.Decide(disabledFallback, new[] { 0.0, 0.0, 0.0 }, 3, 2, false, null, 1, 1, t).Cuts, "a fallback the game has turned off is not used");
    p = sw.Decide(new[] { VantageState.Ok, VantageState.Occluded, VantageState.Occluded }, new[] { -9.0, 0.0, 0.0 }, 3, 2, false, null, 2.5, 2.5, t);
    Check(p.Index == 0 && p.Reason == CutReason.Regained && !sw.NoView, "the moment one regains sight it cuts there, inside the hold and with no margin");
}

// A list that changes under the camera.
{
    var t = NetworkTuning.Default;
    var sw = new NetworkSwitch();
    sw.CutTo(1, 0, 0);
    sw.Renumber(-1);
    var p = sw.Decide(new[] { VantageState.Ok, VantageState.Ok }, new[] { -1.0, -2.0 }, 2, -1, false, null, 1, 1, t);
    Check(p.Index == 0 && p.Reason == CutReason.Removed, "a removed current vantage cuts at once to the best");
    Check(!new NetworkSwitch().Decide(new VantageState[0], new double[0], 0, -1, false, null, 0, 0, t).Cuts, "an empty list never cuts");
    var fresh = new NetworkSwitch();
    fresh.Renumber(-1);
    Check(fresh.Decide(new[] { VantageState.Ok }, new[] { -1.0 }, 1, -1, false, null, 1, 1, t).Reason == CutReason.Start,
        "a list replaced before the first cut still starts, rather than reading as removed");
    var blindStart = new NetworkSwitch();
    var inside = blindStart.Decide(new[] { VantageState.IsSubject, VantageState.Occluded }, new[] { 0.0, 0.0 }, 2, 0, false, null, 1, 1, t);
    Check(inside.Index == 1, "with no view it never rides the craft it watches, even as the fallback");
}

// The ground station list the sidecar reads.
{
    var stations = new[]
    {
        new GroundStation { NodeName = "KSC", DisplayName = "Kerbal \"Space\" Center", Body = "Kerbin", BodyIndex = 1, Latitude = -0.0972, Longitude = -74.5577, Altitude = 64.8, Enabled = true },
        new GroundStation { NodeName = "Nowhere", DisplayName = "", Body = "Kerbin", Latitude = double.NaN, Longitude = 0, Altitude = 0 },
        new GroundStation { NodeName = "Crater Rim", DisplayName = "", Body = "Kerbin", BodyIndex = 1, Latitude = 9.4, Longitude = -172.1, Altitude = 4000, Enabled = false },
    };
    string json = GroundStationList.Json(stations);
    Check(json == "{\"stations\":[{\"nodeName\":\"KSC\",\"displayName\":\"Kerbal \\\"Space\\\" Center\",\"body\":\"Kerbin\",\"latitude\":-0.0972,\"longitude\":-74.5577,\"altitude\":64.8,\"enabled\":true},"
        + "{\"nodeName\":\"Crater Rim\",\"displayName\":\"Crater Rim\",\"body\":\"Kerbin\",\"latitude\":9.4,\"longitude\":-172.1,\"altitude\":4000,\"enabled\":false}]}\n",
        "the station list is the sidecar's shape: escaped names, a missing name falls back to the node, an unplaceable station is left out");
    Check(GroundStationList.Json(new GroundStation[0]) == "{\"stations\":[]}\n", "no stations is an empty list, not a missing one");
}

// The log line proves a cut from KSP.log alone.
{
    var line = NetworkMath.CutLine(7, 1234.5, new NetworkPick { Index = 1, Reason = CutReason.Forecast }, 0,
        new[] { "KSC", "Crater Rim" }, new[] { "KSC", "CraterRim" }, new[] { VantageState.Ok, VantageState.Ok }, new[] { -1.234, -2.5 }, 2);
    Check(line.Contains("event=network-cut reason=forecast") && line.Contains("from='KSC' (KSC)") && line.Contains("to='Crater Rim' (CraterRim)")
        && line.Contains("fromScore=-1.23") && line.Contains("toScore=-2.50") && line.Contains("UT=1234.50"), "one line names from, to, why and the scores");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
