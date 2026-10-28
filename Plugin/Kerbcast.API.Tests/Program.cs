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
Check(KerbcastApi.ApiMinor == 0, "ApiMinor is 0 for API 1.0");
Check(KerbcastApi.Supports(ApiFeature.CameraControl) && KerbcastApi.Supports(ApiFeature.CraftIdentity)
    && KerbcastApi.Supports(ApiFeature.Throttle) && KerbcastApi.Supports(ApiFeature.CommandFeeds)
    && KerbcastApi.Supports(ApiFeature.LightSpeed) && KerbcastApi.Supports(ApiFeature.CommandLock)
    && KerbcastApi.Supports(ApiFeature.CommandAim) && KerbcastApi.Supports(ApiFeature.WorldModel),
    "shipped: every feature of API 1.0");
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
    Check(cmd.TakeLock("modA", LockOptions.WorldModelRequired, "Authority silent") == LockResult.Taken, "TakeLock takes a free lock");
    Check(kc.Command.Aims.Count == 0, "taking the lock drops aims set before it");
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
    Check(cmd.Feeds().Single().SiteKey == "KSC" && cmd.Targets(500).Count == 0, "feeds and targets as arranged");
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
