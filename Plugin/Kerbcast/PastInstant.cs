using System;

namespace Kerbcast
{
    /* A point that moves with time: where it was at `ut`, false where it has no position. */
    internal interface IPastPath
    {
        bool TryAt(double ut, out DVec3 position);
    }

    /* The bodies as a tree: each body's parent, and its offset about that parent at `ut`. */
    internal interface IBodyTree
    {
        /* False for the root. */
        bool TryParent(int body, out int parent);
        bool TryOffset(int body, double ut, out DVec3 offset);
    }

    internal enum PastStatus
    {
        /* The light arriving now left the craft at the solved instant; from
           SolveFrom, possibly instead the instant its source first vouches for. */
        Found,
        /* No light from the craft has arrived yet: its past instant is before
           its history starts, or before the ring's floor. */
        Unseen,
        /* The last light the craft sent has already arrived: it ended (destroyed,
           recovered, merged) more than a light time ago. */
        Gone,
        /* The solve did not settle; nothing is drawn rather than something early. */
        Unsolved,
    }

    /* A craft as its arriving light shows it. */
    internal struct PastSight
    {
        public PastStatus Status;
        /* The past instant, and the craft's state about its body then. */
        public double Ut;
        public HistoryState State;
        /* The life of the craft id the light left. */
        public HistoryTrack Track;
        /* The body whose inertial frame the light time was solved in, and the
           craft's position about it at Ut. */
        public int Frame;
        public DVec3 About;

        public bool Found => Status == PastStatus.Found;
    }

    /*
     * The past instant t_r at which light arriving at a lens at t left a
     * craft: the latest root of t_r + |lens - craft(t_r)| / c = t. Pure, so
     * any camera's picture, and the knowledge side on its own positions, can
     * call it.
     *
     * The equation is solved in the inertial frame of the lowest common
     * ancestor of the lens's body and the craft's: the lens about that body
     * now, the craft about it at t_r, each through its own chain of bodies at
     * its own instant, so the ancestor's own motion drops out. Two craft
     * round Kerbin are solved in Kerbin's frame, a lens at Kerbin and a craft
     * at Duna in the Sun's.
     *
     * g(t_r) = t_r + |lens - craft(t_r)| / c - t is d / c >= 0 at t_r = t and
     * changes at a rate between 1 - v/c and 1 + v/c for a craft whose speed
     * in that frame never exceeds v. Every Found instant has g <= 0 on the
     * path itself, so its light has arrived and nothing shows early whatever
     * the path does: a bound the craft breaks, or a position that jumps
     * between ring entries (the 1 m gate, a centre of mass against an orbit
     * at load), costs only freshness or which root is taken. The exception
     * is SolveFrom's held instant: the source vouches for the craft from
     * then, though no light from it has arrived.
     *
     * Below c, g is strictly increasing, so the root is unique; it is
     * bracketed and narrowed, and the instant returned is within the
     * tolerance below it. At or above c (a craft closing faster than a heavily
     * scaled light speed) there may be several roots. Steps back by
     * g / (1 + v/c) never pass one; a speculative secant step ahead of them
     * that lands on g <= 0 is narrowed from there. The instant returned then
     * has its light arriving within the tolerance of t, and is the latest root
     * unless a speculative step crossed an even number of roots.
     */
    internal static class PastInstant
    {
        /* The narrowed bracket is at most this wide, in seconds, or RelativeTolerance of the starting light time. */
        public const double ToleranceS = 1e-6;
        public const double RelativeTolerance = 1e-9;
        /* Speed bounds are widened by this, for rounding in the maxima. */
        public const double SpeedMargin = 1.01;
        private const int MaxNarrowSteps = 64;
        private const int MaxTraceSteps = 256;
        private const int MaxLives = 8;
        private const int MaxDepth = 16;

        /*
         * The latest t_r in [lo, min(hi, t)] solving the equation for `path`,
         * whose speed in the lens's frame never exceeds `speedBound`.
         */
        public static PastStatus Solve<T>(ref T path, DVec3 lens, double t, double c, double speedBound,
            double lo, double hi, out double tr) where T : IPastPath
        {
            tr = double.NaN;
            if (!(c > 0) || double.IsNaN(t)) return PastStatus.Unsolved;
            double b = Math.Min(hi, t);
            if (!(b >= lo)) return PastStatus.Unseen;
            if (!G(ref path, lens, t, c, b, out double gb)) return PastStatus.Unsolved;
            if (gb < 0) return PastStatus.Gone;
            if (gb == 0)
            {
                tr = b;
                return PastStatus.Found;
            }
            double v = double.IsNaN(speedBound) ? 0 : Math.Max(0, speedBound * SpeedMargin);
            double tol = Math.Max(ToleranceS, RelativeTolerance * gb);
            return v < c
                ? Bracket(ref path, lens, t, c, v, lo, b, gb, tol, out tr)
                : Trace(ref path, lens, t, c, v, lo, b, gb, tol, out tr);
        }

        /*
         * Solve for a path known only from `lo`, such as a world model's
         * record that vouches for nothing before its ValidFromUt. Where the
         * root lies before `lo` the craft is taken at `lo`, as the source
         * first knows it, rather than not at all: the source's knowledge
         * arrived by its own route, not by light from the craft to this lens.
         * An unbounded `lo` is plain Solve, where Unseen means the craft did
         * not exist yet.
         */
        public static PastStatus SolveFrom<T>(ref T path, DVec3 lens, double t, double c, double speedBound,
            double lo, double hi, out double tr) where T : IPastPath
        {
            var status = Solve(ref path, lens, t, c, speedBound, lo, hi, out tr);
            if (status != PastStatus.Unseen || double.IsInfinity(lo) || !(lo <= Math.Min(hi, t))) return status;
            if (!path.TryAt(lo, out _)) return PastStatus.Unsolved;
            tr = lo;
            return PastStatus.Found;
        }

        /* Whether light from `path` at `ut` has reached the lens by t: the test every solved instant passes. */
        public static bool Arrived<T>(ref T path, DVec3 lens, double t, double c, double ut) where T : IPastPath =>
            G(ref path, lens, t, c, ut, out double g) && g <= 0;

        internal static bool G<T>(ref T path, DVec3 lens, double t, double c, double ut, out double g) where T : IPastPath
        {
            g = 0;
            if (!path.TryAt(ut, out var p)) return false;
            g = ut + (lens - p).Magnitude / c - t;
            return true;
        }

        /* g rises at no less than 1 - v/c, so the root is no earlier than b - g(b) / (1 - v/c). */
        private static PastStatus Bracket<T>(ref T path, DVec3 lens, double t, double c, double v,
            double lo, double b, double gb, double tol, out double tr) where T : IPastPath
        {
            tr = double.NaN;
            double down = b - gb / (1 - v / c);
            double gDown;
            if (down <= lo)
            {
                down = lo;
                if (!G(ref path, lens, t, c, down, out gDown)) return PastStatus.Unsolved;
                if (gDown > 0) return PastStatus.Unseen;
            }
            else if (!G(ref path, lens, t, c, down, out gDown)) return PastStatus.Unsolved;
            /* Only a craft faster than its bound leaves g positive this far back. */
            if (gDown > 0) return Trace(ref path, lens, t, c, v, lo, down, gDown, tol, out tr);
            return Narrow(ref path, lens, t, c, down, gDown, b, gb, tol, out tr);
        }

        /*
         * Steps back by g / (1 + v/c), which no root lies within, each time
         * first trying a secant step just past where the root looks to be: any
         * point with g <= 0 closes a bracket to narrow.
         */
        private static PastStatus Trace<T>(ref T path, DVec3 lens, double t, double c, double v,
            double lo, double b, double gb, double tol, out double tr) where T : IPastPath
        {
            tr = double.NaN;
            double k = 1 + v / c;
            double slope = 1;
            bool loPositive = false;
            for (int i = 0; i < MaxTraceSteps; i++)
            {
                double safe = b - gb / k;
                double guess = Math.Max(lo, b - gb / slope - tol);
                if (guess < safe && !(guess == lo && loPositive))
                {
                    if (!G(ref path, lens, t, c, guess, out double gGuess)) return PastStatus.Unsolved;
                    if (gGuess <= 0) return Narrow(ref path, lens, t, c, guess, gGuess, b, gb, tol, out tr);
                    /* g > 0 at both ends can still hide a pair of roots between, so keep stepping, aiming the next guess by this one. */
                    loPositive |= guess == lo;
                    double sg = (gb - gGuess) / (b - guess);
                    if (sg > 0 && !double.IsInfinity(sg)) slope = Math.Min(slope, sg);
                }
                if (safe < lo)
                {
                    if (!G(ref path, lens, t, c, lo, out double gLo)) return PastStatus.Unsolved;
                    if (gLo > 0) return PastStatus.Unseen;
                    return Narrow(ref path, lens, t, c, lo, gLo, b, gb, tol, out tr);
                }
                if (!G(ref path, lens, t, c, safe, out double gs)) return PastStatus.Unsolved;
                if (gs <= 0) return Narrow(ref path, lens, t, c, safe, gs, b, gb, tol, out tr);
                double s = (gb - gs) / (b - safe);
                if (s > 0 && !double.IsInfinity(s)) slope = s;
                b = safe;
                gb = gs;
            }
            return PastStatus.Unsolved;
        }

        /*
         * Narrows [down, up] with g(down) <= 0 < g(up) by secant steps, every
         * third a bisection so a lopsided bracket still halves. Returns `down`,
         * which always has g <= 0, so even an unfinished narrowing is safe.
         */
        private static PastStatus Narrow<T>(ref T path, DVec3 lens, double t, double c,
            double down, double gDown, double up, double gUp, double tol, out double tr) where T : IPastPath
        {
            for (int i = 0; i < MaxNarrowSteps && up - down > tol && gDown < 0; i++)
            {
                double x = down + (up - down) * (-gDown / (gUp - gDown));
                if (i % 3 == 2 || !(x > down && x < up)) x = 0.5 * (down + up);
                if (!G(ref path, lens, t, c, x, out double gx)) break;
                if (gx > 0) { up = x; gUp = gx; }
                else { down = x; gDown = gx; }
            }
            tr = down;
            return PastStatus.Found;
        }

        /* The lowest body that is `a` or an ancestor of it and also `b` or an ancestor of it; -1 if none. */
        public static int CommonAncestor<TB>(ref TB tree, int a, int b) where TB : IBodyTree
        {
            int x = a;
            for (int i = 0; i < MaxDepth; i++)
            {
                int y = b;
                for (int j = 0; j < MaxDepth; j++)
                {
                    if (y == x) return x;
                    if (!tree.TryParent(y, out y)) break;
                }
                if (!tree.TryParent(x, out x)) return -1;
            }
            return -1;
        }

        /* Body `body`'s centre about body `frame`'s centre at `ut`, each through its own chain of parents. */
        public static bool TryAbout<TB>(ref TB tree, int body, int frame, double ut, out DVec3 position) where TB : IBodyTree
        {
            position = default;
            int b = body;
            for (int i = 0; i < MaxDepth; i++)
            {
                if (b == frame) return true;
                if (!tree.TryParent(b, out int parent))
                {
                    /* `frame` is not above `body`: take its own offset from the root away. */
                    if (!TryAbout(ref tree, frame, b, ut, out var f)) return false;
                    position = position - f;
                    return true;
                }
                if (!tree.TryOffset(b, ut, out var offset)) return false;
                position = position + offset;
                b = parent;
            }
            return false;
        }

        /*
         * The craft with persistentId `pid` as light arriving at t at a lens
         * `lensOffset` from the centre of body `lensBody` shows it, through the
         * ghost-aware reads: the latest life of the id whose light is arriving.
         * A life whose light has not arrived yet passes to the life before it.
         *
         * The frame is the common ancestor of the lens's body at t and the
         * craft's body at t_r, where the light left it: solved first in the
         * frame of the body the craft is at by t, then again if the body at
         * the solved instant gives another, so an SOI crossing changes the
         * frame when its light arrives. Should the two frames each put t_r on
         * the other's side of the crossing, the older instant is taken.
         *
         * `notBefore` holds the instant: the last one drawn for this craft, in
         * frame `heldFrame`. A solution older than it (an end crossing into a
         * frame whose light time is longer) leaves the held instant drawn while
         * its light has arrived in that frame, the same test every solution
         * passes; otherwise the solution is drawn.
         *
         * `spreadM` stands for several lenses sharing one solve: each within
         * that many metres of the lens. The light must have arrived at all of
         * them, so the instant is solved as though it arrived spreadM / c
         * earlier: never early for any of them, and older by that over
         * 1 - v/c for a craft moving at v in the frame.
         */
        public static PastSight Craft<TB>(LightHistory ring, uint pid, double t, int lensBody, DVec3 lensOffset, double c,
            ref TB tree, double notBefore = double.NegativeInfinity, int heldFrame = -1, double spreadM = 0) where TB : IBodyTree
        {
            /* Zero spread leaves `arrive` exactly t. */
            double arrive = t - spreadM / c;
            var sight = new PastSight { Status = PastStatus.Unseen, Ut = double.NaN, Frame = -1 };
            var track = ring.TrackFor(pid, t);
            for (int life = 0; life < MaxLives && track != null && track.HasEntries; life++)
            {
                double lo = Math.Max(track.StartUt, ring.FloorUt);
                double hi = Math.Min(track.EndUt, t);
                var status = PastStatus.Unseen;
                double tr = double.NaN;
                var path = new RingPath<TB> { Ring = ring, Track = track, Tree = tree, Frame = -1 };
                DVec3 lens = default;
                if (hi >= lo && ring.TryStateAt(track, hi, out var latest))
                    status = SolveInFrames(ref path, ref tree, ring, track, lensBody, lensOffset, t, arrive, c, lo, latest.Body, out tr, out lens);
                if (status == PastStatus.Found && notBefore > tr && notBefore <= hi && notBefore >= lo)
                {
                    /* The held instant stays while its light has arrived in the frame it was drawn in. */
                    var held = path;
                    if (heldFrame >= 0) held.Frame = heldFrame;
                    if (TryAbout(ref tree, lensBody, held.Frame, t, out var heldLens)
                        && Arrived(ref held, heldLens + lensOffset, arrive, c, notBefore))
                    {
                        path = held;
                        tr = notBefore;
                    }
                }
                if (status == PastStatus.Found)
                {
                    if (path.TryAt(tr, out var at))
                    {
                        sight.Status = status;
                        sight.Ut = tr;
                        sight.State = path.Last;
                        sight.Track = track;
                        sight.Frame = path.Frame;
                        sight.About = at;
                        return sight;
                    }
                    status = PastStatus.Unsolved;
                }
                sight.Status = status;
                /* Older than the ring, or this life's last light is in: nothing before it can show. */
                if (status != PastStatus.Unseen || lo > track.StartUt) return sight;
                track = ring.EarlierLife(track);
            }
            return sight;
        }

        private static PastStatus SolveInFrames<TB>(ref RingPath<TB> path, ref TB tree, LightHistory ring, HistoryTrack track,
            int lensBody, DVec3 lensOffset, double t, double arrive, double c, double lo, int craftBody, out double tr, out DVec3 lens)
            where TB : IBodyTree
        {
            tr = double.NaN;
            lens = default;
            int frame = CommonAncestor(ref tree, lensBody, craftBody);
            int firstFrame = frame;
            double firstTr = double.NaN;
            DVec3 firstLens = default;
            var status = PastStatus.Unsolved;
            for (int pass = 0; pass < 2; pass++)
            {
                if (frame < 0 || !TryAbout(ref tree, lensBody, frame, t, out var lensAt)) return PastStatus.Unsolved;
                path.Frame = frame;
                lens = lensAt + lensOffset;
                status = Solve(ref path, lens, arrive, c, ring.MaxSpeedAbout(track, frame), lo, track.EndUt, out tr);
                if (status != PastStatus.Found || !ring.TryStateAt(track, tr, out var then)) return status;
                int next = CommonAncestor(ref tree, lensBody, then.Body);
                if (next == frame) return status;
                if (pass == 0)
                {
                    firstTr = tr;
                    firstLens = lens;
                    frame = next;
                }
            }
            /* Neither frame agrees with itself across the crossing: the older instant. */
            if (firstTr < tr)
            {
                tr = firstTr;
                lens = firstLens;
                path.Frame = firstFrame;
            }
            return status;
        }

        /*
         * Body `body` as light arriving at t at a lens `lensOffset` from the
         * centre of body `lensBody` shows it: solved in the frame of their
         * common ancestor, the lens's chain at t and the body's at its past
         * instant, no earlier than `lo`. Bodies keep their parents, so the
         * frame never changes and nothing is held. The lens's own body and the
         * bodies above it are the frame's centre itself: Found at t, with
         * Frame equal to `body`.
         *
         * A body whose speed in the frame may reach c can have several roots,
         * so it is followed from frame to frame by `trace`, this lens's own for
         * this body, taking at most `budget` reads: drawn at the latest root,
         * or at the last instant drawn while its light has arrived, or not at
         * all (Unsolved) while the search carries on. With no trace it is
         * solved afresh, however many reads that takes.
         */
        public static PastSight Body<TB>(ref TB tree, LightHistory ring, int body, int lensBody, DVec3 lensOffset,
            double t, double c, double lo, BodyTrace trace = null, int budget = BodyTrace.DefaultBudget) where TB : IBodyTree
        {
            trace?.BeginFrame();
            var sight = new PastSight { Status = PastStatus.Unsolved, Ut = double.NaN, Frame = -1 };
            int frame = CommonAncestor(ref tree, lensBody, body);
            if (frame < 0 || !TryAbout(ref tree, lensBody, frame, t, out var lensAt)) return sight;
            sight.Frame = frame;
            if (frame == body)
            {
                sight.Status = PastStatus.Found;
                sight.Ut = t;
                return sight;
            }
            var path = new BodyPath<TB> { Tree = tree, Body = body, Frame = frame };
            double speed = ring != null ? ring.BodySpeedBound(body, frame) : double.NaN;
            if (double.IsNaN(speed)) return sight;
            double accel = ring.BodyAccelBound(body, frame);
            PastStatus status;
            double tr;
            if (speed * SpeedMargin < c)
            {
                trace?.Reset();
                status = Solve(ref path, lensAt + lensOffset, t, c, speed, lo, t, out tr);
            }
            else if (trace != null) status = trace.Follow(ref path, frame, lensAt + lensOffset, t, c, speed, lo, budget, out tr, accel);
            else status = new BodyTrace().Follow(ref path, frame, lensAt + lensOffset, t, c, speed, lo, int.MaxValue, out tr, accel);
            if (status == PastStatus.Found && !path.TryAt(tr, out sight.About)) status = PastStatus.Unsolved;
            sight.Status = status;
            if (status == PastStatus.Found) sight.Ut = tr;
            return sight;
        }

        /*
         * Where to put a craft seen in state `s` about a body drawn as it is
         * at `t`: its offset at the past instant, kept in the inertial frame
         * for a craft in flight, carried round with the body's spin for a craft
         * on the surface, so a landed craft stays on its own ground.
         */
        public static DVec3 AboutDrawnBody(in HistoryState s, double t)
        {
            if (s.Kind != HistoryKind.Surface) return s.Position;
            return DQuat.FromRotationVector(s.AngularVelocity * (t - s.Ut)).Rotate(s.Position);
        }

        /* The craft's attitude as AboutDrawnBody places it: turned with its body's spin while on the surface. */
        public static DQuat AttitudeAboutDrawnBody(in HistoryState s, double t)
        {
            if (s.Kind != HistoryKind.Surface) return s.Attitude;
            return (DQuat.FromRotationVector(s.AngularVelocity * (t - s.Ut)) * s.Attitude).Normalized;
        }

        /* A craft's past in the ring as a point about the frame body: its state about its body plus that body about the frame, both at one instant. */
        private struct RingPath<TB> : IPastPath where TB : IBodyTree
        {
            public LightHistory Ring;
            public HistoryTrack Track;
            public TB Tree;
            public int Frame;
            public HistoryState Last;

            public bool TryAt(double ut, out DVec3 position)
            {
                position = default;
                if (!Ring.TryStateAt(Track, ut, out var s)) return false;
                if (!TryAbout(ref Tree, s.Body, Frame, ut, out var body)) return false;
                Last = s;
                position = body + s.Position;
                return true;
            }
        }
    }

    /*
     * One body's latest root as one lens sees it, followed from frame to
     * frame for a body whose speed in the frame may reach c, where the
     * equation can have several roots and a fresh trace from t each frame
     * would cost thousands of reads.
     *
     * A step of g / k back from a point, k = 1 + v/c, never passes a root.
     * Where the path's acceleration is bounded too, g bends by no more than
     * a known curvature, so the line through the last two points less that
     * bend reaches further: about g / slope, which near a root is far more
     * than g / k when the body outruns light. A span between two points is
     * clear where either bound keeps g above zero across it.
     * The trace keeps the points it stepped through, newest lowest, each with
     * a lower bound on g there that holds in later frames too: g at a fixed
     * instant falls by no more than the time passed plus the distance the
     * lens moved over c, so a point stores g plus a clock that runs at that
     * rate, and its bound is that sum less the clock now. Two neighbouring
     * points still rule out a root between them while their bounds reach each
     * other. Each point stores the clock until which the span above it is
     * clear, and the clock below which every span from t down to it is. A
     * frame resumes from the lowest point still clear and works g out afresh
     * below it, dropping any point whose neighbours alone clear the span
     * across it for a while yet. Where one span has run out and those below
     * it are still clear, only that span is stepped through again; then the
     * trace steps on down from its lowest point. An image birth dips g near
     * zero somewhere above the last root, which makes the spans there
     * expire, so the trace passes through it again and finds the new root.
     *
     * The last instant drawn, while its light has arrived, bounds the search
     * from below, since the latest root is then no earlier; it closes a
     * bracket only a step of g / k could not cross, or one a few tolerances
     * wide, since a root born inside a wider one would be passed over frame
     * after frame. Near a root a secant guess just past it saves the slow
     * last steps of a body crossing the line of sight; it reaches at most
     * GuessWindowS, so a guess that crossed a pair of roots still lands
     * within that of the latest, and is aimed afresh from above each frame.
     *
     * A search that runs out of reads carries into the next frame from where
     * it stopped. Meanwhile the last instant drawn stays drawn while its light
     * has arrived, otherwise nothing is, never the present.
     *
     * The bound assumes the path at an instant stays what it was. A path read
     * from the ring changes where new entries replace a prediction and where
     * the ring forgets its oldest: Revise names those spans, and the next
     * frame reads the points above again and drops those below.
     */
    internal sealed class BodyTrace
    {
        /* Reads one body may take in one frame. */
        public const int DefaultBudget = 512;
        /* The widest bracket the last instant drawn closes, with nothing to rule out a second root in it, in tolerances. */
        public const double CloseTolerances = 4;
        /* Guesses past the last step reach at most this far, in seconds. */
        public const double GuessWindowS = 1.0;
        private const int MaxCloseSteps = 64;
        /* Points kept; past this about half are dropped, those whose neighbours clear the span across them longest. */
        public const int MaxPoints = 1024;
        /* The closing tolerance grows with the size of the instants, so it stays above their rounding. */
        private const double Rounding = 1e-14;
        /*
         * A point is dropped only while its neighbours clear the span across
         * it for this many frames at the clock's last rate, or as long as the
         * two spans it parts did.
         */
        private const double MergeFrames = 8;

        private readonly int _most;
        private bool _set;
        private int _frame;
        private double _c, _k, _a, _t, _clock, _step, _tol;
        private DVec3 _lens;
        /*
         * Each point's instant, where the path was then, g there plus the
         * clock when it was last worked out, the clock until which the span
         * just above it is clear, and the clock below which every span above
         * it is.
         */
        private double[] _x = new double[32], _h = new double[32], _span = new double[32], _clear = new double[32];
        private DVec3[] _p = new DVec3[32];
        private double[] _lasts;
        private int _n;
        /* The point whose span above has run out while the spans below it are clear, -1 for none. */
        private int _gap = -1;
        private double _drawn = double.NaN;
        private double _reviseAbove = double.PositiveInfinity, _reviseBelow = double.NegativeInfinity;

        /* A trace keeping at most `most` points, at least 8. */
        public BodyTrace(int most = MaxPoints)
        {
            _most = Math.Max(8, most);
        }

        /* Reads the last frame took. */
        public int Reads { get; private set; }
        /* The last frame's search ran out of reads and carries on next frame. */
        public bool Carried { get; private set; }
        /* The last frame held the instant drawn before it while the search carried on. */
        public bool Held { get; private set; }
        /* The last frame traced afresh from t. */
        public bool Restarted { get; private set; }
        /* Points the trace keeps. */
        public int Points => _n;
        /* Thinnings while a gap was being stepped through, since the trace began. */
        internal int GapThins { get; private set; }
        /* Whether the points run strictly down from t, as every span needs. */
        internal bool Descending
        {
            get
            {
                for (int j = 1; j < _n; j++)
                    if (!(_x[j] < _x[j - 1])) return false;
                return true;
            }
        }
        /* The closing tolerance of the current trace, in seconds. */
        public double Tolerance => _tol;

        public void Reset()
        {
            BeginFrame();
            _set = false;
            _n = 0;
            _gap = -1;
            _drawn = double.NaN;
            _reviseAbove = double.PositiveInfinity;
            _reviseBelow = double.NegativeInfinity;
        }

        /* Clears the last frame's counts, for a frame that does not reach Follow. */
        public void BeginFrame()
        {
            Reads = 0;
            Carried = Held = Restarted = false;
        }

        /* The path changed at and after `above`, and before `below`. */
        public void Revise(double above, double below)
        {
            if (above < _reviseAbove) _reviseAbove = above;
            if (below > _reviseBelow) _reviseBelow = below;
        }

        /*
         * The instant to draw `path`, about frame body `frame`, from a lens at
         * `lens` at t, in at most about `budget` reads: Found with the latest
         * root or the held instant, Unseen if no light from after `lo` has
         * arrived, otherwise Unsolved. `accelBound` bounds the path's
         * acceleration in the frame; infinite, it is not used.
         */
        public PastStatus Follow<T>(ref T path, int frame, DVec3 lens, double t, double c, double speedBound, double lo,
            int budget, out double tr, double accelBound = double.PositiveInfinity) where T : IPastPath
        {
            var counted = new CountedPath<T> { Inner = path };
            var status = Follow(ref counted, frame, lens, t, c, speedBound, accelBound, lo, budget, out tr);
            Reads = counted.Reads;
            return status;
        }

        private PastStatus Follow<T>(ref CountedPath<T> path, int frame, DVec3 lens, double t, double c, double speedBound,
            double accelBound, double lo, int budget, out double tr) where T : IPastPath
        {
            tr = double.NaN;
            BeginFrame();
            if (!(c > 0) || double.IsNaN(t) || double.IsNaN(speedBound)) return PastStatus.Unsolved;
            double k = 1 + Math.Max(0, speedBound * PastInstant.SpeedMargin) / c;
            double a = double.IsNaN(accelBound) ? double.PositiveInfinity : Math.Max(0, accelBound * PastInstant.SpeedMargin);
            if (_set && (frame != _frame || c != _c || k > _k || a > _a || !(t >= _t))) Reset();
            if (_set)
            {
                _step = (t - _t) + (lens - _lens).Magnitude / c;
                _clock += _step;
                /* A tighter bound holds over every span already cleared: a body back on its Kepler path bends by it at once. */
                _a = a;
            }
            else
            {
                _set = true;
                _frame = frame;
                _c = c;
                _k = k;
                _a = a;
                _clock = 0;
                _step = 0;
            }
            _t = t;
            _lens = lens;
            k = _k;
            if (!ApplyRevisions(ref path, lens, t, c)) return PastStatus.Unsolved;
            Recheck(lens, t, c);

            double down = double.NaN, gDown = 0;
            if (_drawn >= lo && _drawn <= t)
            {
                if (!PastInstant.G(ref path, lens, t, c, _drawn, out gDown)) return PastStatus.Unsolved;
                if (gDown <= 0) down = _drawn;
            }
            while (_gap > 0)
            {
                if (!(_h[_gap] > _clock))
                {
                    /* Only the last point can have g <= 0: it closes the span above it, a bracket like any step. */
                    _n = _gap;
                    _gap = -1;
                    break;
                }
                if (path.Reads >= budget) return Carry(down, out tr);
                int above = _gap - 1;
                double xa = _x[above], ga = _h[above] - _clock;
                double reach = Math.Min(xa - Math.Max(ga / k, Reach(lens, t, c, above, ga)), xa - _tol);
                if (reach <= _x[_gap])
                {
                    /* The step from the point above reaches the point below: the span is clear again, this frame at least. */
                    _span[_gap] = Math.Max(_span[_gap], Meets(above, _gap));
                    for (int j = _gap; j < _n; j++) _clear[j] = Math.Min(_clear[j - 1], _span[j]);
                    _gap = -1;
                    break;
                }
                /* A root in the span is later than any below it, so those go. */
                if (down >= reach && down < xa)
                {
                    _n = _gap;
                    _gap = -1;
                    return Found(ref path, lens, t, c, down, gDown, xa, ga, budget, out tr);
                }
                if (reach <= lo)
                {
                    _n = _gap;
                    _gap = -1;
                    break;
                }
                if (!Read(ref path, lens, t, c, reach, out double gr, out var pr)) return PastStatus.Unsolved;
                if (gr <= 0)
                {
                    _n = _gap;
                    _gap = -1;
                    return Found(ref path, lens, t, c, reach, gr, xa, ga, budget, out tr);
                }
                Insert(reach, pr, gr + _clock);
            }
            double x = double.NaN, g = 0;
            if (_n > 0)
            {
                x = _x[_n - 1];
                g = _h[_n - 1] - _clock;
            }
            if (_n == 0)
            {
                Restarted = true;
                x = t;
                if (!Read(ref path, lens, t, c, x, out g, out var at)) return PastStatus.Unsolved;
                if (g <= 0)
                {
                    tr = _drawn = t;
                    return PastStatus.Found;
                }
                _tol = Math.Max(PastInstant.ToleranceS, Rounding * (Math.Abs(t) + g));
                _x[0] = x;
                _p[0] = at;
                _h[0] = g + _clock;
                _span[0] = _clear[0] = double.PositiveInfinity;
                _n = 1;
            }

            double narrow = CloseTolerances * _tol;
            double window = Math.Max(GuessWindowS, narrow);
            double slope = 0;
            while (true)
            {
                if (down < x && x - down <= narrow) return Found(ref path, lens, t, c, down, gDown, x, g, budget, out tr);
                if (path.Reads >= budget) break;
                double safe = Math.Min(x - Math.Max(g / k, Reach(lens, t, c, _n - 1, g)), x - _tol);
                if (slope > 0)
                {
                    double guess = x - g / slope - _tol;
                    if (guess < safe && x - guess <= window && guess > lo && !(down >= guess))
                    {
                        if (!PastInstant.G(ref path, lens, t, c, guess, out double gGuess)) return PastStatus.Unsolved;
                        if (gGuess <= 0) return Found(ref path, lens, t, c, guess, gGuess, x, g, budget, out tr);
                        if (path.Reads >= budget) break;
                    }
                }
                /* No root lies in (safe, x], so the last instant drawn, at or above safe, is the latest. */
                if (down >= safe) return Found(ref path, lens, t, c, down, gDown, x, g, budget, out tr);
                if (safe <= lo)
                {
                    if (!PastInstant.G(ref path, lens, t, c, lo, out double gLo)) return PastStatus.Unsolved;
                    if (gLo > 0) return PastStatus.Unseen;
                    return Found(ref path, lens, t, c, lo, gLo, x, g, budget, out tr);
                }
                if (!Read(ref path, lens, t, c, safe, out double gs, out var ps)) return PastStatus.Unsolved;
                if (gs <= 0) return Found(ref path, lens, t, c, safe, gs, x, g, budget, out tr);
                Push(safe, ps, gs + _clock);
                slope = (g - gs) / (x - safe);
                x = safe;
                g = gs;
            }

            return Carry(down, out tr);
        }

        /* Out of reads: the last instant drawn stays while its light has arrived, otherwise nothing is drawn. */
        private PastStatus Carry(double down, out double tr)
        {
            Carried = true;
            tr = double.NaN;
            if (double.IsNaN(down)) return PastStatus.Unsolved;
            Held = true;
            tr = down;
            return PastStatus.Found;
        }

        /*
         * Closes [down, up], g(down) <= 0 < g(up), on a root by regula falsi,
         * halving the weight of an end kept twice running (Illinois) so both
         * ends close in, and draws `down`, whose light has always arrived. Out
         * of reads it draws `down` as it stands and carries the closing on.
         */
        private PastStatus Found<T>(ref CountedPath<T> path, DVec3 lens, double t, double c, double down, double gDown, double up, double gUp,
            int budget, out double tr) where T : IPastPath
        {
            int kept = 0;
            for (int i = 0; i < MaxCloseSteps && up - down > _tol && gDown < 0; i++)
            {
                if (path.Reads >= budget)
                {
                    Carried = true;
                    break;
                }
                double x = down + (up - down) * (gDown / (gDown - gUp));
                if (!(x > down && x < up)) x = 0.5 * (down + up);
                if (!PastInstant.G(ref path, lens, t, c, x, out double gx)) break;
                if (gx > 0)
                {
                    up = x;
                    gUp = gx;
                    if (kept < 0) gDown *= 0.5;
                    kept = kept < 0 ? kept - 1 : -1;
                }
                else
                {
                    down = x;
                    gDown = gx;
                    if (kept > 0) gUp *= 0.5;
                    kept = kept > 0 ? kept + 1 : 1;
                }
            }
            tr = _drawn = down;
            return PastStatus.Found;
        }

        /*
         * Works g out afresh, from the lens now and where the path was, at
         * the top point and at every point below the lowest the clock still
         * clears. The first span below it that has run out becomes the gap;
         * at a second, that point and everything below it are dropped.
         * Nothing is read: the path at a point's instant is what it was.
         */
        private void Recheck(DVec3 lens, double t, double c)
        {
            _gap = -1;
            if (_n == 0) return;
            _h[0] = Exact(0, lens, t, c) + _clock;
            if (!(_clock < _h[0] - _k * (t - _x[0])))
            {
                _n = 0;
                return;
            }
            int m = Resume();
            if (m > 0)
            {
                _h[m] = Exact(m, lens, t, c) + _clock;
                /* Only a body faster than its bound reads g <= 0 where the bound said g > 0. */
                if (!(_h[m] > _clock))
                {
                    _n = 0;
                    return;
                }
            }
            int w = m + 1, gap = -1;
            for (int j = m + 1; j < _n; j++, w++)
            {
                double span = _span[j];
                _x[w] = _x[j];
                _p[w] = _p[j];
                _h[w] = Exact(w, lens, t, c) + _clock;
                _span[w] = Math.Max(span, Meets(w - 1, w));
                if (w - 2 >= m && w - 1 != gap && Merges(w))
                {
                    /* The point above is not needed: its neighbours clear the span across it. */
                    double across = Math.Max(Meets(w - 2, w), Math.Min(_span[w - 1], _span[w]));
                    w--;
                    _x[w] = _x[w + 1];
                    _p[w] = _p[w + 1];
                    _h[w] = _h[w + 1];
                    _span[w] = across;
                }
                _clear[w] = Math.Min(_clear[w - 1], _span[w]);
                if (!(_span[w] > _clock))
                {
                    if (gap >= 0) break;
                    gap = w;
                }
            }
            _n = w;
            _gap = gap < _n ? gap : -1;
        }

        private double Exact(int j, DVec3 lens, double t, double c) => _x[j] + (lens - _p[j]).Magnitude / c - t;

        /*
         * Whether point w - 1 may go: the span from w - 2 to w lasts as long
         * as the two it parts, or MergeFrames more frames. With no curvature
         * bound any span still clear will do: stepping by g / k alone, a
         * short trace costs fewer reads than the spans a point could save.
         */
        private bool Merges(int w)
        {
            double merged = Meets(w - 2, w);
            if (!(merged > _clock)) return false;
            if (double.IsPositiveInfinity(_a)) return true;
            return merged >= Math.Min(Meets(w - 2, w - 1), Meets(w - 1, w)) || merged > _clock + MergeFrames * _step;
        }

        /*
         * The most g can bend, |g''|, over the instants from `top` down
         * `width` seconds, in this frame and every later one: the path's
         * acceleration plus the turn of the line of sight, v^2 / d, over c.
         * While g >= 0 the path is at least c (t - top) away, and nowhere in
         * the span is it nearer than that less its speed times the width,
         * wherever g is. Infinite where no bound holds.
         */
        private double Curve(double top, double width) => Bend(_c * (_t - top) - (_k - 1) * _c * width);

        /* |g''| where the path is never nearer the lens than `near` metres. */
        private double Bend(double near)
        {
            if (double.IsPositiveInfinity(_a) || !(near > 0)) return double.PositiveInfinity;
            double v = (_k - 1) * _c;
            return (_a + v * v / near) / _c;
        }

        /*
         * How far below point i, with g there, g stays above zero by the line
         * through it and the point above it less the most the curve can bend
         * away from that line. Zero with no point above or no curvature
         * bound. It reaches no further than 4 g + 1 seconds, the span the
         * curvature bound is worked out over. The step is taken in this frame
         * alone, so the bound uses the lens's own distance from the point
         * above, less the path's speed times the span.
         */
        private double Reach(DVec3 lens, double t, double c, int i, double g)
        {
            if (i < 1 || double.IsPositiveInfinity(_a)) return 0;
            double up = _x[i - 1], h = up - _x[i];
            if (!(h > 0)) return 0;
            double most = 4 * g + 1;
            double curve = Bend((lens - _p[i - 1]).Magnitude - (_k - 1) * _c * (h + most));
            if (double.IsPositiveInfinity(curve)) return 0;
            double slope = (Exact(i - 1, lens, t, c) - g) / h;
            double b = slope + 0.5 * curve * h;
            double root = b + Math.Sqrt(b * b + 2 * curve * g);
            return root > 0 ? Math.Min(most, 2 * g / root) : most;
        }

        private static bool Read<T>(ref CountedPath<T> path, DVec3 lens, double t, double c, double ut, out double g, out DVec3 at)
            where T : IPastPath
        {
            g = 0;
            if (!path.TryAt(ut, out at)) return false;
            g = ut + (lens - at).Magnitude / c - t;
            return true;
        }

        /*
         * Points below where the path changed are dropped, and points at or
         * above it read again, the spans below them cleared anew from there.
         * False if a read fails.
         */
        private bool ApplyRevisions<T>(ref CountedPath<T> path, DVec3 lens, double t, double c) where T : IPastPath
        {
            double above = _reviseAbove, below = _reviseBelow;
            _reviseAbove = double.PositiveInfinity;
            _reviseBelow = double.NegativeInfinity;
            if (_n == 0) return true;
            for (int j = 0; j < _n; j++)
                if (_x[j] < below)
                {
                    _n = j;
                    break;
                }
            if (_n == 0 || !(above <= _x[0])) return true;
            for (int j = 0; j < _n && _x[j] >= above; j++)
            {
                if (!Read(ref path, lens, t, c, _x[j], out double g, out _p[j])) return false;
                if (!(g > 0))
                {
                    _n = j;
                    break;
                }
                _h[j] = g + _clock;
            }
            for (int j = 1; j < _n; j++)
            {
                _span[j] = Meets(j - 1, j);
                _clear[j] = Math.Min(_clear[j - 1], _span[j]);
            }
            return true;
        }

        /* The lowest point every span above which is still clear: _clear only falls down the list. */
        private int Resume()
        {
            int lo = 0, hi = _n - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_clock < _clear[mid]) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /*
         * The clock until which the span between points a and b is clear:
         * under both bounds, and under the clock at which the two bounds'
         * reach no longer meets, or, with a curvature bound, at which the
         * line between them less the most the curve can bend reaches zero.
         */
        private double Meets(int a, int b)
        {
            double width = _x[a] - _x[b];
            double ends = Math.Min(_h[a], _h[b]);
            double cone = Math.Min(0.5 * (_h[a] + _h[b] - _k * width), ends);
            double curve = Curve(_x[a], width);
            if (double.IsPositiveInfinity(curve) || !(width > 0)) return cone;
            /* g >= line - curve/2 u (width - u), u up from b; its least is where the slope of the line is spent. */
            double slope = (_h[a] - _h[b]) / width;
            double u = 0.5 * width - slope / curve;
            double bent = u > 0 && u < width ? _h[b] + slope * u - 0.5 * curve * u * (width - u) : ends;
            return Math.Max(cone, bent);
        }

        /* A point below the last, where the path was then, with `h` its g plus the clock. */
        private void Push(double x, DVec3 p, double h)
        {
            Room();
            _x[_n] = x;
            _p[_n] = p;
            _h[_n] = h;
            _span[_n] = Meets(_n - 1, _n);
            _clear[_n] = Math.Min(_clear[_n - 1], _span[_n]);
            _n++;
        }

        /* A point in the gap, just below the point above it; the span below the new point is the gap now. */
        private void Insert(double x, DVec3 p, double h)
        {
            Room();
            /* Thinning cleared the span across the gap: the point is not needed. */
            if (_gap < 0) return;
            int i = _gap;
            Array.Copy(_x, i, _x, i + 1, _n - i);
            Array.Copy(_p, i, _p, i + 1, _n - i);
            Array.Copy(_h, i, _h, i + 1, _n - i);
            Array.Copy(_span, i, _span, i + 1, _n - i);
            Array.Copy(_clear, i, _clear, i + 1, _n - i);
            _n++;
            _x[i] = x;
            _p[i] = p;
            _h[i] = h;
            _span[i] = Meets(i - 1, i);
            _clear[i] = Math.Min(_clear[i - 1], _span[i]);
            _gap = i + 1;
        }

        /*
         * Room for one more point, thinning a full trace. The gap is found
         * again by its lower point's instant, or by the point that took over
         * its span.
         */
        private void Room()
        {
            if (_n >= _most)
            {
                double below = _gap > 0 ? _x[_gap] : double.NaN;
                if (_gap > 0) GapThins++;
                Thin();
                if (_gap < 0) return;
                _gap = -1;
                for (int j = 1; j < _n; j++)
                    if (_x[j] <= below)
                    {
                        if (!(_span[j] > _clock)) _gap = j;
                        break;
                    }
            }
            else if (_n == _x.Length)
            {
                int size = Math.Min(_most, _x.Length * 2);
                Array.Resize(ref _x, size);
                Array.Resize(ref _p, size);
                Array.Resize(ref _h, size);
                Array.Resize(ref _span, size);
                Array.Resize(ref _clear, size);
            }
        }

        /*
         * Drops about half the points but the first and the last: those whose
         * dropping leaves the span across them clear longest. A span made of
         * several is clear while all of them are, or while its own two ends
         * clear it, whichever lasts.
         *
         * With no curvature bound a run of spans down to the root, joined,
         * lasts only as long as its last few frames near the root, and once
         * it runs out it takes the whole run's reads to cross again. So then
         * no two neighbours go in one thinning, and none whose joined span
         * would run out within MergeFrames.
         *
         * Should too few points go, every other point of the lower half does,
         * near the root, where the spans run out soon whatever is kept.
         */
        private void Thin()
        {
            int inner = _n - 2;
            if (_lasts == null) _lasts = new double[_most];
            for (int j = 1; j <= inner; j++) _lasts[j - 1] = Math.Max(Meets(j - 1, j + 1), Math.Min(_span[j], _span[j + 1]));
            Array.Sort(_lasts, 0, inner);
            bool flat = double.IsPositiveInfinity(_a);
            double floor = flat ? _clock + MergeFrames * _step : double.NegativeInfinity;
            if (Drop(Math.Max(_lasts[inner / 2], floor), floor, flat, 1) >= (flat ? inner / 8 : 1)) return;
            Drop(double.NegativeInfinity, double.NegativeInfinity, true, _n / 2);
        }

        /*
         * Drops up to half the points from `from` down, the last kept, whose
         * joined span would stay clear to `bar` and past `floor`, and no two
         * neighbours when `apart`; every other one when the bar is not
         * finite. Returns how many went.
         */
        private int Drop(double bar, double floor, bool apart, int from)
        {
            bool any = double.IsNegativeInfinity(bar);
            int w = from, dropped = 0, most = (_n - from) / 2;
            double parts = double.PositiveInfinity;
            for (int r = from; r < _n; r++)
            {
                double joined = Math.Min(parts, _span[r]);
                if (r < _n - 1 && dropped < most && !(apart && !double.IsPositiveInfinity(parts)))
                {
                    double across = Math.Max(Meets(w - 1, r + 1), Math.Min(joined, _span[r + 1]));
                    if (any ? (r - from) % 2 == 0 : across >= bar && across > floor)
                    {
                        parts = joined;
                        dropped++;
                        continue;
                    }
                }
                double span = Math.Max(joined, Meets(w - 1, r));
                _x[w] = _x[r];
                _p[w] = _p[r];
                _h[w] = _h[r];
                _span[w] = span;
                _clear[w] = Math.Min(_clear[w - 1], span);
                parts = double.PositiveInfinity;
                w++;
            }
            _n = w;
            return dropped;
        }
    }

    /*
     * The reads every lens's body traces may take in one frame, shared out
     * fairly whatever order the lenses are solved in: each lens is offered
     * what is left over the lenses still to come, counting as many as solved
     * last frame, and never more than `perLens`. What a lens leaves goes to
     * those after it; a lens beyond last frame's count takes what is left.
     */
    internal sealed class BodyReadPool
    {
        private readonly int _total, _perLens;
        private int _frame = int.MinValue, _left, _lenses, _lastLenses;

        public BodyReadPool(int total, int perLens)
        {
            _total = total;
            _perLens = perLens;
        }

        /* The reads the next lens solved in `frame` may take. */
        public int Offer(int frame)
        {
            if (frame != _frame)
            {
                if (_frame != int.MinValue) _lastLenses = _lenses;
                _frame = frame;
                _left = _total;
                _lenses = 0;
            }
            int toCome = Math.Max(1, _lastLenses - _lenses);
            _lenses++;
            return Math.Max(0, Math.Min(_perLens, _left / toCome));
        }

        public void Spend(int reads) => _left -= reads;
    }

    /* A path that counts its reads. */
    internal struct CountedPath<T> : IPastPath where T : IPastPath
    {
        public T Inner;
        public int Reads;

        public bool TryAt(double ut, out DVec3 position)
        {
            Reads++;
            return Inner.TryAt(ut, out position);
        }
    }

    /* A body's centre about the frame body, through its chain at each instant. */
    internal struct BodyPath<TB> : IPastPath where TB : IBodyTree
    {
        public TB Tree;
        public int Body;
        public int Frame;

        public bool TryAt(double ut, out DVec3 position) => PastInstant.TryAbout(ref Tree, Body, Frame, ut, out position);
    }

    /*
     * Each body from the ring's own track while one covers the instant, which
     * a body has only once it has left its Kepler path, otherwise from `Kepler`.
     */
    internal struct RingBodies<TK> : IBodyTree where TK : IBodyTree
    {
        public LightHistory Ring;
        public TK Kepler;

        public RingBodies(LightHistory ring, TK kepler)
        {
            Ring = ring;
            Kepler = kepler;
        }

        public bool TryParent(int body, out int parent) => Kepler.TryParent(body, out parent);

        public bool TryOffset(int body, double ut, out DVec3 offset)
        {
            if (Ring != null && Ring.TryBodyStateAt(body, ut, out var s))
            {
                offset = s.Position;
                return true;
            }
            return Kepler.TryOffset(body, ut, out offset);
        }
    }

    /*
     * The drawn past instant of one craft must never step backward: when an
     * end crosses into another frame its solution can jump back by about
     * v_body d / c². The last instant and its frame are offered as a floor:
     * an older solution leaves the instant where it is until the solution
     * passes it, as long as its light has arrived in the frame it was drawn
     * in. A rewind, a render UT earlier than the last, or a new light speed
     * or lens body starts afresh.
     */
    internal struct InstantHold
    {
        private bool _set;
        private double _t, _tr, _c;
        private long _generation;
        private int _lensBody;

        /* The frame the held instant was solved in. */
        public int Frame { get; private set; }

        /* The earliest instant the next solution at `t` may show. */
        public double NotBefore(double t, long generation, double c, int lensBody) =>
            _set && generation == _generation && t >= _t && c == _c && lensBody == _lensBody ? _tr : double.NegativeInfinity;

        public void Shown(double t, double tr, long generation, double c, int lensBody, int frame)
        {
            _set = true;
            _t = t;
            _tr = tr;
            _generation = generation;
            _c = c;
            _lensBody = lensBody;
            Frame = frame;
        }

        public void Reset() => _set = false;
    }
}
