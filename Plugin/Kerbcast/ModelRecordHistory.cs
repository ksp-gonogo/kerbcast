using System;
using System.Collections.Generic;
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
     * read forward past its own window when the next came late. Before the
     * oldest it is read back along its path, held no lower than the floor it
     * was kept with. Within the newest record's read-back reach the newest
     * places the craft itself, so nothing here changes a lens whose light
     * time is short.
     */
    internal sealed class ModelRecordHistory
    {
        /* About half an hour of records at one a second; older ones are dropped. */
        public const int MaxRecords = 2048;

        private struct Kept
        {
            public ModelRecord Record;
            public KeplerPath Path;
            public double FloorM;
        }

        private readonly List<Kept> _kept = new List<Kept>();

        public int Count => _kept.Count;

        /* The earliest start a record before the newest gives, NaN while only the newest is kept. */
        public double EarliestUt => _kept.Count > 1 ? _kept[0].Record.ValidFromUt : double.NaN;

        public void Clear() => _kept.Clear();

        /*
         * Keeps `r` as the craft's newest record, with the floor in metres
         * from its body's centre that a read back before its start is held
         * at (NaN for none). A record starting no later than a kept one
         * replaces it and every one after it, as a republished record does.
         * False, keeping nothing, for a record that is not positioned or whose
         * path cannot be built on `body`.
         */
        public bool Add(in ModelRecord r, in KinematicsBody body, double floorM)
        {
            if (r.State != CraftFixState.Positioned) return false;
            var path = default(KeplerPath);
            if (r.Kinematics != KinematicsKind.Surface && !WorldModelSnapshot.BuildPath(r, body, out path)) return false;
            int n = _kept.Count;
            while (n > 0 && !(_kept[n - 1].Record.ValidFromUt < r.ValidFromUt)) _kept.RemoveAt(--n);
            if (n >= MaxRecords) _kept.RemoveRange(0, n - MaxRecords + 1);
            _kept.Add(new Kept { Record = r, Path = path, FloorM = floorM });
            return true;
        }

        /*
         * The kept record that places the craft at `ut`, its path and its
         * floor; `readBack` when `ut` is before the oldest record and is read
         * back along it. False when the newest record reaches `ut` itself (at
         * its start or within PastInstant.MaxReadBackS before it) or only the
         * newest is kept.
         */
        public bool TryFind(double ut, out ModelRecord record, out KeplerPath path, out double floorM, out bool readBack)
        {
            record = default;
            path = default;
            floorM = double.NaN;
            readBack = false;
            int n = _kept.Count;
            if (n < 2 || !(ut < PastInstant.ReadBackFloor(_kept[n - 1].Record.ValidFromUt))) return false;
            int k = n - 2;
            while (k > 0 && _kept[k].Record.ValidFromUt > ut) k--;
            var kept = _kept[k];
            readBack = ut < kept.Record.ValidFromUt;
            record = kept.Record;
            path = kept.Path;
            floorM = kept.FloorM;
            return true;
        }

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
         * The fastest the records before the newest move the craft about their
         * bodies between the two UTs, each over the span it holds (the oldest
         * back to `fromUt`); NaN when none covers it. Surface records add
         * nothing: they move only with their body, which the caller bounds.
         */
        public double SpeedOver(double fromUt, double toUt)
        {
            double v = double.NaN;
            for (int k = 0; k < _kept.Count - 1; k++)
            {
                var kept = _kept[k];
                double from = k == 0 ? Math.Min(fromUt, kept.Record.ValidFromUt) : kept.Record.ValidFromUt;
                double until = _kept[k + 1].Record.ValidFromUt;
                double a = Math.Max(from, fromUt), b = Math.Min(until, toUt);
                if (!(a <= b) || kept.Record.Kinematics == KinematicsKind.Surface) continue;
                double s = kept.Path.SpeedOver(a, b);
                if (!(v >= s)) v = s;
            }
            return v;
        }
    }
}
