using System;
using System.Collections.Generic;
using Kerbcast.Api;

namespace Kerbcast.DevProvider
{
    /* One craft as the game had it at one UT, in plain values. */
    public struct CraftSample
    {
        public uint PersistentId;
        public string Name;
        public string Kind;
        /* flightGlobalsIndex. */
        public int BodyIndex;
        /* Sitting on its body: placed by latitude, longitude and altitude, carried by the body's spin. */
        public bool OnSurface;
        public double Latitude, Longitude, Altitude;
        /* KSP Orbit elements (degrees, metres, mean anomaly in radians, epoch in seconds). */
        public double Inclination, Eccentricity, SemiMajorAxis, Lan, ArgumentOfPeriapsis, MeanAnomalyAtEpoch, Epoch;
        /* Under its own control and able to thrust, so its conic holds only briefly. */
        public bool MayManoeuvre;
        /* For a craft that may manoeuvre: its largest acceleration (m/s²) and the delta-v it has left (m/s). */
        public double AccelMps2, DeltaVMps;
        /* How far its news travels to reach the model, in metres, for the caller's delay. */
        public double DistanceM;
    }

    /*
     * A world model that knows what the game knew a while ago: each sample of
     * a craft, and each dock, destroy or recovery, is heard only once its news
     * has arrived (`delayOf` says how long that takes, asked once per sample).
     * It publishes through Kerbcast.API's public builder, as any other
     * provider would.
     *
     * A craft is absent until its first sample is heard, so a craft split off
     * by an undock appears only then. A dock is heard as an alias from the
     * absorbed craft to the survivor; a destroyed or recovered craft goes on
     * listing by name, refused. A persistentId KSP reuses for a new craft
     * starts a new life, which replaces the old once it is heard.
     *
     * Samples that carry the same state are kept as one run, from the first of
     * them to the last: a coasting conic is exact back to where its run began,
     * so that is where its record is valid from. A state that changes faster
     * than ThinSeconds keeps only one sample per ThinSeconds; the others are
     * dropped, never folded in, so nothing is heard before its news arrives.
     * A craft that may manoeuvre carries an uncertainty that grows as a burn
     * at its full acceleration would, until its delta-v is spent.
     *
     * A sample with a UT earlier than the last means the game went back (a
     * quickload or revert): everything after it is forgotten first. Pruned
     * runs leave one floor a minute for an hour, so a load back into that
     * time still knows each craft as it was then.
     *
     * One scope serves every site, so a lens away from home reads home's
     * knowledge without the ageing a real command path would add.
     *
     * No game types: the caller samples the game and hands in plain values.
     */
    public sealed class DelayedTruth
    {
        /* The one scope every site is bound to. */
        public const string ScopeKey = "delayed-truth";
        public const string Destroyed = "destroyed";
        public const string Recovered = "recovered";

        private readonly Func<CraftSample, double> _delayOf;
        private readonly Dictionary<uint, List<Life>> _lives = new Dictionary<uint, List<Life>>();

        /* How long a coasting craft's conic is offered, and a manoeuvring craft's, past the news arriving. */
        public double CoastWindowS { get; set; } = 3600;
        public double ManoeuvreWindowS { get; set; } = 10;
        /* A changing state keeps at most one sample this often. */
        public double ThinSeconds { get; set; } = 2;
        /* Pruned runs kept for loads: one this often, back this far. */
        public double FloorSpacingS { get; set; } = 60;
        public double FloorDepthS { get; set; } = 3600;

        /* The UT of the latest sample; NaN before any. */
        public double LatestUt { get; private set; } = double.NaN;

        /* Runs held over every craft, for tests and the log. */
        public int RunCount
        {
            get
            {
                int n = 0;
                foreach (var lives in _lives.Values)
                    foreach (var l in lives) n += l.Runs.Count;
                return n;
            }
        }

        private sealed class Run
        {
            public double StartUt, StartDelay;
            public double LastUt, LastDelay;
            public CraftSample Craft;
            public double StartArrives => StartUt + StartDelay;
            public double LastArrives => LastUt + LastDelay;
            public Run Copy() => (Run)MemberwiseClone();
        }

        private sealed class Life
        {
            public readonly List<Run> Runs = new List<Run>();
            /* Runs Prune dropped, thinned, so a load back past what is held still knows the craft. */
            public readonly List<Run> Floors = new List<Run>();
            public double EndUt = double.PositiveInfinity;
            public string EndCode;
            public uint MergedInto;
            public bool Ended => !double.IsPositiveInfinity(EndUt);
            public double StartUt => Floors.Count > 0 ? Floors[0].StartUt : Runs.Count > 0 ? Runs[0].StartUt : double.PositiveInfinity;
        }

        public DelayedTruth(Func<CraftSample, double> delayOf)
        {
            _delayOf = delayOf ?? (_ => 0);
        }

        /* The delay this model was made with, for a fresh one. */
        public Func<CraftSample, double> DelayFunction => _delayOf;

        /* Every craft the game has at `ut`. True when `ut` went back and the timeline after it was forgotten first. */
        public bool Sample(double ut, IEnumerable<CraftSample> craft)
        {
            bool rewound = !double.IsNaN(LatestUt) && ut < LatestUt;
            if (rewound) Rewind(ut);
            LatestUt = ut;
            foreach (var c in craft)
            {
                if (c.PersistentId == 0) continue;
                var life = OpenLife(c.PersistentId, ut);
                double delay = Math.Max(0, _delayOf(c));
                var last = life.Runs.Count > 0 ? life.Runs[life.Runs.Count - 1] : null;
                if (last != null && SameState(last.Craft, c))
                {
                    last.LastUt = ut;
                    last.LastDelay = delay;
                    continue;
                }
                if (last != null && SameKind(last.Craft, c) && ut - last.StartUt < ThinSeconds) continue;
                life.Runs.Add(new Run { StartUt = ut, StartDelay = delay, LastUt = ut, LastDelay = delay, Craft = c });
            }
            return rewound;
        }

        /* A merge, renumber, destroy or recovery the game reported; a split shows as the new craft's samples. */
        public void Identity(IdentityEvent e)
        {
            if (e == null || e.PersistentId == 0) return;
            var life = Current(e.PersistentId);
            if (life == null || life.Ended) return;
            switch (e.Change)
            {
                case IdentityChange.Merge:
                case IdentityChange.Renumber:
                    if (e.OtherPersistentId == 0 || e.OtherPersistentId == e.PersistentId) return;
                    life.EndUt = e.Ut;
                    life.MergedInto = e.OtherPersistentId;
                    break;
                case IdentityChange.Destroy:
                    life.EndUt = e.Ut;
                    life.EndCode = Destroyed;
                    break;
                case IdentityChange.Recover:
                    life.EndUt = e.Ut;
                    life.EndCode = Recovered;
                    break;
            }
        }

        /* The game went back to `ut`: whatever happened after it never did. */
        public void Rewind(double ut)
        {
            var empty = new List<uint>();
            foreach (var kv in _lives)
            {
                var lives = kv.Value;
                lives.RemoveAll(l => l.StartUt > ut);
                foreach (var l in lives)
                {
                    l.Runs.RemoveAll(r => r.StartUt > ut);
                    l.Floors.RemoveAll(r => r.StartUt > ut);
                    if (l.Runs.Count == 0)
                        foreach (var f in l.Floors) l.Runs.Add(f.Copy());
                    foreach (var r in l.Runs)
                        if (r.LastUt > ut) r.LastUt = ut;
                    if (l.EndUt > ut)
                    {
                        l.EndUt = double.PositiveInfinity;
                        l.EndCode = null;
                        l.MergedInto = 0;
                    }
                }
                lives.RemoveAll(l => l.Runs.Count == 0);
                if (lives.Count == 0) empty.Add(kv.Key);
            }
            foreach (var pid in empty) _lives.Remove(pid);
            LatestUt = ut;
        }

        /* Drops runs no publish at `now` or later can hear as the newest, keeping a few as floors for loads. */
        public void Prune(double now)
        {
            foreach (var lives in _lives.Values)
                foreach (var l in lives)
                {
                    int newestHeard = NewestHeard(l, now);
                    if (newestHeard <= 0) continue;
                    for (int i = 0; i < newestHeard; i++)
                    {
                        var run = l.Runs[i];
                        if (l.Floors.Count == 0 || run.StartUt - l.Floors[l.Floors.Count - 1].StartUt >= FloorSpacingS) l.Floors.Add(run);
                    }
                    l.Floors.RemoveAll(f => f.StartUt < now - FloorDepthS);
                    l.Runs.RemoveRange(0, newestHeard);
                }
        }

        /*
         * What the model knows at `now`: every craft heard of, in one scope
         * bound to every site in `siteKeys`, and the aliases of every dock heard.
         */
        public void Fill(WorldModelBuilder b, double now, IEnumerable<string> siteKeys)
        {
            b.Scope(ScopeKey);
            var aliases = new Dictionary<uint, uint>();
            var pids = new List<uint>(_lives.Keys);
            pids.Sort();
            var recorded = new HashSet<uint>();
            foreach (var pid in pids)
            {
                var lives = _lives[pid];
                for (int i = lives.Count - 1; i >= 0; i--)
                {
                    var life = lives[i];
                    int heard = NewestHeard(life, now);
                    if (heard < 0) continue;
                    var run = life.Runs[heard];
                    /* The end's news travels as far as the craft's last word did. */
                    var last = life.Runs[life.Runs.Count - 1];
                    if (life.Ended && life.EndUt + last.LastDelay <= now)
                    {
                        if (life.MergedInto != 0) aliases[pid] = life.MergedInto;
                        else
                        {
                            b.Refused(pid, life.EndCode);
                            Describe(b, run.Craft);
                            recorded.Add(pid);
                        }
                        break;
                    }
                    Place(b, run, now);
                    recorded.Add(pid);
                    break;
                }
            }
            /* A craft with a record of its own is itself, whatever an older life of its id merged into. */
            foreach (var kv in aliases)
                if (!recorded.Contains(kv.Key) && !Loops(aliases, kv.Key)) b.Alias(kv.Key, kv.Value);
            var bound = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in siteKeys)
                if (!string.IsNullOrEmpty(key) && bound.Add(key)) b.BindSite(key, ScopeKey);
        }

        /* Valid from where its unchanged run began, until its newest heard confirmation plus the window. */
        private void Place(WorldModelBuilder b, Run run, double now)
        {
            var s = run.Craft;
            double heardUntil = Math.Min(run.LastArrives, now);
            double until = heardUntil + (s.MayManoeuvre ? ManoeuvreWindowS : CoastWindowS);
            if (s.OnSurface)
                b.Surface(s.PersistentId, s.BodyIndex, s.Latitude, s.Longitude, s.Altitude, run.StartUt, until);
            else
                b.Conic(s.PersistentId, s.BodyIndex, s.Inclination, s.Eccentricity, s.SemiMajorAxis, s.Lan,
                    s.ArgumentOfPeriapsis, s.MeanAnomalyAtEpoch, s.Epoch, run.StartUt, until);
            Describe(b, s);
            if (s.MayManoeuvre && s.AccelMps2 > 0) b.Uncertainty(0, 0, s.AccelMps2, Math.Max(0, s.DeltaVMps));
        }

        private static void Describe(WorldModelBuilder b, CraftSample s)
        {
            if (!string.IsNullOrEmpty(s.Name)) b.Name(s.Name);
            if (!string.IsNullOrEmpty(s.Kind)) b.Kind(s.Kind);
        }

        /* The index of the newest run whose news has arrived by `now`, -1 for none; runs arrive in order. */
        private static int NewestHeard(Life life, double now)
        {
            int lo = 0, hi = life.Runs.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (life.Runs[mid].StartArrives <= now)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return found;
        }

        /* The same craft in the same place by the same rule: a later sample only confirms it. */
        private static bool SameState(CraftSample a, CraftSample b) =>
            SameKind(a, b) && a.MayManoeuvre == b.MayManoeuvre
            && (a.OnSurface
                ? a.Latitude == b.Latitude && a.Longitude == b.Longitude && a.Altitude == b.Altitude
                : a.Inclination == b.Inclination && a.Eccentricity == b.Eccentricity && a.SemiMajorAxis == b.SemiMajorAxis
                  && a.Lan == b.Lan && a.ArgumentOfPeriapsis == b.ArgumentOfPeriapsis
                  && a.MeanAnomalyAtEpoch == b.MeanAnomalyAtEpoch && a.Epoch == b.Epoch);

        /* Close enough to thin: same body, same rule (surface or conic), same name and kind. */
        private static bool SameKind(CraftSample a, CraftSample b) =>
            a.BodyIndex == b.BodyIndex && a.OnSurface == b.OnSurface
            && string.Equals(a.Name, b.Name, StringComparison.Ordinal) && string.Equals(a.Kind, b.Kind, StringComparison.Ordinal);

        private static bool Loops(Dictionary<uint, uint> aliases, uint from)
        {
            var seen = new HashSet<uint> { from };
            uint at = from;
            while (aliases.TryGetValue(at, out uint next))
            {
                if (!seen.Add(next)) return true;
                at = next;
            }
            return false;
        }

        private Life Current(uint pid) =>
            _lives.TryGetValue(pid, out var lives) && lives.Count > 0 ? lives[lives.Count - 1] : null;

        /* The craft's open life, or a new one when the last ended: a reused id is a new craft. */
        private Life OpenLife(uint pid, double ut)
        {
            if (!_lives.TryGetValue(pid, out var lives))
            {
                lives = new List<Life>();
                _lives[pid] = lives;
            }
            var last = lives.Count > 0 ? lives[lives.Count - 1] : null;
            if (last != null && (!last.Ended || ut < last.EndUt)) return last;
            var life = new Life();
            lives.Add(life);
            return life;
        }
    }

    /*
     * The provider's stand-in for Kerbcast's default aim. Under a lock a
     * default aim names no craft, so the holder aims every feed itself; this
     * one aims each at the craft Kerbcast would watch by default, as truth
     * has it. A feed already watching that craft is left alone; any other is
     * aimed, again every RetrySeconds while it does not take (the craft not
     * yet heard, or the aim dropped by Kerbcast), and at once when the
     * default craft changes.
     */
    public sealed class DefaultAims
    {
        public const float RetrySeconds = 5f;

        private readonly Dictionary<uint, (uint Craft, float At)> _tried = new Dictionary<uint, (uint, float)>();

        /* The feeds to aim now, each with its default craft (`craftOf`, 0 for none), into `into`; `now` is real time. */
        public void Due(IReadOnlyList<CommandFeed> feeds, Func<CommandFeed, uint> craftOf, float now, List<(uint Feed, uint Craft)> into)
        {
            into.Clear();
            if (feeds == null) return;
            foreach (var f in feeds)
            {
                uint craft = craftOf(f);
                if (craft == 0 || f.SubjectPersistentId == craft) continue;
                if (_tried.TryGetValue(f.FlightId, out var t) && t.Craft == craft && now - t.At < RetrySeconds) continue;
                _tried[f.FlightId] = (craft, now);
                into.Add((f.FlightId, craft));
            }
        }

        /*
         * Kerbcast's Space Center default among (persistentId, name, launch
         * time) candidates: the latest launched craft named `wanted`
         * (trimmed, any case), or with no name or no match the latest
         * launched of all; 0 for none.
         */
        public static uint Latest(IEnumerable<(uint Pid, string Name, double Launch)> craft, string wanted)
        {
            wanted = (wanted ?? "").Trim();
            uint best = 0, bestNamed = 0;
            double launch = double.MinValue, launchNamed = double.MinValue;
            foreach (var c in craft)
            {
                if (c.Launch > launch)
                {
                    launch = c.Launch;
                    best = c.Pid;
                }
                if (wanted.Length > 0 && string.Equals((c.Name ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase) && c.Launch > launchNamed)
                {
                    launchNamed = c.Launch;
                    bestNamed = c.Pid;
                }
            }
            return bestNamed != 0 ? bestNamed : best;
        }
    }
}
