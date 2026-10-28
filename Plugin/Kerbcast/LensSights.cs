using System;
using System.Collections.Generic;

namespace Kerbcast
{
    /* One craft as a lens's arriving light shows it in one frame. */
    internal struct LensSight
    {
        public PastStatus Status;
        /* The craft id whose light this is: the one asked about, or the craft it was part of then. */
        public uint Pid;
        public double Ut;
        public HistoryState State;
        /* The life of the craft id the light left. */
        public HistoryTrack Track;
        /* The craft's parts at Ut, and their size in metres, 0 when unknown. */
        public Composition Parts;
        public double SizeM;
        /* The body whose inertial frame the light time was solved in, and the
           craft's position about it at Ut. */
        public int Frame;
        public DVec3 About;

        public bool Found => Status == PastStatus.Found;
    }

    /*
     * The past-instant solves of one lens craft, shared by every camera on it:
     * each craft is solved once a frame from one point on the lens craft, with
     * a spread wide enough to cover every camera that asked this frame or the
     * one before, so the light has arrived at each of them.
     *
     * Each craft keeps its instant hold here too, so the instant drawn on any
     * camera of the craft never steps back.
     */
    internal sealed class LensSights
    {
        private const int MinKeep = 64;
        private const double StaleUt = 60;

        private sealed class Seen
        {
            public double T = double.NaN;
            public DVec3 Lens;
            public double Spread;
            public double C;
            public int LensBody;
            public long Generation;
            public LensSight Sight;
            public InstantHold Hold;
        }

        private readonly Dictionary<uint, Seen> _seen = new Dictionary<uint, Seen>();
        private readonly List<uint> _scratch = new List<uint>();
        /* Grows when every craft kept was asked about this frame, so a frame never evicts its own sights. */
        private int _keep = MinKeep;
        private double _coverT = double.NaN;
        private double _spreadNow, _spreadBefore;

        /* Metres from the solve point within which every camera that asked this frame or the one before sits. */
        public double SpreadM => Math.Max(_spreadNow, _spreadBefore);
        /* The last render UT any camera asked at. */
        public double LastT { get; private set; } = double.NaN;
        /* Solves run, against sights answered from the frame's earlier solve. */
        public long Solves { get; private set; }
        public long Shared { get; private set; }

        /* Takes in a camera `distanceM` from the solve point asking at render UT `t`. */
        public void Cover(double t, double distanceM)
        {
            if (t != _coverT)
            {
                _spreadBefore = _spreadNow;
                _spreadNow = 0;
                _coverT = t;
            }
            if (distanceM > _spreadNow) _spreadNow = distanceM;
        }

        /*
         * Craft `pid` as light arriving at t shows it to a lens `lensOffset`
         * from the centre of body `lensBody`, in the ring's frame, solved in
         * `tree`'s bodies. Answered from the frame's earlier solve when the
         * lens, the spread and the ring are unchanged.
         */
        public LensSight See<TB>(LightHistory ring, uint pid, double t, int lensBody, DVec3 lensOffset, double c, ref TB tree)
            where TB : IBodyTree
        {
            LastT = t;
            if (!_seen.TryGetValue(pid, out var seen))
            {
                if (_seen.Count >= _keep) MakeRoom(t);
                _seen[pid] = seen = new Seen();
            }
            if (seen.T == t && seen.Generation == ring.Generation && seen.Spread == SpreadM && seen.C == c && seen.LensBody == lensBody
                && seen.Lens.X == lensOffset.X && seen.Lens.Y == lensOffset.Y && seen.Lens.Z == lensOffset.Z)
            {
                Shared++;
                return seen.Sight;
            }
            Solves++;
            seen.T = t;
            seen.Lens = lensOffset;
            seen.Spread = SpreadM;
            seen.C = c;
            seen.LensBody = lensBody;
            seen.Generation = ring.Generation;
            double notBefore = seen.Hold.NotBefore(t, ring.Generation, c, lensBody);
            var sight = PastInstant.Craft(ring, pid, t, lensBody, lensOffset, c, ref tree, notBefore, seen.Hold.Frame, SpreadM);
            seen.Sight = new LensSight { Status = sight.Status, Pid = pid, Ut = sight.Ut, State = sight.State, Track = sight.Track, Frame = sight.Frame, About = sight.About };
            if (!sight.Found)
            {
                seen.Hold.Reset();
                return seen.Sight;
            }
            seen.Hold.Shown(t, sight.Ut, ring.Generation, c, lensBody, sight.Frame);
            var parts = ring.CompositionAt(sight.Track, sight.Ut);
            seen.Sight.Parts = parts;
            if (parts != null) seen.Sight.SizeM = parts.SizeM;
            return seen.Sight;
        }

        /*
         * The craft as its arriving light shows it, in the identity it had
         * then. A craft that came off another (undock, decouple, EVA) is drawn
         * as part of the craft it came off until the light of its split
         * arrives; that craft's parts at the instant include it.
         */
        public LensSight SeeAsItWas<TB>(LightHistory ring, uint pid, double t, int lensBody, DVec3 lensOffset, double c, ref TB tree)
            where TB : IBodyTree
        {
            var sight = See(ring, pid, t, lensBody, lensOffset, c, ref tree);
            for (int i = 0; i < IdentityReplay.MaxDepth && sight.Status == PastStatus.Unseen; i++)
            {
                uint parent = IdentityReplay.SplitParent(ring, sight.Pid, t);
                if (parent == 0) break;
                sight = See(ring, parent, t, lensBody, lensOffset, c, ref tree);
            }
            return sight;
        }

        /*
         * A feed's subject, now named `subject` and last drawn as `drawn`.
         * When the subject became another craft at the present by merging (a
         * dock or a grab the feed followed, or several in a row), each craft
         * of the chain is drawn while its light is arriving, and the next one
         * only once that light is in: the subject never changes at the
         * present, and no link is skipped. Any other change of subject is a
         * new choice and is drawn at once.
         */
        public LensSight SeeSubject<TB>(LightHistory ring, uint subject, ref uint drawn, double t, int lensBody, DVec3 lensOffset, double c,
            ref TB tree) where TB : IBodyTree
        {
            if (drawn != 0 && drawn != subject && IdentityReplay.MergedInto(ring, drawn, subject))
            {
                for (int i = 0; i < IdentityReplay.MaxDepth && drawn != subject; i++)
                {
                    var life = IdentityReplay.MergedLife(ring, drawn);
                    if (life == null) break;
                    var was = SeeAsItWas(ring, drawn, t, lensBody, lensOffset, c, ref tree);
                    /* Held only while the light is the docked life's own, or older than it; another craft under the id ends the hold. */
                    bool otherLife = was.Found && was.Pid == drawn && (was.Track != life || was.Ut > life.EndUt);
                    if (was.Status != PastStatus.Gone && !otherLife) return was;
                    if (otherLife) break;
                    drawn = IdentityReplay.NextLink(ring, drawn);
                }
            }
            drawn = subject;
            return SeeAsItWas(ring, subject, t, lensBody, lensOffset, c, ref tree);
        }

        /* Drops craft not asked about for a while, or from a later UT before a
           rewind; failing that the one asked about longest ago, keeping every
           other craft's hold. */
        private void MakeRoom(double t)
        {
            _scratch.Clear();
            foreach (var kv in _seen)
                if (!(kv.Value.T >= t - StaleUt && kv.Value.T <= t)) _scratch.Add(kv.Key);
            foreach (var k in _scratch) _seen.Remove(k);
            if (_seen.Count < _keep) return;
            uint oldest = 0;
            double oldestT = t;
            foreach (var kv in _seen)
                if (kv.Value.T < oldestT) { oldestT = kv.Value.T; oldest = kv.Key; }
            if (oldestT < t) _seen.Remove(oldest);
            else _keep *= 2;
        }

        /* Whether nobody has asked for a while, or the clock went back past the last ask. */
        public bool Stale(double t) => !(LastT >= t - StaleUt && LastT <= t);
    }

    /* How craft ids follow one another through docks and splits, from the ring's identity events. */
    internal static class IdentityReplay
    {
        public const int MaxDepth = 8;

        /* Whether craft `from` ended merging into `into`, directly or through a chain of merges. */
        public static bool MergedInto(LightHistory ring, uint from, uint into)
        {
            uint at = from;
            for (int i = 0; i < MaxDepth; i++)
            {
                at = NextLink(ring, at);
                if (at == 0) return false;
                if (at == into) return true;
            }
            return false;
        }

        /* The latest life of `pid` that ended merging into another craft, past any later life under the same id; null if none. */
        public static HistoryTrack MergedLife(LightHistory ring, uint pid)
        {
            var t = ring.Track(LightHistory.CraftKey(pid));
            for (int i = 0; i < MaxDepth && t != null; i++, t = ring.EarlierLife(t))
                if (t.Ended && t.EndKind == IdentityKind.MergedInto) return t;
            return null;
        }

        /* The craft `pid` merged into, from MergedLife; 0 if none. */
        public static uint NextLink(LightHistory ring, uint pid)
        {
            var t = MergedLife(ring, pid);
            if (t == null) return 0;
            var events = t.Identity;
            for (int k = events.Count - 1; k >= 0; k--)
                if (events[k].Kind == IdentityKind.MergedInto) return events[k].Other;
            return 0;
        }

        /* The craft whose life of `pid` at `ut` began by coming off it, while
           that split is still in the ring; 0 otherwise, so a craft whose
           history is merely older than the ring is never drawn as its parent. */
        public static uint SplitParent(LightHistory ring, uint pid, double ut)
        {
            var t = ring.TrackFor(pid, ut);
            if (t == null || t.ParentPid == 0 || t.ParentPid == pid || !(t.StartUt > ring.FloorUt)) return 0;
            var events = t.Identity;
            for (int k = 0; k < events.Count; k++)
                if (events[k].Kind == IdentityKind.SplitFrom && events[k].Other == t.ParentPid) return t.ParentPid;
            return 0;
        }
    }

    /* Every lens craft's sights, by lens key: a craft's persistentId, or a key of its own for a lens on no craft. */
    internal sealed class LensSightsTable
    {
        private readonly Dictionary<long, LensSights> _lenses = new Dictionary<long, LensSights>();
        private readonly List<long> _scratch = new List<long>();

        public int Count => _lenses.Count;

        public LensSights For(long key, double t)
        {
            if (_lenses.TryGetValue(key, out var lens)) return lens;
            Prune(t);
            _lenses[key] = lens = new LensSights();
            return lens;
        }

        public void Prune(double t)
        {
            _scratch.Clear();
            foreach (var kv in _lenses)
                if (kv.Value.Stale(t)) _scratch.Add(kv.Key);
            foreach (var k in _scratch) _lenses.Remove(k);
        }

        public void Clear() => _lenses.Clear();

        /* The lens craft took another id (it docked into another craft): its sights and holds go with it, unless that craft has its own. */
        public void Rekey(long from, long to)
        {
            if (from == to || _lenses.ContainsKey(to) || !_lenses.TryGetValue(from, out var lens)) return;
            _lenses.Remove(from);
            _lenses[to] = lens;
        }
    }
}
