using System;
using System.Collections.Generic;
using System.Linq;
using Kerbcast;
using Kerbcast.Api;
using Kerbcast.Api.Testing;
using Kerbcast.DevProvider;
using CraftFixState = Kerbcast.CraftFixState;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

const double Delay = 10;
const string Site = "KSC";
const string Owner = "delayed-truth-test";

CraftSample Craft(uint pid, string name, string kind = "Ship", bool surface = false, bool manoeuvre = false) => new CraftSample
{
    PersistentId = pid, Name = name, Kind = kind, BodyIndex = 1, OnSurface = surface,
    Latitude = surface ? -0.1 : 0, Longitude = surface ? -74.5 : 0, Altitude = surface ? 70 : 0,
    Inclination = 1, Eccentricity = 0.01, SemiMajorAxis = 700000 + pid, MeanAnomalyAtEpoch = 0.5, Epoch = 0,
    MayManoeuvre = manoeuvre,
};

/* The timeline: who the game has at each second, and what happens to whom. */
IEnumerable<CraftSample> Alive(int t)
{
    if (t <= 300) yield return Craft(1, "Station");
    if (t <= 100)
    {
        var ferry = Craft(2, "Ferry", manoeuvre: true);
        ferry.AccelMps2 = 15;
        ferry.DeltaVMps = 800;
        yield return ferry;
    }
    if (t >= 200) yield return Craft(3, "Pod", "Probe");
    if (t <= 400) yield return Craft(4, "Lander", "Lander", surface: true);
    if (t <= 500) yield return Craft(5, "Probe Five", "Probe");
    if (t >= 520) yield return Craft(5, "Probe Five B", "Probe");
}
var events = new Dictionary<int, IdentityEvent>
{
    [100] = new IdentityEvent(IdentityChange.Merge, 100, 2, Guid.Empty, 1),
    [200] = new IdentityEvent(IdentityChange.Split, 200, 3, Guid.Empty, 1),
    [300] = new IdentityEvent(IdentityChange.Destroy, 300, 1, Guid.Empty),
    [400] = new IdentityEvent(IdentityChange.Recover, 400, 4, Guid.Empty),
    [500] = new IdentityEvent(IdentityChange.Destroy, 500, 5, Guid.Empty),
};

var truth = new DelayedTruth(_ => Delay);
var store = new WorldModelStore(null, null);
var real = store.Attach(Owner);
long frame = 0, seq = 0;
bool BodyKnown(int i) => i == 1;

using (var kc = RecordingKerbcast.Install())
{
    kc.Command.TakeLock(Owner, LockOptions.WorldModelRequired);
    var fake = kc.Command.AttachWorldModel(Owner);

    /* Publishes the stamp through both, and reads it back as Kerbcast's knowledge side does. */
    (PublishedSnapshot published, Func<uint, ModelFix> known) Stamp(double now, long epoch = 1)
    {
        seq++;
        var rb = real.Begin(seq, epoch, now);
        truth.Fill(rb, now, new[] { Site });
        var fb = fake.Begin(seq, epoch, now);
        truth.Fill(fb, now, new[] { Site });
        var r1 = real.Publish(rb, out var e1);
        var r2 = fake.Publish(fb, out var e2);
        Check(r1 == PublishResult.Accepted && r2 == PublishResult.Accepted, $"t={now}: the stamp is accepted by Kerbcast and the recorder {e1}{e2}");
        var view = store.Capture(++frame, now);
        int scope = ModelLookup.ScopeFor(view, Site, out _);
        return (kc.Command.Published.Last(), pid => ModelLookup.Find(view, scope, pid, now, BodyKnown));
    }

    var checks = new Dictionary<int, Action<PublishedSnapshot, Func<uint, ModelFix>>>
    {
        [5] = (p, k) =>
        {
            Check(p.Records.Count == 0 && k(1).State == CraftFixState.Unknown, "t=5: nothing is heard before the first news arrives");
            Check(p.SiteBindings[Site] == DelayedTruth.ScopeKey, "t=5: every site is bound to the provider's scope");
        },
        [15] = (p, k) =>
        {
            var r = p.Find(DelayedTruth.ScopeKey, 1);
            Check(r != null && r.State == Kerbcast.Api.CraftFixState.Positioned && r.Kinematics == KinematicsKind.Conic && r.Name == "Station",
                "t=15: a coasting craft is published as a conic, by name");
            Check(r != null && r.ValidFromUt == 0 && r.ValidUntilUt == 15 + 3600,
                "t=15: valid from where the unchanged conic began, until well past the newest news");
            var ferry = p.Find(DelayedTruth.ScopeKey, 2);
            Check(ferry != null && ferry.ValidUntilUt == 15 + 10, "t=15: a manoeuvring craft's conic holds only briefly past its news");
            Check(ferry != null && ferry.HasUncertainty && ferry.AccelMps2 == 15 && ferry.SpeedCapMps == 800 && ferry.UncertaintyRadiusM == 0,
                "t=15: a manoeuvring craft carries a free-fall uncertainty: its full acceleration until its delta-v is spent");
            Check(p.Find(DelayedTruth.ScopeKey, 1).HasUncertainty == false, "t=15: a coasting craft carries none");
            var lander = p.Find(DelayedTruth.ScopeKey, 4);
            Check(lander != null && lander.Kinematics == KinematicsKind.Surface && lander.Latitude == -0.1, "t=15: a landed craft is published on the surface");
            Check(k(1).State == CraftFixState.Positioned && k(4).State == CraftFixState.Positioned, "t=15: the knowledge side positions both");
        },
        [60] = (p, k) =>
        {
            /* Kerbcast reads a lens's subject at its past instant, now minus the lens's light time, which may exceed the model's delay. */
            var view = store.Capture(++frame, 60);
            int scope = ModelLookup.ScopeFor(view, Site, out _);
            Check(ModelLookup.Find(view, scope, 1, 60 - 25, BodyKnown).State == CraftFixState.Positioned,
                "t=60: read at a past instant 25 s back (more than the 10 s delay), a coasting craft is still positioned");
            Check(truth.RunCount <= 6, $"t=60: unchanged samples are one run per craft ({truth.RunCount} runs held)");
        },
        [105] = (p, k) =>
        {
            Check(p.Find(DelayedTruth.ScopeKey, 2) != null && !p.Aliases.Any(), "dock, t=105: before its news, both craft are still apart");
            Check(k(2).PersistentId == 2 && k(2).State == CraftFixState.Positioned, "dock, t=105: an aim at the ferry still finds the ferry");
        },
        [111] = (p, k) =>
        {
            Check(p.Find(DelayedTruth.ScopeKey, 2) == null && p.Aliases.Contains(new KeyValuePair<uint, uint>(2, 1)),
                "dock, t=111: once heard, the ferry is an alias of the station");
            Check(k(2).PersistentId == 1 && k(2).State == CraftFixState.Positioned, "dock, t=111: an aim at the ferry follows into the station");
        },
        [205] = (p, k) => Check(k(3).State == CraftFixState.Unknown && p.Find(DelayedTruth.ScopeKey, 3) == null,
            "undock, t=205: the new craft is unknown until heard"),
        [211] = (p, k) => Check(k(3).State == CraftFixState.Positioned && k(3).Name == "Pod", "undock, t=211: heard, it is positioned by name"),
        [305] = (p, k) => Check(k(1).State == CraftFixState.Positioned, "destroy, t=305: the station is still where it was last heard"),
        [311] = (p, k) =>
        {
            var r = p.Find(DelayedTruth.ScopeKey, 1);
            Check(r != null && r.State == Kerbcast.Api.CraftFixState.Refused && r.RefusalCode == DelayedTruth.Destroyed && r.Name == "Station",
                "destroy, t=311: heard, it is refused as destroyed, still named");
            Check(k(1).State == CraftFixState.Refused && k(1).RefusalCode == DelayedTruth.Destroyed, "destroy, t=311: the knowledge side refuses it");
            Check(k(2).PersistentId == 1 && k(2).State == CraftFixState.Refused, "destroy, t=311: the docked ferry's id follows into the lost station");
        },
        [405] = (p, k) => Check(k(4).State == CraftFixState.Positioned, "recover, t=405: still on the surface until heard"),
        [411] = (p, k) => Check(k(4).State == CraftFixState.Refused && k(4).RefusalCode == DelayedTruth.Recovered && k(4).Name == "Lander",
            "recover, t=411: heard, refused as recovered, still named"),
        [505] = (p, k) => Check(k(5).State == CraftFixState.Positioned && k(5).Name == "Probe Five", "reused id, t=505: the first craft"),
        [515] = (p, k) => Check(k(5).State == CraftFixState.Refused && k(5).Name == "Probe Five", "reused id, t=515: the first craft heard destroyed"),
        [529] = (p, k) => Check(k(5).State == CraftFixState.Refused && k(5).Name == "Probe Five",
            "reused id, t=529: the new craft with the same id is not heard yet"),
        [531] = (p, k) =>
        {
            Check(k(5).State == CraftFixState.Positioned && k(5).Name == "Probe Five B", "reused id, t=531: the new craft replaces the old once heard");
            Check(p.Records.Count(r => r.PersistentId == 5) == 1, "reused id, t=531: one record for the id, never two");
        },
    };

    for (int t = 0; t <= 600; t++)
    {
        truth.Sample(t, Alive(t));
        if (events.TryGetValue(t, out var e)) truth.Identity(e);
        if (checks.TryGetValue(t, out var check))
        {
            var (p, k) = Stamp(t);
            check(p, k);
        }
        truth.Prune(t);
    }

    /* A load back to 450, seen only as UT going back: the second probe and the first one's loss never happened. */
    bool rewound = truth.Sample(451, new[] { Craft(5, "Probe Five"), Craft(3, "Pod", "Probe") });
    Check(rewound, "load: a sample earlier than the last is noticed as the game going back");
    for (int t = 452; t <= 470; t++)
        truth.Sample(t, new[] { Craft(5, "Probe Five"), Craft(3, "Pod", "Probe") });
    var (stale, staleKnown) = Stamp(470);
    Check(staleKnown(5).State == CraftFixState.Refused && staleKnown(5).RefusalCode == CommandCameraMath.RefusalStale,
        "load: UT went back, so Kerbcast reads the old epoch's snapshot stale");
    var (after, known) = Stamp(470, epoch: 2);
    Check(known(5).State == CraftFixState.Positioned && known(5).Name == "Probe Five", "load: after a rewind the first probe is back, as heard before");
    Check(known(4).State == CraftFixState.Refused, "load: what was heard before the load stays heard");
}

/* A load back before a destroy in the abandoned timeline: no phantom destroy, and the craft is known at once. */
{
    var t2 = new DelayedTruth(_ => 10);
    for (int t = 0; t <= 1000; t++)
    {
        var craft = Craft(7, "Lost Then Found");
        craft.MeanAnomalyAtEpoch = t / 5 * 0.01;
        t2.Sample(t, t <= 950 ? new[] { craft } : new CraftSample[0]);
        if (t == 950) t2.Identity(new IdentityEvent(IdentityChange.Destroy, 950, 7, Guid.Empty));
        t2.Prune(t);
    }
    var again = Craft(7, "Lost Then Found");
    Check(t2.Sample(900, new[] { again }), "rewind: back to 900 from 1000");
    var b = new WorldModelBuilder(null, 1, 2, 905);
    t2.Fill(b, 905, new string[0]);
    var rec = b.Records.FirstOrDefault(r => r.PersistentId == 7);
    Check(rec != null && rec.State == Kerbcast.Api.CraftFixState.Positioned, "rewind: right after a load the craft is still known from before the load point, after pruning");
    for (int t = 901; t <= 965; t++) t2.Sample(t, new[] { again });
    var b2 = new WorldModelBuilder(null, 2, 2, 965);
    t2.Fill(b2, 965, new string[0]);
    var rec2 = b2.Records.FirstOrDefault(r => r.PersistentId == 7);
    Check(rec2 != null && rec2.State == Kerbcast.Api.CraftFixState.Positioned, "rewind: the destroy at 950 belonged to the abandoned timeline and is never heard");
}

/* A conic that changes every second (a burn) keeps one sample per ThinSeconds, never folding later news into an earlier one. */
{
    var burn = new DelayedTruth(_ => 10);
    for (int t = 0; t < 100; t++)
    {
        var c = Craft(8, "Burning", manoeuvre: true);
        c.SemiMajorAxis = 700000 + t * 100;
        burn.Sample(t, new[] { c });
    }
    Check(burn.RunCount == 50, $"thin: a hundred changing samples keep fifty runs ({burn.RunCount})");
    var b = new WorldModelBuilder(null, 1, 1, 30.5);
    burn.Fill(b, 30.5, new string[0]);
    var rec = b.Records.First(r => r.PersistentId == 8);
    Check(rec.SemiMajorAxis == 700000 + 20 * 100 && rec.ValidFromUt == 20, "thin: at 30.5 the newest heard state is the sample from 20, not a later one");
}

/* A delay that depends on the craft: the far one is heard later. */
{
    var far = new DelayedTruth(c => c.DistanceM / 1000.0);
    var near = Craft(10, "Near");
    near.DistanceM = 2000;
    var distant = Craft(11, "Distant");
    distant.DistanceM = 60000;
    far.Sample(0, new[] { near, distant });
    var b = new WorldModelBuilder(null, 1, 1, 10);
    far.Fill(b, 10, new string[0]);
    Check(b.Records.Any(r => r.PersistentId == 10) && !b.Records.Any(r => r.PersistentId == 11),
        "delay: a craft 2 s away is heard at t=10, one 60 s away is not");
}

{
    var aims = new DefaultAims();
    var due = new List<(uint Feed, uint Craft)>();
    var feeds = new List<CommandFeed> { new CommandFeed { FlightId = 1 }, new CommandFeed { FlightId = 2 } };
    uint Seven(CommandFeed f) => 7;
    aims.Due(feeds, f => 0, 0, due);
    Check(due.Count == 0, "default aims: no default craft aims nothing");
    aims.Due(feeds, Seven, 0, due);
    Check(due.SequenceEqual(new[] { (1u, 7u), (2u, 7u) }), "default aims: every feed is aimed at its default craft");
    feeds[0].SubjectPersistentId = 7;
    aims.Due(feeds, Seven, 1, due);
    Check(due.Count == 0, "default aims: a feed watching its craft is left alone, one just tried waits");
    aims.Due(feeds, Seven, 1 + DefaultAims.RetrySeconds, due);
    Check(due.SequenceEqual(new[] { (2u, 7u) }), "default aims: a feed whose aim did not take (or was dropped) is aimed again after RetrySeconds");
    feeds[0].SubjectPersistentId = 0;
    aims.Due(feeds, Seven, 2 + DefaultAims.RetrySeconds, due);
    Check(due.SequenceEqual(new[] { (1u, 7u) }), "default aims: an aim Kerbcast dropped is recovered");
    aims.Due(feeds, Seven, 3 + DefaultAims.RetrySeconds, due);
    Check(due.Count == 0, "default aims: never more than once per RetrySeconds a feed");
    aims.Due(feeds, f => f.FlightId == 1 ? 8u : 7u, 4 + DefaultAims.RetrySeconds, due);
    Check(due.SequenceEqual(new[] { (1u, 8u) }), "default aims: a new default craft (the active craft switched) is aimed at once");

    var craft = new List<(uint, string, double)> { (1, "Old", 10), (2, " Pad Craft ", 20), (3, "Newest", 30), (4, "pad craft", 5) };
    Check(DefaultAims.Latest(craft, "") == 3, "default craft: the latest launch with no name");
    Check(DefaultAims.Latest(craft, "PAD CRAFT") == 2, "default craft: the latest launch of the named craft, trimmed, any case");
    Check(DefaultAims.Latest(craft, "Missing") == 3, "default craft: no match falls back to the latest launch");
    Check(DefaultAims.Latest(new List<(uint, string, double)>(), "") == 0, "default craft: none without craft");
}

/* The provider stops once its lock goes, so it never aims over a client while the lock is free. */
using (var kc = RecordingKerbcast.Install())
{
    const string Provider = "lock-tenure-test";
    var tenure = new LockTenure(Provider);
    var heard = new List<bool>();
    Action<LockLostEvent> onLost = e => heard.Add(tenure.Lost(e));
    KerbcastApi.LockLost += onLost;
    try
    {
        Check(kc.Command.TakeLock(Provider, LockOptions.WorldModelRequired) == LockResult.Taken, "tenure: the lock is taken");
        tenure.Took();
        var publisher = kc.Command.AttachWorldModel(Provider);
        KerbcastApi.RaiseLockLost(new LockLostEvent("another-mod", LockLossReason.ReleasedByPlayer), null);
        Check(tenure.Holding && heard.SequenceEqual(new[] { false }), "tenure: another owner's loss changes nothing");
        kc.Command.ReleaseByPlayer();
        Check(!tenure.Holding && heard.SequenceEqual(new[] { false, true }), "tenure: the player's release ends it");
        var b = publisher.Begin(1, 1, 10);
        var refused = publisher.Publish(b, out _);
        Check(refused == PublishResult.NotLockHolder && !tenure.Refused(refused), "tenure: a refusal after the loss is not a second stop");
        Check(kc.Command.SetAim(1, Aim.Vessel(7), Provider) != AimResult.NotLockHolder,
            "tenure: with the lock free any caller may aim, which is why the provider must stop");

        tenure.Took();
        Check(tenure.Refused(PublishResult.NotLockHolder) && !tenure.Holding, "tenure: a NotLockHolder refusal ends it when no LockLost was heard");
        tenure.Took();
        Check(!tenure.Refused(PublishResult.OutOfOrder) && tenure.Holding, "tenure: other refusals keep it");

        heard.Clear();
        Check(kc.Command.TakeLock(Provider, LockOptions.WorldModelRequired) == LockResult.Taken, "tenure: taken again");
        kc.Command.ChangeScene();
        Check(!tenure.Holding && heard.SequenceEqual(new[] { true }), "tenure: a scene change ends it");
    }
    finally
    {
        KerbcastApi.LockLost -= onLost;
    }
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
