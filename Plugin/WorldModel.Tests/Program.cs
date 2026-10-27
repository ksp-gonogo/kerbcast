using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Kerbcast;
using Kerbcast.Api;
using Kerbcast.Api.Testing;
using CraftFixState = Kerbcast.CraftFixState;

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

var log = new List<string>();
IReadOnlyDictionary<string, string> lastBindings = null;
WorldModelStore NewStore() => new WorldModelStore(log.Add, b => lastBindings = new Dictionary<string, string>((IDictionary<string, string>)b));
long frame = 0;
/* Each frame's UT rises unless a test says otherwise. */
WorldModelView Frame(WorldModelStore s, double ut = double.NaN) => s.Capture(++frame, double.IsNaN(ut) ? 1000 + frame : ut);

// The snapshot: scopes, sorting, aliases, bindings.
{
    var store = NewStore();
    var pub = store.Attach("modA");
    var b = pub.Begin(1, 1, 50);
    b.Scope("home");
    b.Listed(30);
    b.Name("Thirty");
    b.Kind("Probe");
    b.Refused(10, "beyond-horizon", "Past the horizon");
    b.Conic(20, 1, 0, 0, 700000, 0, 0, 0, 0, 0, 1000);
    b.Radius(12);
    b.Scope("remote");
    b.Listed(5);
    b.BindSite("KSC", "home");
    b.BindSite("vessel:9", "remote");
    b.Alias(7, 20);
    b.Alias(8, 7);
    Check(pub.Publish(b, out var err) == PublishResult.Accepted, "a well-formed snapshot is accepted " + err);
    var snap = Frame(store).Snapshot;
    int home = snap.ScopeOf("home"), remote = snap.ScopeOf("remote");
    Check(home >= 0 && remote >= 0 && home != remote && snap.ScopeOf("nowhere") == -1, "each scope has its own range; a scope with no records reads -1");
    Check(snap.CountIn(home) == 3 && snap.RecordAt(home, 0).PersistentId == 10 && snap.RecordAt(home, 2).PersistentId == 30,
        "a scope's records are sorted by persistentId");
    Check(snap.TryFind(home, 30, out var r30) && r30.State == CraftFixState.Listed && r30.Name == "Thirty" && r30.Kind == "Probe",
        "a listed record keeps its name and kind");
    Check(snap.TryFind(home, 10, out var r10) && r10.State == CraftFixState.Refused && r10.RefusalCode == "beyond-horizon"
        && r10.RefusalText == "Past the horizon", "a refused record keeps its code and text");
    Check(snap.TryFind(home, 20, out var r20) && r20.State == CraftFixState.Positioned && r20.RadiusM == 12
        && r20.Kinematics == KinematicsKind.Conic && r20.SemiMajorAxis == 700000, "a conic keeps its elements and radius");
    Check(!snap.TryFind(home, 5, out _) && snap.TryFind(remote, 5, out _), "records belong to their own scope only");
    Check(snap.Resolve(8) == 20 && snap.Resolve(7) == 20 && snap.Resolve(99) == 99, "aliases chain to the craft the model now knows");
    Check(snap.ScopeForSite("KSC") == "home" && snap.ScopeForSite("vessel:9") == "remote" && snap.ScopeForSite("Pad") == null,
        "site bindings come with the snapshot");
    Check(lastBindings != null && lastBindings["KSC"] == "home" && lastBindings.Count == 2, "each accepted snapshot hands its bindings on");
}

// Section 4's knowledge cases, as a feed's source reads them.
{
    var store = NewStore();
    var pub = store.Attach("modA");
    var b = pub.Begin(1, 1, 0);
    b.Scope("ksc");
    b.Conic(100, 1, 0, 0, 700000, 0, 0, 0, 0, 0, 2000);
    b.Name("Relay");
    b.Listed(200);
    b.Name("Lost Probe");
    b.Kind("Probe");
    b.Refused(300, "beyond-horizon");
    b.Scope("duna");
    b.Listed(100);
    b.Scope("empty-is-never-sent");
    b.Listed(999);
    b.BindSite("KSC", "ksc");
    b.BindSite("vessel:5", "duna");
    b.BindSite("Island", "silent");
    b.Alias(150, 100);
    pub.Publish(b, out _);
    var view = Frame(store);
    bool Known(int i) => i == 1;
    int ksc = ModelLookup.ScopeFor(view, "KSC", out bool kscBound);
    int duna = ModelLookup.ScopeFor(view, "vessel:5", out _);
    int island = ModelLookup.ScopeFor(view, "Island", out bool islandBound);
    int pad = ModelLookup.ScopeFor(view, "Pad", out bool padBound);
    Check(kscBound && ksc >= 0 && islandBound && island == -1 && !padBound && pad == -1,
        "a bound site reads its scope; a scope with no records knows nothing; an unbound site reads none");
    Check(ModelLookup.Find(view, ksc, 777, 500, Known).State == CraftFixState.Unknown, "craft never heard: unknown");
    Check(ModelLookup.Find(view, pad, 100, 500, Known).State == CraftFixState.Unknown, "unbound site: even a craft the model knows is unknown");
    Check(ModelLookup.Find(view, island, 100, 500, Known).State == CraftFixState.Unknown, "a site bound to an empty scope: unknown");
    var lost = ModelLookup.Find(view, ksc, 200, 500, Known);
    Check(lost.State == CraftFixState.Listed && lost.Name == "Lost Probe" && lost.Kind == "Probe",
        "craft gone but still known: listed by the model's name, with no ProtoVessel");
    var relay = ModelLookup.Find(view, ksc, 100, 500, Known);
    Check(relay.State == CraftFixState.Positioned && relay.Record.Kinematics == KinematicsKind.Conic && relay.Name == "Relay",
        "a positioned record inside its window is positioned");
    Check(ModelLookup.Find(view, ksc, 150, 500, Known).PersistentId == 100, "after the model hears of a dock, the absorbed craft's id finds the survivor");
    Check(ModelLookup.Find(view, duna, 100, 500, Known).State == CraftFixState.Listed,
        "two stations: each reads its own scope, so the same craft can be positioned at one and listed at the other");
    var refused = ModelLookup.Find(view, ksc, 300, 500, Known);
    Check(refused.State == CraftFixState.Refused && refused.RefusalCode == "beyond-horizon", "past the model's horizon: refused with its code");
    var late = ModelLookup.Find(view, ksc, 100, 2500, Known);
    Check(late.State == CraftFixState.Refused && late.RefusalCode == CommandCameraMath.RefusalStale, "outside its window: refused stale");
    Check(ModelLookup.Find(view, ksc, 100, double.NaN, Known).State == CraftFixState.Positioned, "asking only whether it is known needs no window");
    Check(ModelLookup.Find(view, ksc, 100, 500, i => false).RefusalCode == CommandCameraMath.RefusalStale, "an unknown body: refused stale");
    store.InvalidateForLoad();
    var loaded = Frame(store);
    var afterLoad = ModelLookup.Find(loaded, ModelLookup.ScopeFor(loaded, "KSC", out _), 200, 500, Known);
    Check(loaded.Mode == WorldModelMode.Stale && afterLoad.State == CraftFixState.Refused && afterLoad.RefusalCode == CommandCameraMath.RefusalStale
        && afterLoad.Name == "Lost Probe", "after a load every record reads stale but still lists by name");
    pub.Dispose();
    var waiting = Frame(store);
    Check(waiting.Mode == WorldModelMode.Waiting && ModelLookup.ScopeFor(waiting, "KSC", out _) == -1, "disposed: no scope, every craft unknown");
}

// Record rules at a UT.
{
    bool Known(int i) => i == 1;
    var r = new ModelRecord { State = CraftFixState.Positioned, BodyIndex = 1, ValidFromUt = 10, ValidUntilUt = 20 };
    Check(ModelRules.StateAt(r, 15, Known, out _) == CraftFixState.Positioned, "rules: inside its window, positioned");
    Check(ModelRules.StateAt(r, 9.9, Known, out var c1) == CraftFixState.Refused && c1 == CommandCameraMath.RefusalStale, "rules: before its window, stale");
    Check(ModelRules.StateAt(r, 20.1, Known, out var c2) == CraftFixState.Refused && c2 == CommandCameraMath.RefusalStale, "rules: after its window, stale");
    r.BodyIndex = 7;
    Check(ModelRules.StateAt(r, 15, Known, out var c3) == CraftFixState.Refused && c3 == CommandCameraMath.RefusalStale, "rules: an unknown body, stale");
    var listed = new ModelRecord { State = CraftFixState.Listed };
    Check(ModelRules.StateAt(listed, 15, Known, out _) == CraftFixState.Listed, "rules: listed stays listed");
    var refused = new ModelRecord { State = CraftFixState.Refused, RefusalCode = "cannot-bound" };
    Check(ModelRules.StateAt(refused, 15, Known, out var c4) == CraftFixState.Refused && c4 == "cannot-bound", "rules: refused keeps its own code");
}

WorldModelBuilder One(IWorldModelPublisher p, long seq, long epoch)
{
    var b = p.Begin(seq, epoch, 0);
    b.Scope("home");
    b.Listed(1);
    b.BindSite("KSC", "home");
    return b;
}

// The fail-closed table (5.6), as far as the store decides it.
{
    var store = NewStore();
    Check(Frame(store).Mode == WorldModelMode.Truth, "no world model attached: truth");
    var pub = store.Attach("modA");
    Check(Frame(store).Mode == WorldModelMode.Waiting && store.Governed, "attached, nothing published: every craft unknown");
    Check(store.HeardWithin(5), "attaching is a heartbeat");
    Check(pub.Publish(One(pub, 1, 1), out _) == PublishResult.Accepted && Frame(store).Mode == WorldModelMode.Model, "a valid snapshot: the provider only");
    Check(store.HeardWithin(5), "an accepted publish is a heartbeat");

    store.InvalidateForLoad();
    Check(Frame(store).Mode == WorldModelMode.Stale, "a game-state load: every record stale");
    Check(pub.Publish(One(pub, 2, 1), out _) == PublishResult.Accepted && Frame(store).Mode == WorldModelMode.Stale,
        "a publish at the same epoch after a load stays stale");
    Check(pub.Publish(One(pub, 3, 2), out _) == PublishResult.Accepted && Frame(store).Mode == WorldModelMode.Model,
        "a publish with a newer epoch is valid again");

    Frame(store, 5000);
    Check(Frame(store, 4000).Mode == WorldModelMode.Stale, "UT going backwards: every record stale");
    Check(pub.Publish(One(pub, 4, 3), out _) == PublishResult.Accepted && Frame(store, 4001).Mode == WorldModelMode.Model,
        "a newer epoch after a backwards UT is valid again");

    frame += 10000;
    pub.Dispose();
    Check(Frame(store).Mode == WorldModelMode.Waiting, "the publisher disposed: unknown, never truth");
    Check(pub.Publish(One(pub, 5, 3), out _) == PublishResult.NotLockHolder, "a disposed publisher's publish is refused");

    var p2 = store.Attach("modA");
    Check(p2.Publish(One(p2, 1, 3), out _) == PublishResult.Accepted && Frame(store).Mode == WorldModelMode.Model,
        "a new publisher's sequence starts afresh; the epoch carries within the tenure");
    Check(p2.Publish(One(p2, 2, 2), out var oe) == PublishResult.OlderEpoch && oe.Contains("older"), "an older epoch in the tenure is refused");
    p2.Detach(returnToTruth: false);
    Check(Frame(store).Mode == WorldModelMode.Waiting, "Detach without truth fails closed");

    var p3 = store.Attach("modA");
    p3.Publish(One(p3, 1, 3), out _);
    p3.Detach(returnToTruth: true);
    Check(Frame(store).Mode == WorldModelMode.Truth && !store.Governed, "Detach(returnToTruth) shows truth while the lock stays held");

    var p4 = store.Attach("modA");
    p4.Publish(One(p4, 1, 3), out _);
    store.EndTenure();
    Check(Frame(store).Mode == WorldModelMode.Truth, "the lock released: truth");
    Check(p4.Publish(One(p4, 2, 3), out _) == PublishResult.NotLockHolder, "a publisher from an ended tenure is refused");
    var p5 = store.Attach("modA");
    Check(p5.Publish(One(p5, 1, 0), out _) == PublishResult.Accepted, "epochs restart with a new tenure");

    store.Close();
    Check(p5.Publish(One(p5, 2, 0), out _) == PublishResult.SceneChanged && Frame(store).Mode == WorldModelMode.Truth,
        "a scene change: the publisher gets SceneChanged and feeds read truth");
    Check(!p5.Attached, "a closed scene's publisher is not attached");
}

// Publish results and builder rules match the recorder gonogo's tests use.
{
    using (var kc = RecordingKerbcast.Install())
    {
        var cmd = kc.Command;
        cmd.TakeLock("modA", LockOptions.WorldModelRequired);
        var store = NewStore();
        var real = store.Attach("modA");
        var fake = cmd.AttachWorldModel("modA");
        var steps = new List<Func<IWorldModelPublisher, PublishResult>>
        {
            p => p.Publish(One(p, 1, 5), out _),
            p => p.Publish(One(p, 1, 5), out _),
            p => p.Publish(One(p, 0, 5), out _),
            p => p.Publish(One(p, 2, 4), out _),
            p => { var b = p.Begin(3, 5, double.NaN); b.Scope("home"); return p.Publish(b, out _); },
            p => { var b = p.Begin(4, 5, 0); b.Scope("home"); b.Listed(1); b.Listed(1); return p.Publish(b, out _); },
            p => { var b = p.Begin(5, 5, 0); b.Scope("home"); b.Conic(1, 1, 0, -1, 1, 0, 0, 0, 0, 0, 1); return p.Publish(b, out _); },
            p => p.Publish(One(p, 6, 6), out _),
        };
        bool same = true;
        var seen = new List<string>();
        foreach (var step in steps)
        {
            var a = step(real);
            var e = step(fake);
            seen.Add($"{a}/{e}");
            if (a != e) same = false;
        }
        Check(same, "publish results match the recorder step for step: " + string.Join(", ", seen));
        Check(real.LastAcceptedSequence == fake.LastAcceptedSequence, "LastAcceptedSequence matches the recorder");
        var builder = real.Begin(9, 6, 0);
        real.Abandon(builder);
        Check(Throws<InvalidOperationException>(() => builder.Scope("x")), "an abandoned builder throws, as the recorder's does");
        var published = real.Begin(10, 6, 0);
        published.Scope("home");
        real.Publish(published, out _);
        Check(Throws<InvalidOperationException>(() => published.Listed(2)), "a published builder throws instead of writing into a live snapshot");
        Check(Throws<ArgumentException>(() => real.Publish(fake.Begin(11, 6, 0), out _)), "a builder from another publisher throws");
        Check(Throws<ArgumentNullException>(() => real.Publish(null, out _)), "a null builder throws");
        var again = store.Attach("modA");
        var fakeAgain = cmd.AttachWorldModel("modA");
        Check(real.Publish(One(real, 20, 6), out _) == fake.Publish(One(fake, 20, 6), out _),
            "a retired publisher is refused the same way");
        Check(!real.Attached && again.Attached && !fake.Attached && fakeAgain.Attached, "Attached matches the recorder after a re-attach");

        /* Kerbcast's side of a lock release: the store's tenure ends and the bindings clear, as CommandAuthority.Reset does. */
        var sites = new SiteBindings();
        var bound = new WorldModelStore(log.Add, sites.Replace);
        cmd.ReleaseLock("modA");
        cmd.TakeLock("modA", LockOptions.None);
        kc.Command.AddFeed(new CommandFeed { FlightId = 1, SiteKey = "KSC", Scope = "given" });
        kc.Command.AddFeed(new CommandFeed { FlightId = 2, SiteKey = "Pad" });
        string FakeScope(uint id) => cmd.Feeds().First(f => f.FlightId == id).Scope;
        Check(FakeScope(1) == null && sites.ScopeFor("KSC") == null, "before any snapshot, no feed has a scope");
        var rb = bound.Attach("modA");
        var fb = cmd.AttachWorldModel("modA");
        Check(rb.Publish(One(rb, 1, 1), out _) == fb.Publish(One(fb, 1, 1), out _), "both accept the first snapshot of a tenure");
        Check(FakeScope(1) == sites.ScopeFor("KSC") && FakeScope(1) == "home" && FakeScope(2) == null && sites.ScopeFor("Pad") == null,
            "feeds report the bound scope, null when unbound, on both");
        cmd.ReleaseLock("modA");
        bound.EndTenure();
        sites.Clear();
        cmd.TakeLock("modA", LockOptions.None);
        Check(rb.Publish(One(rb, 2, 1), out _) == PublishResult.NotLockHolder && fb.Publish(One(fb, 2, 1), out _) == PublishResult.NotLockHolder,
            "release then take again: the old publisher is refused on both");
        Check(FakeScope(1) == null && sites.ScopeFor("KSC") == null, "after release no feed has a scope, on both");
        kc.Command.ChangeScene();
        bound.Close();
        cmd.TakeLock("modA", LockOptions.None);
        var after = cmd.AttachWorldModel("modA");
        Check(after.Publish(One(after, 1, 0), out _) == PublishResult.Accepted && fb.Publish(One(fb, 3, 0), out _) == PublishResult.SceneChanged,
            "a scene change refuses an earlier scene's publisher");
    }
}

// Review probes: generations, an unread store, a load before any publish, aliases, builder storage.
{
    var store = NewStore();
    var a = store.Attach("modA");
    a.Publish(One(a, 7, 1), out _);
    long genA = Frame(store).Snapshot.Generation;
    var b = store.Attach("modA");
    b.Publish(One(b, 7, 1), out _);
    long genB = Frame(store).Snapshot.Generation;
    Check(genB > genA, "another publisher's sequence 7 is a new generation, so no memo serves the old elements");

    var quiet = NewStore();
    var qp = quiet.Attach("modA");
    for (int i = 1; i <= 3000; i++) qp.Publish(One(qp, i, 1), out _);
    Check(quiet.Retired <= WorldModelStore.RetiredCap, $"with no frame reading, retired snapshots stay capped ({quiet.Retired} held)");

    var early = NewStore();
    var ep = early.Attach("modA");
    early.InvalidateForLoad();
    ep.Publish(One(ep, 1, 4), out _);
    Check(Frame(early).Mode == WorldModelMode.Stale, "a load before the first publish leaves that first snapshot stale");
    ep.Publish(One(ep, 2, 5), out _);
    Check(Frame(early).Mode == WorldModelMode.Model, "a newer epoch after it is valid");
    early.InvalidateForLoad();
    var ep2 = early.Attach("modA");
    Check(Frame(early).Mode == WorldModelMode.Waiting, "a new attach starts waiting, with no snapshot carried over");
    ep2.Publish(One(ep2, 1, 5), out _);
    Check(Frame(early).Mode == WorldModelMode.Stale, "a new attach does not lift a load's staleness: the same epoch stays stale");
    ep2.Publish(One(ep2, 2, 6), out _);
    Check(Frame(early).Mode == WorldModelMode.Model, "only a newer epoch lifts it");

    var aliasStore = NewStore();
    var al = aliasStore.Attach("modA");
    var dup = al.Begin(1, 1, 0);
    dup.Scope("home");
    dup.Alias(5, 1);
    dup.Alias(5, 2);
    Check(al.Publish(dup, out var dupErr) == PublishResult.Invalid && dupErr.Contains("twice"), "a craft aliased twice is invalid");
    var loop = al.Begin(2, 1, 0);
    loop.Scope("home");
    loop.Alias(1, 2);
    loop.Alias(2, 3);
    loop.Alias(3, 1);
    Check(al.Publish(loop, out var loopErr) == PublishResult.Invalid && loopErr.Contains("loop"), "an alias loop is invalid");
    var chain = al.Begin(3, 1, 0);
    chain.Scope("home");
    chain.Alias(1, 2);
    chain.Alias(2, 3);
    Check(al.Publish(chain, out _) == PublishResult.Accepted, "a chain of aliases is fine");

    var reuse = NewStore();
    var rp = reuse.Attach("modA");
    var first = rp.Begin(1, 1, 0);
    first.Scope("home");
    first.Listed(1);
    var record = first.Records[0];
    rp.Publish(first, out _);
    Check(Throws<InvalidOperationException>(() => first.Listed(2)) && Throws<InvalidOperationException>(() => first.Name("x")),
        "a builder kept after publishing still throws");
    var second = rp.Begin(2, 1, 0);
    second.Scope("home");
    second.Listed(9);
    Check(ReferenceEquals(second.Records[0], record) && record.PersistentId == 9 && double.IsNaN(record.RadiusM),
        "the next builder reuses the record objects, reset");
    rp.Publish(second, out _);
    Check(Frame(reuse).Snapshot.CountIn(0) == 1, "the snapshot holds its own copy, whatever the builder storage does next");
}

// The lock (5.6 rules around it), against the recorder step for step.
{
    float now = 0;
    var reservation = new LockReservation();
    bool assemblyHere = true;
    var real = new RealCommand(reservation, a => assemblyHere, () => now);
    using (var kc = RecordingKerbcast.Install())
    {
        var fake = kc.Command;
        fake.AssemblyLoadedCheck = a => assemblyHere;
        bool Same<T>(T a, T b, string what)
        {
            bool ok = EqualityComparer<T>.Default.Equals(a, b);
            Check(ok, $"lock: {what} ({a} / {b})");
            return ok;
        }
        bool BothThrow<T>(Action a, Action b, string what) where T : Exception
        {
            bool ok = Throws<T>(a) && Throws<T>(b);
            Check(ok, "lock: " + what);
            return ok;
        }

        Check(real.Mode == WorldModelMode.Truth && fake.LockOwner == null, "lock: no holder reads truth");
        Same(real.TakeLock("modA", LockOptions.WorldModelRequired, "Uplink silent"), fake.TakeLock("modA", LockOptions.WorldModelRequired, "Uplink silent"), "taking a free lock");
        Same(real.Lock.Owner, fake.LockOwner, "the holder");
        Same(real.Lock.SilentText, fake.SilentText, "the holder's silence text, which the blank shows");
        Check(real.Mode == WorldModelMode.Waiting, "lock: WorldModelRequired reads every craft unknown from TakeLock, before any attach");
        Same(real.TakeLock("modB", LockOptions.None), fake.TakeLock("modB", LockOptions.None), "another owner is refused");
        Same(real.TakeLock("modA", LockOptions.None), fake.TakeLock("modA", LockOptions.None), "the holder renews");
        Same(real.Lock.Options, fake.LockOptions, "a renewal takes the new options");
        BothThrow<ArgumentException>(() => real.TakeLock("", LockOptions.None), () => fake.TakeLock("", LockOptions.None), "an empty owner throws on both");

        Same(real.SetLightSpeed("modB", 1e6), fake.SetLightSpeed("modB", 1e6), "a non-holder's light speed is refused");
        Same(real.SetLightSpeed("modA", 1e6), fake.SetLightSpeed("modA", 1e6), "the holder sets a light speed");
        Same(real.Lock.LightSpeed, fake.LightSpeed, "the light speed");
        BothThrow<ArgumentOutOfRangeException>(() => real.SetLightSpeed("modA", -1), () => fake.SetLightSpeed("modA", -1), "a bad light speed throws on both");

        Same(real.Attach("modB") == null, fake.AttachWorldModel("modB") == null, "a non-holder gets no publisher");
        var rp = real.Attach("modA");
        var fp = fake.AttachWorldModel("modA");
        Same(rp.Publish(One(rp, 1, 1), out _), fp.Publish(One(fp, 1, 1), out _), "the holder publishes");
        now += 3;
        Check(!real.Lock.TimedOut(now + 4), "lock: a publish is a heartbeat, so the holder is not silent yet");
        var silent = new RealCommand(new LockReservation(), a => true, () => 0);
        silent.TakeLock("modC", LockOptions.None);
        Check(!silent.Lock.TimedOut(4) && silent.Lock.TimedOut(5.1f), "lock: 5 s of real time with no call or publish times the holder out");

        Same(real.ReleaseLock("modB"), false, "only the holder releases (real)");
        fake.ReleaseLock("modB");
        Same(real.Lock.Owner, fake.LockOwner, "a non-holder's release changes nothing");
        real.ReleaseLock("modA");
        fake.ReleaseLock("modA");
        Same(real.Lock.Owner, fake.LockOwner, "the holder releases");
        Same(real.Lock.LightSpeed, fake.LightSpeed, "releasing clears the light speed");
        Same(real.Lock.Options, fake.LockOptions, "releasing clears the options");
        Check(real.Mode == WorldModelMode.Truth, "lock: released reads truth");
        Same(rp.Publish(One(rp, 2, 1), out _), fp.Publish(One(fp, 2, 1), out _), "the released holder's publisher is refused");

        Same(real.TakeLock("modA", LockOptions.None), fake.TakeLock("modA", LockOptions.None), "taking the lock without WorldModelRequired");
        Check(real.Mode == WorldModelMode.Truth, "lock: without WorldModelRequired, truth until the holder attaches");
        var rp2 = real.Attach("modA");
        var fp2 = fake.AttachWorldModel("modA");
        Check(real.Mode == WorldModelMode.Waiting, "lock: attaching reads every craft unknown until the first publish");
        rp2.Dispose();
        fp2.Dispose();
        Check(real.Mode == WorldModelMode.Waiting && real.Lock.Owner == fake.LockOwner, "lock: Dispose fails closed and keeps the lock");
        real.ReleaseLock("modA");
        fake.ReleaseLock("modA");

        real.TakeLock("modA", LockOptions.WorldModelRequired);
        fake.TakeLock("modA", LockOptions.WorldModelRequired);
        real.Attach("modA").Detach(returnToTruth: true);
        fake.AttachWorldModel("modA").Detach(returnToTruth: true);
        Check(real.Mode == WorldModelMode.Truth && fake.ReturnedToTruth, "lock: Detach(returnToTruth) shows truth, on both");
        Same(real.TakeLock("modA", LockOptions.WorldModelRequired), fake.TakeLock("modA", LockOptions.WorldModelRequired), "a renewal with the same options");
        Check(real.Mode == WorldModelMode.Truth && fake.ReturnedToTruth, "lock: a renewal heartbeat does not undo a return to truth, on both");
        real.TakeLock("modA", LockOptions.None);
        fake.TakeLock("modA", LockOptions.None);
        real.TakeLock("modA", LockOptions.WorldModelRequired);
        fake.TakeLock("modA", LockOptions.WorldModelRequired);
        Check(real.Mode == WorldModelMode.Waiting && !fake.ReturnedToTruth,
            "lock: newly gaining WorldModelRequired on a renewal requires a model again, on both");
        real.ReleaseLock("modA");
        fake.ReleaseLock("modA");

        BothThrow<ArgumentException>(() => real.Reservation.Reserve(null, "x"), () => fake.Reserve(null, "x"), "a reservation needs an owner, on both");
        real.Reservation.Reserve("modA", Me);
        fake.Reserve("modA", Me);
        real.Reservation.Clear("modB");
        fake.ClearReservation("modB");
        Same(real.Reservation.Owner, fake.ReservedOwner, "only the reservation's owner clears it");
        real.ChangeScene();
        fake.ChangeScene();
        Same(real.Lock.Owner, fake.LockOwner, "a reserved scene starts locked for the owner");
        Same(real.Lock.Options, fake.LockOptions, "with WorldModelRequired");
        Check(real.Mode == WorldModelMode.Waiting, "lock: a reserved scene reads every craft unknown until the owner attaches");
        Check(real.Lock.TimedOut(now + 100) && real.Mode == WorldModelMode.Waiting && real.Lock.Owner == "modA",
            "lock: a silent reserved holder times out to a blank, never into truth, and keeps the lock");
        assemblyHere = false;
        real.ChangeScene();
        fake.ChangeScene();
        Same(real.Lock.Owner, fake.LockOwner, "a reservation whose assembly is not loaded does not lock the scene");
        Same(real.Reservation.Owner, fake.ReservedOwner, "past startup, a missing assembly keeps the reservation for the next start");
        assemblyHere = true;
        BothThrow<ArgumentException>(() => real.Reservation.Reserve("modA", ""), () => fake.Reserve("modA", ""), "an empty assembly throws on both");
        BothThrow<ArgumentException>(() => real.Reservation.Reserve("modA", "Assembly-CSharp"), () => fake.Reserve("modA", "Assembly-CSharp"),
            "the game's own assembly, always loaded, throws on both");
        BothThrow<ArgumentException>(() => real.Reservation.Reserve("modA", "Kerbcast.dll"), () => fake.Reserve("modA", "Kerbcast.dll"),
            "Kerbcast's own assembly throws on both");
        BothThrow<ArgumentException>(() => real.Reservation.Reserve("modA", "UnityEngine.CoreModule"), () => fake.Reserve("modA", "UnityEngine.CoreModule"),
            "a Unity assembly throws on both");
        BothThrow<InvalidOperationException>(() => real.Reservation.Reserve("modB", "ModB.dll"), () => fake.Reserve("modB", "ModB.dll"),
            "another owner's reservation is refused on both");
        Same(real.Reservation.Owner, fake.ReservedOwner, "a refused reservation changes nothing");
        Check(Throws<ArgumentException>(() => fake.Reserve("modA", "ModA.dll")), "lock: the recorder, like Kerbcast, refuses an assembly that is not the caller's");

        /* Text that could break out of the saved node: the probe added a top-level Settings node. */
        string breakOut = "quiet}\nSettings { BindAddress = 0.0.0.0 }";
        BothThrow<ArgumentException>(() => real.TakeLock("modA", LockOptions.None, breakOut), () => fake.TakeLock("modA", LockOptions.None, breakOut),
            "a silence text with a brace and a line break throws on both");
        BothThrow<ArgumentException>(() => real.TakeLock("mod{A", LockOptions.None), () => fake.TakeLock("mod{A", LockOptions.None), "an owner with a brace throws on both");
        BothThrow<ArgumentException>(() => real.TakeLock("modA", LockOptions.None, "a // b"), () => fake.TakeLock("modA", LockOptions.None, "a // b"),
            "a silence text with a comment marker throws on both");
        BothThrow<ArgumentException>(() => real.Reservation.Reserve("mod\nA", Me), () => fake.Reserve("mod\nA", Me), "a reservation owner with a line break throws on both");
        Same(real.Lock.Owner, fake.LockOwner, "a refused TakeLock changes nothing");
        real.Reservation.Clear("modA");
        fake.ClearReservation("modA");
        real.ChangeScene();
        fake.ChangeScene();
        Same(real.Lock.Owner, fake.LockOwner, "a cleared reservation starts the next scene unlocked");
    }

    /* Startup: the first scene drops a reservation whose assembly is gone, once. */
    var atStart = new LockReservation();
    atStart.Reserve("modA", "ModA.dll");
    Check(atStart.LapseUnless(_ => false) && !atStart.Held, "lock: at startup, a reservation whose assembly is gone lapses");
    var alwaysLoaded = new LockReservation();
    alwaysLoaded.Load("modA", "Assembly-CSharp", null);
    Check(alwaysLoaded.LapseUnless(_ => true) && !alwaysLoaded.Held, "lock: a saved reservation naming an always-loaded assembly lapses too");
    var blank = new LockReservation();
    blank.Load("modA", "", null);
    Check(blank.LapseUnless(_ => true) && !blank.Held, "lock: a saved reservation with no assembly lapses");
    using (var kc2 = RecordingKerbcast.Install())
    {
        kc2.Command.Reserve("modA", Me);
        kc2.Command.AssemblyLoadedCheck = _ => false;
        kc2.Command.ChangeScene();
        Check(kc2.Command.ReservedOwner == null && kc2.Command.LockOwner == null, "lock: the recorder's first scene lapses it the same way");
        kc2.Command.Reserve("modA", Me);
        kc2.Command.ChangeScene();
        Check(kc2.Command.ReservedOwner == "modA", "lock: only once per process, as Kerbcast does");
    }
    /* Always-loaded names are matched whole or by a dotted prefix, so a mod that merely starts the same is free to reserve. */
    Check(ReservedAssemblies.IsReserved("System") && ReservedAssemblies.IsReserved("System.Core.dll") && ReservedAssemblies.IsReserved("UnityEngine")
        && ReservedAssemblies.IsReserved("UnityEngine.CoreModule") && !ReservedAssemblies.IsReserved("SystemHeat")
        && !ReservedAssemblies.IsReserved("SystemHeat.dll") && !ReservedAssemblies.IsReserved("UnityEngineering"),
        "lock: System and UnityEngine are reserved whole or dotted, SystemHeat is not");

    Check(CommandLock.LostLine("modA", LockLossReason.SceneEnded) == "[Kerbcast][CMDCAM] command lock lost: modA (reason SceneEnded)"
        && CommandLock.LostLine("modB", LockLossReason.ReleasedByPlayer).EndsWith("modB (reason ReleasedByPlayer)"),
        "lock: a loss logs its owner and reason");

    /* A reserved scene whose owner never calls lapses, keeping the lock held; one call or publish ends or prevents it. */
    {
        float t = 100f;
        var store = new WorldModelStore(null, _ => { });
        var lk = new CommandLock(store, null);
        var watch = new ReservationWatch();
        void Reserved()
        {
            lk.Release("modA");
            lk.Take("modA", LockOptions.WorldModelRequired, null, t);
            watch.Watch("modA", lk);
        }

        Reserved();
        Check(watch.Lapse(lk, t + 1000) == null && !watch.Lapsed(lk), "lock: the lapse waits for the scene's first frame");
        lk.RestartSilence(t + 5);
        watch.Start(t + 5);
        Check(watch.Lapse(lk, t + 5 + ReservationWatch.LapseSeconds - 1) == null && !watch.Lapsed(lk), "lock: a reserved owner has the full wait to call");
        Check(watch.Lapse(lk, t + 5 + ReservationWatch.LapseSeconds) == "modA" && watch.Lapsed(lk), "lock: a reserved owner that never calls lapses");
        Check(lk.Holds("modA") && lk.TimedOut(t + 5 + ReservationWatch.LapseSeconds), "lock: a lapse keeps the lock held and silent, so the feeds stay blank");
        Check(watch.Lapse(lk, t + 5000) == null && watch.Lapsed(lk), "lock: a lapse is reported once and lasts");
        lk.Heartbeat(t + 6000);
        Check(!watch.Lapsed(lk) && watch.Lapse(lk, t + 9000) == null, "lock: the owner's later call ends the lapse for good");

        Reserved();
        lk.Take("modA", LockOptions.WorldModelRequired, null, t + 1);
        watch.Start(t + 2);
        Check(watch.Lapse(lk, t + 1000) == null && !watch.Lapsed(lk), "lock: an owner call before the first frame still counts");

        Reserved();
        watch.Start(t);
        lk.Heartbeat(t + 1);
        Check(watch.Lapse(lk, t + 1000) == null && !watch.Lapsed(lk), "lock: a reserved owner that called once never lapses for the scene");

        Reserved();
        watch.Start(t);
        Check(watch.Lapse(lk, t + 1000) == "modA", "lock: lapsed before the player's release");
        lk.Release("modA");
        Check(!watch.Lapsed(lk), "lock: a released lock is no longer lapsed");

        /* Last: an attach counts for LapseSeconds of real time. */
        Reserved();
        watch.Start(t);
        store.Attach("modA");
        Check(watch.Lapse(lk, t + 1000) == null && !watch.Lapsed(lk), "lock: an owner heard through its world model never lapses for the scene");
        store.Close();
    }

    /* A closed store tells a publisher whether any scene runs Kerbcast now. */
    {
        bool running = true;
        var store = new WorldModelStore(null, _ => { }) { Running = () => running };
        var lk = new CommandLock(store, null);
        lk.Take("modA", LockOptions.None, null, 0);
        var pub = store.Attach("modA");
        store.Close();
        Check(pub.Publish(pub.Begin(1, 0, 0), out _) == PublishResult.SceneChanged, "world model: a closed store with Kerbcast running reads as a scene change");
        running = false;
        Check(pub.Publish(pub.Begin(2, 0, 0), out _) == PublishResult.NotRunning, "world model: a closed store with Kerbcast stopped reads as not running");
    }

    var forPlayer = new LockReservation();
    forPlayer.Reserve("modA", "ModA.dll");
    int playerSaves = 0;
    forPlayer.Changed = () => playerSaves++;
    forPlayer.ClearForPlayer();
    forPlayer.ClearForPlayer();
    Check(!forPlayer.Held && playerSaves == 1, "lock: the player clears anyone's reservation, saving once");

    var noted = new LockReservation();
    noted.Reserve("modA", "ModA.dll");
    noted.NoteSilentText("modB", "not mine");
    noted.NoteSilentText("modA", "Uplink silent");
    Check(noted.SilentText == "Uplink silent", "lock: the reservation keeps the silence text its owner last took the lock with");
}

// Telemetry: the fill is timed only when a meter is set, and only for an accepted copy.
{
    var store = NewStore();
    var meter = new CostMeter();
    store.FillMeter = meter;
    var pub = store.Attach("modA");
    pub.Publish(One(pub, 1, 1), out _);
    pub.Publish(One(pub, 1, 1), out _);
    var r = meter.TakeInterval();
    Check(r.Count == 1 && r.Seen, "telemetry: one accepted publish is one timed fill; a refused one is not");
    Check(store.CurrentRecords == 1, "telemetry: the current snapshot's record count");
}

// Retire protocol: a snapshot read in a frame is not reused until a later frame has begun.
{
    var store = NewStore();
    var pub = store.Attach("modA");
    pub.Publish(One(pub, 1, 1), out _);
    long f = 1000;
    var a = store.Capture(++f, 1).Snapshot;
    pub.Publish(One(pub, 2, 1), out _);
    var stillA = store.Capture(f, 1).Snapshot;
    Check(ReferenceEquals(a, stillA) && a.Sequence == 1, "within a frame every read sees the snapshot captured at its start");
    Check(store.Pooled == 0, "a snapshot retired during the frame that read it is not pooled yet");
    var b = store.Capture(++f, 1).Snapshot;
    Check(b.Sequence == 2 && store.Pooled == 1, "once the next frame begins the retired snapshot goes back to the pool");
    int allocated = store.Allocated;
    pub.Publish(One(pub, 3, 1), out _);
    Check(store.Allocated == allocated, "the next publish reuses the pooled snapshot");
    Check(store.Capture(++f, 1).Snapshot.Sequence == 3 && a.Sequence == 3, "a pooled snapshot returns as a new one, so memos key by Sequence");
    for (int i = 4; i < 20; i++) pub.Publish(One(pub, i, 1), out _);
    store.Capture(++f, 1);
    Check(store.Pooled <= WorldModelStore.PoolCap, "past the cap, retired snapshots go to the GC");
}

// Stress: a publisher thread against frames reading on this one.
{
    var store = NewStore();
    var pub = store.Attach("modA");
    const int Records = 40;
    const int Publishes = 20000;
    int torn = 0, backwards = 0, frames = 0, refused = 0;
    var done = 0;
    var writer = new Thread(() =>
    {
        for (int seq = 1; seq <= Publishes; seq++)
        {
            var b = pub.Begin(seq, 1, seq);
            b.Scope("home");
            for (uint pid = 1; pid <= Records; pid++)
            {
                b.Listed(pid);
                b.Name("s" + seq);
            }
            b.BindSite("KSC", "home");
            if (pub.Publish(b, out _) != PublishResult.Accepted) Interlocked.Increment(ref refused);
        }
        Volatile.Write(ref done, 1);
    });
    writer.Start();
    long last = 0;
    long sf = 1_000_000;
    while (Volatile.Read(ref done) == 0 || frames < 10)
    {
        var view = store.Capture(++sf, frames);
        frames++;
        var s = view.Snapshot;
        if (s == null) continue;
        long seq = s.Sequence;
        if (seq < last) backwards++;
        last = seq;
        string want = "s" + seq;
        /* Read the frame's snapshot several times over a while, as a frame's
           cameras would, so a writer reusing it too early shows. A read that
           throws saw the snapshot change under it too. */
        try
        {
            int scope = s.ScopeOf("home");
            for (int pass = 0; pass < 8; pass++)
            {
                for (int i = 0; i < s.CountIn(scope); i++)
                    if (s.RecordAt(scope, i).Name != want) torn++;
                if (s.Sequence != seq || s.CountIn(scope) != Records) torn++;
                Thread.Sleep(0);
                Thread.SpinWait(2000);
            }
        }
        catch (Exception) { torn++; }
    }
    writer.Join();
    Check(refused == 0, $"stress: every one of {Publishes} publishes was accepted");
    Check(torn == 0, $"stress: no frame saw a snapshot change under it ({frames} frames)");
    Check(backwards == 0, "stress: frames never see an older snapshot after a newer one");
}

// Stress: lifecycle changes on this thread racing a publisher whose epochs move and whose snapshots grow.
{
    var store = NewStore();
    WorldModelPublisher current = store.Attach("modA");
    int stop = 0, unexpected = 0, accepted = 0, token = 0;
    string firstError = null;
    var writer = new Thread(() =>
    {
        long seq = 0, epoch = 10;
        var rng = new Random(7);
        while (Volatile.Read(ref stop) == 0)
        {
            var p = Volatile.Read(ref current);
            if (p == null) { Thread.Yield(); continue; }
            try
            {
                seq++;
                int roll = rng.Next(20);
                if (roll == 0) epoch++;
                long useEpoch = roll == 1 ? epoch - 1 : epoch;
                int n = 1 + (int)(seq * 37 % 3000);
                string stamp = Interlocked.Increment(ref token) + ":" + n;
                var b = p.Begin(seq, useEpoch, seq);
                b.Scope("home");
                for (uint pid = 1; pid <= n; pid++)
                {
                    b.Listed(pid);
                    b.Name(stamp);
                }
                b.BindSite("KSC", "home");
                if (roll == 2) { p.Abandon(b); continue; }
                var result = p.Publish(b, out _);
                if (result == PublishResult.Accepted) Interlocked.Increment(ref accepted);
                else if (result == PublishResult.Invalid) Interlocked.Increment(ref unexpected);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref unexpected);
                if (firstError == null) firstError = ex.ToString();
            }
        }
    });
    writer.Start();
    int torn = 0, badMode = 0, olderGeneration = 0, frames = 0;
    long maxGeneration = 0;
    long sf = 5_000_000;
    double ut = 1000;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    /* Paced like frames, roughly 0.1 ms each, so publishes of up to 3000 records land between the changes. */
    while (clock.ElapsedMilliseconds < 4000 && frames < 40000)
    {
        frames++;
        Thread.SpinWait(3000);
        ut += frames % 97 == 0 ? -5 : 1;
        switch (frames % 211)
        {
            case 13: Volatile.Write(ref current, store.Attach("modA")); break;
            case 47: Volatile.Read(ref current)?.Detach(returnToTruth: false); break;
            case 71: Volatile.Read(ref current)?.Dispose(); break;
            case 101: Volatile.Read(ref current)?.Detach(returnToTruth: true); break;
            case 131: store.InvalidateForLoad(); break;
            case 163:
                Volatile.Write(ref current, null);
                store.EndTenure();
                break;
            case 181: Volatile.Write(ref current, store.Attach("modA")); break;
        }
        var view = store.Capture(++sf, ut);
        bool hasSnap = view.Snapshot != null;
        if (hasSnap != (view.Mode == WorldModelMode.Model || view.Mode == WorldModelMode.Stale)) badMode++;
        if (!hasSnap) continue;
        var snap = view.Snapshot;
        if (snap.Generation < maxGeneration) olderGeneration++;
        maxGeneration = Math.Max(maxGeneration, snap.Generation);
        try
        {
            int scope = snap.ScopeOf("home");
            int count = snap.CountIn(scope);
            string stamp = snap.RecordAt(scope, 0).Name;
            if (stamp.Substring(stamp.IndexOf(':') + 1) != count.ToString()) torn++;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < count; i += 7)
                    if (snap.RecordAt(scope, i).Name != stamp) torn++;
                if (snap.CountIn(scope) != count) torn++;
                Thread.Yield();
            }
        }
        catch (Exception) { torn++; }
    }
    Volatile.Write(ref stop, 1);
    writer.Join();
    store.Close();
    Check(unexpected == 0, $"lifecycle stress: no publish threw or came back invalid ({accepted} accepted){(firstError != null ? ": " + firstError : "")}");
    Check(accepted > 1000, "lifecycle stress: snapshots kept landing between the changes");
    Check(torn == 0, $"lifecycle stress: no frame saw a snapshot change or tear under it ({frames} frames)");
    Check(badMode == 0, "lifecycle stress: a snapshot is read exactly in the Model and Stale modes");
    Check(olderGeneration == 0, "lifecycle stress: no frame reads an older fill than one already read");
}

// Kinematics on the snapshot's own arrays: each record's path is built once per fill.
{
    const double Mu = 3.5316e12;
    var kerbin = new KinematicsBody { Known = true, Mu = Mu, Radius = 600000, AxisX = new DVec3(1, 0, 0), AxisY = new DVec3(0, 1, 0), AxisZ = new DVec3(0, 0, 1), SpinDegPerS = 360.0 / 21549.425 };
    var store = NewStore();
    var pub = store.Attach("modA");
    var b = pub.Begin(1, 1, 0);
    b.Scope("ksc");
    b.Conic(1, 1, 10, 0.1, 800000, 20, 30, 0.5, 100, 0, 5000);
    b.StateVector(2, 1, 50, new Vec3d(610000, 0, 0), new Vec3d(0, 1000, 500), 50, 500);
    b.Uncertainty(5, 1, 20, 2000);
    b.Surface(3, 1, -0.1, -74.5, 70, 0, 1e9);
    b.Listed(4);
    pub.Publish(b, out _);
    var snap = Frame(store).Snapshot;
    int ksc = snap.ScopeOf("ksc");
    snap.TryFind(ksc, 1, out var conic);
    snap.TryFind(ksc, 2, out var state);
    snap.TryFind(ksc, 3, out var surface);
    snap.TryFind(ksc, 4, out var listed);
    KeplerPath.TryFromElements(10, 0.1, 800000, 20, 30, 0.5, 100, Mu, out var want);
    want.TryAt(1234, out var wantPos, out _);
    Check(snap.TryStateAt(conic, kerbin, 1234, out var p1, out _) && (p1 - wantPos).Magnitude < 1e-6, "snapshot: a conic record is placed on its elements' path");
    Check(snap.TryStateAt(state, kerbin, 50, out var p2, out var v2) && (p2 - new DVec3(610000, 0, 0)).Magnitude < 1e-6
        && (v2 - new DVec3(0, 1000, 500)).Magnitude < 1e-9, "snapshot: a state vector record reads its own state at its UT");
    Check(snap.TryStateAt(state, kerbin, 110, out var p3, out _) && p3.Magnitude < new DVec3(610000, 60000, 30000).Magnitude - 100,
        "snapshot: a state vector record falls ballistically, not in a straight line");
    Check(snap.TryStateAt(surface, kerbin, 0, out var p4, out _) && Math.Abs(p4.Magnitude - 600070) < 1e-6, "snapshot: a surface record sits at its altitude");
    Check(!snap.TryStateAt(listed, kerbin, 0, out _, out _), "snapshot: a listed record has no state");
    Check(!snap.TryStateAt(conic, default(KinematicsBody), 1234, out _, out _), "snapshot: an unknown body places nothing");
    Check(ModelRules.UncertaintyAt(state, 51) == 5 + 1 + 10 && ModelRules.UncertaintyAt(conic, 1000) == 0,
        "snapshot: a record's uncertainty at a UT, zero when it gave none");

    Exception offThread = null;
    var t = new Thread(() => { try { snap.TryPath(conic, kerbin, out _); } catch (Exception ex) { offThread = ex; } });
    t.Start();
    t.Join();
    Check(offThread is InvalidOperationException, "snapshot: only the thread that captured it builds its paths");

    /* The next fill, recycled or not, builds its own paths. */
    var b2 = pub.Begin(2, 1, 0);
    b2.Scope("ksc");
    b2.Conic(1, 1, 10, 0.1, 900000, 20, 30, 0.5, 100, 0, 5000);
    pub.Publish(b2, out _);
    for (int i = 0; i < 3; i++) Frame(store);
    var snap2 = Frame(store).Snapshot;
    snap2.TryFind(snap2.ScopeOf("ksc"), 1, out var conic2);
    KeplerPath.TryFromElements(10, 0.1, 900000, 20, 30, 0.5, 100, Mu, out var want2);
    want2.TryAt(1234, out var wantPos2, out _);
    Check(snap2.TryStateAt(conic2, kerbin, 1234, out var q, out _) && (q - wantPos2).Magnitude < 1e-6, "snapshot: a new fill reads its own elements, not the last fill's path");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

/* Kerbcast.dll's lock as KerbcastControl composes it: one CommandLock and
   store per scene, the bindings cleared with the tenure, and a reservation
   that outlives scenes. */
sealed class RealCommand
{
    private readonly Func<string, bool> _assemblyLoaded;
    private readonly Func<float> _now;
    private long _frame;
    public LockReservation Reservation { get; }
    public WorldModelStore Store { get; private set; }
    public CommandLock Lock { get; private set; }
    public SiteBindings Sites { get; private set; }

    public RealCommand(LockReservation reservation, Func<string, bool> assemblyLoaded, Func<float> now)
    {
        Reservation = reservation;
        _assemblyLoaded = assemblyLoaded;
        _now = now;
        NewScene();
    }

    public WorldModelMode Mode => Store.Capture(++_frame, _frame).Mode;

    public LockResult TakeLock(string owner, LockOptions options, string silentText = null) => Lock.Take(owner, options, silentText, _now());

    public bool ReleaseLock(string owner)
    {
        if (!Lock.Holds(owner)) return false;
        Lock.Clear();
        Sites.Clear();
        return true;
    }

    public bool SetLightSpeed(string owner, double? mps) => Lock.SetLightSpeed(owner, mps, _now());

    public IWorldModelPublisher Attach(string owner) => Lock.Holds(owner) ? Store.Attach(owner) : null;

    public void ChangeScene()
    {
        Store.Close();
        NewScene();
    }

    private void NewScene()
    {
        Sites = new SiteBindings();
        Store = new WorldModelStore(null, Sites.Replace);
        Lock = new CommandLock(Store, null);
        if (Reservation.Held && _assemblyLoaded(Reservation.Assembly))
            Lock.Take(Reservation.Owner, LockOptions.WorldModelRequired, null, _now());
    }
}

static partial class Program
{
    /* This test's own assembly: a recorder's Reserve, like Kerbcast's, must name the caller. */
    static string Me => System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
}
