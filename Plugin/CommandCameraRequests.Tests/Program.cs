using System;
using System.Collections.Generic;
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
  {""id"":3,""op"":""create"",""token"":""t-1"",""name"":""Pad"",""vantage"":{""kind"":""launchSite"",""site"":""LaunchPad"",""distance"":300,""bearing"":90,""height"":10},""targetPersistentId"":77,""lookAt"":null},
  {""id"":4,""op"":""create"",""token"":""t-2"",""vantage"":{""kind"":""surface"",""body"":""Kerbin"",""latitude"":-0.05,""longitude"":-74.7,""height"":2},""lookAt"":{""kind"":""surface"",""body"":""Kerbin"",""latitude"":-0.0972,""longitude"":-74.5577,""altitude"":70,""framingMarginM"":40}},
  {""id"":5,""op"":""destroy"",""flightId"":2147483905},
  {""id"":6,""op"":""lookAt"",""flightId"":2147483905,""lookAt"":null},
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
Check(r3.TargetPersistentId == 77 && r3.LookAt == null, "target and absent look-at");
var r4 = reqs[1];
Check(r4.Vantage.Kind == CommandVantageKind.Surface && r4.Vantage.Body == "Kerbin" && r4.Vantage.Height == 2,
    "surface vantage fields");
Check(r4.LookAt != null && r4.LookAt.Kind == "surface" && r4.LookAt.Altitude == 70 && r4.LookAt.FramingMarginM == 40,
    "surface look-at fields");
Check(r4.LookAt.Invalid() == null, "surface look-at is valid");
Check(reqs[2].Op == CommandRequestOp.Destroy && reqs[2].FlightId == 2147483905u, "destroy carries a full 32-bit flightId");
Check(reqs[3].Op == CommandRequestOp.LookAt && reqs[3].LookAt == null, "look-at null clears");
Check(reqs[4].Vantage.Kind == CommandVantageKind.Station && reqs[4].Vantage.Height == 30, "station height defaults when absent");
Check(reqs[5].Vantage.PersistentId == 4000000001u, "vessel vantage persistentId above 2^31");

Check(CommandCameraRequest.ParseFile("not json", skipped).Count == 0, "unreadable file yields nothing");
Check(CommandCameraRequest.ParseFile("{}", null).Count == 0, "file without requests yields nothing");
Check(CommandCameraRequest.ParseFile(@"{""requests"":[{""id"":1,""op"":""create"",""vantage"":{""kind"":""moon""}}]}", null).Count == 0,
    "unknown vantage kind skipped");

// Look-at validation.
Check(new CommandLookAtSpec { Kind = "vessel", PersistentId = 0 }.Invalid() != null, "vessel look-at needs an id");
Check(new CommandLookAtSpec { Kind = "vessel", PersistentId = 9 }.Invalid() == null, "vessel look-at with an id is valid");
Check(new CommandLookAtSpec { Kind = "surface", Body = "Mun", Latitude = 0, Longitude = 0, FramingMarginM = -1 }.Invalid() != null,
    "negative framing margin refused");
Check(new CommandLookAtSpec { Kind = "orbit" }.Invalid() != null, "unknown look-at kind refused");

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

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
