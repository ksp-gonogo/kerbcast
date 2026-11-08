using System;
using System.Collections.Generic;
using System.IO;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

// Vantage factories carry the settings' defaults.
var pad = CommandVantage.LaunchSite();
Check(pad.Kind == CommandVantageKind.LaunchSite && pad.Name == "", "launch site defaults to the active craft's site");
Check(pad.Distance == 500 && pad.Bearing == 180 && pad.Height == 5, "launch site defaults 500 m, 180 deg, 5 m");
Check(pad.Invalid() == null, "default launch site is valid");
Check(CommandVantage.Station("KSC").Height == 30, "station height defaults to 30 m");
Check(CommandVantage.Surface("Kerbin", -0.1, -74.6).Height == 5, "surface height defaults to 5 m");

// Validation.
Check(CommandVantage.LaunchSite(distance: -1).Invalid() != null, "negative distance refused");
Check(CommandVantage.LaunchSite(bearing: double.NaN).Invalid() != null, "NaN bearing refused");
Check(CommandVantage.Station("").Invalid() != null, "station needs a node name");
Check(CommandVantage.Station("  ").Invalid() != null, "blank station name refused");
Check(CommandVantage.Surface("", 0, 0).Invalid() != null, "surface needs a body");
Check(CommandVantage.Surface("Mun", 91, 0).Invalid() != null, "latitude above 90 refused");
Check(CommandVantage.Surface("Mun", 10, double.PositiveInfinity).Invalid() != null, "infinite longitude refused");
Check(CommandVantage.Surface("Mun", 10, 200).Invalid() == null, "longitude outside -180..180 is accepted (it wraps)");
Check(CommandVantage.Vessel(0).Invalid() != null, "vessel vantage needs a persistentId");
Check(CommandVantage.Vessel(42).Invalid() == null, "vessel vantage with an id is valid");

// Default feed names.
Check(CommandCameraNaming.FeedName(null, CommandVantageKind.LaunchSite, "LaunchPad", "Launch Pad", "Kerbin") == "Launchpad camera",
    "stock pad is 'Launchpad camera'");
Check(CommandCameraNaming.FeedName(null, CommandVantageKind.LaunchSite, "Runway", "Runway", "Kerbin") == "Runway camera",
    "other launch site is '<site> camera'");
Check(CommandCameraNaming.FeedName(null, CommandVantageKind.Station, "KSC", "Kerbal Space Center", "Kerbin") == "Kerbal Space Center tracking",
    "station is '<station> tracking'");
Check(CommandCameraNaming.FeedName(null, CommandVantageKind.Station, "Harvester", "", "Kerbin") == "Harvester tracking",
    "station without a display name uses its node name");
Check(CommandCameraNaming.FeedName(null, CommandVantageKind.Vessel, "vessel:7", "Relay 1", "Mun") == "Relay 1 camera",
    "vessel is '<craft> camera'");
Check(CommandCameraNaming.FeedName("", CommandVantageKind.Surface, "surface:Mun:1:2", "Mun", "Mun") == "Mun surface camera",
    "surface is '<body> surface camera'");
Check(CommandCameraNaming.FeedName("  Hill cam ", CommandVantageKind.Surface, "x", "Mun", "Mun") == "Hill cam",
    "a given name wins, trimmed");

// Created wire ids: distinct per serial, never the default camera's.
uint c1 = CameraId.CommandCameraWireId(CameraId.CreatedCommandCameraKey(1));
uint c2 = CameraId.CommandCameraWireId(CameraId.CreatedCommandCameraKey(2));
Check(c1 != c2, "created serials get different ids");
Check(c1 == CameraId.CommandCameraWireId(CameraId.CreatedCommandCameraKey(1)), "created id is stable for its serial");
Check(c1 != CameraId.CommandCameraWireId("LaunchPad") && c1 != CameraId.CommandCameraWireId("KSC"),
    "created id differs from the default camera's");
Check(CameraId.IsKerbalId(c1), "created id is outside the part namespace");

// Request file parsing.
const string file = @"{""requests"":[
  {""id"":3,""op"":""create"",""token"":""t-1"",""name"":""Pad"",""vantage"":{""kind"":""launchSite"",""site"":""LaunchPad"",""distance"":300,""bearing"":90,""height"":10},""aim"":{""kind"":""vessel"",""persistentId"":77}},
  {""id"":4,""op"":""create"",""token"":""t-2"",""vantage"":{""kind"":""surface"",""body"":""Kerbin"",""latitude"":-0.05,""longitude"":-74.7,""height"":2},""aim"":{""kind"":""surface"",""body"":""Kerbin"",""latitude"":-0.0972,""longitude"":-74.5577,""altitude"":70,""framingMarginM"":40}},
  {""id"":5,""op"":""destroy"",""flightId"":2147483905},
  {""id"":6,""op"":""aim"",""flightId"":2147483905,""aim"":null},
  {""id"":7,""op"":""create"",""token"":""t-3"",""vantage"":{""kind"":""station"",""station"":""KSC""}},
  {""id"":8,""op"":""create"",""token"":""t-4"",""vantage"":{""kind"":""vessel"",""persistentId"":4000000001}},
  {""id"":9,""op"":""explode""},
  {""op"":""destroy"",""flightId"":1}
]}";
var skipped = new List<string>();
var reqs = CommandCameraRequest.ParseFile(file, skipped);
Check(reqs.Count == 6, "six well-formed requests parsed");
Check(skipped.Count == 2, "unknown op and missing id skipped");
var r3 = reqs[0];
Check(r3.Id == 3 && r3.Op == CommandRequestOp.Create && r3.Token == "t-1" && r3.Name == "Pad", "create header fields");
Check(r3.Vantage.Kind == CommandVantageKind.LaunchSite && r3.Vantage.Name == "LaunchPad"
    && r3.Vantage.Distance == 300 && r3.Vantage.Bearing == 90 && r3.Vantage.Height == 10, "launch site vantage fields");
Check(r3.Aim != null && r3.Aim.Kind == "vessel" && r3.Aim.PersistentId == 77 && r3.Aim.Invalid() == null, "vessel aim");
var r4 = reqs[1];
Check(r4.Vantage.Kind == CommandVantageKind.Surface && r4.Vantage.Body == "Kerbin" && r4.Vantage.Height == 2,
    "surface vantage fields");
Check(r4.Aim != null && r4.Aim.Kind == "surface" && r4.Aim.Altitude == 70 && r4.Aim.FramingMarginM == 40,
    "surface aim fields");
Check(r4.Aim.Invalid() == null, "surface aim is valid");
Check(reqs[2].Op == CommandRequestOp.Destroy && reqs[2].FlightId == 2147483905u, "destroy carries a full 32-bit flightId");
Check(reqs[3].Op == CommandRequestOp.Aim && reqs[3].Aim == null, "aim null returns to the default");
Check(reqs[4].Vantage.Kind == CommandVantageKind.Station && reqs[4].Vantage.Height == 30, "station height defaults when absent");
Check(reqs[5].Vantage.PersistentId == 4000000001u, "vessel vantage persistentId above 2^31");

Check(CommandCameraRequest.ParseFile("not json", skipped).Count == 0, "unreadable file yields nothing");
Check(CommandCameraRequest.ParseFile("{}", null).Count == 0, "file without requests yields nothing");
Check(CommandCameraRequest.ParseFile(@"{""requests"":[{""id"":1,""op"":""create"",""vantage"":{""kind"":""moon""}}]}", null).Count == 0,
    "unknown vantage kind skipped");

// Aim parsing and validation.
CommandAimSpec AimOf(string json) =>
    CommandCameraRequest.ParseFile(@"{""requests"":[{""id"":1,""op"":""aim"",""flightId"":5,""aim"":" + json + "}]}", null)[0].Aim;
Check(new CommandAimSpec { Kind = "vessel", PersistentId = 0 }.Invalid() != null, "vessel aim needs an id");
var search = AimOf(@"{""kind"":""vessel"",""persistentId"":7,""allowUncertain"":true}");
Check(search.AllowUncertain && search.PersistentId == 7 && search.Invalid() == null, "a vessel aim can allow an uncertain craft");
Check(!AimOf(@"{""kind"":""vessel"",""persistentId"":7}").AllowUncertain, "allowUncertain is false when absent");
Check(!AimOf(@"{""kind"":""vessel"",""persistentId"":7,""allowUncertain"":""yes""}").AllowUncertain, "and anything but true is false");
Check(new CommandAimSpec { Kind = "vessel", PersistentId = 9 }.Invalid() == null, "vessel aim with an id is valid");
Check(new CommandAimSpec { Kind = "surface", Body = "Mun", Latitude = 0, Longitude = 0, FramingMarginM = -1 }.Invalid() != null,
    "negative framing margin refused");
Check(new CommandAimSpec { Kind = "moon" }.Invalid() != null, "unknown aim kind refused");
var dir = AimOf(@"{""kind"":""direction"",""azimuth"":90,""elevation"":30,""framingMarginM"":5}");
Check(dir.Kind == "direction" && dir.Azimuth == 90 && dir.Elevation == 30 && dir.FramingMarginM == 5 && dir.Invalid() == null,
    "direction aim fields");
Check(AimOf(@"{""kind"":""direction"",""azimuth"":90}").Invalid() != null, "direction needs an elevation");
Check(AimOf(@"{""kind"":""direction"",""azimuth"":90,""elevation"":95}").Invalid() != null, "elevation above 90 refused");
var pos = AimOf(@"{""kind"":""positionAt"",""body"":""Kerbin"",""ut"":100,""position"":[1,2,3],""velocity"":[0,0,1],""subjectPersistentId"":4000000001}");
Check(pos.Position[2] == 3 && pos.Velocity[2] == 1 && pos.SubjectPersistentId == 4000000001u && pos.Invalid() == null,
    "positionAt aim fields and subject");
Check(AimOf(@"{""kind"":""positionAt"",""body"":""Kerbin"",""ut"":100,""position"":[1,2],""velocity"":[0,0,1]}").Invalid() != null,
    "positionAt needs three position components");
var orbit = AimOf(@"{""kind"":""orbit"",""body"":""Kerbin"",""inclination"":0,""eccentricity"":0.01,""semiMajorAxis"":700000,""lan"":0,""argumentOfPeriapsis"":0,""meanAnomalyAtEpoch"":0,""epoch"":0}");
Check(orbit.Invalid() == null && orbit.SubjectPersistentId == 0, "orbit aim without a subject is valid");
Check(AimOf(@"{""kind"":""orbit"",""body"":""Kerbin"",""inclination"":0}").Invalid() != null, "orbit needs every element");

// Ledger: each id once, in order; replays and stale files apply nothing.
var ledger = new CommandRequestLedger();
var fresh = ledger.Fresh(reqs);
Check(fresh.Count == 6 && fresh[0].Id == 3 && fresh[5].Id == 8, "first read applies every request in id order");
Check(ledger.LastAppliedId == 8, "last applied id advances");
Check(ledger.Fresh(reqs).Count == 0, "re-reading the same file applies nothing");
var older = CommandCameraRequest.ParseFile(@"{""requests"":[{""id"":4,""op"":""create"",""token"":""t-2"",""vantage"":{""kind"":""vessel"",""persistentId"":1}}]}", null);
Check(ledger.Fresh(older).Count == 0, "a stale flush with older ids applies nothing");
var shuffled = CommandCameraRequest.ParseFile(@"{""requests"":[{""id"":11,""op"":""destroy"",""flightId"":1},{""id"":10,""op"":""destroy"",""flightId"":2}]}", null);
var next = ledger.Fresh(shuffled);
Check(next.Count == 2 && next[0].Id == 10 && next[1].Id == 11, "new requests sorted by id");

// Tokens: once per scene.
Check(ledger.ClaimToken("t-1"), "first use of a token claims it");
Check(!ledger.ClaimToken("t-1"), "a reused token is refused");
Check(ledger.ClaimToken("") && ledger.ClaimToken(""), "empty token is never deduplicated");

// A new scene skips what the old one left in the file.
var scene = new CommandRequestLedger();
scene.SeedFrom(reqs);
Check(scene.LastAppliedId == 8 && scene.Fresh(reqs).Count == 0, "seeded ledger skips earlier requests");

// MiniJson edge cases.
Check(MiniJson.Parse(@"{""s"":""a\""b\\cA""}") is Dictionary<string, object> d && (string)d["s"] == "a\"b\\cA", "string escapes");
Check((double)MiniJson.Parse(" -1.5e2 ") == -150.0, "number with exponent");
bool threw = false;
try { MiniJson.Parse(@"{""a"":1,}"); } catch (FormatException) { threw = true; }
Check(threw, "trailing comma rejected");

// The request file is read the frame it changes, and otherwise only every
// SafetySeconds.
var poll = new CommandFilePoll();
Check(poll.Due(0.016f, 0), "the first frame reads the file");
Check(!poll.Due(0.016f, 0), "an unchanged count is not read again");
Check(poll.Due(0.016f, 1), "a new count is read on that frame");
Check(!poll.SignalMissed(true), "a change the count announced is no miss");
Check(poll.Due(0.016f, 2) && poll.Due(0.016f, 3), "counts on consecutive frames read on each frame");
Check(!poll.Due(0.1f, 3) && !poll.Due(0.1f, 3), "still unchanged inside the safety interval");
Check(poll.Due(0.1f, 3), "the safety read comes once the interval has passed");
Check(!poll.SignalMissed(false), "a safety read that finds nothing new is no miss");
Check(poll.SignalMissed(true), "a safety read that finds a change means the signal missed it");
Check(!poll.Due(0.016f, 3), "and restarts the interval");

/* Ten aims a second for five seconds at 60 fps, each write bumping the
   count: every one is read on the frame it lands, none waits for the
   safety read. */
var stream = new CommandFilePoll();
long count = 0;
int reads = 0, readOnArrival = 0;
for (int frame = 0; frame < 300; frame++)
{
    bool arrives = frame % 6 == 0;
    if (arrives) count++;
    if (!stream.Due(1f / 60f, count)) continue;
    reads++;
    if (arrives) readOnArrival++;
}
Check(readOnArrival == 50, $"every streamed write is read on its frame ({readOnArrival} of 50)");
Check(reads == 50, $"and the stream causes no extra reads ({reads})");

/* A read that throws is retried on the next frame, once; a second failure
   waits for the safety read. */
var flaky = new CommandFilePoll();
Check(flaky.Due(0.016f, 0), "the first frame reads");
Check(flaky.Due(0.016f, 1), "a new count is read");
flaky.ReadFailed();
Check(flaky.Due(0.016f, 1), "a failed read is retried on the next frame though the count has not moved");
Check(!flaky.SignalMissed(true), "a retry finding the change is no miss");
flaky.ReadFailed();
Check(!flaky.Due(0.016f, 1), "a failed retry is not retried every frame");
Check(!flaky.Due(0.1f, 1) && flaky.Due(0.2f, 1), "it waits for the safety read");
flaky.ReadFailed();
Check(flaky.Due(0.016f, 1), "which, failing, gets its own retry");
Check(!flaky.Due(0.016f, 1), "and a good read leaves no retry behind");

/* No signal at all (an older sidecar): the safety read alone, four a second. */
var none = new CommandFilePoll();
int noneReads = 0;
for (int frame = 0; frame < 60; frame++)
    if (none.Due(1f / 60f, 0)) noneReads++;
Check(noneReads == 4, $"without a signal the file is read every safety interval ({noneReads} in 1 s)");

/* The signal file as the sidecar lays it out: magic, then the count. */
string signalDir = Path.Combine(Path.GetTempPath(), "kc-signal-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(signalDir);
try
{
    string signalPath = Path.Combine(signalDir, "global.commands.signal");
    Check(ChangeSignal.Open(signalPath) == null, "a missing signal file does not open");
    File.WriteAllBytes(signalPath, new byte[16]);
    Check(ChangeSignal.Open(signalPath) == null, "a file without the magic does not open");
    File.WriteAllBytes(signalPath, new byte[8]);
    Check(ChangeSignal.Open(signalPath) == null, "a short file does not open");
    var bytes = new byte[16];
    BitConverter.GetBytes(ChangeSignal.Magic).CopyTo(bytes, 0);
    BitConverter.GetBytes(41L).CopyTo(bytes, 8);
    File.WriteAllBytes(signalPath, bytes);
    using (var signal = ChangeSignal.Open(signalPath))
    {
        Check(signal != null && signal.Count == 41, "the count reads through the mapping");
        using (var fs = new FileStream(signalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Seek(8, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes(42L), 0, 8);
        }
        Check(signal.Count == 42, "a bump by the writer shows in the next read");
    }
}
finally { Directory.Delete(signalDir, true); }

/* Saved tracking networks: requests, validation, slots, revisions, the file and the JSON. */
{
    string Save(string id = null, int rev = -1, string name = "Polar net", string vantages = null, string watch = null, string options = null) =>
        "{\"requests\":[{\"id\":1,\"op\":\"saveNetwork\",\"token\":\"t1\""
        + (id != null ? $",\"networkId\":\"{id}\"" : "")
        + (rev >= 0 ? $",\"baseRevision\":{rev}" : "")
        + (name != null ? $",\"name\":\"{name}\"" : "")
        + (watch != null ? $",\"watch\":{watch}" : "")
        + (options != null ? $",\"options\":{options}" : "")
        + ",\"vantages\":" + (vantages ?? "[{\"vantage\":{\"kind\":\"station\",\"station\":\"KSC\"},\"fallback\":true},{\"vantage\":{\"kind\":\"vessel\",\"persistentId\":77},\"label\":\"Relay 1\"}]")
        + "}]}";
    CommandCameraRequest One(string json)
    {
        var skipped = new List<string>();
        var list = CommandCameraRequest.ParseFile(json, skipped);
        Check(list.Count == 1 && skipped.Count == 0, "network request parses: " + string.Join("; ", skipped));
        return list[0];
    }

    var req = One(Save());
    Check(req.Op == CommandRequestOp.SaveNetwork && req.Token == "t1" && req.NetworkId == "" && req.BaseRevision == -1,
        "a create carries its token and no id or revision");
    Check(req.Vantages.Count == 2 && req.Vantages[0].Fallback && req.Vantages[0].Vantage.Name == "KSC"
        && req.Vantages[1].Vantage.PersistentId == 77 && req.Vantages[1].Label == "Relay 1", "vantages, labels and fallback are read");

    var set = new SavedNetworkSet();
    int ids = 0;
    Func<string> newId = () => "n" + (++ids);
    var made = set.Save(req, newId, out var before, out var saved);
    Check(made.Ok && made.NetworkId == "n1" && before == null, "a create is saved under a new id " + made.Code + " " + made.Text);
    Check(saved.Slot == 0 && saved.FlightId == 0xFFFF0000u && saved.Revision == 1, "the first network takes slot 0 at revision 1");
    var second = set.Save(One(Save(name: "  ")), newId, out _, out var saved2);
    Check(second.Ok && saved2.Slot == 1 && saved2.Name == "Tracking network", "a blank name reads Tracking network, next slot");

    var stale = set.Save(One(Save(id: "n1", rev: 0, name: "Renamed")), newId, out _, out _);
    Check(!stale.Ok && stale.Code == "stale", "an edit of an old revision is refused as stale");
    var edit = set.Save(One(Save(id: "n1", rev: 1, name: "Renamed", watch: "{\"kind\":\"vessel\",\"persistentId\":9}")), newId, out before, out saved);
    Check(edit.Ok && saved.Revision == 2 && saved.Slot == 0 && saved.WatchPersistentId == 9, "an edit keeps the slot and bumps the revision");
    Check(before.Name == "Polar net" && SavedNetwork.NeedsRebuild(before, saved), "a rename rebuilds the camera");
    Check(!set.Save(One(Save(id: "n9")), newId, out _, out _).Ok, "an unknown id is refused");
    Check(set.Save(One(Save(id: "n9")), newId, out _, out _).Code == "notFound", "as notFound");

    Check(set.Save(One(Save(vantages: "[]")), newId, out _, out _).Code == "noVantages", "no vantages refused");
    Check(set.Save(One(Save(vantages: "[{\"vantage\":{\"kind\":\"station\",\"station\":\"\"}}]")), newId, out _, out _).Code == "badVantage",
        "a station with no name refused");
    Check(set.Save(One(Save(vantages: "[{\"vantage\":{\"kind\":\"moon\"}}]")), newId, out _, out _).Code == "badVantage",
        "an unknown vantage kind is answered, not dropped");
    Check(set.Save(One(Save(watch: "{\"kind\":\"direction\",\"azimuth\":1,\"elevation\":2}")), newId, out _, out _).Code == "badAim",
        "only a craft can be watched");
    Check(set.Save(One(Save(name: new string('x', 49))), newId, out _, out _).Code == "badName", "a 49 character name refused");
    Check(set.Save(One(Save(options: "{\"marginStops\":-1}")), newId, out _, out _).Code == "badNumber", "a negative margin refused");
    var many = "[" + string.Join(",", System.Linq.Enumerable.Repeat("{\"vantage\":{\"kind\":\"station\",\"station\":\"KSC\"}}", 65)) + "]";
    Check(set.Save(One(Save(vantages: many)), newId, out _, out _).Code == "tooManyVantages", "65 vantages refused");
    Check(set.Networks.Count == 2, "refusals change nothing");

    Check(set.SetWatch(0xFFFF0000u, 0) && set.Find("n1").WatchPersistentId == 0 && set.Find("n1").Revision == 3,
        "a viewer's retarget writes back and bumps the revision");
    Check(!set.SetWatch(0xFFFF0000u, 0), "the same watch again is no change");
    Check(!set.SetWatch(12345u, 7), "a camera that is no saved network is left alone");

    var withOptions = set.Save(One(Save(options: "{\"minHoldUt\":20}")), newId, out _, out var opt);
    Check(withOptions.Ok && opt.Tuning.MinHoldUt == 20 && opt.Tuning.MarginStops == NetworkTuning.DefaultMarginStops,
        "options left out keep the defaults");

    var del = set.Delete(One("{\"requests\":[{\"id\":1,\"op\":\"deleteNetwork\",\"token\":\"t2\",\"networkId\":\"n2\"}]}"), out var removed);
    Check(del.Ok && removed.Slot == 1 && set.Find("n2") == null, "a delete removes the network");
    var again = set.Save(One(Save()), newId, out _, out var freshNet);
    Check(again.Ok && freshNet.Slot == 3, "a deleted slot is not handed out again straight away");

    var skippedCfg = new List<string>();
    string cfg = set.ToCfg();
    var loaded = SavedNetworkSet.FromCfg(cfg, skippedCfg);
    Check(skippedCfg.Count == 0 && loaded.Networks.Count == set.Networks.Count, "networks.cfg round trips");
    var n1 = loaded.Find("n1");
    Check(n1 != null && n1.Name == "Renamed" && n1.Revision == 3 && n1.Slot == 0 && n1.Vantages.Count == 2
        && n1.Vantages[0].Fallback && n1.Vantages[1].Label == "Relay 1" && n1.Vantages[1].Vantage.PersistentId == 77,
        "a loaded network keeps every field");
    Check(loaded.Find("n3").MinHoldUt == 20 && double.IsNaN(loaded.Find("n3").MarginStops), "options round trip, unset stays unset");
    var after = loaded.Save(One(Save()), newId, out _, out var nextAfterLoad);
    Check(after.Ok && nextAfterLoad.Slot == 4, "the next slot survives a reload");
    var broken = SavedNetworkSet.FromCfg("KERBCAST_NETWORKS\n{\n\tNETWORK\n\t{\n\t\tid = a\n\t\tslot = 2\n\t\tVANTAGE\n\t\t{\n\t\t\tkind = station\n\t\t}\n\t}\n"
        + "\tNETWORK\n\t{\n\t\tid = b\n\t\tslot = 2\n\t}\n\tNETWORK\n\t{\n\t\tid = c\n\t\tslot = 5\n\t\tname = Ok { net }\n\t\tVANTAGE\n\t\t{\n\t\t\tkind = vessel\n\t\t\tpersistentId = 4\n\t\t}\n\t}\n}\n", skippedCfg);
    Check(broken.Networks.Count == 1 && broken.Networks[0].NetworkId == "c" && broken.Networks[0].Name == "Ok { net }" && skippedCfg.Count == 2,
        "a network that no longer parses is skipped alone");
    Check(SavedNetworkSet.FromCfg("", null).Networks.Count == 0, "an empty file is no networks");

    set.Record(made);
    set.Record(stale);
    var host = new SavedNetwork { NetworkId = "host-5", Name = "Mod net", Owner = "some-mod", HostFlightId = 5,
        Vantages = new List<SavedVantage> { new SavedVantage { Vantage = CommandVantage.Station("KSC") } } };
    string json = set.Json(id => id == 0xFFFF0000u, new[] { host });
    var root = (Dictionary<string, object>)MiniJson.Parse(json);
    var nets = (List<object>)root["networks"];
    var j1 = (Dictionary<string, object>)nets[0];
    Check(nets.Count == 4, "every saved network and the mod's are listed");
    Check((string)j1["networkId"] == "n1" && (double)j1["flightId"] == 0xFFFF0000u && (double)j1["revision"] == 3 && !j1.ContainsKey("watch"),
        "a running network has its flightId; an active vessel watch is absent");
    var jv = (Dictionary<string, object>)((List<object>)j1["vantages"])[1];
    Check((string)((Dictionary<string, object>)jv["vantage"])["kind"] == "vessel" && (string)jv["label"] == "Relay 1" && !(bool)jv["fallback"],
        "a vantage is written in the wire's shape");
    Check(!((Dictionary<string, object>)nets[1]).ContainsKey("flightId"), "a network not running has no flightId");
    var jh = (Dictionary<string, object>)nets[3];
    Check((string)jh["owner"] == "some-mod" && (double)jh["flightId"] == 5, "a mod's network carries its owner and camera");
    var results = (List<object>)root["results"];
    Check(results.Count == 2 && (string)((Dictionary<string, object>)results[1])["code"] == "stale", "outcomes ride beside the networks");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
