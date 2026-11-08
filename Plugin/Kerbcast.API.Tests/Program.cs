using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Kerbcast;
using Kerbcast.Api;
using Kerbcast.Api.Testing;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

bool Throws<T>(Action a) where T : Exception
{
    try { a(); return false; }
    catch (T) { return true; }
}

T OnOtherThread<T>(Func<T> f)
{
    T result = default;
    Exception caught = null;
    var t = new Thread(() => { try { result = f(); } catch (Exception ex) { caught = ex; } });
    t.Start();
    t.Join();
    if (caught != null) throw caught;
    return result;
}

var guidA = Guid.NewGuid();
var guidB = Guid.NewGuid();

// Entry point with nothing installed.
Check(KerbcastApi.Instance == null, "Instance is null without Kerbcast");
Check(KerbcastApi.ApiMinor == 5, "ApiMinor is 5 for API 1.5");
Check(KerbcastApi.Supports(ApiFeature.CameraControl) && KerbcastApi.Supports(ApiFeature.CraftIdentity)
    && KerbcastApi.Supports(ApiFeature.Throttle) && KerbcastApi.Supports(ApiFeature.CommandFeeds)
    && KerbcastApi.Supports(ApiFeature.LightSpeed) && KerbcastApi.Supports(ApiFeature.CommandLock)
    && KerbcastApi.Supports(ApiFeature.CommandAim) && KerbcastApi.Supports(ApiFeature.WorldModel),
    "shipped: every feature of API 1.0");
Check(KerbcastApi.Supports(ApiFeature.Optics), "shipped: optics, from API 1.3");
Check(KerbcastApi.Supports(ApiFeature.NetworkCameras), "shipped: network cameras, from API 1.4");
Check(KerbcastApi.Supports(ApiFeature.GroundStations) && (int)ApiFeature.GroundStations == 10, "shipped: the ground station list, from API 1.5");
Check(KerbcastApi.Supports(ApiFeature.LineOfSight) && (int)ApiFeature.LineOfSight == 11, "shipped: line of sight, from API 1.5");
Check(KerbcastApi.Supports(ApiFeature.VantageResolution) && (int)ApiFeature.VantageResolution == 12, "shipped: vantage resolution, from API 1.5");
Check(!KerbcastApi.Supports((ApiFeature)999), "an unknown feature is unsupported");

// Enum numbers are part of the contract.
Check(default(AimResult) == AimResult.Unknown && default(PublishResult) == PublishResult.Unknown && default(LockResult) == LockResult.Unknown
    && default(LockLossReason) == LockLossReason.Unknown, "a result nobody set is Unknown, never success");
Check((int)AimResult.Ok == 1 && (int)PublishResult.Accepted == 1 && (int)LockResult.Taken == 1 && (int)LockResult.NotRunning == 4
    && (int)LockLossReason.ReleasedByPlayer == 2 && (int)AimResult.NotRunning == 11 && (int)PublishResult.NotRunning == 7,
    "result numbers are pinned");
Check((int)AimResult.BadNumber == 10 && (int)PublishResult.Invalid == 6 && (byte)CraftFixState.Listed == 3
    && (int)SiteKind.Vessel == 3 && (int)LockOptions.WorldModelRequired == 1 && (int)ApiFeature.WorldModel == 7
    && (int)IdentityChange.Recover == 3 && (int)IdentityChange.Renumber == 4 && (int)TrackMode.Target == 2, "enum values are pinned");
Check(default(OpticsResult) == OpticsResult.Unknown && (int)OpticsResult.Ok == 1 && (int)OpticsResult.NotLockHolder == 2
    && (int)OpticsResult.NotCommandFeed == 3 && (int)OpticsResult.BadNumber == 4 && (int)OpticsResult.NotRunning == 5
    && (int)ApiFeature.Optics == 8, "optics numbers are pinned");
Check(default(NetworkResult) == NetworkResult.Unknown && (int)NetworkResult.Ok == 1 && (int)NetworkResult.NotLockHolder == 2
    && (int)NetworkResult.NotCommandFeed == 3 && (int)NetworkResult.NoVantages == 4 && (int)NetworkResult.NotRunning == 5
    && (int)NetworkResult.BadVantage == 6 && (int)NetworkResult.BadAim == 7 && (int)NetworkResult.BadNumber == 8
    && (int)ApiFeature.NetworkCameras == 9, "network numbers are pinned");
{
    var opts = new NetworkOptions();
    Check(opts.MinHoldUt == 10 && opts.MinHoldRealSeconds == 3 && opts.MarginStops == 0.5, "network options default to a 10 s hold, 3 real seconds and half a stop");
    var st = NetworkVantage.Station("Crater Rim", "North dish");
    Check(st.Kind == SiteKind.Station && st.Name == "Crater Rim" && st.Label == "North dish" && !st.Fallback
        && st.Height == NetworkVantage.DefaultStationHeight, "a station vantage keeps its name, label and height");
    var fb = st.AsFallback();
    Check(fb.Fallback && fb.Label == "North dish" && fb.Name == "Crater Rim" && !st.Fallback, "AsFallback marks a copy, leaving the original");
    Check(NetworkVantage.Vessel(42).PersistentId == 42 && NetworkVantage.Vessel(42).Label == null
        && NetworkVantage.Surface(1, -0.1, -74.5).BodyIndex == 1 && NetworkVantage.LaunchSite("Runway").Distance == NetworkVantage.DefaultLaunchSiteDistance,
        "every vantage kind fills its own fields");
}
var unset = new OpticsSettings();
Check(double.IsNaN(unset.ResolutionArcsec) && double.IsNaN(unset.SeeingArcsec) && double.IsNaN(unset.SeeingAirmassPower)
    && double.IsNaN(unset.BrightnessHold) && double.IsNaN(unset.DaySkyStrength), "new optics settings keep every default (NaN)");

// Registration.
var first = new RecordingKerbcast();
var second = new RecordingKerbcast();
KerbcastApi.Register(first);
KerbcastApi.Unregister(second);
Check(KerbcastApi.Instance == first, "Unregister of another instance leaves Instance alone");
KerbcastApi.Unregister(first);
Check(KerbcastApi.Instance == null, "Unregister clears its own instance");

using (var kc = RecordingKerbcast.Install())
{
    Check(KerbcastApi.Instance == kc, "Install makes the recorder Instance");
    Check(KerbcastApi.Supports(ApiFeature.WorldModel), "the recorder supports every feature by default");
    kc.SetSupported(f => f != ApiFeature.CommandAim);
    Check(!KerbcastApi.Supports(ApiFeature.CommandAim), "SetSupported changes what Supports answers");
    Check(Throws<NotSupportedException>(() => kc.Command.SetAim(1, Aim.Vessel(5), "o")), "an unsupported member throws");
    kc.SetSupported(null);

    // Cameras.
    kc.Cameras.Add(new CameraView
    {
        FlightId = 10, Kind = CameraKind.Part, VesselPersistentId = 42, VesselId = guidA, PartPersistentId = 900,
        SupportsZoom = true, SupportsPan = true, Fov = 40, FovMin = 5, FovMax = 60,
        PanYawMin = -90, PanYawMax = 90, PanPitchMin = -30, PanPitchMax = 30, HasDockingNode = true,
        DockingNodeType = "size1", DockingNodeState = "Ready",
    });
    kc.Cameras.Add(new CameraView { FlightId = 11, Kind = CameraKind.Part, VesselPersistentId = 42, VesselId = guidA, Fov = 50 });
    kc.Cameras.Add(new CameraView { FlightId = 12, Kind = CameraKind.Command, VesselPersistentId = 42, VesselId = guidA });
    kc.Cameras.Add(new CameraView { FlightId = 13, Kind = CameraKind.Part, VesselPersistentId = 43, VesselId = guidB });
    ICameras cams = KerbcastApi.Instance.Cameras;

    var on42 = cams.CamerasFor(42u);
    Check(on42.Count == 2 && on42.All(v => v.Kind == CameraKind.Part), "CamerasFor(pid) lists only that craft's part cameras");
    Check(cams.CamerasFor(guidB).Single().FlightId == 13, "CamerasFor(guid) finds by Vessel.id");
    Check(cams.CamerasFor(7u).Count == 0, "CamerasFor an unknown craft is empty");
    var docked = cams.View(10);
    Check(docked.HasDockingNode == true && docked.DockingNodeType == "size1" && docked.PartPersistentId == 900, "views carry part ids and docking facts");
    docked.Fov = 1;
    Check(cams.View(10).Fov == 40, "a view is a copy");
    Check(cams.View(12).Kind == CameraKind.Command && cams.View(99) == null, "View finds command feeds too, marked, and null when unknown");
    var clear = new CameraView();
    Check(!clear.Occluded && double.IsNaN(clear.OccludedSinceUt) && clear.Occluder == null,
        "a view nobody filled reads as not occluded, with no since time and no occluder");
    kc.Cameras.Add(new CameraView
    {
        FlightId = 12, Kind = CameraKind.Command, VesselPersistentId = 42, VesselId = guidA,
        Occluded = true, OccludedSinceUt = 1234.5, Occluder = "Mun",
    });
    var blocked = cams.View(12);
    Check(blocked.Occluded && blocked.OccludedSinceUt == 1234.5 && blocked.Occluder == "Mun", "a command feed's view carries its occlusion");
    blocked.Occluder = "Kerbin";
    Check(cams.View(12).Occluder == "Mun" && blocked.Clone().Occluder == "Kerbin", "occlusion is copied with the view");
    Check(!cams.View(10).Occluded && double.IsNaN(cams.View(10).OccludedSinceUt), "a part camera reads as not occluded");
    Check(!cams.SetFov(12, 30) && !cams.SetPan(12, 0, 0) && !cams.AimAt(12, Vec3d.Zero) && !cams.SetTrackMode(12, TrackMode.Target),
        "the controls refuse a command feed");

    Check(cams.SetFov(10, 100) && cams.View(10).Fov == 60, "SetFov clamps to the range");
    Check(!cams.SetFov(11, 30), "SetFov fails without zoom");
    Check(!cams.SetFov(10, double.NaN), "SetFov refuses NaN");
    Check(cams.SetPan(10, -200, 10) && cams.View(10).PanYaw == -90 && cams.View(10).PanPitch == 10, "SetPan clamps");
    Check(!cams.SetPan(11, 0, 0), "SetPan fails without pan");
    Check(cams.AimAt(10, new Vec3d(1, 2, 3)) && kc.Cameras.TryAimedAt(10, out var aimed) && aimed == new Vec3d(1, 2, 3), "AimAt is recorded");
    Check(!cams.AimAt(99, Vec3d.Zero), "AimAt an unknown camera fails");
    Check(cams.SetTrackMode(10, TrackMode.Target) && cams.GetTrackMode(10) == TrackMode.Target, "track mode on a pan and zoom camera");
    Check(!cams.SetTrackMode(11, TrackMode.ActiveVessel) && cams.GetTrackMode(11) == TrackMode.None, "track mode needs pan and zoom");
    Check(kc.Calls.Contains("SetFov 10 100"), "calls are recorded");

    Check(cams.TryVessel(42, out var g) && g == guidA, "cameras' craft join TryVessel's map");
    kc.Cameras.Learn(77, guidB);
    Check(cams.TryVessel(77, out g) && g == guidB && !cams.TryVessel(78, out _), "Learn adds a craft with no camera");
    Check(cams.TryPersistentId(guidA, out var pid) && pid == 42 && !cams.TryPersistentId(Guid.NewGuid(), out _), "TryPersistentId inverts TryVessel");
    kc.Cameras.Learn(79, guidB);
    Check(cams.TryPersistentId(guidB, out pid) && pid == 79 && cams.TryVessel(77, out g) && g == guidB, "a renumbered craft resolves to its latest persistentId");

    var heard = new List<IdentityEvent>();
    Action<IdentityEvent> listener = e => heard.Add(e);
    KerbcastApi.Identity += listener;
    kc.Cameras.Raise(new IdentityEvent(IdentityChange.Merge, 100, 43, Guid.Empty, 42));
    KerbcastApi.Identity -= listener;
    kc.Cameras.Raise(new IdentityEvent(IdentityChange.Destroy, 101, 42, Guid.Empty));
    Check(heard.Count == 1 && heard[0].Change == IdentityChange.Merge && heard[0].OtherPersistentId == 42, "Identity reaches subscribers until they leave");

    Check(cams.Throttle.MainScreen == false, "throttle reads the save");
    Check(cams.Throttle.SetMainScreen(true) && kc.Cameras.Throttle.Value == true, "throttle writes the save");
    kc.Cameras.Throttle.Value = null;
    Check(cams.Throttle.MainScreen == null && !cams.Throttle.SetMainScreen(false), "no save: null and false");

    // Threading contract.
    Check(OnOtherThread(() => Throws<InvalidOperationException>(() => cams.View(10))), "ICameras throws off the main thread");
    Check(OnOtherThread(() => Throws<InvalidOperationException>(() => kc.Command.Feeds())), "ICommand throws off the main thread");
    Check(OnOtherThread(() => KerbcastApi.Instance == kc && KerbcastApi.Supports(ApiFeature.Throttle) && kc.SidecarAlive),
        "KerbcastApi and IKerbcast are fine on any thread");

    // Lock and aims.
    ICommand cmd = kc.Command;
    kc.Command.AddFeed(new CommandFeed { FlightId = 500, SiteKey = "KSC", SiteKind = SiteKind.LaunchSite });
    Check(cmd.SetAim(500, Aim.Vessel(42), null) == AimResult.Ok, "without a lock anyone aims");
    Check(cmd.SetOptics(500, new OpticsSettings { ResolutionArcsec = 30 }, null) == OpticsResult.Ok
        && kc.Command.Optics[500].ResolutionArcsec == 30, "without a lock anyone sets a feed's optics");
    Check(cmd.TakeLock("modA", LockOptions.WorldModelRequired, "Authority silent") == LockResult.Taken, "TakeLock takes a free lock");
    Check(kc.Command.Aims.Count == 0, "taking the lock drops aims set before it");
    Check(kc.Command.Optics.Count == 0, "and optics set before it");
    Check(cmd.SetOptics(500, new OpticsSettings { SeeingArcsec = 2 }, "modB") == OpticsResult.NotLockHolder, "only the holder sets optics");
    Check(cmd.SetOptics(501, new OpticsSettings(), "modA") == OpticsResult.NotCommandFeed, "optics on an unknown feed are refused");
    Check(cmd.SetOptics(500, new OpticsSettings { BrightnessHold = 1.5 }, "modA") == OpticsResult.BadNumber
        && cmd.SetOptics(500, new OpticsSettings { ResolutionArcsec = -1 }, "modA") == OpticsResult.BadNumber
        && cmd.SetOptics(500, new OpticsSettings { SeeingArcsec = double.PositiveInfinity }, "modA") == OpticsResult.BadNumber
        && kc.Command.Optics.Count == 0, "out of range optics are refused and not kept");
    int beats = kc.Command.Heartbeats;
    Check(cmd.SetOptics(500, new OpticsSettings { ResolutionArcsec = 0, SeeingArcsec = 2 }, "modA") == OpticsResult.Ok
        && kc.Command.Optics[500].SeeingArcsec == 2 && double.IsNaN(kc.Command.Optics[500].BrightnessHold)
        && kc.Command.Heartbeats == beats + 1, "the holder's optics are kept, NaN fields stay default, and the call is a heartbeat");
    Check(cmd.SetOptics(500, null, "modA") == OpticsResult.Ok && kc.Command.Optics.Count == 0, "null returns the feed to its own optics");
    Check(cmd.SetOptics(500, new OpticsSettings { SeeingArcsec = 2 }, "modA") == OpticsResult.Ok
        && cmd.SetOptics(500, new OpticsSettings(), "modA") == OpticsResult.Ok && kc.Command.Optics.Count == 0,
        "settings with every value NaN are no override, as null");
    // Network cameras, under the same lock.
    {
        var stations = new[] { NetworkVantage.Station("KSC"), NetworkVantage.Station("Crater Rim", "North dish").AsFallback() };
        Check(cmd.CreateNetworkCamera(stations, null, null, null, "modB", out uint none) == NetworkResult.NotLockHolder && none == 0,
            "only the holder creates a network camera");
        int before = kc.Command.Heartbeats;
        Check(cmd.CreateNetworkCamera(new NetworkVantage[0], null, null, null, "modA", out _) == NetworkResult.NoVantages
            && cmd.CreateNetworkCamera(null, null, null, null, "modA", out _) == NetworkResult.NoVantages,
            "there is no default list: an empty or null one is refused");
        Check(kc.Command.Heartbeats == before + 2, "and each holder's call is a heartbeat");
        Check(cmd.CreateNetworkCamera(new[] { NetworkVantage.Station("") }, null, null, null, "modA", out _) == NetworkResult.BadVantage
            && cmd.CreateNetworkCamera(new[] { NetworkVantage.Vessel(0) }, null, null, null, "modA", out _) == NetworkResult.BadVantage
            && cmd.CreateNetworkCamera(new NetworkVantage[] { null }, null, null, null, "modA", out _) == NetworkResult.BadVantage,
            "a malformed vantage is refused");
        Check(cmd.CreateNetworkCamera(stations, null, null, new NetworkOptions { MarginStops = -1 }, "modA", out _) == NetworkResult.BadNumber,
            "a negative option is refused");
        Check(cmd.CreateNetworkCamera(stations, "Ground net", null, null, "modA", out uint net) == NetworkResult.Ok && net != 0,
            "the holder creates one");
        var feed = cmd.Feeds().FirstOrDefault(f => f.FlightId == net);
        Check(feed != null && feed.Network && feed.Created && feed.SiteKey == "KSC", "Feeds lists it as a network camera at its first vantage");
        Check(feed.FollowsActiveVessel, "a network made with no aim follows the active vessel");
        var status = cmd.NetworkStatus(net);
        Check(status.Count == 2 && status[0].Active && !status[1].Active && status[1].Fallback && status[1].Label == "North dish",
            "NetworkStatus lists every vantage in order");
        Check(cmd.NetworkStatus(500).Count == 0, "a feed at one site has no network status");
        var relay = NetworkVantage.Vessel(77);
        var sight = cmd.LineOfSight(relay, 1000, 1, new Vec3d(7e5, 0, 0));
        Check(!sight.Known && double.IsNaN(sight.RangeM), "line of sight is unknown until a test sets an answer");
        kc.Command.SetLineOfSight(new LineOfSightResult(true, false, "Mun", false, 2.5e6));
        sight = cmd.LineOfSight(relay, 1000, 1, new Vec3d(7e5, 0, 0), 4);
        Check(sight.Known && !sight.Visible && sight.Occluder == "Mun" && sight.RangeM == 2.5e6 && !sight.Disabled, "a test sets what line of sight answers");
        kc.Command.SetLineOfSight(new LineOfSightResult(true, false, null, false, 1e6, disabled: true));
        Check(cmd.LineOfSight(NetworkVantage.Station("Crater Rim"), 1000, 1, new Vec3d(7e5, 0, 0)).Disabled, "and can answer for a station switched off");
        Check(OnOtherThread(() => Throws<InvalidOperationException>(() => cmd.LineOfSight(relay, 1000, 1, new Vec3d(7e5, 0, 0)))),
            "line of sight keeps to the main thread");
        Check(double.IsNaN(new LineOfSightResult(true, true, null, false, 1e6).ResolutionRad), "a line of sight answer built without a resolution has none");
        kc.Command.SetLineOfSight(new LineOfSightResult(true, true, null, false, 1e6, resolutionRad: 2e-6));
        Check(cmd.LineOfSight(relay, 1000, 1, new Vec3d(7e5, 0, 0)).ResolutionRad == 2e-6, "a test sets the resolution line of sight answers");
        var res = cmd.VantageResolution(relay);
        Check(!res.Known && double.IsNaN(res.FovMinDeg) && double.IsNaN(res.PixelRad) && double.IsNaN(res.LensArcsec) && double.IsNaN(res.BestResolutionRad),
            "vantage resolution is unknown until a test sets an answer");
        var set = new VantageResolution { Known = true, FovMinDeg = 0.02, FrameWidthPx = 1024, FrameHeightPx = 576, PixelRad = 6.06e-7, OpticsOn = true, LensArcsec = 4, BestResolutionRad = 1.94e-5 };
        kc.Command.SetVantageResolution(set);
        set.FovMinDeg = 9;
        res = cmd.VantageResolution(NetworkVantage.Station("KSC"));
        Check(res.Known && res.FovMinDeg == 0.02 && res.FrameWidthPx == 1024 && res.FrameHeightPx == 576 && res.OpticsOn && res.LensArcsec == 4
            && res.BestResolutionRad == 1.94e-5, "a test sets what vantage resolution answers, kept as it was set");
        res.FrameWidthPx = 1;
        Check(cmd.VantageResolution(relay).FrameWidthPx == 1024, "each answer is the caller's own copy");
        Check(!cmd.VantageResolution(null).Known, "a null vantage resolves nothing");
        Check(OnOtherThread(() => Throws<InvalidOperationException>(() => cmd.VantageResolution(relay))), "vantage resolution keeps to the main thread");
        Check(cmd.GroundStations().Count == 0, "no ground stations until a test sets them");
        kc.Command.SetGroundStations(new[]
        {
            new GroundStationInfo { NodeName = "KSC", DisplayName = "Kerbal Space Center", BodyIndex = 1, Latitude = -0.1, Longitude = -74.6, Altitude = 70, Enabled = true },
            new GroundStationInfo { NodeName = "Crater Rim", DisplayName = "Crater Rim", BodyIndex = 1, Latitude = 9.4, Longitude = -172.1, Altitude = 4000, Enabled = false },
        });
        var listed = cmd.GroundStations();
        Check(listed.Count == 2 && listed[0].NodeName == "KSC" && listed[0].Enabled && !listed[1].Enabled && listed[1].Altitude == 4000,
            "GroundStations lists every station set, on or off");
        Check(cmd.CreateNetworkCamera(listed.Select(s => NetworkVantage.Station(s.NodeName)).ToList(), null, null, null, "modA", out uint fromList) == NetworkResult.Ok
            && kc.Command.Networks[fromList].Count == 2, "a host builds a network from the list");
        cmd.DestroyNetworkCamera(fromList, "modA");
        Check(cmd.CreateNetworkCamera(new[] { NetworkVantage.Station("KSC"), NetworkVantage.Vessel(77) }, null, null, null, "modA", out uint mixed) == NetworkResult.Ok,
            "a network may mix stations and craft");
        var mixedStatus = cmd.NetworkStatus(mixed);
        Check(mixedStatus[0].SiteKind == SiteKind.Station && mixedStatus[0].VantagePersistentId == 0
            && mixedStatus[1].SiteKind == SiteKind.Vessel && mixedStatus[1].VantagePersistentId == 77 && mixedStatus[1].SiteKey == "vessel:77",
            "NetworkStatus names each vantage's kind and craft, so a host can bind every site");
        Check(cmd.CreateNetworkCamera(new[] { NetworkVantage.Station("KSC") }, null, Aim.Vessel(55), null, "modA", out uint watching) == NetworkResult.Ok
            && !cmd.Feeds().First(f => f.FlightId == watching).FollowsActiveVessel, "a network made to watch a chosen craft does not");
        cmd.DestroyNetworkCamera(watching, "modA");
        cmd.DestroyNetworkCamera(mixed, "modA");
        Check(cmd.CreateNetworkCamera(new[] { NetworkVantage.Surface(2, 1.5, 2.25) }, null, null, null, "modA", out uint mun) == NetworkResult.Ok
            && cmd.Feeds().First(f => f.FlightId == mun).SiteKey == "surface:Mun:1.50000:2.25000", "a surface vantage's key names its body as Kerbcast does");
        Check(cmd.CreateNetworkCamera(new[] { NetworkVantage.Surface(99, 0, 0) }, null, null, null, "modA", out _) == NetworkResult.BadVantage,
            "an unknown body is refused");
        cmd.DestroyNetworkCamera(mun, "modA");
        Check(cmd.SetNetworkVantages(500, stations, null, "modA") == NetworkResult.NotCommandFeed, "only a network camera takes a list");
        Check(cmd.SetNetworkVantages(net, new[] { NetworkVantage.Station("KSC") }, null, "modB") == NetworkResult.NotLockHolder, "only the holder replaces it");
        Check(cmd.SetNetworkVantages(net, new[] { NetworkVantage.Station("Nye Island") }, new NetworkOptions { MinHoldUt = 30 }, "modA") == NetworkResult.Ok
            && kc.Command.Networks[net].Count == 1 && kc.Command.NetworkOptionsSet[net].MinHoldUt == 30, "the holder replaces the list and options");
        Check(cmd.SetNetworkVantages(net, new NetworkVantage[0], null, "modA") == NetworkResult.NoVantages && kc.Command.Networks[net].Count == 1,
            "an empty replacement is refused and the list kept");
        kc.Command.SetNetworkStatus(net, new[] { new NetworkVantageStatus { SiteKey = "Nye Island", Label = "Nye Island", Active = true, State = "occluded", Score = double.NaN } });
        Check(cmd.NetworkStatus(net)[0].State == "occluded", "a test sets the status it wants to read");
        Check(cmd.DestroyNetworkCamera(net, "modB") == NetworkResult.NotLockHolder, "only the holder removes it");
        Check(cmd.DestroyNetworkCamera(500, "modA") == NetworkResult.NotCommandFeed, "a feed at one site is no network to remove");
        Check(cmd.DestroyNetworkCamera(net, "modA") == NetworkResult.Ok && cmd.Feeds().All(f => f.FlightId != net)
            && cmd.NetworkStatus(net).Count == 0 && cmd.DestroyNetworkCamera(net, "modA") == NetworkResult.NotCommandFeed,
            "the holder removes it, once");
    }
    Check(cmd.TakeLock("modA", LockOptions.WorldModelRequired) == LockResult.Renewed, "the holder renews");
    Check(cmd.TakeLock("modB", LockOptions.None) == LockResult.HeldByOther, "another owner is refused");
    Check(Throws<ArgumentException>(() => cmd.TakeLock("", LockOptions.None)), "an empty owner throws");
    Check(cmd.SetAim(500, Aim.Vessel(42), "modB") == AimResult.NotLockHolder, "only the holder aims");
    Check(cmd.SetAim(501, Aim.Vessel(42), "modA") == AimResult.NotCommandFeed, "an unknown feed is refused");
    kc.Command.AimJudge = (id, a) => a.SubjectPersistentId == 13 ? AimResult.TooUncertain : AimResult.Ok;
    Check(cmd.SetAim(500, Aim.Vessel(13), "modA") == AimResult.TooUncertain && kc.Command.Aims.Count == 0, "the judge decides and a refusal is not kept");
    kc.Command.AimJudge = (id, a) => a.SubjectPersistentId == 13 && !a.AllowUncertain ? AimResult.TooUncertain : AimResult.Ok;
    Check(cmd.SetAim(500, Aim.Vessel(13, allowUncertain: true), "modA") == AimResult.Ok && kc.Command.Aims[500].AllowUncertain,
        "an aim that allows an uncertain craft reaches the implementation with the flag");
    Check(cmd.SetAim(500, Aim.Orbit(1, 0, 0.01, 700000, 0, 0, 0, 0, 42), "modA") == AimResult.Ok
        && kc.Command.Aims[500].Kind == AimKind.Orbit && kc.Command.Aims[500].SemiMajorAxis == 700000, "the holder's aim is kept");
    Check(cmd.Feeds().Single(f => !f.Network).SiteKey == "KSC" && cmd.Targets(500).Count == 0, "feeds and targets as arranged");
    Check(Throws<ArgumentOutOfRangeException>(() => cmd.SetLightSpeed("modA", 0)) && Throws<ArgumentOutOfRangeException>(() => cmd.SetLightSpeed("modA", double.PositiveInfinity))
        && kc.Command.LightSpeed == null, "SetLightSpeed refuses zero and infinity; no delay until set");
    Check(!cmd.SetLightSpeed("modB", 3e8) && kc.Command.LightSpeed == null, "only the lock holder sets the light speed");
    Check(cmd.SetLightSpeed("modA", 3e8) && kc.Command.LightSpeed == 3e8, "the holder's light speed is recorded");
    Check(cmd.SetLightSpeed("modA", null) && kc.Command.LightSpeed == null, "null turns the delay off");

    // World model.
    Check(cmd.AttachWorldModel("modB") == null, "AttachWorldModel is null for a non-holder");
    var pub = cmd.AttachWorldModel("modA");
    Check(pub != null && pub.Attached && pub.LastAcceptedSequence == long.MinValue, "the holder gets a publisher");

    var b = pub.Begin(1, 3, 1000);
    b.Scope("home");
    b.BindSite("KSC", "home");
    b.Conic(42, 1, 0, 0.01, 700000, 0, 0, 0, 900, 900, 1100);
    b.Name("Station");
    b.Uncertainty(10, 1, 20, 2000);
    b.Radius(15);
    b.Listed(43);
    b.Refused(44, "cannot-bound", "no fix");
    b.Scope("remote");
    b.StateVector(42, 2, 950, new Vec3d(1, 2, 3), new Vec3d(4, 5, 6), 950, 1000);
    b.Attitude(950, Quatd.Identity, Vec3d.Zero);
    b.Surface(45, 1, -0.1, -74.5, 70, 0, 2000);
    b.Alias(46, 42);
    Check(pub.Publish(b, out var err) == PublishResult.Accepted && err == null && pub.LastAcceptedSequence == 1, "a valid snapshot is accepted");
    var snap = kc.Command.Published.Single();
    kc.Command.AddFeed(new CommandFeed { FlightId = 501, SiteKey = "Pad2", SiteKind = SiteKind.LaunchSite, Scope = "stale" });
    var fedScopes = cmd.Feeds();
    Check(fedScopes.Single(f => f.FlightId == 500).Scope == "home" && fedScopes.Single(f => f.FlightId == 501).Scope == null,
        "once a snapshot is accepted, feeds report the scope their site is bound to, null when unbound");
    var conic = snap.Find("home", 42);
    Check(snap.Epoch == 3 && snap.KnowledgeUt == 1000 && snap.Records.Count == 5, "the snapshot keeps its header and every record");
    Check(conic.State == CraftFixState.Positioned && conic.Kinematics == KinematicsKind.Conic && conic.SemiMajorAxis == 700000
        && conic.Name == "Station" && conic.HasUncertainty && conic.AccelMps2 == 20 && conic.RadiusM == 15, "conic fields and modifiers land on their record");
    Check(snap.Find("home", 43).State == CraftFixState.Listed && double.IsNaN(snap.Find("home", 43).RadiusM), "a listed record");
    Check(snap.Find("home", 44).RefusalCode == "cannot-bound" && snap.Find("home", 44).RefusalText == "no fix", "a refused record keeps code and text");
    var sv = snap.Find("remote", 42);
    Check(sv.Kinematics == KinematicsKind.StateVector && sv.Velocity == new Vec3d(4, 5, 6) && sv.HasAttitude, "the same craft in another scope");
    Check(snap.Find("remote", 45).Latitude == -0.1 && snap.SiteBindings["KSC"] == "home" && snap.Aliases.Single().Key == 46, "surface, bindings and aliases");
    Check(Throws<InvalidOperationException>(() => b.Listed(50)), "a published builder throws");
    Check(Throws<InvalidOperationException>(() => pub.Publish(b, out _)), "publishing a builder twice throws");

    var again = pub.Begin(1, 3, 1001);
    Check(pub.Publish(again, out err) == PublishResult.OutOfOrder && err != null, "a repeated sequence is out of order");
    var older = pub.Begin(2, 2, 1001);
    Check(pub.Publish(older, out _) == PublishResult.OlderEpoch, "a lower epoch is refused");

    var bad = pub.Begin(3, 3, 1002);
    bad.Scope("home");
    bad.Listed(42);
    bad.Listed(42);
    Check(pub.Publish(bad, out err) == PublishResult.Invalid && err.Contains("twice"), "a craft twice in a scope is invalid");
    var nan = pub.Begin(4, 3, 1002);
    nan.Scope("home");
    nan.Conic(42, 1, 0, double.NaN, 700000, 0, 0, 0, 900, 900, 1100);
    Check(pub.Publish(nan, out err) == PublishResult.Invalid && err.Contains("non-finite"), "a non-finite element is invalid");
    var parabola = pub.Begin(5, 3, 1002);
    parabola.Scope("home");
    parabola.Conic(42, 1, 0, 1.0, 700000, 0, 0, 0, 900, 900, 1100);
    Check(pub.Publish(parabola, out err) == PublishResult.Invalid && err.Contains("parabola"), "a parabola is invalid as a conic");
    var hyperbola = pub.Begin(5, 3, 1002);
    hyperbola.Scope("home");
    hyperbola.Conic(42, 1, 0, 1.5, 700000, 0, 0, 0, 900, 900, 1100);
    Check(pub.Publish(hyperbola, out err) == PublishResult.Invalid && err.Contains("semi-major"), "a hyperbola needs a negative semi-major axis, as KSP keeps it");
    var ellipse = pub.Begin(5, 3, 1002);
    ellipse.Scope("home");
    ellipse.Conic(42, 1, 0, 0.5, -700000, 0, 0, 0, 900, 900, 1100);
    Check(pub.Publish(ellipse, out err) == PublishResult.Invalid && err.Contains("semi-major"), "an ellipse needs a positive semi-major axis");
    var centre = pub.Begin(5, 3, 1002);
    centre.Scope("home");
    centre.StateVector(42, 1, 900, new Vec3d(0, 0, 0), new Vec3d(0, 2000, 0), 900, 1100);
    Check(pub.Publish(centre, out err) == PublishResult.Invalid && err.Contains("centre"), "a state vector at the body's centre is invalid");
    var window = pub.Begin(5, 3, 1002);
    window.Scope("home");
    window.Surface(42, 1, 0, 0, 0, 10, 5);
    Check(pub.Publish(window, out _) == PublishResult.Invalid, "a window that ends before it starts is invalid");
    var unc = pub.Begin(6, 3, 1002);
    unc.Scope("home");
    unc.Listed(42);
    unc.Uncertainty(-1, 0, 0, 0);
    Check(pub.Publish(unc, out _) == PublishResult.Invalid, "a negative uncertainty is invalid");
    var noScope = pub.Begin(7, 3, 1002);
    Check(Throws<InvalidOperationException>(() => noScope.Listed(1)), "a record before any scope throws");
    var noRecord = pub.Begin(8, 3, 1002);
    noRecord.Scope("home");
    Check(Throws<InvalidOperationException>(() => noRecord.Name("x")), "a modifier before any record throws");
    pub.Abandon(noRecord);
    Check(Throws<InvalidOperationException>(() => noRecord.Scope("x")), "an abandoned builder throws");
    var otherPub = new RecordingKerbcast();
    Check(Throws<ArgumentException>(() => pub.Publish(new WorldModelBuilder(otherPub, 9, 3, 0), out _)), "a builder from another publisher throws");

    // Publishing from another thread while the main thread reads.
    int accepted = OnOtherThread(() =>
    {
        int n = 0;
        for (long seq = 10; seq < 210; seq++)
        {
            var wb = pub.Begin(seq, 3, 1000 + seq);
            wb.Scope("home");
            wb.Listed(42);
            if (pub.Publish(wb, out _) == PublishResult.Accepted) n++;
        }
        return n;
    });
    Check(accepted == 200 && kc.Command.Published.Count == 201, "publishes from another thread are all accepted");

    // Fail-closed endings.
    var p2 = cmd.AttachWorldModel("modA");
    Check(!pub.Attached && p2.Attached, "attaching again retires the old publisher");
    var late = pub.Begin(300, 3, 2000);
    Check(pub.Publish(late, out _) == PublishResult.NotLockHolder, "a retired publisher is refused");
    pub.Detach(returnToTruth: true);
    Check(!kc.Command.ReturnedToTruth && p2.Attached, "Detach(true) from a retired publisher changes nothing");
    var p2b = p2.Begin(1, 2, 2000);
    Check(p2.Publish(p2b, out _) == PublishResult.OlderEpoch, "the epoch carries across publishers within one tenure");
    p2.Dispose();
    Check(!p2.Attached && kc.Command.LockOwner == "modA", "dispose detaches but keeps the lock");
    Check(p2.Publish(p2.Begin(1, 0, 0), out _) == PublishResult.NotLockHolder, "a disposed publisher is refused");

    var p3 = cmd.AttachWorldModel("modA");
    p3.Detach(returnToTruth: true);
    Check(kc.Command.ReturnedToTruth && !p3.Attached, "Detach(true) returns to truth");

    var p4 = cmd.AttachWorldModel("modA");
    Check(!kc.Command.ReturnedToTruth, "attaching again after a return to truth governs feeds again");
    cmd.SetLightSpeed("modA", 1e6);
    kc.Command.ChangeScene();
    Check(p4.Publish(p4.Begin(1, 0, 0), out _) == PublishResult.SceneChanged && kc.Command.LockOwner == null, "a scene change resets lock and publisher");
    Check(kc.Command.LightSpeed == null && kc.Command.SilentText == null && kc.Command.LockOptions == LockOptions.None
        && !kc.Command.ReturnedToTruth, "a scene change clears the tenure's state");
    p4.Detach(returnToTruth: true);
    Check(!kc.Command.ReturnedToTruth, "Detach(true) from an earlier scene's publisher changes nothing");
    cmd.TakeLock("modA", LockOptions.None);
    var p6 = cmd.AttachWorldModel("modA");
    Check(p6.Publish(p6.Begin(1, 0, 0), out _) == PublishResult.Accepted, "a new tenure starts its epochs afresh");
    cmd.ReleaseLock("modA");

    Check(Throws<ArgumentException>(() => cmd.TakeLock(" modA", LockOptions.None)) && Throws<ArgumentException>(() => cmd.Reserve("modA ", "ModA.dll"))
        && kc.Command.LockOwner == null, "owners with edge whitespace throw, since settings.cfg would trim them");
    Check(Throws<ArgumentException>(() => cmd.Reserve(null, "ModA.dll")) && Throws<ArgumentException>(() => cmd.Reserve("", "ModA.dll"))
        && kc.Command.ReservedOwner == null, "Reserve needs an owner");
    cmd.Reserve("modA", System.Reflection.Assembly.GetExecutingAssembly().GetName().Name);
    cmd.ClearReservation("modB");
    Check(kc.Command.ReservedOwner == "modA", "only the reservation's owner clears it");
    cmd.ClearReservation("modA");
    kc.Command.ChangeScene();
    Check(kc.Command.ReservedOwner == null && kc.Command.LockOwner == null, "a scene change after a clear starts unreserved");
    cmd.Reserve("modA", System.Reflection.Assembly.GetExecutingAssembly().GetName().Name);
    kc.Command.ChangeScene();
    Check(kc.Command.LockOwner == "modA", "a reservation takes the lock on the next scene");
    cmd.ReleaseLock("modB");
    Check(kc.Command.LockOwner == "modA", "only the holder releases");
    var p5 = cmd.AttachWorldModel("modA");
    cmd.SetLightSpeed("modA", 1e6);
    cmd.ReleaseLock("modA");
    Check(kc.Command.LightSpeed == null, "releasing the lock clears the light speed");
    Check(kc.Command.LockOwner == null && !p5.Attached && p5.Publish(p5.Begin(1, 0, 0), out _) == PublishResult.NotLockHolder,
        "releasing the lock ends the publisher");
}
Check(KerbcastApi.Instance == null && KerbcastApi.Supports(ApiFeature.WorldModel), "disposing the recorder restores the shipped state");

// The recorder raises LockLost as Kerbcast does: at a scene change and on the player's release.
using (var kc = RecordingKerbcast.Install())
{
    var lostEvents = new List<LockLostEvent>();
    Action<LockLostEvent> onLost = e => lostEvents.Add(e);
    KerbcastApi.LockLost += onLost;
    var cmd = kc.Command;
    kc.Command.ChangeScene();
    Check(lostEvents.Count == 0, "a scene change with no lock raises nothing");
    cmd.TakeLock("modA", LockOptions.None);
    var retakes = new List<LockResult>();
    Action<LockLostEvent> retake = e => retakes.Add(cmd.TakeLock(e.Owner, LockOptions.None));
    KerbcastApi.LockLost += retake;
    kc.Command.ChangeScene();
    KerbcastApi.LockLost -= retake;
    Check(lostEvents.Count == 1 && lostEvents[0].Owner == "modA" && lostEvents[0].Reason == LockLossReason.SceneEnded,
        "a scene change tells the holder once");
    Check(retakes.Count == 1 && retakes[0] == LockResult.NotRunning && kc.Command.LockOwner == null,
        "a take from the handler is refused, so the new scene starts unlocked");
    cmd.Reserve("modA", System.Reflection.Assembly.GetExecutingAssembly().GetName().Name);
    kc.Command.ChangeScene();
    Check(lostEvents.Count == 1 && kc.Command.LockOwner == "modA", "an unlocked scene's end raises nothing, and the reservation locks the next");
    kc.Command.ReleaseByPlayer();
    Check(lostEvents.Count == 2 && lostEvents[1].Reason == LockLossReason.ReleasedByPlayer && kc.Command.LockOwner == null
        && kc.Command.ReservedOwner == null, "the player's release frees the lock and clears the reservation");
    kc.Command.ChangeScene();
    Check(kc.Command.LockOwner == null && lostEvents.Count == 2, "and the next scene starts unlocked");
    KerbcastApi.LockLost -= onLost;
}

// A throwing Identity subscriber does not stop the others.
var errors = new List<Exception>();
int after = 0;
Action<IdentityEvent> throwing = e => throw new InvalidOperationException("subscriber bug");
Action<IdentityEvent> good = e => after++;
KerbcastApi.Identity += throwing;
KerbcastApi.Identity += good;
KerbcastApi.RaiseIdentity(new IdentityEvent(IdentityChange.Destroy, 0, 5, Guid.Empty), errors.Add);
KerbcastApi.Identity -= throwing;
KerbcastApi.Identity -= good;
Check(errors.Count == 1 && after == 1, "a throwing Identity subscriber is reported and the rest still run");

// Identity ledger.
var ledger = new VesselIdentityLedger();
ledger.Learn(1, guidA);
ledger.Learn(0, guidB);
ledger.Learn(2, Guid.Empty);
Check(ledger.TryGet(1, out var lg) && lg == guidA && ledger.Count == 1, "ledger ignores unnamed craft");
var merge = ledger.Merge(1, Guid.Empty, 2, guidB, 50);
Check(merge.Change == IdentityChange.Merge && merge.PersistentId == 1 && merge.VesselId == guidA
    && merge.OtherPersistentId == 2 && merge.OtherVesselId == guidB && merge.Ut == 50, "merge names both craft, guid from the map");
Check(ledger.Lost(IdentityChange.Destroy, 1, guidA, 51) == null, "a merged craft reports no later destroy");
Check(ledger.TryGet(1, out lg) && lg == guidA, "the map keeps a craft KSP no longer has");
var split = ledger.Split(3, Guid.NewGuid(), 2, guidB, 60);
Check(split.Change == IdentityChange.Split && split.PersistentId == 3 && split.OtherPersistentId == 2 && ledger.TryGet(3, out _), "split learns the new craft");
var lost = ledger.Lost(IdentityChange.Recover, 3, Guid.Empty, 70);
Check(lost.Change == IdentityChange.Recover && lost.VesselId == split.VesselId, "recover takes the guid from the map");
Check(ledger.Lost(IdentityChange.Destroy, 3, Guid.Empty, 71) == null, "one end per craft");
ledger.Learn(3, split.VesselId);
Check(ledger.Lost(IdentityChange.Destroy, 3, Guid.Empty, 80) != null, "a craft seen again (revert) can end again");
Check(ledger.Merge(2, guidB, 2, guidB, 90) == null && ledger.Split(0, guidA, 2, guidB, 90) == null, "self-merge and unnamed split are ignored");
var collided = Guid.NewGuid();
ledger.Learn(50, collided);
var renumbered = ledger.Renumber(50, 51, Guid.Empty, 96);
Check(renumbered.Change == IdentityChange.Renumber && renumbered.PersistentId == 50 && renumbered.OtherPersistentId == 51
    && renumbered.VesselId == collided && renumbered.OtherVesselId == collided && ledger.TryGet(51, out lg) && lg == collided,
    "a renumber names old and new ids of one craft");
Check(ledger.TryGetPersistentId(collided, out var lp) && lp == 51, "the reverse map follows a renumber");
Check(ledger.TryGetPersistentId(guidA, out lp) && lp == 1, "the reverse map keeps a craft KSP no longer has");
ledger.Learn(40, guidA);
ledger.Learn(41, guidB);
Check(ledger.Merge(40, guidA, 41, guidB, 97) != null && ledger.Renumber(40, 41, Guid.Empty, 97) == null,
    "a coupling's own persistentId change is not also a renumber");
/* KSP's order on a clash: the arriving craft's fresh id is registered to no craft yet, so no guid is known for it. */
var original = Guid.NewGuid();
ledger.Learn(60, original);
Check(ledger.Renumber(60, 61, Guid.Empty, 98, clash: true) == null && ledger.TryGet(60, out lg) && lg == original
    && ledger.TryGetPersistentId(original, out lp) && lp == 60,
    "a craft arriving under a taken id raises nothing, and the craft that had it keeps it");
/* Two craft saved under one id: while the save loads neither exists yet, and the second is the one renumbered. */
var savedFirst = Guid.NewGuid();
ledger.Learn(70, savedFirst);
Check(ledger.Renumber(70, 72, Guid.Empty, 98, clash: true) == null && ledger.TryGet(70, out lg) && lg == savedFirst
    && ledger.Lost(IdentityChange.Recover, 70, Guid.Empty, 99) != null,
    "a save holding two craft under one id keeps the first under it, and its later recover still raises its event");
var newcomer = Guid.NewGuid();
ledger.Learn(61, newcomer);
var originalLost = ledger.Lost(IdentityChange.Destroy, 60, Guid.Empty, 99);
Check(originalLost != null && originalLost.VesselId == original && ledger.TryGet(61, out lg) && lg == newcomer,
    "and the original's later destroy still raises its event");
ledger.Reset();
Check(!ledger.TryGet(1, out _) && !ledger.TryGetPersistentId(guidA, out _) && ledger.Count == 0 && ledger.Lost(IdentityChange.Destroy, 3, Guid.Empty, 99) != null, "Reset forgets the map and the ends");
var stranger = Guid.NewGuid();
var strangerLost = ledger.Lost(IdentityChange.Destroy, 9, stranger, 95);
Check(strangerLost.VesselId == stranger && ledger.TryGet(9, out lg) && lg == stranger && ledger.TryGetPersistentId(stranger, out lp) && lp == 9,
    "a craft first seen on its loss is learned");

// Site bindings: no default scope, and each unbound feed is logged once.
var sites = new SiteBindings();
Check(sites.ScopeFor("KSC") == null && sites.ScopeFor(null) == null, "no binding reads no scope");
Check(sites.FirstUnbound(500, "KSC") && !sites.FirstUnbound(500, "KSC") && sites.FirstUnbound(501, "KSC"), "each unbound feed is reported once");
sites.Replace(new Dictionary<string, string> { ["KSC"] = "home", ["vessel:41"] = "relay", [""] = "x", ["Pad"] = "" });
Check(sites.ScopeFor("KSC") == "home" && sites.ScopeFor("vessel:41") == "relay" && sites.Count == 2, "bindings resolve; empty keys and scopes are dropped");
Check(!sites.FirstUnbound(500, "KSC") && sites.FirstUnbound(500, "Pad"), "a bound feed is not reported; new bindings report afresh");
sites.Replace(new Dictionary<string, string> { ["KSC"] = "home", ["vessel:41"] = "relay" });
Check(!sites.FirstUnbound(500, "Pad"), "republishing the same bindings does not report a feed again");
sites.Clear();
Check(sites.ScopeFor("KSC") == null, "Clear unbinds every site");
var swaps = new Thread(() =>
{
    for (int i = 0; i < 2000; i++)
        sites.Replace(new Dictionary<string, string> { ["KSC"] = i % 2 == 0 ? "home" : "away" });
});
swaps.Start();
bool readsHold = true;
while (swaps.IsAlive)
{
    var scope = sites.ScopeFor("KSC");
    if (scope != null && scope != "home" && scope != "away") readsHold = false;
    sites.FirstUnbound(600, "KSC");
}
swaps.Join();
Check(readsHold && sites.ScopeFor("KSC") == "away", "bindings swap on another thread while the main thread reads");

// Values.
Check(new Vec3d(1, 2, 3) == new Vec3d(1, 2, 3) && new Vec3d(1, 2, 3) != new Vec3d(1, 2, 4), "Vec3d equality");
Check(!new Vec3d(double.NaN, 0, 0).IsFinite && new Vec3d(3, 4, 0).Magnitude == 5, "Vec3d finiteness and magnitude");
Check(Quatd.Identity.W == 1 && !new Quatd(0, 0, 0, double.PositiveInfinity).IsFinite, "Quatd identity and finiteness");
var surfaceAim = Aim.Surface(1, -0.1, -74.5, 70, 5);
Check(surfaceAim.Kind == AimKind.Surface && surfaceAim.BodyIndex == 1 && surfaceAim.FramingMarginM == 5 && !surfaceAim.AllowUncertain, "Aim factories fill their kind");
Check(Aim.Vessel(5, allowUncertain: true).AllowUncertain && Aim.Direction(90, 10).Elevation == 10, "Aim flags and direction");
Check(Aim.Vessel(5, allowUncertain: true).ToString() == "Vessel(5, allowUncertain)" && Aim.Vessel(5).ToString() == "Vessel(5)",
    "a vessel aim's text says when it allows an uncertain craft, so a recording shows it");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
