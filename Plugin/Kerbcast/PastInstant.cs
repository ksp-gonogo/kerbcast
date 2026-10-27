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
        /* The light arriving now left the craft at the solved instant. */
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
     * at load), costs only freshness or which root is taken.
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

        /* Whether light from `path` at `ut` has reached the lens by t: the test every solved instant passes. */
        public static bool Arrived<T>(ref T path, DVec3 lens, double t, double c, double ut) where T : IPastPath =>
            G(ref path, lens, t, c, ut, out double g) && g <= 0;

        private static bool G<T>(ref T path, DVec3 lens, double t, double c, double ut, out double g) where T : IPastPath
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

    /* Bodies from the ring's own tracks, for when they leave their Kepler orbits (Principia). */
    internal struct RingBodies : IBodyTree
    {
        public LightHistory Ring;

        public RingBodies(LightHistory ring) { Ring = ring; }

        public bool TryParent(int body, out int parent)
        {
            parent = -1;
            if (!Ring.TryBody(body, out var b) || b.Parent < 0) return false;
            parent = b.Parent;
            return true;
        }

        public bool TryOffset(int body, double ut, out DVec3 offset)
        {
            offset = default;
            if (!Ring.TryBodyStateAt(body, ut, out var s)) return false;
            offset = s.Position;
            return true;
        }
    }

    /*
     * The drawn past instant of one craft must never step backward: when an
     * end crosses into another frame its solution can jump back by about
     * v_body d / c². The last instant and its frame are offered as a floor:
     * an older solution leaves the instant where it is until the solution
     * passes it, as long as its light has arrived in the frame it was drawn
     * in. A rewind, a render UT earlier than the last, or a new signal speed
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
