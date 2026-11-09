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
Check(double.IsNaN(dir.FovDeg), "an aim without fovDeg leaves the field to its framing");
var wide = AimOf(@"{""kind"":""direction"",""azimuth"":180,""elevation"":5,""fovDeg"":30}");
Check(wide.FovDeg == 30 && wide.Invalid() == null, "an aim carries a field of view");
Check(AimOf(@"{""kind"":""direction"",""azimuth"":180,""elevation"":5,""fovDeg"":0}").Invalid() != null, "a field of 0 refused");
Check(AimOf(@"{""kind"":""vessel"",""persistentId"":7,""fovDeg"":-2}").Invalid() != null, "a negative field refused");
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
    int tokens = 0;
    string Save(string id = null, int rev = -1, string name = "Polar net", string vantages = null, string watch = null, string options = null,
        string token = null, string queuedAt = null) =>
        "{\"requests\":[{\"id\":1,\"op\":\"saveNetwork\",\"token\":\"" + (token ?? "t" + (++tokens)) + "\""
        + (queuedAt != null ? $",\"queuedAtMs\":{queuedAt}" : "")
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
    Check(req.Op == CommandRequestOp.SaveNetwork && req.Token == "t1" && req.NetworkId == "" && req.BaseRevision == -1 && double.IsNaN(req.QueuedAtMs),
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

    Check(set.SetWatch(0xFFFF0000u, 0) && set.Find("n1").WatchPersistentId == 0 && set.Find("n1").Revision == 3
        && set.Find("n1").ContentRevision == 2, "a viewer's retarget writes back and bumps the revision, not the content revision");
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
    Check(SavedNetworkSet.TryFromCfg(cfg, skippedCfg, out var loaded), "the written file reads back");
    Check(skippedCfg.Count == 0 && loaded.Networks.Count == set.Networks.Count, "networks.cfg round trips");
    var n1 = loaded.Find("n1");
    Check(n1 != null && n1.Name == "Renamed" && n1.Revision == 3 && n1.ContentRevision == 2 && n1.CreateToken == "t1" && n1.Slot == 0 && n1.Vantages.Count == 2
        && n1.Vantages[0].Fallback && n1.Vantages[1].Label == "Relay 1" && n1.Vantages[1].Vantage.PersistentId == 77,
        "a loaded network keeps every field");
    Check(loaded.Find("n3").MinHoldUt == 20 && double.IsNaN(loaded.Find("n3").MarginStops), "options round trip, unset stays unset");
    var after = loaded.Save(One(Save()), newId, out _, out var nextAfterLoad);
    Check(after.Ok && nextAfterLoad.Slot == 4, "the next slot survives a reload");
    string brokenText = "KERBCAST_NETWORKS\n{\n\tNETWORK\n\t{\n\t\tid = a\n\t\tslot = 2\n\t\tVANTAGE\n\t\t{\n\t\t\tkind = telescope\n\t\t\tmirror = 8\n\t\t}\n\t}\n"
        + "\tNETWORK\n\t{\n\t\tid = b\n\t\tslot = 2\n\t}\n\tNETWORK\n\t{\n\t\tid = c\n\t\tslot = 5\n\t\tname = Ok { net }\n\t\tVANTAGE\n\t\t{\n\t\t\tkind = vessel\n\t\t\tpersistentId = 4\n\t\t}\n\t}\n}\n";
    skippedCfg.Clear();
    Check(SavedNetworkSet.TryFromCfg(brokenText, skippedCfg, out var broken), "a file with networks this version can not read still loads");
    Check(broken.Networks.Count == 1 && broken.Networks[0].NetworkId == "c" && broken.Networks[0].Name == "Ok { net }" && skippedCfg.Count == 2,
        "a network that does not parse is set aside alone");
    string rewritten = broken.ToCfg();
    Check(rewritten.Contains("kind = telescope") && rewritten.Contains("mirror = 8") && rewritten.Contains("id = b"),
        "networks set aside are written back as they were, not erased");
    var afterBroken = broken.Save(One(Save()), newId, out _, out var notOnKept);
    Check(afterBroken.Ok && notOnKept.Slot != 2 && notOnKept.Slot != 5, "a set-aside network's slot is never handed out");
    Check(SavedNetworkSet.TryFromCfg("", null, out var emptySet) && emptySet.Networks.Count == 0, "an empty file is no networks");
    Check(!SavedNetworkSet.TryFromCfg("garbage \u0001 not a cfg", null, out _), "a file that is not a networks file is unreadable");
    Check(!SavedNetworkSet.TryFromCfg(cfg.Substring(0, cfg.Length / 2), null, out _), "a file cut off part way is unreadable");
    Check(!SavedNetworkSet.TryFromCfg("OTHER\n{\n}\n", null, out _), "a cfg with no KERBCAST_NETWORKS node is unreadable");

    /* The same create token twice is one network: a retry or a carried-over request finds the first. */
    var firstMade = set.Save(One(Save(token: "dup")), newId, out _, out var dupNet);
    var repeat = set.Save(One(Save(token: "dup", name: "Other")), newId, out var repBefore, out var repSaved);
    Check(firstMade.Ok && repeat.Ok && repeat.NetworkId == dupNet.NetworkId && repSaved == null && repBefore == null && dupNet.Name == "Polar net",
        "a repeated create token returns the network it made and changes nothing");

    /* A retarget since the form opened does not make the form stale; a content edit does. */
    var n1Now = set.Find("n1");
    Check(set.Save(One(Save(id: "n1", rev: 2, name: "Renamed")), newId, out _, out _).Ok, "an edit based before a retarget is accepted");
    Check(set.Save(One(Save(id: "n1", rev: 3, name: "Again")), newId, out _, out _).Code == "stale", "an edit based before another edit is stale");
    Check(set.Save(One(Save(id: "n1", rev: 99, name: "Again")), newId, out _, out _).Code == "stale", "a revision from the future is stale");

    /* Undo puts a refused save or delete back exactly. */
    uint revBefore = n1Now.Revision;
    var edit2 = set.Save(One(Save(id: "n1", rev: (int)revBefore, name: "Changed")), newId, out var b2, out var s2);
    set.Undo(b2, s2);
    Check(edit2.Ok && set.Find("n1").Name == "Renamed" && set.Find("n1").Revision == revBefore, "an undone edit restores the network");
    int netCount = set.Networks.Count;
    set.Save(One(Save()), newId, out var b3, out var s3);
    set.Undo(b3, s3);
    Check(set.Networks.Count == netCount && set.Find(s3.NetworkId) == null, "an undone create removes the network");
    var victim = set.Networks[1];
    int at = set.IndexOf(victim);
    set.Delete(One("{\"requests\":[{\"id\":1,\"op\":\"deleteNetwork\",\"token\":\"t9\",\"networkId\":\"" + victim.NetworkId + "\"}]}"), out var gone);
    set.Undo(null, null, gone, at);
    Check(set.Networks.Count == netCount && set.IndexOf(victim) == at, "an undone delete puts the network back in its place");

    /* Saves and deletes queued while a scene loaded are carried into the next; anything else, or older, is not. */
    var queuedFile = CommandCameraRequest.ParseFile("{\"requests\":["
        + "{\"id\":5,\"queuedAtMs\":100000,\"op\":\"saveNetwork\",\"token\":\"a\",\"vantages\":[]},"
        + "{\"id\":6,\"queuedAtMs\":30000,\"op\":\"deleteNetwork\",\"token\":\"b\",\"networkId\":\"n1\"},"
        + "{\"id\":7,\"queuedAtMs\":100000,\"op\":\"destroy\",\"flightId\":3},"
        + "{\"id\":8,\"op\":\"deleteNetwork\",\"token\":\"c\",\"networkId\":\"n1\"},"
        + "{\"id\":4,\"queuedAtMs\":95000,\"op\":\"deleteNetwork\",\"token\":\"d\",\"networkId\":\"n1\"}]}", null);
    var carried = CommandRequestLedger.CarriedOver(queuedFile, 110000);
    Check(carried.Count == 2 && carried[0].Token == "d" && carried[1].Token == "a",
        "only recent network requests carry over, in id order");

    set.Record(made, 1234);
    set.Record(stale, 0);
    var host = new SavedNetwork { NetworkId = "host-5", Name = "Mod net", Owner = "some-mod", HostFlightId = 5,
        Vantages = new List<SavedVantage> { new SavedVantage { Vantage = CommandVantage.Station("KSC") } } };
    string json = set.Json(id => id == 0xFFFF0000u, new[] { host }, editable: true);
    var root = (Dictionary<string, object>)MiniJson.Parse(json);
    var nets = (List<object>)root["networks"];
    var j1 = (Dictionary<string, object>)nets[0];
    Check(nets.Count == set.Networks.Count + 1 && (bool)root["editable"], "every saved network and the mod's are listed, and whether saves apply");
    Check((string)j1["networkId"] == "n1" && (double)j1["flightId"] == 0xFFFF0000u && (double)j1["revision"] == set.Find("n1").Revision && !j1.ContainsKey("watch"),
        "a running network has its flightId; an active vessel watch is absent");
    var jv = (Dictionary<string, object>)((List<object>)j1["vantages"])[1];
    Check((string)((Dictionary<string, object>)jv["vantage"])["kind"] == "vessel" && (string)jv["label"] == "Relay 1" && !(bool)jv["fallback"],
        "a vantage is written in the wire's shape");
    Check(!((Dictionary<string, object>)nets[1]).ContainsKey("flightId"), "a network not running has no flightId");
    Check((string)((Dictionary<string, object>)nets[0])["createToken"] == "t1", "a network carries the token that made it");
    var jh = (Dictionary<string, object>)nets[nets.Count - 1];
    Check((string)jh["owner"] == "some-mod" && (double)jh["flightId"] == 5, "a mod's network carries its owner and camera");
    var results = (List<object>)root["results"];
    Check(results.Count == 2 && (string)((Dictionary<string, object>)results[1])["code"] == "stale", "outcomes ride beside the networks");
    Check((double)((Dictionary<string, object>)results[0])["atMs"] == 1234 && !((Dictionary<string, object>)results[1]).ContainsKey("atMs"),
        "an outcome carries when it was decided");
    Check(!((bool)((Dictionary<string, object>)MiniJson.Parse(set.Json(_ => false, null, editable: false)))["editable"]),
        "a scene leaving says saves do not apply");

    /* networks.cfg on disk: a save keeps the last file as .bak, and a missing or empty file is recovered. */
    string netDir = Path.Combine(Path.GetTempPath(), "kerbcast-networks-" + Guid.NewGuid().ToString("N"));
    string path = Path.Combine(netDir, "Kerbcast", "networks.cfg");
    var diskLog = new List<string>();
    Check(SavedNetworkFile.Load(path, diskLog, out var noFile) == SavedNetworkFile.LoadResult.Ok && noFile.Networks.Count == 0 && diskLog.Count == 0,
        "no file yet is no networks, and saves may go ahead");
    SavedNetworkFile.Save(path, set.ToCfg());
    Check(File.Exists(path) && !File.Exists(path + ".tmp") && !File.Exists(path + ".bak"), "the first save writes the file alone");
    string firstText = File.ReadAllText(path);
    set.Save(One(Save(name: "Later")), newId, out _, out _);
    SavedNetworkFile.Save(path, set.ToCfg());
    Check(File.ReadAllText(path + ".bak") == firstText && File.ReadAllText(path).Contains("Later"), "the next save keeps the last file as .bak");
    Check(SavedNetworkFile.Load(path, diskLog, out var reread) == SavedNetworkFile.LoadResult.Ok && reread.Networks.Count == set.Networks.Count,
        "the saved file loads");

    File.WriteAllText(path, "");
    diskLog.Clear();
    Check(SavedNetworkFile.Load(path, diskLog, out var fromBak) == SavedNetworkFile.LoadResult.Ok && fromBak.Networks.Count == set.Networks.Count - 1
        && diskLog.Count == 1 && diskLog[0].Contains(".bak"), "an empty file is recovered from .bak");
    File.Delete(path);
    File.WriteAllText(path + ".tmp", set.ToCfg());
    diskLog.Clear();
    Check(SavedNetworkFile.Load(path, diskLog, out var fromTmp) == SavedNetworkFile.LoadResult.Ok && fromTmp.Networks.Count == set.Networks.Count
        && diskLog[0].Contains(".tmp"), "a save cut off before its swap is recovered from .tmp");
    File.WriteAllText(path, firstText.Substring(0, firstText.Length - 20));
    diskLog.Clear();
    Check(SavedNetworkFile.Load(path, diskLog, out var cut) == SavedNetworkFile.LoadResult.Unreadable && cut.Networks.Count == 0 && diskLog.Count == 1,
        "a truncated file is unreadable, so nothing saves over it");
    File.WriteAllText(path, "just some text");
    Check(SavedNetworkFile.Load(path, null, out _) == SavedNetworkFile.LoadResult.Unreadable, "a garbage file is unreadable");

    /* A read that fails is not a missing file: the real file must not be replaced by an older spare. */
    File.WriteAllText(path, set.ToCfg());
    File.WriteAllText(path + ".bak", firstText);
    bool lockedThrew = false;
    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        try { SavedNetworkFile.Load(path, null, out _); }
        catch (IOException) { lockedThrew = true; }
    }
    Check(lockedThrew, "a transient read error reaches the caller, which goes read only");
    File.Delete(path);
    File.WriteAllText(path + ".bak", firstText);
    Check(SavedNetworkFile.Load(path, null, out var stillRecovers) == SavedNetworkFile.LoadResult.Ok && stillRecovers.Networks.Count > 0,
        "a file truly missing still recovers from its spare");
    string folderAtPath = Path.Combine(netDir, "folder.cfg");
    Directory.CreateDirectory(folderAtPath);
    bool folderThrew = false;
    try { SavedNetworkFile.Load(folderAtPath, null, out _); }
    catch (Exception) { folderThrew = true; }
    Check(folderThrew, "a path that exists but cannot be read is not taken for a missing file");

    /* An edit under a held lock is stored, but the live aim is the holder's. */
    var planA = new SavedNetwork { Name = "A", WatchPersistentId = 1 };
    var planWatch = new SavedNetwork { Name = "A", WatchPersistentId = 2 };
    var planName = new SavedNetwork { Name = "B", WatchPersistentId = 1 };
    var freePlan = SavedNetwork.PlanEdit(planA, planWatch, running: true, lockHeld: false);
    Check(!freePlan.Rebuild && freePlan.ApplyWatch && !freePlan.CarryLiveAim, "without a lock a changed watch is aimed at at once");
    var heldPlan = SavedNetwork.PlanEdit(planA, planWatch, running: true, lockHeld: true);
    Check(!heldPlan.Rebuild && !heldPlan.ApplyWatch, "under a held lock a changed watch is stored, not aimed at");
    var heldRename = SavedNetwork.PlanEdit(planA, planName, running: true, lockHeld: true);
    Check(heldRename.Rebuild && heldRename.CarryLiveAim && !heldRename.ApplyWatch, "a rename under a held lock rebuilds and keeps the holder's aim");
    var freeRename = SavedNetwork.PlanEdit(planA, planName, running: true, lockHeld: false);
    Check(freeRename.Rebuild && !freeRename.CarryLiveAim, "a rename without a lock starts on the saved watch");
    Check(SavedNetwork.PlanEdit(planA, planA, running: false, lockHeld: false).Rebuild, "a camera not running is started");

    /* Docking retires a craft's id: saved vantages and the watch follow into the survivor. */
    var mergeSet = new SavedNetworkSet();
    string craftList = "[{\"vantage\":{\"kind\":\"vessel\",\"persistentId\":77},\"label\":\"Relay\",\"fallback\":true},{\"vantage\":{\"kind\":\"station\",\"station\":\"KSC\"}}]";
    mergeSet.Save(One(Save(vantages: craftList, watch: "{\"kind\":\"vessel\",\"persistentId\":88}")), newId, out _, out var mergeNet);
    uint mergeRevBefore = mergeNet.Revision, contentBefore = mergeNet.ContentRevision;
    Check(!mergeSet.FollowMerge(5, 6) && mergeNet.Revision == mergeRevBefore, "a merge of craft no network names changes nothing");
    Check(mergeSet.FollowMerge(77, 99) && mergeNet.Vantages[0].Vantage.PersistentId == 99 && mergeNet.Vantages[0].Label == "Relay" && mergeNet.Vantages[0].Fallback
        && mergeNet.WatchPersistentId == 88 && mergeNet.Revision == mergeRevBefore + 1 && mergeNet.ContentRevision == contentBefore,
        "a merged craft vantage follows the survivor, keeping its label and fallback, and leaves open forms valid");
    Check(!mergeSet.FollowMerge(77, 99), "KSP's second announcement of a merge is a no-op");
    Check(mergeSet.FollowMerge(88, 99) && mergeNet.WatchPersistentId == 99, "a watch on the merged craft follows it");
    Check(!mergeSet.FollowMerge(0, 99) && !mergeSet.FollowMerge(99, 0) && !mergeSet.FollowMerge(99, 99), "a merge with no craft or into itself is ignored");
    SavedNetworkSet.TryFromCfg(mergeSet.ToCfg(), null, out var mergeUnsaved);
    Check(mergeUnsaved.Networks[0].Vantages[0].Vantage.PersistentId == 77 && mergeUnsaved.Networks[0].WatchPersistentId == 88,
        "until the game saves a merge the file keeps the original craft, so a quickload finds the network there");
    Check(!mergeSet.SettleMerges(id => id == 77 || id == 88 || id == 99), "a save still holding the absorbed craft, as a quickload's does, settles nothing");
    Check(!mergeSet.SettleMerges(id => false), "a save without the survivor settles nothing");
    SavedNetworkSet.TryFromCfg(mergeSet.ToCfg(), null, out mergeUnsaved);
    Check(mergeUnsaved.Networks[0].Vantages[0].Vantage.PersistentId == 77, "and leaves the file on the original craft");
    Check(mergeSet.SettleMerges(id => id == 99), "a save holding the merge settles it");
    Check(!mergeSet.SettleMerges(id => id == 99), "a later save has nothing more to settle");
    SavedNetworkSet.TryFromCfg(mergeSet.ToCfg(), null, out var mergeReload);
    Check(mergeReload.Networks[0].Vantages[0].Vantage.PersistentId == 99 && mergeReload.Networks[0].WatchPersistentId == 99, "the settled ids reach the file");
    Check(System.Linq.Enumerable.Count(mergeSet.Watching()) == 1, "a network with a craft to watch is put back after a lock change");
    mergeSet.SetWatch(mergeNet.FlightId, 0);
    Check(System.Linq.Enumerable.Count(mergeSet.Watching()) == 0, "a network on the active vessel has no watch to put back");

    /* A form opened before a dock still names the absorbed craft. */
    string craftSeven = "[{\"vantage\":{\"kind\":\"vessel\",\"persistentId\":7}}]";
    string watchSeven = "{\"kind\":\"vessel\",\"persistentId\":7}";
    var formSet = new SavedNetworkSet();
    formSet.Save(One(Save(vantages: craftSeven, watch: watchSeven)), newId, out _, out var formNet);
    string formId = formNet.NetworkId;
    formSet.FollowMerge(7, 9);
    Check(formSet.Save(One(Save(id: formId, rev: 2, vantages: craftSeven, watch: watchSeven)), newId, out _, out formNet).Ok
        && formNet.WatchPersistentId == 9 && formNet.Vantages[0].Vantage.PersistentId == 9,
        "a save naming the absorbed craft lands on the survivor");
    SavedNetworkSet.TryFromCfg(formSet.ToCfg(), null, out var formFile);
    Check(formFile.Networks[0].WatchPersistentId == 7 && formFile.Networks[0].Vantages[0].Vantage.PersistentId == 7,
        "and stays pending for the file until the game saves the merge");
    string craftNine = "[{\"vantage\":{\"kind\":\"vessel\",\"persistentId\":9}}]";
    string watchNine = "{\"kind\":\"vessel\",\"persistentId\":9}";
    formSet.Save(One(Save(id: formId, rev: (int)formNet.Revision, vantages: craftNine, watch: watchNine)), newId, out _, out formNet);
    SavedNetworkSet.TryFromCfg(formSet.ToCfg(), null, out formFile);
    Check(formNet.WatchPersistentId == 9 && formFile.Networks[0].WatchPersistentId == 7 && formFile.Networks[0].Vantages[0].Vantage.PersistentId == 7,
        "a form re-sending the survivor it was shown leaves the merge pending");
    formSet.FollowMerge(9, 11);
    SavedNetworkSet.TryFromCfg(formSet.ToCfg(), null, out formFile);
    Check(formNet.WatchPersistentId == 11 && formFile.Networks[0].WatchPersistentId == 7, "a second dock keeps the id from before the first");
    Check(formSet.SettleMerges(id => id == 11), "a save holding the chain settles it");
    Check(formSet.Save(One(Save(id: formId, rev: (int)formNet.Revision, vantages: craftSeven, watch: watchSeven)), newId, out _, out formNet).Ok
        && formNet.WatchPersistentId == 11, "after the save the absorbed id still resolves through both docks");
    SavedNetworkSet.TryFromCfg(formSet.ToCfg(), null, out formFile);
    Check(formFile.Networks[0].WatchPersistentId == 11 && formFile.Networks[0].Vantages[0].Vantage.PersistentId == 11, "and is written as the survivor");
    Check(formSet.FollowMerge(11, 7) && formNet.WatchPersistentId == 7 && formSet.Save(One(Save(id: formId, rev: (int)formNet.Revision, vantages: craftSeven, watch: watchSeven)), newId, out _, out formNet).Ok
        && formNet.WatchPersistentId == 7, "a craft absorbed earlier that survives a later dock resolves to itself");
    var retargetSet = new SavedNetworkSet();
    retargetSet.Save(One(Save(vantages: craftSeven, watch: watchSeven)), newId, out _, out var retargetNet);
    retargetSet.FollowMerge(7, 9);
    retargetSet.SetWatch(retargetNet.FlightId, 5);
    SavedNetworkSet.TryFromCfg(retargetSet.ToCfg(), null, out var retargetFile);
    Check(retargetFile.Networks[0].WatchPersistentId == 5, "a viewer's retarget after a dock is written as aimed");

    /* A retarget that cannot be written is undone and answered. */
    mergeSet.SetWatch(mergeNet.FlightId, 321);
    uint aimedRev = mergeNet.Revision;
    Check(mergeSet.SetWatch(mergeNet.FlightId, 654, out var aimBefore, out var aimSaved) && mergeNet.WatchPersistentId == 654, "a retarget changes the watch");
    mergeSet.Undo(aimBefore, aimSaved);
    Check(mergeNet.WatchPersistentId == 321 && mergeNet.Revision == aimedRev, "a retarget undone restores its watch and revision");
    Check(!mergeSet.SetWatch(mergeNet.FlightId, 321, out var noBefore, out var noSaved) && noBefore == null && noSaved == null, "no change has nothing to undo");
    Check(One("{\"requests\":[{\"id\":1,\"op\":\"aim\",\"token\":\"a1\",\"flightId\":5}]}").Token == "a1", "an aim's token is read, so a failed retarget can be answered");

    /* A retried token hears its answer again, with a later atMs, from any scene. */
    var seenTokens = new SavedNetworkTokens(2);
    Check(seenTokens.Claim("t1") && !seenTokens.Claim("t1") && seenTokens.Claim(""), "a token is claimed once; an empty token never counts");
    Check(!seenTokens.TryLast("t1", out _), "no answer to repeat before one is decided");
    seenTokens.Remember(new SavedNetworkOutcome { Token = "t1", Ok = true, NetworkId = "n9" });
    Check(seenTokens.TryLast("t1", out var lastAnswer) && lastAnswer.Ok && lastAnswer.NetworkId == "n9", "the last answer is kept for its token");
    seenTokens.Remember(new SavedNetworkOutcome { Token = "never-claimed", Ok = true });
    Check(!seenTokens.TryLast("never-claimed", out _), "an unclaimed token keeps nothing");
    seenTokens.Claim("t2");
    seenTokens.Claim("t3");
    Check(!seenTokens.TryLast("t1", out _) && seenTokens.Claim("t1"), "the oldest token is forgotten past the limit");
    var replaySet = new SavedNetworkSet();
    var replayOutcome = new SavedNetworkOutcome { Token = "r1", Ok = false, NetworkId = "n1", Code = "stale", Text = "old" };
    replaySet.Record(replayOutcome, 1000);
    replaySet.Record(replayOutcome, 1000);
    Check(replaySet.Outcomes.Count == 1 && replaySet.Outcomes[0].AtMs == 1001, "a repeat replaces its entry with a strictly later atMs");
    replaySet.Record(replayOutcome, 5000);
    Check(replaySet.Outcomes.Count == 1 && replaySet.Outcomes[0].AtMs == 5000 && replaySet.Json(_ => false, null, true).Contains("\"atMs\":5000"),
        "the repeat is published with its new atMs");

    /* The listing written at Stop stands until the next scene. */
    var writes = new List<string>();
    bool writeWorks = true;
    var publisher = new SavedNetworkPublisher(j => { if (writeWorks) writes.Add(j); return writeWorks; });
    publisher.Publish("a", force: false);
    publisher.Publish("a", force: false);
    Check(writes.Count == 1, "the file is rewritten only when it differs");
    publisher.Publish("a", force: true);
    Check(writes.Count == 2, "a change can force a rewrite");
    writeWorks = false;
    publisher.Publish("b", force: false);
    writeWorks = true;
    publisher.Publish("b", force: false);
    Check(writes.Count == 3 && writes[2] == "b", "a failed write is tried again");
    publisher.Stop("stopped");
    publisher.Stop("stopped again");
    Check(publisher.Stopped && writes.Count == 4 && writes[3] == "stopped", "Stop writes once");
    publisher.Publish("editable", force: false);
    publisher.Publish("editable", force: true);
    Check(writes.Count == 4, "nothing rewrites the file after Stop");

    Check(NetworkMath.MayChangeHostNetwork("modA", "modA"), "a mod may change or remove its own network");
    Check(!NetworkMath.MayChangeHostNetwork("modA", "modB") && !NetworkMath.MayChangeHostNetwork("modA", null), "no other caller may change or remove it");
    Check(NetworkMath.MayChangeHostNetwork("", null) && NetworkMath.MayChangeHostNetwork("", "modB"), "a network made under no name can be changed or removed by anyone");

    Directory.Delete(netDir, true);
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
