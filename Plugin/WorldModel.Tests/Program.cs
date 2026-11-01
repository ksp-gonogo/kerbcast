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
    Check(ModelRules.UncertaintyAlong(state, 49) == 5 + 1 + 10 && ModelRules.UncertaintyAlong(state, 51) == 5 + 1 + 10
        && ModelRules.UncertaintyAt(state, 49) == 5 && ModelRules.UncertaintyAlong(conic, 0) == 0,
        "snapshot: read back before its start, a record's uncertainty grows as it does after, where UncertaintyAt holds its start");

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

/*
 * A command camera's framing radius across world model handovers, read as
 * CommandCameraRig.KnownSubject reads it off the orbit branch: the light-time
 * solve on the record's own positions from its ValidFromUt, the held instant,
 * then the uncertainty at that instant from the same frame's record. A craft
 * climbs at 300 m/s from 2 km to about 10 km from the lens; a record is
 * published every 0.53 s, late by `lag`, its radius and rate meeting the
 * last one's at its start. Sampled at 60 fps, the radius must never rise
 * above the trend at render UT (a spike) nor jump by more than the trend can
 * move in the gap a clamp to ValidFromUt skips.
 */
{
    const double Mu = 3.5316e12;
    const double T0 = 10000, Period = 0.53, Climb = 300, CraftRadius = 10;
    const double U0 = 30, URate = 2;
    var kerbin = new KinematicsBody { Known = true, Mu = Mu, Radius = 600000, AxisX = new DVec3(1, 0, 0), AxisY = new DVec3(0, 1, 0), AxisZ = new DVec3(0, 0, 1), SpinDegPerS = 360.0 / 21549.425 };
    var launch = kerbin.SurfaceAt(0, 0.19, 0, T0, out var launchVel);
    var up = launch.Normalized;
    DVec3 Truth(double t) => launch + launchVel * (t - T0) + up * (Climb * (t - T0));
    DVec3 TruthVel() => launchVel + up * Climb;
    double Trend(double t) => U0 + URate * (t - T0);

    foreach (double? c in new double?[] { null, 3e8, 1e5, 2e4 })
    foreach (double lag in new[] { 0.0, 0.12, 0.3 })
    {
        var store = NewStore();
        var pub = store.Attach("modA");
        int published = 0;
        void PublishUpTo(double ut)
        {
            while (T0 + published * Period + lag <= ut)
            {
                double from = T0 + published * Period;
                var b = pub.Begin(published + 1, 1, from);
                b.Scope("ksc");
                var p = Truth(from);
                var v = TruthVel();
                b.StateVector(7, 1, from, new Vec3d(p.X, p.Y, p.Z), new Vec3d(v.X, v.Y, v.Z), from, from + 2.0);
                b.Uncertainty(Trend(from), URate, 0, 0);
                b.Radius(CraftRadius);
                b.BindSite("KSC", "ksc");
                pub.Publish(b, out _);
                published++;
            }
        }

        var hold = new InstantHold();
        double last = double.NaN, maxAbove = double.NegativeInfinity, maxJump = 0, maxJumpAt = 0;
        int unpositioned = 0, samples = 0;
        for (int n = 0; n < 60 * 30; n++)
        {
            double ut = T0 + lag + n / 60.0;
            PublishUpTo(ut);
            var view = Frame(store, ut);
            int scope = ModelLookup.ScopeFor(view, "KSC", out _);
            var station = kerbin.SurfaceAt(0, 0, 70, ut, out _);
            var path = new HandoverPath { View = view, Scope = scope, Body = kerbin };
            double tr = ut;
            if (c.HasValue)
            {
                var motion = ModelLookup.Find(view, scope, 7, ut, i => i == 1);
                double validFrom = motion.State == CraftFixState.Positioned ? motion.Record.ValidFromUt : double.NegativeInfinity;
                if (PastInstant.SolveFrom(ref path, station, ut, c.Value, 400, validFrom, ut, out tr) != PastStatus.Found)
                {
                    unpositioned++;
                    continue;
                }
                double held = hold.NotBefore(ut, 0, c.Value, 1);
                if (held > tr && PastInstant.Arrived(ref path, station, ut, c.Value, held)) tr = held;
                hold.Shown(ut, tr, 0, c.Value, 1, 1);
            }
            var fix = ModelLookup.Find(view, scope, 7, tr, i => i == 1);
            if (fix.State != CraftFixState.Positioned)
            {
                unpositioned++;
                continue;
            }
            double radius = fix.RadiusM + ModelRules.UncertaintyAt(fix.Record, tr);
            samples++;
            maxAbove = Math.Max(maxAbove, radius - (CraftRadius + Trend(ut)));
            if (!double.IsNaN(last) && Math.Abs(radius - last) > maxJump)
            {
                maxJump = Math.Abs(radius - last);
                maxJumpAt = ut;
            }
            last = radius;
        }
        /* Off the clamp the radius moves at the trend's rate; a clamp to the new record's start skips at most the publish lag plus a light time. */
        double light = c.HasValue ? 10500 / c.Value : 0;
        double allowed = URate * (1 / 60.0 + Math.Min(Period, lag + light)) + 1e-6;
        string name = $"handover c={(c.HasValue ? c.Value.ToString("G3") : "inf")} lag={lag}";
        Check(unpositioned == 0 && samples == 60 * 30, $"{name}: every frame has a fix ({unpositioned} without)");
        Check(maxAbove <= 1e-6, $"{name}: the framing radius never rises above the trend at render UT (most {maxAbove:G3} m)");
        Check(maxJump <= allowed, $"{name}: the framing radius steps at most {allowed:G3} m a frame (most {maxJump:G3} m at {maxJumpAt - T0:F3} s)");
    }
}

/* A state-vector record with a net acceleration, gravity included, propagates under it in place of the body's gravity. */
{
    const double Mu = 3.5316e12;
    var kerbin = new KinematicsBody { Known = true, Mu = Mu, Radius = 600000, AxisX = new DVec3(1, 0, 0), AxisY = new DVec3(0, 1, 0), AxisZ = new DVec3(0, 0, 1), SpinDegPerS = 360.0 / 21549.425 };
    var store = NewStore();
    var pub = store.Attach("modA");
    var b = pub.Begin(1, 1, 0);
    b.Scope("ksc");
    /* A straight-line case: falling at 6 m/s with net acceleration zero (a real chute descent also carries the body's centripetal term). */
    b.StateVector(1, 1, 100, new Vec3d(602000, 0, 0), new Vec3d(-6, 0, 0), 100, 200);
    b.Acceleration(new Vec3d(0, 0, 0));
    b.StateVector(2, 1, 100, new Vec3d(602000, 0, 0), new Vec3d(-6, 0, 0), 100, 200);
    b.StateVector(3, 1, 100, new Vec3d(602000, 0, 0), new Vec3d(-6, 3, 1), 100, 200);
    b.Acceleration(new Vec3d(-2, 0.5, 0.25));
    Check(pub.Publish(b, out var error) == PublishResult.Accepted, $"acceleration: a record with one is accepted ({error})");
    var snap = Frame(store).Snapshot;
    int ksc = snap.ScopeOf("ksc");
    snap.TryFind(ksc, 1, out var chute);
    snap.TryFind(ksc, 2, out var ballistic);
    snap.TryFind(ksc, 3, out var pushed);

    bool line = true;
    for (double d = 0; d <= 100; d += 0.25)
    {
        line &= snap.TryStateAt(chute, kerbin, 100 + d, out var p, out var v)
            && (p - new DVec3(602000 - 6 * d, 0, 0)).Magnitude < 1e-9 && (v - new DVec3(-6, 0, 0)).Magnitude < 1e-12;
    }
    Check(line, "acceleration: zero net acceleration under gravity propagates in a straight line at constant speed");
    snap.TryStateAt(chute, kerbin, 102, out var c2, out _);
    bool fell = snap.TryStateAt(ballistic, kerbin, 102, out var b2, out _);
    Check(fell && Math.Abs((c2 - b2).Magnitude - 0.5 * Mu / (602000.0 * 602000.0) * 4) < 0.05,
        $"acceleration: without it the same state falls about g d^2 / 2 below (19.5 m at 2 s, got {(c2 - b2).Magnitude:F2})");

    KeplerPath.TryFromState(new DVec3(602000, 0, 0), new DVec3(-6, 0, 0), 100, Mu, out var old, 600000);
    bool exact = true;
    foreach (double ut in new[] { 100.0, 100.5, 101, 117.25, 150, 199.9 })
    {
        exact &= snap.TryStateAt(ballistic, kerbin, ut, out var p, out var v) && old.TryAt(ut, out var op, out var ov)
            && p.X == op.X && p.Y == op.Y && p.Z == op.Z && v.X == ov.X && v.Y == ov.Y && v.Z == ov.Z;
    }
    Check(exact && !ballistic.HasAcceleration, "acceleration: a record without one matches the old ballistic propagation exactly");

    var a = new DVec3(-2, 0.5, 0.25);
    var v0 = new DVec3(-6, 3, 1);
    Check(snap.TryStateAt(pushed, kerbin, 104, out var pp, out var pv)
        && (pp - (new DVec3(602000, 0, 0) + v0 * 4 + a * 8)).Magnitude < 1e-9 && (pv - (v0 + a * 4)).Magnitude < 1e-12
        && snap.TryStateAt(pushed, kerbin, 99, out var back, out _) && (back - (new DVec3(602000, 0, 0) - v0 + a * 0.5)).Magnitude < 1e-9,
        "acceleration: position is x0 + v d + a d^2 / 2 and velocity v + a d, either side of the state's UT");
    Check(snap.TryPath(pushed, kerbin, out var pushedPath) && Math.Abs(pushedPath.PeriapsisSpeed - (v0 + a * 100).Magnitude) < 1e-9
        && Math.Abs(pushedPath.SpeedOver(100, 104) - (v0 + a * 4).Magnitude) < 1e-12
        && Math.Abs(pushedPath.SpeedOver(98, 100) - v0.Magnitude) < 1e-12 && (v0 - a * 2).Magnitude < v0.Magnitude,
        "acceleration: the speed bound is the faster end of the window asked, the record's window by default");

    /* The storage a publisher reuses must not carry an acceleration into the next record in its slot. */
    var b3 = pub.Begin(2, 1, 0);
    b3.Scope("ksc");
    b3.StateVector(1, 1, 100, new Vec3d(602000, 0, 0), new Vec3d(-6, 0, 0), 100, 200);
    b3.StateVector(3, 1, 100, new Vec3d(602000, 0, 0), new Vec3d(-6, 3, 1), 100, 200);
    pub.Publish(b3, out _);
    for (int i = 0; i < 3; i++) Frame(store);
    var snap3 = Frame(store).Snapshot;
    snap3.TryFind(snap3.ScopeOf("ksc"), 1, out var reused);
    Check(!reused.HasAcceleration && snap3.TryStateAt(reused, kerbin, 102, out var rp, out _) && (rp - b2).Magnitude < 1e-9,
        "acceleration: a reused record slot starts without one");
}

/* The light-time solve on an accelerated record: the root satisfies the equation on the record's own path, below and above c. */
{
    const double Mu = 3.5316e12;
    var kerbin = new KinematicsBody { Known = true, Mu = Mu, Radius = 600000, AxisX = new DVec3(1, 0, 0), AxisY = new DVec3(0, 1, 0), AxisZ = new DVec3(0, 0, 1), SpinDegPerS = 360.0 / 21549.425 };
    var x0 = new DVec3(610000, 0, 0);
    var station = new DVec3(607000, -2000, 0);
    foreach (var (v0, a, c) in new[]
    {
        (new DVec3(-6, 0, 0), new DVec3(0, 0, 0), 3e3),
        (new DVec3(0, 200, 0), new DVec3(0, 30, 0), 2e4),
        (new DVec3(0, -400, 0), new DVec3(0, -40, 0), 300.0),
    })
    {
        var store = NewStore();
        var pub = store.Attach("modA");
        var b = pub.Begin(1, 1, 0);
        b.Scope("ksc");
        b.StateVector(7, 1, 100, new Vec3d(x0.X, x0.Y, x0.Z), new Vec3d(v0.X, v0.Y, v0.Z), 90, 140);
        b.Acceleration(new Vec3d(a.X, a.Y, a.Z));
        pub.Publish(b, out _);
        double ut = 130;
        var view = Frame(store, ut);
        int scope = view.Snapshot.ScopeOf("ksc");
        view.Snapshot.TryFind(scope, 7, out var r);
        view.Snapshot.TryPath(r, kerbin, out var kp);
        double bound = kp.SpeedOver(90, ut);
        var path = new HandoverPath { View = view, Scope = scope, Body = kerbin };
        var status = PastInstant.SolveFrom(ref path, station, ut, c, bound, 90, ut, out double tr);
        double d = tr - 100;
        var at = x0 + v0 * d + a * (0.5 * d * d);
        double g = tr + (station - at).Magnitude / c - ut;
        string name = $"acceleration light time c={c:G3} bound={bound:F0}";
        Check(status == PastStatus.Found && g <= 0 && g > -1e-5, $"{name}: the root is on the accelerated path (g {g:G3}, tr {tr:F4})");
    }
}

/* Acceleration through the builder: kept on a state vector, refused elsewhere, the same on the recorder. */
{
    using (var kc = RecordingKerbcast.Install())
    {
        kc.Command.TakeLock("modA", LockOptions.WorldModelRequired);
        var fake = kc.Command.AttachWorldModel("modA");
        var b = fake.Begin(1, 1, 0);
        b.Scope("home");
        b.StateVector(1, 1, 5, new Vec3d(610000, 0, 0), new Vec3d(0, 1, 0), 5, 50);
        b.Acceleration(new Vec3d(0.5, -1.25, 2));
        b.StateVector(2, 1, 5, new Vec3d(610000, 0, 0), new Vec3d(0, 1, 0), 5, 50);
        Check(fake.Publish(b, out _) == PublishResult.Accepted, "acceleration: the recorder accepts it");
        var one = kc.Command.Published[0].Find("home", 1);
        var two = kc.Command.Published[0].Find("home", 2);
        Check(one.HasAcceleration && one.Acceleration == new Vec3d(0.5, -1.25, 2) && !two.HasAcceleration && two.Acceleration == default(Vec3d),
            "acceleration: the builder round-trips it, and a record without one reads none");
    }
    var store = NewStore();
    var pub = store.Attach("modA");
    PublishResult With(long seq, Action<WorldModelBuilder> add, out string error)
    {
        var bb = pub.Begin(seq, 1, 0);
        bb.Scope("home");
        add(bb);
        return pub.Publish(bb, out error);
    }
    Check(With(1, bb => { bb.Conic(1, 1, 0, 0.1, 700000, 0, 0, 0, 0, 0, 10); bb.Acceleration(new Vec3d(0, 0, 0)); }, out var e1) == PublishResult.Invalid
        && e1.Contains("without a state vector"), $"acceleration: on a conic it is invalid ({e1})");
    Check(With(2, bb => { bb.Surface(1, 1, 0, 0, 0, 0, 10); bb.Acceleration(new Vec3d(0, 0, 0)); }, out _) == PublishResult.Invalid,
        "acceleration: on a surface record it is invalid");
    Check(With(3, bb => { bb.StateVector(1, 1, 5, new Vec3d(610000, 0, 0), new Vec3d(0, 1, 0), 5, 50); bb.Acceleration(new Vec3d(double.NaN, 0, 0)); }, out var e3)
        == PublishResult.Invalid && e3.Contains("non-finite acceleration"), $"acceleration: a non-finite one is invalid ({e3})");
    Check(Throws<InvalidOperationException>(() => { var bb = pub.Begin(4, 1, 0); bb.Scope("home"); bb.Acceleration(new Vec3d(0, 0, 0)); }),
        "acceleration: before any record it throws, like every modifier");
    var b4 = pub.Begin(5, 1, 0);
    b4.Scope("home");
    b4.StateVector(1, 1, 5, new Vec3d(610000, 0, 0), new Vec3d(0, 1, 0), 5, 50);
    b4.Acceleration(new Vec3d(1, 2, 3));
    Check(pub.Publish(b4, out _) == PublishResult.Accepted, "acceleration: Kerbcast accepts it on a state vector");
    var snap = Frame(store).Snapshot;
    snap.TryFind(snap.ScopeOf("home"), 1, out var rec);
    Check(rec.HasAcceleration && rec.Acceleration == new Vec3d(1, 2, 3), "acceleration: the snapshot copies it");
}

/*
 * A Launchpad lens 500 m from the pad watching a vertical climb under a world
 * model, with the rig's numbers: 50 m/s^2 net for 40 s to 2 km/s at 40 km,
 * then a coast, and one state-vector record a second starting at its publish
 * UT. The picture draws the craft at its light time on its true path; the aim
 * solves the same equation on the model's records. Read back only from the
 * newest record, the aim is held at its 4 s cap while the light time runs to
 * tens of seconds, so it sits hundreds of pixels off the drawn craft at the
 * 1 degree lens floor at 3 km/s, and the 30 degree lens at 30 m/s. Placed from the record published for the instant, it
 * lands on the drawn craft. The lens's body is taken as not turning, which
 * leaves the light-time instants as they are.
 */
{
    const double Mu = 3.5316e12, R = 600000, PadAlt = 75, BurnS = 40, Accel = 50, FramePx = 576;
    var kerbin = new KinematicsBody { Known = true, Mu = Mu, Radius = R, AxisX = new DVec3(1, 0, 0), AxisY = new DVec3(0, 1, 0), AxisZ = new DVec3(0, 0, 1) };
    var pad = new DVec3(R + PadAlt, 0, 0);
    var ground = new DVec3(0, 174.9, 0);
    var lens = new DVec3(R + 70, 0, -500);
    var thrust = new DVec3(Accel, 0, 0);
    KeplerPath.TryAccelerated(pad, ground, thrust, 0, 0, BurnS, out var burn);
    burn.TryAt(BurnS, out var bp, out var bv);
    KeplerPath.TryFromState(bp, bv, BurnS, Mu, out var coast, R);
    DVec3 Truth(double t)
    {
        if (t <= 0) return pad + ground * t;
        DVec3 p, v;
        if (t <= BurnS) burn.TryAt(t, out p, out v);
        else coast.TryAt(t, out p, out v);
        return p;
    }
    ModelRecord Published(double at)
    {
        var p = Truth(at);
        var v = (Truth(at + 1e-3) - Truth(at - 1e-3)) * (1.0 / 2e-3);
        bool burning = at > 0 && at < BurnS;
        return new ModelRecord
        {
            PersistentId = 7, State = CraftFixState.Positioned, Kinematics = KinematicsKind.StateVector, BodyIndex = 1,
            ValidFromUt = at, ValidUntilUt = at + 10, StateUt = at,
            Position = new Vec3d(p.X, p.Y, p.Z), Velocity = new Vec3d(v.X, v.Y, v.Z),
            HasAcceleration = burning, Acceleration = new Vec3d(thrust.X, thrust.Y, thrust.Z),
        };
    }
    double Px(DVec3 a, DVec3 b, double fovDeg)
    {
        double cos = DVec3.Dot((a - lens).Normalized, (b - lens).Normalized);
        return Math.Acos(Math.Min(1, cos)) / Kinematics.Deg2Rad / fovDeg * FramePx;
    }

    /* The aim's instant and place at frame UT `ut`, from the records published up to it, as KnownSubject solves it. */
    void Aim(double ut, double c, bool kept, out double tr, out DVec3 at)
    {
        var history = new ModelRecordHistory();
        double last = Math.Floor(ut - 0.5);
        for (double p = -20; p <= last; p++) history.Add(Published(p), kerbin, double.NaN);
        var newest = Published(last);
        WorldModelSnapshot.BuildPath(newest, kerbin, out var newestPath);
        DVec3? Along(double t)
        {
            if (kept && history.TryFind(t, out var r, out var path, out double floor, out bool back))
                return ModelRecordHistory.TryPlace(r, path, floor, back, kerbin, t, out var q) ? q : (DVec3?)null;
            return newestPath.TryAt(t, out var n, out _) ? n : (DVec3?)null;
        }
        DVec3? Windowed(double t) => t >= newest.ValidFromUt && newestPath.TryAt(t, out var n, out _) ? n : (DVec3?)null;
        var along = new FnPath { At = Along };
        var windowed = new FnPath { At = Windowed };
        PastInstant.SolveAlong(ref along, ref windowed, lens, ut, c, 2200, newest.ValidFromUt, ut, out tr,
            kept ? history.EarliestUt : double.NaN);
        at = Along(tr) ?? default;
    }

    void Drawn(double ut, double c, out double tr, out DVec3 at)
    {
        var truth = new FnPath { At = t => Truth(t) };
        PastInstant.Solve(ref truth, lens, ut, c, 2200, -1e6, ut, out tr);
        at = Truth(tr);
    }

    foreach (var (c, fov, uts) in new[] { (3000.0, 1.0, new[] { 105.5, 117.5, 141.5, 165.5 }), (30.0, 30.0, new[] { 70.5, 110.5, 160.5 }) })
    {
        double oldWorst = 0, newWorst = 0, oldLag = 0;
        foreach (double ut in uts)
        {
            Drawn(ut, c, out double drawnUt, out var drawn);
            Aim(ut, c, false, out double oldUt, out var oldAt);
            Aim(ut, c, true, out double newUt, out var newAt);
            oldWorst = Math.Max(oldWorst, Px(oldAt, drawn, fov));
            newWorst = Math.Max(newWorst, Px(newAt, drawn, fov));
            oldLag = Math.Max(oldLag, oldUt - drawnUt);
            Console.WriteLine($"     c={c} ut={ut} drawn at {drawnUt:F2} ({drawn.Magnitude - R:F0} m); newest only {oldUt:F2}, {Px(oldAt, drawn, fov):F1} px; kept {newUt:F2}, {Px(newAt, drawn, fov):F2} px");
        }
        Check(oldWorst > 80 && oldLag > 3, $"history c={c}: the newest record alone holds the aim at its read-back cap, {oldLag:F1} s after the drawn craft and up to {oldWorst:F0} px off it");
        Check(newWorst < 2, $"history c={c}: the records kept for each instant aim within {newWorst:F2} px of the drawn craft");
    }

    foreach (double ut in new[] { 20.5, 75.5, 140.5 })
    {
        const double c = 300000;
        Aim(ut, c, false, out double oldUt, out var oldAt);
        Aim(ut, c, true, out double newUt, out var newAt);
        Check(oldUt == newUt && (oldAt - newAt).Magnitude == 0, $"history c=300 km/s ut={ut}: a light time within the newest record's read-back is solved as before");
    }

    var h = new ModelRecordHistory();
    h.Add(Published(10), kerbin, double.NaN);
    Check(!h.TryFind(0, out _, out _, out _, out _) && double.IsNaN(h.EarliestUt), "history: the newest record alone keeps nothing earlier");
    for (double p = 11; p <= 20; p++) h.Add(Published(p), kerbin, double.NaN);
    Check(!h.TryFind(16.5, out _, out _, out _, out _), "history: within the newest record's read-back the newest places the craft");
    Check(h.TryFind(12.5, out var r12, out _, out _, out bool back12) && r12.ValidFromUt == 12 && !back12, "history: an earlier instant takes the record published for it");
    Check(h.TryFind(8, out var r10, out _, out _, out bool back8) && r10.ValidFromUt == 10 && back8, "history: before the oldest record it is read back along it");
    h.Add(Published(15), kerbin, double.NaN);
    Check(h.Count == 6 && h.TryFind(10.5, out var r15, out _, out _, out _) && r15.ValidFromUt == 10, "history: a republished record drops those starting at or after it");
    Check(Math.Abs(h.SpeedOver(10, 15) - Math.Sqrt(Accel * 15 * Accel * 15 + ground.Y * ground.Y)) < 0.01,
        "history: the kept records bound the craft's speed over the span they hold");
    var floored = Published(30);
    floored.HasAcceleration = false;
    var fh = new ModelRecordHistory();
    fh.Add(floored, kerbin, R + 30000);
    fh.Add(Published(40), kerbin, double.NaN);
    Check(fh.TryFind(0, out var fr, out var fp, out double ff, out bool fb) && fb
        && ModelRecordHistory.TryPlace(fr, fp, ff, fb, kerbin, 0, out var fpos) && fpos.Magnitude >= R + 30000 - 1e-6,
        "history: a read back before the oldest record is held no lower than its floor");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

/* A record's positions about its body as the rig's KnownPath reads them: none outside the record's window. */
sealed class HandoverPath : IPastPath
{
    public WorldModelView View;
    public int Scope;
    public KinematicsBody Body;

    public bool TryAt(double ut, out DVec3 position)
    {
        position = default;
        var fix = ModelLookup.Find(View, Scope, 7, ut, i => i == 1);
        return fix.State == CraftFixState.Positioned && View.Snapshot.TryStateAt(fix.Record, Body, ut, out position, out _);
    }
}

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

/* A path from a function of UT, null where it has no place. */
struct FnPath : IPastPath
{
    public Func<double, DVec3?> At;

    public bool TryAt(double ut, out DVec3 position)
    {
        var p = At(ut);
        position = p ?? default;
        return p.HasValue;
    }
}
