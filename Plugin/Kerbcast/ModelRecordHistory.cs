using System;
using Kerbcast.Api;

namespace Kerbcast
{
    /*
     * The records a world model has published for one craft, oldest first,
     * each with its own path. A lens whose light time is longer than the
     * newest record's age plus its read-back cap sees the craft at an instant
     * an earlier record vouched for, and that record places it there as the
     * model then predicted it. So the aim follows the craft the picture
     * draws, rather than holding where the newest record's read-back stops.
     *
     * A kept record holds from its ValidFromUt until the next one starts,
     * read forward past its own window when the next came late, but never
     * more than StaleMarginS past its ValidUntilUt. A record that starts
     * later than that, or a keep long after the last one, starts the history
     * over: nothing kept bridges a gap the source did not watch. Before the
     * oldest it is read back along its path, held no lower than the floor it
     * was kept with. Within the newest record's read-back reach the newest
     * places the craft itself, so nothing here changes a lens whose light
     * time is short.
     */
    internal sealed class ModelRecordHistory
    {
        /* How far back records are kept, in UT: past any light time a lens is given. */
        public const double RetainS = 3600;
        /* The newest record is replaced rather than kept behind a new one until it starts this long after the one before it, so reach does not depend on the publish rate. */
        public const double MinSpacingS = 1.0;
        /* A kept record is read no further than this past its ValidUntilUt: past the spacing plus a publish interval. */
        public const double StaleMarginS = 2.0;
        /* A keep this long in UT after the last one starts the history over. */
        public const double GapS = 30.0;
        /* A backstop on memory; RetainS at MinSpacingS stays under it. */
        public const int MaxRecords = 4096;

        private struct Kept
        {
            public ModelRecord Record;
            public KeplerPath Path;
            public double FloorM;
        }

        /* A ring, oldest at _head, with each record's ValidFromUt alongside for the search. */
        private Kept[] _kept = new Kept[16];
        private double[] _from = new double[16];
        private int _head;
        private int _count;
        private double _lastKeepUt = double.NaN;

        private uint _pid;
        private long _epoch;
        private long _tenure;
        private string _siteKey;
        private string _scope;

        public int Count => _count;

        /* The earliest start a record before the newest gives, NaN while only the newest is kept. */
        public double EarliestUt => _count > 1 ? _from[_head] : double.NaN;

        private int Slot(int i) => (_head + i) % _kept.Length;

        private ref Kept At(int i) => ref _kept[Slot(i)];

        public void Clear()
        {
            Array.Clear(_kept, 0, _kept.Length);
            _head = 0;
            _count = 0;
            _lastKeepUt = double.NaN;
        }

        /* Whether the history is the one kept for this craft, site key and scope. */
        public bool OwnedBy(uint pid, string siteKey, string scope) =>
            _count > 0 && pid == _pid && string.Equals(siteKey, _siteKey, StringComparison.Ordinal)
            && string.Equals(scope, _scope, StringComparison.Ordinal);

        /* Takes the history for this craft, publisher epoch, lock tenure, site key and scope, starting it over when any differs. */
        public void Claim(uint pid, long epoch, long tenure, string siteKey, string scope)
        {
            if (pid == _pid && epoch == _epoch && tenure == _tenure && string.Equals(siteKey, _siteKey, StringComparison.Ordinal)
                && string.Equals(scope, _scope, StringComparison.Ordinal))
                return;
            Clear();
            _pid = pid;
            _epoch = epoch;
            _tenure = tenure;
            _siteKey = siteKey;
            _scope = scope;
        }

        /*
         * Keeps `r` as the craft's newest record, at UT `keptAtUt` (NaN when
         * not known), with the floor in metres from its body's centre that a
         * read back before its start is held at (NaN for none). A record
         * starting no later than a kept one replaces it and every one after
         * it, as a republished record does. False, keeping nothing, for a
         * record that is not positioned or whose path cannot be built on
         * `body`.
         */
        public bool Add(in ModelRecord r, in KinematicsBody body, double floorM, double keptAtUt = double.NaN)
        {
            if (r.State != CraftFixState.Positioned) return false;
            var path = default(KeplerPath);
            if (r.Kinematics != KinematicsKind.Surface && !WorldModelSnapshot.BuildPath(r, body, out path)) return false;
            if (_count > 0 && (At(_count - 1).Record.ValidUntilUt + StaleMarginS < r.ValidFromUt || keptAtUt - _lastKeepUt > GapS))
                Clear();
            _lastKeepUt = keptAtUt;
            while (_count > 0 && !(_from[Slot(_count - 1)] < r.ValidFromUt)) At(--_count) = default;
            if (_count >= 2 && _from[Slot(_count - 1)] - _from[Slot(_count - 2)] < MinSpacingS) At(--_count) = default;
            while (_count > 1 && _from[Slot(1)] <= r.ValidFromUt - RetainS) Drop();
            if (_count >= MaxRecords) Drop();
            if (_count == _kept.Length) Grow();
            int s = Slot(_count++);
            _kept[s] = new Kept { Record = r, Path = path, FloorM = floorM };
            _from[s] = r.ValidFromUt;
            return true;
        }

        private void Drop()
        {
            _kept[_head] = default;
            _head = (_head + 1) % _kept.Length;
            _count--;
        }

        private void Grow()
        {
            var kept = new Kept[_kept.Length * 2];
            var from = new double[kept.Length];
            for (int i = 0; i < _count; i++)
            {
                kept[i] = _kept[Slot(i)];
                from[i] = _from[Slot(i)];
            }
            _kept = kept;
            _from = from;
            _head = 0;
        }

        /* The last of the first `n` records starting at or before `ut`, -1 for none. */
        private int LastFrom(double ut, int n)
        {
            int lo = 0, hi = n - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_from[Slot(mid)] <= ut)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return found;
        }

        /*
         * The kept record that places the craft at `ut`, its path and its
         * floor; `readBack` when `ut` is before the oldest record and is read
         * back along it. False when the newest record reaches `ut` itself (at
         * its start or within PastInstant.MaxReadBackS before it), when only
         * the newest is kept, or when `ut` is past the found record's
         * ValidUntilUt by more than StaleMarginS.
         */
        public bool TryFind(double ut, out ModelRecord record, out KeplerPath path, out double floorM, out bool readBack)
        {
            record = default;
            path = default;
            floorM = double.NaN;
            readBack = false;
            int n = _count;
            if (n < 2 || !(ut < PastInstant.ReadBackFloor(_from[Slot(n - 1)]))) return false;
            int k = Math.Max(0, LastFrom(ut, n - 1));
            ref var kept = ref At(k);
            if (ut > kept.Record.ValidUntilUt + StaleMarginS) return false;
            readBack = ut < kept.Record.ValidFromUt;
            record = kept.Record;
            path = kept.Path;
            floorM = kept.FloorM;
            return true;
        }

        /*
         * Whether the current record, starting at `currentFromUt`, is read
         * back to `ut` itself. Further back only a kept record places the
         * craft; without one it is held, as the read-back cap holds it.
         */
        public static bool ReadsCurrentBack(double ut, double currentFromUt) => !(ut < PastInstant.ReadBackFloor(currentFromUt));

        /*
         * The record's place at `ut` about its body's centre in the fixed
         * frame, on `path` or, for a surface record, carried by `body`'s spin;
         * a read back before its start is held no lower than `floorM`.
         */
        public static bool TryPlace(in ModelRecord r, in KeplerPath path, double floorM, bool readBack, in KinematicsBody body,
            double ut, out DVec3 position)
        {
            position = default;
            if (!body.Known) return false;
            if (r.Kinematics == KinematicsKind.Surface)
                position = body.SurfaceAt(r.Latitude, r.Longitude, r.Altitude, ut, out _);
            else
            {
                if (!path.TryAt(ut, out position, out _)) return false;
                if (readBack) position = Kinematics.NoLowerThan(position, floorM);
            }
            return Kinematics.Finite(position.X) && Kinematics.Finite(position.Y) && Kinematics.Finite(position.Z);
        }

        /*
         * The fastest the records before the newest move the craft between
         * the two UTs, each over the span it holds (the oldest back to
         * `fromUt`), as `toFrame` bounds a speed about a record's body (by
         * index) in the caller's frame; NaN when none covers it. Surface
         * records add nothing: they move only with their body, which the
         * caller bounds.
         */
        public double SpeedOver(double fromUt, double toUt, Func<double, int, double> toFrame)
        {
            double v = double.NaN;
            int last = _count - 1;
            int start = Math.Max(0, LastFrom(fromUt, last));
            int body = -1;
            double own = double.NaN;
            for (int k = start; k < last; k++)
            {
                ref var kept = ref At(k);
                if (!(kept.Record.ValidFromUt < toUt) && k > 0) break;
                double from = k == 0 ? Math.Min(fromUt, kept.Record.ValidFromUt) : kept.Record.ValidFromUt;
                double until = _from[Slot(k + 1)];
                double a = Math.Max(from, fromUt), b = Math.Min(until, toUt);
                if (!(a < b) || kept.Record.Kinematics == KinematicsKind.Surface) continue;
                if (kept.Record.BodyIndex != body)
                {
                    if (body >= 0) v = Max(v, toFrame(own, body));
                    body = kept.Record.BodyIndex;
                    own = double.NaN;
                }
                own = Max(own, kept.Path.SpeedOver(a, b));
            }
            if (body >= 0) v = Max(v, toFrame(own, body));
            return v;
        }

        private static double Max(double v, double s) => double.IsNaN(s) || v >= s ? v : s;
    }
}
