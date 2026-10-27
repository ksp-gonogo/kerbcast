using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

/*
 * Two allowlists over command camera code.
 *
 * Truth reads: no KSP read about a craft (where it is, what it is, whether
 * it exists) outside the members allowed to. Knowledge (aim point, range,
 * framing, occlusion and sunlight at the aim, forecast, targets, which craft
 * an aim names) goes through IFixSource; the picture reads truth through
 * DrawnSubject, SubjectTrack and every camera's PicturePass, and only the
 * picture pass and its shared solves (LensSights) read the light-time
 * history. Calls into the picture pass count as picture state, so knowledge
 * code cannot reach the past instants through it.
 *
 * Crossings: knowledge code never touches the picture's state (_drawn*,
 * DrawnSubject, DrawnPlan), and picture code never touches knowledge
 * (_known, KnownRadius, IFixSource, CraftFix, the selection), except at the
 * pinned lines where one is meant to feed the other.
 *
 * This is a source scan rather than a type-level check: the plugin DLL only
 * builds against KSP's and Unity's assemblies, which the Unity-free test job
 * does not have, while the source is always there. The scan drops comments
 * and the text of string literals (keeping interpolated code), matches over
 * the whole file so a read split across lines is still caught, and names
 * each hit's member by tracking the file's braces. Pins name a member and,
 * for single reads, the exact trimmed line and how often it appears.
 *
 * It is not complete. It matches KSP members by name, so a `using` alias,
 * reflection, an extension method, or a helper in a file outside the scope
 * that does the read and returns a plain value all get past it. To keep the
 * scope from going stale, any plugin file that names CommandCameraRig,
 * IFixSource or DrawnSubject must be in scope. The picture pass is in scope
 * by name: KerbcastCamera and AnchorProjector call it for every camera.
 */

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

/* KSP members that say where a craft is, what it is or whether it exists. */
var truthReads = new[]
{
    new Rule("FlightGlobals vessel lookup", @"FlightGlobals\s*\.\s*(Vessels|VesselsLoaded|VesselsUnloaded|ActiveVessel|FindVessel|FindLoadedPart|fetch\s*\.\s*(activeVessel|vessels|VesselTarget))\b"),
    new Rule("saved craft records", @"\bprotoVessels\b|\bProtoVessel\b|\.\s*orbitSnapShot\b|\.\s*vesselRef\b"),
    new Rule("craft position", @"\.\s*(CoMD|CoM|GetWorldPos3D|orbit|orbitDriver|GetOrbit|mainBody|latitude|longitude|altitude|vesselTransform|ReferenceTransform|obt_velocity|srf_velocity|GetObtVelocity|GetSrfVelocity)\b"),
    new Rule("craft state", @"\.\s*(loaded|packed|situation|LandedOrSplashed|parts|rootPart|state|vessel)\b|\bVessel\s*\.\s*State\b|\bVesselTarget\b|\.\s*(patchedConicSolver|targetObject)\b"),
    new Rule("a transform's position (a craft's when the receiver is one)", @"\.\s*transform\s*\.\s*position\b"),
    new Rule("craft identity", @"\.\s*(persistentId|vesselName|vesselType|GetDisplayName|launchTime|launchedFrom|DiscoveryInfo|discoveryInfo)\b"),
};

/* The subject's KSP handles, shared by TruthFixSource and DrawnSubject and read nowhere else. */
var trackRule = new Rule("the subject's KSP handles", @"\bSubjectTrack\b|\b_track\s*\.\s*\w+");
var trackFiles = new[] { "SubjectTrack.cs", "TruthFixSource.cs", "DrawnSubject.cs" };
var trackPins = new[]
{
    Pin.Line("CommandCameraProvider.cs", "Feed._track", "private SubjectTrack _track;", 1, "the feed owns its subject's handles"),
    Pin.Line("CommandCameraProvider.cs", "Feed.Enumerate", "_track = new SubjectTrack();", 1, "the feed owns its subject's handles"),
    Pin.Line("CommandCameraProvider.cs", "Feed.ResolveDefault", "? _track.LaunchedFrom", 1,
        "the launch site the default lens is placed at, from the Space Center's chosen craft"),
    Pin.Line("CommandCameraProvider.cs", "Feed.ResolveCreated", ": _p._spaceCenter ? _track.LaunchedFrom", 1,
        "the launch site a created lens is placed at, from the Space Center's chosen craft"),
    Pin.Line("CommandCameraSite.cs", "CommandCameraSite.VantageExists", "if (SubjectTrack.RecordCounts(protos[i]) && protos[i].persistentId == persistentId) return true;", 1,
        "the shared saved-record rule"),
};

/* The light-time history holds truth about every craft, so only the picture reads it. The solve itself is pure and anyone may run it on their own positions. */
var ringRule = new Rule("the light-time history",
    @"\bLightHistory\b|\bLightHistoryHost\b|\bRingBodies\b|\bPastSight\b|\bHistoryTrack\b|\bPastInstant\s*\.\s*Craft\b"
    + @"|\busing\s+static\s+Kerbcast\s*\.\s*PastInstant\b|(?<![\w.])Craft\s*\(|\bComposition\b|\bPartRecord\b");
var ringFiles = new[] { "PicturePass.cs", "LensSights.cs" };
/* The lock hands the holder's signal speed to the history, which writes to it and reads nothing back; the history's parts at a craft's past instant go from the picture pass to the stand-in built from them. */
var ringPins = new[]
{
    Pin.Line("KerbcastCore.cs", "KerbcastCore.AppendCosts", "var ring = LightHistoryHost.History;", 1,
        "telemetry reads the history's size and track counts, never a craft's state"),
    Pin.Line("KerbcastControl.cs", "KerbcastControl.SetSignalSpeedCore", "LightHistoryHost.History.SetSignalSpeed(metresPerSecond);", 1,
        "the holder's signal speed goes to the history; nothing is read back"),
    Pin.Line("CommandAuthority.cs", "CommandAuthority.Reset", "if (Lock.SignalSpeed.HasValue) LightHistoryHost.History.SetSignalSpeed(null);", 1,
        "the signal speed ends with the lock; nothing is read back"),
    Pin.Line("DrawnSubject.cs", "DrawnSubject.ShownParts", "public Composition ShownParts { get; private set; }", 1, "the subject's parts at its past instant"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig._modelFailedParts", "private Composition _modelFailedParts;", 1, "parts a stand-in could not be built from, not retried"),
    Pin.Whole("CommandCameraTargetModel.cs", "CommandCameraTargetModel.Parts", "the parts a history-built stand-in shows"),
    Pin.Whole("CommandCameraTargetModel.cs", "CommandCameraTargetModel.CommandCameraTargetModel", "builds a stand-in from the history's parts"),
    Pin.Whole("CommandCameraTargetModel.cs", "CommandCameraTargetModel.AddRecord", "one part of a history-built stand-in"),
    Pin.Whole("CommandCameraTargetModel.cs", "CommandCameraTargetModel.ComOf", "the point a history-built stand-in hangs from"),
    Pin.Whole("CommandCameraTargetModel.cs", "CommandCameraTargetModel.Update", "a history-built stand-in's parts changing"),
    Pin.Whole("CommandCameraTargetModel.cs", "CommandCameraTargetModel.Build", "builds a stand-in from the history's parts"),
};

/* The picture's state, and knowledge's. */
var knowledgeTokens = Rules.Knowledge();

/* Files that are a seam's own implementation, read whole. */
var truthFiles = new Dictionary<string, string>
{
    ["TruthFixSource.cs"] = "the truth fix source: knowledge read from KSP, including choosing the default subject",
    ["DrawnSubject.cs"] = "the picture's subject: where the craft is drawn",
    ["CommandCameraTargetModel.cs"] = "the picture's stand-in model, built from the saved record while the craft is drawn live, or from the history's parts at its past instant",
};

/* Members allowed to read truth whole, and single pinned lines. */
var truthPins = new[]
{
    Pin.Whole("PicturePass.cs", "PictureLens.OnCraft", "the picture: a camera's lens on the craft it rides"),
    Pin.Whole("PicturePass.cs", "PicturePass.Locate", "the picture: whether a craft draws itself live or is drawn at its past instant"),
    Pin.Whole("PicturePass.cs", "PicturePass.Drawn", "the picture: the craft a tracking camera aims at, where it is drawn"),
    Pin.Whole("PicturePass.cs", "PicturePass.TryCraftPoint", "where a lens on a craft stands, shared by part cameras and a command camera's vantage"),
    Pin.Whole("SubjectTrack.cs", "SubjectTrack.Proto", "KSP's handles for the subject, read through TruthFixSource and DrawnSubject"),
    Pin.Whole("SubjectTrack.cs", "SubjectTrack.Adopt", "captures a chosen craft's handles"),
    Pin.Whole("SubjectTrack.cs", "SubjectTrack.AdoptById", "the picture's own lookup when a non-truth source resolved the subject"),
    Pin.Whole("SubjectTrack.cs", "SubjectTrack.TryLocate", "the subject's position from its handles"),
    Pin.Whole("SubjectTrack.cs", "SubjectTrack.TryMotion", "the subject's conic or surface motion"),
    Pin.Whole("SubjectTrack.cs", "SubjectTrack.StockWorldPosition", "a one-off log check against stock's position"),
    Pin.Whole("SubjectTrack.cs", "SubjectTrack.RecordCounts", "whether a saved record still names a craft"),
    Pin.Whole("CommandCameraSite.cs", "CommandCameraSite.VantageGone", "the lens's own craft is gone"),
    Pin.Whole("CommandCameraSite.cs", "CommandCameraSite.ResolveVessel", "finds the craft a lens rides"),
    Pin.Whole("CommandCameraSite.cs", "CommandCameraSite.FromVessel", "a site on the craft a lens rides"),
    Pin.Whole("CommandCameraSite.cs", "CommandCameraSite.FollowMerge", "the lens follows its craft through a merge"),
    Pin.Whole("CommandCameraSite.cs", "CommandCameraSite.TryVantagePosition", "the lens's position from its own craft"),
    Pin.Whole("CommandCameraSite.cs", "CommandCameraSite.TryStationMotion", "the lens's motion from its own craft, for the forecast"),
    Pin.Whole("CommandCameraSite.cs", "CommandCameraSite.StationBody", "the body the lens's craft is at"),
    Pin.Whole("CommandCameraSite.cs", "CommandCameraSite.VantageName", "the lens's craft's name, for logs"),
    Pin.Whole("CommandCameraSite.cs", "CommandCameraSite.VantageExists", "whether a craft a lens could ride exists"),
    Pin.Whole("CommandCameraSite.cs", "CommandCameraSite.ToString", "the lens's craft in log lines"),
    Pin.Whole("Kerbcast.DevProvider/DevProviderAddon.cs", "DevProviderAddon.ReadTruth",
        "the development provider samples the game's truth to publish it delayed, as any provider reads its own source"),
    Pin.Whole("Kerbcast.DevProvider/DevProviderAddon.cs", "DevProviderAddon.AimFeeds",
        "the development provider, as lock holder, aims each feed at the active craft Kerbcast's default would watch"),
    Pin.Whole("Kerbcast.DevProvider/DevProviderAddon.cs", "DevProviderAddon.LatestLaunch",
        "the development provider finds the named or latest launched craft Kerbcast's default would watch, as Kerbcast's own default does"),
    Pin.Line("CommandCameraMount.cs", "CommandCameraMount.Vessel", "public Vessel Vessel => FlightGlobals.ActiveVessel;", 1,
        "ICamera's owning vessel for camera bookkeeping, not a question about the subject"),
    Pin.Line("CommandCameraProvider.cs", "Feed.ResolveDefault", ": (FlightGlobals.ActiveVessel ?? vessel)?.launchedFrom;", 1,
        "the launch site the default lens is placed at"),
    Pin.Line("CommandCameraProvider.cs", "Feed.ResolveCreated", ": (FlightGlobals.ActiveVessel ?? vessel)?.launchedFrom;", 1,
        "the launch site a created lens is placed at"),
    Pin.Line("CommandCameraProvider.cs", "Feed.ResolveDefault", "if (_site.Vantage.loaded)", 1,
        "warns that the default lens sits inside its own loaded craft"),
    Pin.Line("CommandCameraProvider.cs", "Feed.ResolveCreated", "if (_site.HasVantage && _site.Vantage.loaded)", 1,
        "warns that a created lens sits inside its own loaded craft"),
    Pin.Line("CommandCameraMarker.cs", "CommandCameraMarker.Place", "_go.transform.position = new Vector3((float)p.X, (float)p.Y, (float)p.Z);", 1, "places the marker object, not a craft read"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.SyncScaledPose", "t.position = ScaledSpace.LocalToScaledSpace(near.transform.position);", 1, "the lens's own layer cameras"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.AfgInputsFor", "var lhs = afg.planet.scaledBody.transform.position - camPos;", 1, "a body's scaled mesh"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.AfgInputsFor", "? Mathf.Clamp01(Vector3.Dot(lhs, camPos - sun.scaledBody.transform.position) * afg.dawnFactor)", 1, "the Sun's scaled mesh"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.BeforeScaledRender", "_afgOurs = AfgInputsFor(afg, scaled.transform.position);", 1, "the lens's own scaled camera"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.LogSkyBlockers", "var ray = new Ray(scaled.transform.position, scaled.transform.forward);", 1, "the lens's own scaled camera"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.LogSkyInputs", "var centre = body.scaledBody.transform.position;", 1, "a body's scaled mesh"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.LogSkyInputs", "var toSun = sun != null ? (sun.scaledBody.transform.position - t.position).normalized : Vector3.zero;", 1, "the Sun's scaled mesh"),
    Pin.Line("CommandCameraSite.cs", "CommandCameraSite.PickHome", "float d = (t.position - sc.transform.position).sqrMagnitude;", 1, "the Space Center's own transform"),
    Pin.Line("CommandCameraForecast.cs", "CommandCameraForecast.BodySpeedBound", "if (b.orbit == null) break;", 1, "a body's own conic, not a craft's"),
    Pin.Line("CommandCameraForecast.cs", "CommandCameraForecast.BodySpeedBound", "v += PeriapsisSpeed(b.orbit) * SpeedMargin;", 1, "a body's own conic, not a craft's"),
    Pin.Line("CommandCameraForecast.cs", "CommandCameraForecast.BodySpeedBound", "b = b.orbit.referenceBody;", 1, "a body's own conic, not a craft's"),
};

/* Where picture state may appear outside DrawnSubject.cs, and knowledge inside the picture. */
var picturePins = new[]
{
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.ForgetDrawn", "picture"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.LocateDrawn", "picture"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.SolveDrawn", "picture: the drawn craft's distance, occlusion and light"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.DrawnOrAim", "picture, for the target log"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.UpdateDrawnOffset", "picture: TrueOffsetPx and TrueVisible"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.TrueInFrame", "picture"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.PlaceDrawn", "picture: radius, stand-in model"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.FinishDrawn", "picture: sub-pixel camera, clip plan, marker"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.EnsureModel", "picture: the stand-in model"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.UpdateSubpixel", "picture: the stand-in drawn through the sub-pixel camera"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig._standIn", "picture field: the stand-in model"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.Dispose", "_standIn?.Dispose();", 1, "teardown"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.Dispose", "_standIn = null;", 1, "teardown"),
    Pin.Line("CommandCameraProvider.cs", "Feed.Enumerate", "S.CommandCameraTargetModel,", 1, "the setting that turns the stand-in on, not the model"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.AddOwnAnchors", "picture: the subject anchor is observed"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.TryLocateVessel", "picture: the projector's subject"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.DrawnLogFields", "picture, for the target log"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig.NoteStockCheck", "picture, for a one-off log check"),
    Pin.Line("CommandCameraRig.cs", "DrawnPlan", "private struct DrawnPlan", 1, "the picture's per-frame plan"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig._drawnOk", "picture field"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig._drawnPos", "picture field"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig._drawnBody", "picture field"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig._drawnDistance", "picture field"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig._drawnOccluded", "picture field"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig._drawnSunlit", "picture field"),
    Pin.Whole("CommandCameraRig.cs", "CommandCameraRig._drawnLight", "picture field, for the log"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig._drawn", "private readonly DrawnSubject _drawn;", 1, "the rig holds its picture"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.CommandCameraRig", "DrawnSubject drawn,", 1, "the rig is handed its picture"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.CommandCameraRig", "_drawn = drawn;", 1, "the rig is handed its picture"),
    Pin.Line("TruthFixSource.cs", "TruthFixSource._drawn", "private DrawnSubject _drawn;", 1, "truth frames a craft by the radius the picture measured"),
    Pin.Line("TruthFixSource.cs", "TruthFixSource.SubjectRadiusM", "public double SubjectRadiusM => _drawn != null ? _drawn.Radius : double.NaN;", 1,
        "truth frames a craft by the radius the picture measured: the one read from knowledge into the picture"),
    Pin.Line("TruthFixSource.cs", "TruthFixSource.MeasureWith", "public void MeasureWith(DrawnSubject drawn) => _drawn = drawn;", 1,
        "truth frames a craft by the radius the picture measured"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.TrueOffsetPx", "public double? TrueOffsetPx { get; private set; }", 1, "the picture's output"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.TrueVisible", "public bool TrueVisible { get; private set; }", 1, "the picture's output"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.ReportedTrueOffsetPx", "_authority != null && _authority.Registered && !TrueVisible ? null : TrueOffsetPx;", 1, "the status file reports the picture's output only while the picture shows the craft"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.SolveGeometry", "ForgetDrawn();", 3, "the solve drives the picture: cleared up front and on each failed solve"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.SolveGeometry", "LocateDrawn(ut, station, body);", 1, "the solve drives the picture, from the lens it placed"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.SolveGeometry", "SolveDrawn(station, aim, distance, !aimed && _known.KnowsWhatIsDrawn, aimBody);", 1, "the solve drives the picture, handing it the aim's readings"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.SolveGeometry", "NoteTargetAndOcclusion(ut, distance, aimBody, DrawnOrAim(aim), occluder, occluderSource);", 1, "a log line"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.UpdateBracket", "TrueOffsetPx = null;", 1, "no fix, so no picture offset"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.UpdateBracket", "UpdateDrawnOffset(fov, rows);", 1, "the bracket's field drives the picture offset"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.PrepareRender", "var drawn = PlaceDrawn(cam, ut, now, station, aim, occluder, distance);", 1, "the frame drives the picture"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.PrepareRender", "FinishDrawn(cam, ut, station, anchor.rotation, drawn);", 1, "the frame drives the picture"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.NoteTargetAndOcclusion", "+ $\"{DrawnLogFields()} elev={ElevationDeg:F2}\");", 1, "a log line"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.NoteTargetAndOcclusion", "NoteStockCheck(at, target);", 1, "a log line"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.LogTransitions", "+ $\"trueOffsetPx={(TrueOffsetPx.HasValue ? TrueOffsetPx.Value.ToString(\"F2\", CultureInfo.InvariantCulture) : \"none\")} \"", 1, "a log line"),
    Pin.Line("AnchorProjector.cs", "IAnchorRig.AddOwnAnchors", "void AddOwnAnchors(List<KeyValuePair<string, AnchorSpec>> into);", 1, "the projector is picture: observed anchors"),
    Pin.Line("AnchorProjector.cs", "IAnchorRig.TryLocateVessel", "bool TryLocateVessel(uint pid, out DVec3 world, out double radius, out bool occluded, out bool unseen);", 1, "the projector is picture: observed anchors"),
    Pin.Line("AnchorProjector.cs", "AnchorProjector.Project", "rig?.AddOwnAnchors(_anchors);", 1, "the projector is picture: observed anchors"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.StateNow", "if (offset.HasValue) s.TrueOffsetPx = offset.Value;", 1, "the frame carries the picture's output, as reported: only while the picture shows the craft under a lock"),
    Pin.Line("CommandFrameState.cs", "CommandFrameState.TrueOffsetPx", "public double TrueOffsetPx;", 1, "the frame carries the picture's output"),
    Pin.Line("CommandFrameState.cs", "CommandFrameState.Empty", "TrueOffsetPx = double.NaN,", 1, "the frame carries the picture's output"),
    Pin.Line("CommandFrameState.cs", "CommandFrameState.AppendJson", "Number(sb, \"trueOffsetPx\", TrueOffsetPx);", 1, "the frame carries the picture's output"),
    Pin.Line("CommandFrameState.cs", "CommandTrackJson.Append", "sb.Append(\",\\\"trueOffsetPx\\\":\").Append(!double.IsNaN(s.TrueOffsetPx) ? s.TrueOffsetPx.ToString(\"0.###\", ci) : \"null\");", 1, "the status file reports the picture's output, as 1.11 did"),
    Pin.Line("AnchorProjector.cs", "AnchorProjector.TryProject", "if (f.Rig != null && f.Rig.TryLocateVessel(pid, out world, out radius, out occluded, out unseen))", 1, "the projector is picture: a craft where the frame draws it, or nowhere while its light is not arriving"),
    Pin.Line("CommandCameraSite.cs", "CommandCameraSite.TryVantagePosition", "return PicturePass.TryCraftPoint(v, ut, out world, out source);", 1,
        "the lens stands where the picture's rule puts a craft"),
    Pin.Line("AnchorProjector.cs", "AnchorProjector.TryProject", "var sight = f.Cam.Picture.Locate(pid, out world, out radius);", 1,
        "the projector is picture: every camera marks a craft where its picture draws it"),
    Pin.Line("KerbcastCore.cs", "KerbcastCore.AppendCosts", "sb.Append($\"      \\\"pictureLenses\\\": {PicturePass.LensCount},\\n\");", 1,
        "telemetry reports how many lenses the picture keeps sights for"),
    Pin.Line("CommandCameraProvider.cs", "Feed.Enumerate", "var drawn = new DrawnSubject(_selection, _track, cam.Picture);", 1, "wiring: each rig gets a fresh picture, drawn through its camera's picture pass"),
};

/* Lines in picture code that are meant to read knowledge. */
var knowledgeInPicturePins = new[]
{
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.AddOwnAnchors", "_aimAnchor.VectorX = _poseAimToward.X;", 1, "the aim anchor carries the aim, knowledge the operator holds"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.AddOwnAnchors", "_aimAnchor.VectorY = _poseAimToward.Z;", 1, "the aim anchor carries the aim, knowledge the operator holds"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.AddOwnAnchors", "_aimAnchor.VectorZ = _poseAimToward.Y;", 1, "the aim anchor carries the aim, knowledge the operator holds"),
    Pin.Line("DrawnSubject.cs", "DrawnSubject._selection", "private readonly SubjectSelection _selection;", 1, "the picture draws the craft the selection resolved to"),
    Pin.Line("DrawnSubject.cs", "DrawnSubject.DrawnSubject", "public DrawnSubject(SubjectSelection selection, SubjectTrack truth, PicturePass picture)", 1, "the picture draws the craft the selection resolved to"),
    Pin.Line("DrawnSubject.cs", "DrawnSubject.DrawnSubject", "_selection = selection;", 1, "the picture draws the craft the selection resolved to"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.SolveDrawn", "_drawnOccluded = Occluded;", 1, "a craft drawn at the aim is seen as the aim's readings say"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.SolveDrawn", "_drawnSunlit = Sunlit;", 1, "a craft drawn at the aim is seen as the aim's readings say"),
    Pin.Line("CommandCameraRig.cs", "CommandCameraRig.PlaceDrawn", "Lit = drawnOk ? _drawnSunlit : Sunlit,", 1, "with no craft drawn, the layers are planned around the aim"),
    Pin.Line("DrawnSubject.cs", "DrawnSubject.TryLocate", "bool missing = pid == _missingPid && _selection.Version == _missingVersion;", 1, "a craft the picture could not find is not looked for again until the request changes"),
    Pin.Line("DrawnSubject.cs", "DrawnSubject.TryLocate", "_missingVersion = _selection.Version;", 1, "a craft the picture could not find is not looked for again until the request changes"),
    Pin.Line("DrawnSubject.cs", "DrawnSubject.TryLocate", "uint pid = _selection.ResolvedPersistentId;", 1, "which craft to draw is knowledge's call"),
    Pin.Line("DrawnSubject.cs", "DrawnSubject.TryLocate", "if (pid == 0 && _selection.Locked)", 1,
        "under the lock a feed with no subject draws no craft"),
};
/* Picture state, its outputs, and calls into the rig's picture members. */
var pictureTokens = Rules.Picture(picturePins.Where(p => p.Code == null && p.File == "CommandCameraRig.cs")
    .Select(p => p.Member.Substring(p.Member.LastIndexOf('.') + 1)).Where(n => !n.StartsWith("_", StringComparison.Ordinal)));
var pictureMembers = new HashSet<string>(picturePins.Where(p => p.Code == null).Select(p => p.File + ":" + p.Member));

/* Reads in files outside command camera code proper, pinned with their reason. */
var outsidePins = new List<Pin>();

string Here([CallerFilePath] string path = "") => path;
string pluginDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here()), "..", "Kerbcast"));
Check(Directory.Exists(pluginDir), $"plugin source found at {pluginDir}");

var extraFiles = new[] { "KerbcastControl.cs", "KerbcastCore.cs", "AnchorProjector.cs", "KerbcastApiImpl.cs" };
bool InScope(string file) =>
    file.StartsWith("Command", StringComparison.Ordinal) || extraFiles.Contains(file)
    || new[] { "IFixSource.cs", "TruthFixSource.cs", "DrawnSubject.cs", "PicturePass.cs", "LensSights.cs", "SubjectTrack.cs", "SubjectSelection.cs", "CraftFixState.cs",
        "ProviderFixSource.cs", "WorldModelStore.cs", "WorldModelSnapshot.cs", "Kinematics.cs", "KinematicsWorld.cs" }.Contains(file);

var scoped = Directory.Exists(pluginDir)
    ? Directory.GetFiles(pluginDir, "*.cs").Select(Path.GetFileName).Where(InScope).OrderBy(f => f, StringComparer.Ordinal).ToList()
    : new List<string>();
Check(scoped.Contains("CommandCameraRig.cs") && scoped.Contains("IFixSource.cs") && extraFiles.All(scoped.Contains),
    $"scope covers the rig, the seam, KerbcastControl, KerbcastCore and AnchorProjector ({scoped.Count} files)");
foreach (var f in truthFiles.Keys) Check(scoped.Contains(f), $"allowed file {f} still exists");
foreach (var f in ringFiles) Check(scoped.Contains(f), $"history reader {f} still exists and is scanned");

var sources = scoped.ToDictionary(f => f, f => new Source(f, File.ReadAllText(Path.Combine(pluginDir, f))));

/* The development provider reads truth by design, as any provider must; its own core does not. */
string devDir = Path.GetFullPath(Path.Combine(pluginDir, "..", "Kerbcast.DevProvider"));
var devFiles = Directory.Exists(devDir)
    ? Directory.GetFiles(devDir, "*.cs").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList()
    : new List<string>();
Check(devFiles.Contains("DelayedTruth.cs") && devFiles.Contains("DevProviderAddon.cs"), $"scope covers the development provider ({devFiles.Count} files)");
/* Keyed by their path under Plugin/, so a provider file can never stand in for a Kerbcast file of the same name. */
foreach (var f in devFiles)
{
    string key = "Kerbcast.DevProvider/" + f;
    sources[key] = new Source(key, File.ReadAllText(Path.Combine(devDir, f)));
}

/* Reads outside command camera code proper, pinned as they are today. */
outsidePins.AddRange(OutsidePins.All);

var allTruthPins = truthPins.Concat(outsidePins).ToArray();
var truthResult = Policy.Apply(sources.Values, truthReads, truthFiles.Keys, allTruthPins, _ => true);
foreach (var u in truthResult.Unexpected) Console.WriteLine("     truth read outside the allowlist: " + u);
Check(truthResult.Unexpected.Count == 0, $"no truth reads about craft outside the allowlist ({truthResult.Unexpected.Count} found)");
foreach (var stale in truthResult.Stale) Console.WriteLine("     stale truth pin: " + stale);
Check(truthResult.Stale.Count == 0, $"every truth pin still matches its code ({truthResult.Stale.Count} stale)");

var trackResult = Policy.Apply(sources.Values, new[] { trackRule }, trackFiles, trackPins, _ => true);
foreach (var u in trackResult.Unexpected) Console.WriteLine("     subject handles read outside their readers: " + u);
Check(trackResult.Unexpected.Count == 0, $"only TruthFixSource and DrawnSubject read the subject's KSP handles ({trackResult.Unexpected.Count} found)");
foreach (var stale in trackResult.Stale) Console.WriteLine("     stale handle pin: " + stale);
Check(trackResult.Stale.Count == 0, $"every handle pin still matches its code ({trackResult.Stale.Count} stale)");

var ringResult = Policy.Apply(sources.Values, new[] { ringRule }, ringFiles, ringPins, _ => true);
foreach (var u in ringResult.Unexpected) Console.WriteLine("     light-time history read outside the picture: " + u);
Check(ringResult.Unexpected.Count == 0, $"only the picture pass reads the light-time history ({ringResult.Unexpected.Count} found)");
foreach (var stale in ringResult.Stale) Console.WriteLine("     stale history pin: " + stale);
Check(ringResult.Stale.Count == 0, $"every history pin still matches its code ({ringResult.Stale.Count} stale)");
var ringProbe = Policy.Apply(new[] { new Source("CommandCameraRig.cs", "namespace K { class C { void M() { var h = LightHistoryHost.History; } } }") },
    new[] { ringRule }, ringFiles, new Pin[0], _ => true);
Check(ringProbe.Unexpected.Count == 1, "self-test: the rig reading the light-time history is caught");
var staticProbe = Policy.Apply(new[] { new Source("CommandCameraRig.cs",
        "using static Kerbcast.PastInstant;\nnamespace K { class C { void M() { var s = Craft(r, 1, 0, 0, d, 1, ref b); } } }") },
    new[] { ringRule }, ringFiles, new Pin[0], _ => true);
Check(staticProbe.Unexpected.Count == 2, "self-test: a static import of the ring's solve and a bare Craft( call are caught");

/* LightTime is how knowledge code learns the light speed; it must carry nothing else from the ring. */
string hostPath = Path.Combine(pluginDir, "LightHistoryHost.cs");
var lightTime = File.Exists(hostPath)
    ? Regex.Match(Lexer.Mask(File.ReadAllText(hostPath), blankStrings: true), @"\bclass\s+LightTime\s*\{(?<body>[^{}]*)\}")
    : Match.Empty;
var lightTimeMembers = lightTime.Success
    ? Regex.Matches(lightTime.Groups["body"].Value, @"\b(public|internal)\b[^;{]*?\b(?<name>\w+)\s*(=>|\{|\(|;|=)").Select(m => m.Groups["name"].Value).ToList()
    : new List<string>();
Check(lightTimeMembers.Count == 1 && lightTimeMembers[0] == "SignalSpeed"
    && Regex.IsMatch(lightTime.Groups["body"].Value, @"public\s+static\s+double\?\s+SignalSpeed\s*=>"),
    $"LightTime exposes only the signal speed ({string.Join(", ", lightTimeMembers)})");

var pictureFiles = new[] { "DrawnSubject.cs", "PicturePass.cs", "LensSights.cs", "CommandCameraTargetModel.cs" };
var pictureResult = Policy.Apply(sources.Values, new[] { pictureTokens }, pictureFiles, picturePins, _ => true);
foreach (var u in pictureResult.Unexpected) Console.WriteLine("     picture state outside the picture: " + u);
Check(pictureResult.Unexpected.Count == 0, $"knowledge code reads no picture state ({pictureResult.Unexpected.Count} found)");
foreach (var stale in pictureResult.Stale) Console.WriteLine("     stale picture pin: " + stale);
Check(pictureResult.Stale.Count == 0, $"every picture pin still matches its code ({pictureResult.Stale.Count} stale)");

var pictureProbe = Policy.Apply(new[] { new Source("CommandCameraRig.cs",
        "namespace K { class C { void M() {\nvar s = _cam.Picture.Locate(1, out var w, out var r);\nPicturePass.TryCraftPoint(v, 0, out var p, out _);\n} } }") },
    new[] { pictureTokens }, pictureFiles, picturePins, _ => true);
Check(pictureProbe.Unexpected.Count == 2, "self-test: knowledge code reading the picture pass is caught");

/* KerbcastCamera is not scanned for truth, but its onboard tracking reads the picture, and only there. */
string cameraPath = Path.Combine(pluginDir, "KerbcastCamera.cs");
var cameraPins = new[]
{
    Pin.Line("KerbcastCamera.cs", "KerbcastCamera.ResolveTrackTarget", "return v != null ? Picture.Drawn(v, v.CoM) : null;", 1,
        "onboard tracking aims where the picture draws the craft"),
    Pin.Line("KerbcastCamera.cs", "KerbcastCamera.ResolveTrackTarget", "return craft != null ? Picture.Drawn(craft, tr.position) : tr.position;", 1,
        "onboard tracking aims where the picture draws the craft"),
};
var cameraResult = File.Exists(cameraPath)
    ? Policy.Apply(new[] { new Source("KerbcastCamera.cs", File.ReadAllText(cameraPath)) }, new[] { pictureTokens }, new string[0], cameraPins, _ => true)
    : null;
Check(cameraResult != null && cameraResult.Unexpected.Count == 0 && cameraResult.Stale.Count == 0,
    $"KerbcastCamera reads the picture only for tracking ({(cameraResult == null ? "file missing" : string.Join("; ", cameraResult.Unexpected.Concat(cameraResult.Stale)))})");

bool IsPicture(Hit h) => pictureFiles.Contains(h.File)
    || pictureMembers.Contains(h.File + ":" + h.Member);
var mirrorResult = Policy.Apply(sources.Values, new[] { knowledgeTokens }, new string[0], knowledgeInPicturePins, IsPicture);
foreach (var u in mirrorResult.Unexpected) Console.WriteLine("     knowledge read by picture code: " + u);
Check(mirrorResult.Unexpected.Count == 0, $"picture code reads no knowledge ({mirrorResult.Unexpected.Count} found)");
foreach (var stale in mirrorResult.Stale) Console.WriteLine("     stale mirror pin: " + stale);
Check(mirrorResult.Stale.Count == 0, $"every mirror pin still matches its code ({mirrorResult.Stale.Count} stale)");

/* A new rig helper file must not slip past the scan by its name. */
var mentionsRig = new Regex(@"\b(CommandCameraRig|IFixSource|DrawnSubject)\b");
var outside = Directory.Exists(pluginDir)
    ? Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories)
        .Where(path => !InScope(Path.GetFileName(path)) || Path.GetDirectoryName(path) != pluginDir)
        .Where(path => mentionsRig.IsMatch(Lexer.Mask(File.ReadAllText(path), blankStrings: true)))
        .Select(path => Path.GetRelativePath(pluginDir, path)).ToList()
    : new List<string>();
foreach (var f in outside) Console.WriteLine("     names the rig or the seam but is not scanned: " + f);
Check(outside.Count == 0, $"every plugin file naming CommandCameraRig, IFixSource or DrawnSubject is in scope ({outside.Count} outside)");

SelfTests.Run(Check, truthReads, knowledgeTokens);

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static class Rules
{
    /* Knowledge state and the readings the rig publishes as knowledge. */
    public static Rule Knowledge() => new Rule("knowledge",
        @"\b_known\b|\bKnownRadius\b|\bIFixSource\b|\bTruthFixSource\b|\bFixSources\b|\bCraftFix\b|\b_selection\b|\bSubjectSelection\b"
        + @"|\b(RangeM|Occluded|Sunlit|HasFix|FramingFits|_lastDistance|_lastAimPoint|_poseAimToward)\b");

    /* Picture state, the picture's outputs, and calls to `members` (the rig's picture members). */
    public static Rule Picture(IEnumerable<string> members)
    {
        var names = members.Concat(new[] { "TryLocateVessel", "AddOwnAnchors" }).Distinct().ToList();
        return new Rule("picture state",
            @"\b_drawn\w*\b|\bDrawnSubject\b|\bDrawnPlan\b|\b(TrueOffsetPx|TrueVisible)\b"
            + @"|\bPicture\s*\.\s*(Locate|TrySee|TrySeeSubject|Drawn|Place)\b|\bPicturePass\s*\.|\bLensSights\b|\bPictureLens\s*\.\s*OnCraft\b|\b_standIn\b|\bCommandCameraTargetModel\b"
            + @"|\b(" + string.Join("|", names) + @")\s*\(");
    }
}

sealed class Rule
{
    public readonly string Name;
    public readonly Regex Pattern;
    public Rule(string name, string pattern)
    {
        Name = name;
        Pattern = new Regex(pattern, RegexOptions.Compiled);
    }
}

/* An allowance: a whole member (Code null), or one exact trimmed line in a member, `Count` times. */
sealed class Pin
{
    public string File, Member, Code, Why;
    public int Count;
    public static Pin Whole(string file, string member, string why) => new Pin { File = file, Member = member, Why = why };
    public static Pin Line(string file, string member, string code, int count, string why) =>
        new Pin { File = file, Member = member, Code = code, Count = count, Why = why };
    public override string ToString() => Code == null ? $"{File} {Member} ({Why})" : $"{File} {Member} '{Code}' x{Count} ({Why})";
}

sealed class Hit
{
    public string File, Member, Code, Rule;
    public int Line;
    public override string ToString() => $"{File}:{Line}: [{Member}] {Rule}: {Code}";
}

/* One file, lexed: comments dropped, string text blanked, each line's member named. */
sealed class Source
{
    public readonly string File;
    public readonly string Masked;
    public readonly string[] Lines;
    public readonly string[] Owners;
    private readonly int[] _lineStarts;

    public Source(string file, string text)
    {
        File = file;
        Masked = Lexer.Mask(text, blankStrings: true);
        Lines = Lexer.Mask(text, blankStrings: false).Split('\n').Select(l => l.Trim()).ToArray();
        Owners = Structure.Owners(Masked, Lines.Length);
        var starts = new List<int> { 0 };
        for (int i = 0; i < Masked.Length; i++) if (Masked[i] == '\n') starts.Add(i + 1);
        _lineStarts = starts.ToArray();
    }

    public int LineOf(int index)
    {
        int i = Array.BinarySearch(_lineStarts, index);
        return i >= 0 ? i : ~i - 1;
    }

    public IEnumerable<Hit> Hits(Rule rule)
    {
        foreach (Match m in rule.Pattern.Matches(Masked))
        {
            int line = LineOf(m.Index);
            yield return new Hit { File = File, Line = line + 1, Member = Owners[line], Code = Lines[line], Rule = rule.Name };
        }
    }
}

sealed class PolicyResult
{
    public readonly List<string> Unexpected = new List<string>();
    public readonly List<string> Stale = new List<string>();
}

static class Policy
{
    /* Every hit of `rules` for which `applies` holds must sit in an allowed file, an allowed member or a pinned line. */
    public static PolicyResult Apply(IEnumerable<Source> sources, IEnumerable<Rule> rules, IEnumerable<string> allowedFiles,
        IReadOnlyList<Pin> pins, Func<Hit, bool> applies)
    {
        var result = new PolicyResult();
        var files = new HashSet<string>(allowedFiles);
        var memberHits = new int[pins.Count];
        var lineHits = new HashSet<int>[pins.Count];
        for (int i = 0; i < pins.Count; i++) lineHits[i] = new HashSet<int>();
        var srcList = sources.ToList();
        foreach (var src in srcList)
        {
            if (files.Contains(src.File)) continue;
            var seen = new HashSet<string>();
            foreach (var rule in rules)
            {
                foreach (var hit in src.Hits(rule))
                {
                    if (!applies(hit)) continue;
                    int pin = -1;
                    for (int i = 0; i < pins.Count && pin < 0; i++)
                    {
                        var p = pins[i];
                        if (p.File != hit.File || p.Member != hit.Member) continue;
                        if (p.Code == null || p.Code == hit.Code) pin = i;
                    }
                    if (pin >= 0)
                    {
                        memberHits[pin]++;
                        lineHits[pin].Add(hit.Line);
                        continue;
                    }
                    if (seen.Add(hit.Line + ":" + hit.Rule)) result.Unexpected.Add(hit.ToString());
                }
            }
        }
        var present = new HashSet<string>(srcList.Select(s => s.File));
        for (int i = 0; i < pins.Count; i++)
        {
            var p = pins[i];
            if (!present.Contains(p.File)) continue;
            bool ok = p.Code == null ? memberHits[i] > 0 : lineHits[i].Count == p.Count;
            if (!ok) result.Stale.Add($"{p} seen on {lineHits[i].Count} line(s)");
        }
        return result;
    }
}

static class Lexer
{
    /* Blanks comments, keeping newlines. With `blankStrings` the text of
       string and char literals goes too, while the code inside an
       interpolation hole stays. */
    public static string Mask(string src, bool blankStrings)
    {
        var o = new StringBuilder(src);
        int i = 0;
        Code(src, o, ref i, false, blankStrings);
        return o.ToString();
    }

    private static void Blank(StringBuilder o, int k)
    {
        if (k < o.Length && o[k] != '\n' && o[k] != '\r') o[k] = ' ';
    }

    private static void Code(string s, StringBuilder o, ref int i, bool hole, bool blankStrings)
    {
        int depth = 0;
        while (i < s.Length)
        {
            char c = s[i];
            char next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < s.Length && s[i] != '\n') Blank(o, i++);
            }
            else if (c == '/' && next == '*')
            {
                Blank(o, i++);
                Blank(o, i++);
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')) Blank(o, i++);
                if (i < s.Length) { Blank(o, i++); Blank(o, i++); }
            }
            else if (c == '"' || ((c == '$' || c == '@') && StringStart(s, i, out _, out _)))
            {
                StringStart(s, i, out bool interp, out bool verbatim);
                while (s[i] != '"') { if (blankStrings) Blank(o, i); i++; }
                String(s, o, ref i, interp, verbatim, blankStrings);
            }
            else if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'' && s[i] != '\n')
                {
                    if (s[i] == '\\') { if (blankStrings) Blank(o, i); i++; }
                    if (blankStrings) Blank(o, i);
                    i++;
                }
                i++;
            }
            else if (hole && c == '{') { depth++; i++; }
            else if (hole && c == '}')
            {
                if (depth == 0) return;
                depth--;
                i++;
            }
            else i++;
        }
    }

    /* A string literal starts at `i`: `"`, `$"`, `@"`, `$@"` or `@$"`. */
    private static bool StringStart(string s, int i, out bool interp, out bool verbatim)
    {
        interp = verbatim = false;
        int k = i;
        while (k < s.Length && k < i + 2 && (s[k] == '$' || s[k] == '@'))
        {
            if (s[k] == '$') interp = true; else verbatim = true;
            k++;
        }
        if (k < s.Length && s[k] == '"' && (k == i || !char.IsLetterOrDigit(i > 0 ? s[i - 1] : ' '))) return true;
        interp = verbatim = false;
        return s[i] == '"';
    }

    private static void String(string s, StringBuilder o, ref int i, bool interp, bool verbatim, bool blankStrings)
    {
        i++;
        while (i < s.Length)
        {
            char c = s[i];
            char next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (!verbatim && c == '\\')
            {
                if (blankStrings) { Blank(o, i); Blank(o, i + 1); }
                i += 2;
                continue;
            }
            if (c == '"')
            {
                if (verbatim && next == '"')
                {
                    if (blankStrings) { Blank(o, i); Blank(o, i + 1); }
                    i += 2;
                    continue;
                }
                i++;
                return;
            }
            if (interp && c == '{')
            {
                if (next == '{')
                {
                    if (blankStrings) { Blank(o, i); Blank(o, i + 1); }
                    i += 2;
                    continue;
                }
                if (blankStrings) Blank(o, i);
                i++;
                Code(s, o, ref i, true, blankStrings);
                if (i < s.Length && blankStrings) Blank(o, i);
                i++;
                continue;
            }
            if (interp && c == '}' && next == '}')
            {
                if (blankStrings) { Blank(o, i); Blank(o, i + 1); }
                i += 2;
                continue;
            }
            if (!verbatim && c == '\n') return;
            if (blankStrings) Blank(o, i);
            i++;
        }
    }
}

/* Names the member each line belongs to ("Type.Member", or "Type" between members), from the braces. */
static class Structure
{
    private enum Kind { Namespace, Type, Member, Block }

    private sealed class Frame
    {
        public Kind Kind;
        public string Name;
        public int StartLine;
    }

    private static readonly Regex TypeKeyword = new Regex(@"\b(class|struct|interface|enum)\s+(\w+)");
    private static readonly Regex Attribute = new Regex(@"\[[^\[\]]*\]");

    public static string[] Owners(string masked, int lineCount)
    {
        var owners = new string[lineCount];
        var stack = new List<Frame>();
        var header = new StringBuilder();
        int headerLine = -1;
        int line = 0;

        string TypeName()
        {
            for (int k = stack.Count - 1; k >= 0; k--) if (stack[k].Kind == Kind.Type) return stack[k].Name;
            return "";
        }
        void Assign(int from, int to, string name)
        {
            for (int l = Math.Max(0, from); l <= to && l < lineCount; l++) if (owners[l] == null) owners[l] = name;
        }
        Kind Top() => stack.Count == 0 ? Kind.Namespace : stack[stack.Count - 1].Kind;

        for (int i = 0; i < masked.Length; i++)
        {
            char c = masked[i];
            if (c == '\n') { line++; if (header.Length > 0) header.Append(' '); continue; }
            if (c == '{' || c == '}' || c == ';')
            {
                string h = Clean(header.ToString());
                int hLine = headerLine < 0 ? line : headerLine;
                if (c == '{')
                {
                    var top = Top();
                    var tm = TypeKeyword.Match(h);
                    Frame f;
                    if (Regex.IsMatch(h, @"\bnamespace\b")) f = new Frame { Kind = Kind.Namespace };
                    else if ((top == Kind.Namespace || top == Kind.Type) && tm.Success) f = new Frame { Kind = Kind.Type, Name = tm.Groups[2].Value };
                    else if (top == Kind.Type) f = new Frame { Kind = Kind.Member, Name = TypeName() + "." + MemberName(h) };
                    else f = new Frame { Kind = Kind.Block };
                    f.StartLine = hLine;
                    stack.Add(f);
                }
                else if (c == '}')
                {
                    if (stack.Count > 0)
                    {
                        var f = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        if (f.Kind == Kind.Member) Assign(f.StartLine, line, f.Name);
                        if (f.Kind == Kind.Type) Assign(f.StartLine, line, f.Name);
                    }
                }
                else if (Top() == Kind.Type && h.Length > 0)
                {
                    Assign(hLine, line, TypeName() + "." + MemberName(h));
                }
                header.Clear();
                headerLine = -1;
                continue;
            }
            if (!char.IsWhiteSpace(c) && headerLine < 0) headerLine = line;
            header.Append(c);
        }
        for (int l = 0; l < lineCount; l++) owners[l] = owners[l] ?? "";
        return owners;
    }

    private static string Clean(string h)
    {
        var lines = h.Split('\n').Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal));
        return Attribute.Replace(string.Join(" ", lines), " ").Trim();
    }

    /* The identifier before the first `(`, `=>` or `=`, or the last identifier. */
    public static string MemberName(string h)
    {
        int cut = h.Length;
        for (int i = 0; i < h.Length; i++)
        {
            char c = h[i];
            if (c == '(') { cut = i; break; }
            if (c == '=')
            {
                char next = i + 1 < h.Length ? h[i + 1] : '\0';
                char prev = i > 0 ? h[i - 1] : '\0';
                if (next == '=' || prev == '=' || prev == '!' || prev == '<' || prev == '>') continue;
                cut = i;
                break;
            }
        }
        string before = h.Substring(0, cut).TrimEnd();
        before = StripTrailingGenerics(before);
        var m = Regex.Match(before, @"(\w+)\s*$");
        return m.Success ? m.Groups[1].Value : "?";
    }

    private static string StripTrailingGenerics(string s)
    {
        if (!s.EndsWith(">", StringComparison.Ordinal)) return s;
        int depth = 0;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (s[i] == '>') depth++;
            else if (s[i] == '<' && --depth == 0) return s.Substring(0, i).TrimEnd();
        }
        return s;
    }
}

static class SelfTests
{
    public static void Run(Action<bool, string> check, Rule[] truth, Rule knowledge)
    {
        var picture = Rules.Picture(new[] { "N" });
        List<string> Unexpected(string file, string code, IReadOnlyList<Pin> pins, Rule[] rules, Func<Hit, bool> applies = null)
        {
            var src = new Source(file, code);
            return Policy.Apply(new[] { src }, rules, new string[0], pins, applies ?? (_ => true)).Unexpected;
        }
        string Wrap(string body) => "namespace K {\n class C {\n  void M() {\n" + body + "\n  }\n  void N() { }\n }\n}\n";

        var pinned = new[] { Pin.Line("X.cs", "C.M", "if (_site.Vantage.loaded)", 1, "test") };
        check(Unexpected("X.cs", Wrap("if (_site.Vantage.loaded)\n{ }"), pinned, truth).Count == 0,
            "self-test: a pinned line passes");
        check(Unexpected("X.cs", Wrap("var sneak = FlightGlobals.Vessels[0].orbit; if (_site.Vantage.loaded)\n{ }"), pinned, truth).Count > 0,
            "self-test: a read sharing a pinned line's text is caught (pins match the exact trimmed line)");
        check(Unexpected("X.cs", Wrap("var v = FlightGlobals\n    .ActiveVessel;"), new Pin[0], truth).Count > 0,
            "self-test: a read split across lines is caught");
        check(Unexpected("X.cs", Wrap("var v = FlightGlobals . /* gap */ ActiveVessel;"), new Pin[0], truth).Count > 0,
            "self-test: a comment inside a read does not hide it");

        foreach (var read in new[]
        {
            "v.GetOrbit()", "v.CoM", "v.CoMD", "v.vesselTransform", "v.ReferenceTransform", "v.obt_velocity", "v.srf_velocity",
            "v.GetObtVelocity()", "v.orbitDriver", "FlightGlobals.fetch.vessels", "FlightGlobals.fetch.VesselTarget",
            "var t = (VesselTarget)x", "part.vessel", "FlightGlobals.FindVessel(1, out v)", "pv.orbitSnapShot", "v.mainBody",
        })
            check(Unexpected("X.cs", Wrap("var a = " + read + ";"), new Pin[0], truth).Count > 0, $"self-test: '{read}' is caught");

        check(Unexpected("X.cs", Wrap("// FlightGlobals.ActiveVessel\n/* v.CoMD */ var a = 1;"), new Pin[0], truth).Count == 0,
            "self-test: reads in comments are ignored");
        check(Unexpected("X.cs", Wrap("Debug.Log(\"v.orbit and part.vessel\"); var u = \"http://x\";"), new Pin[0], truth).Count == 0,
            "self-test: words in a string literal are ignored");
        check(Unexpected("X.cs", Wrap("Debug.Log($\"at {v.orbit} {(ok ? \"a\" : \"b\")}\");"), new Pin[0], truth).Count > 0,
            "self-test: a read inside an interpolation hole is caught");
        check(Unexpected("X.cs", Wrap("var r = DrawnSubject.MinRadius; _drawn.Loaded = x; _orbit = null; aim.Latitude = 1;"), new Pin[0], truth).Count == 0,
            "self-test: Kerbcast's own members do not match");

        var memberPin = new[] { Pin.Whole("X.cs", "C.M", "test") };
        check(Unexpected("X.cs", Wrap("var a = v.CoMD;"), memberPin, truth).Count == 0, "self-test: a member pin allows its member");
        check(Unexpected("X.cs", "namespace K {\n class C {\n  void M() { }\n  void N() {\n var a = v.CoMD;\n }\n }\n}\n", memberPin, truth).Count > 0,
            "self-test: a member pin does not allow another member");
        check(Unexpected("X.cs", "namespace K {\n class C {\n  int F => v.CoMD;\n  void M() { }\n }\n}\n", memberPin, truth).Count > 0,
            "self-test: an expression-bodied member is its own member");

        var pictureMember = new HashSet<string> { "X.cs:C.N" };
        bool InPicture(Hit h) => pictureMember.Contains(h.File + ":" + h.Member);
        string Rig(string knowledgeBody, string pictureBody) =>
            "namespace K {\n class C {\n  void M() {\n" + knowledgeBody + "\n  }\n  void N() {\n" + pictureBody + "\n  }\n }\n}\n";
        var pictureAllowed = new[] { Pin.Whole("X.cs", "C.N", "test") };
        check(Unexpected("X.cs", Rig("double r = _drawn.Radius + RangeM;", ""), pictureAllowed, new[] { picture }).Count > 0,
            "self-test: knowledge reading picture state is caught");
        check(Unexpected("X.cs", Rig("double d = (_drawnPos - station).Magnitude;", ""), pictureAllowed, new[] { picture }).Count > 0,
            "self-test: knowledge reading a drawn position is caught");
        check(Unexpected("X.cs", Rig("", "var p = _drawnPos;"), pictureAllowed, new[] { picture }).Count == 0,
            "self-test: picture code may read picture state");
        check(Unexpected("X.cs", Rig("", "double r = KnownRadius; var s = _known.Subject(ut);"), new Pin[0], new[] { knowledge }, InPicture).Count > 0,
            "self-test: picture code reading knowledge is caught");
        check(Unexpected("X.cs", Rig("var s = _known.Subject(ut);", ""), new Pin[0], new[] { knowledge }, InPicture).Count == 0,
            "self-test: knowledge code may read knowledge");
        check(Unexpected("X.cs", Rig("RangeM = (N(aim) - station).Magnitude;", ""), pictureAllowed, new[] { picture }).Count > 0,
            "self-test: knowledge calling a picture member is caught");
        check(Unexpected("X.cs", Rig("var o = TrueOffsetPx;", ""), pictureAllowed, new[] { picture }).Count > 0,
            "self-test: knowledge reading TrueOffsetPx is caught");
        check(Unexpected("Y.cs", Wrap("gr.TryLocateVessel(pid, out var w, out var r, out var o);"), new Pin[0], new[] { picture }).Count > 0,
            "self-test: another file ranging with TryLocateVessel is caught");
        check(Unexpected("X.cs", Rig("", "_lastAimPoint = _drawnPos;"), new Pin[0], new[] { knowledge }, InPicture).Count > 0,
            "self-test: picture code writing a knowledge reading is caught");
        check(Unexpected("X.cs", Wrap("var p = v.rootPart; var q = v.transform.position;"), new Pin[0], truth).Count >= 2,
            "self-test: rootPart and transform.position are caught");

        check(Structure.MemberName("public static bool RecordCounts(ProtoVessel pv)") == "RecordCounts", "self-test: method name");
        check(Structure.MemberName("public uint X => _y") == "X", "self-test: expression-bodied name");
        check(Structure.MemberName("private readonly List<DVec3> _s = new List<DVec3>()") == "_s", "self-test: field name");
        check(Structure.MemberName("public List<T> Get<T>(int a)") == "Get", "self-test: generic method name");
    }
}
