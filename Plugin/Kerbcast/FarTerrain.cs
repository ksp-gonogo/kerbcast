using System;
using System.Collections.Generic;

namespace Kerbcast
{
    /* Which local-space terrain a camera leaves out of its local layers, apart from Unity so it can be tested. */
    internal static class FarTerrain
    {
        /* A turn back smaller than this, in pixels, is not worth losing a body's detailed terrain for. */
        public const double MinSpinSlipPx = 2.0;

        /*
         * How many pixels of a `rows`-high picture at vertical `fovDeg` a
         * body's ground slips when drawn turned back by its light time, at the
         * point under the lens, for a body turning at `spinRadPerS` of radius
         * `radiusM`. The ground moves wR for a light time of range / c, seen
         * from that range, so range cancels and the answer holds at any
         * distance.
         */
        public static double SpinSlipPx(double spinRadPerS, double radiusM, double lightSpeed, double fovDeg, int rows) =>
            lightSpeed > 0 ? Math.Abs(spinRadPerS * radiusM) / lightSpeed / CommandCameraMath.PixelSizeAtUnit(fovDeg, rows) : double.PositiveInfinity;

        /*
         * Whether a camera draws a body away from where its terrain stands in
         * local space: moved to its past instant, left out, or only turned
         * back by at least MinSpinSlipPx. A slip that cannot be read counts as
         * a turn worth hiding for.
         */
        public static bool DrawnAway(bool moves, bool hides, bool spins, double spinSlipPx) =>
            moves || hides || (spins && !(spinSlipPx < MinSpinSlipPx));

        /*
         * Stock parents a sphere's most detailed quads to a storage object
         * every sphere shares, outside that sphere's own hierarchy. Fills
         * `into` with the quads in `storage` that belong to `sphere`.
         */
        public static void OwnedQuads<TQuad, TSphere>(List<TQuad> storage, TSphere sphere, Func<TQuad, TSphere> rootOf, List<TQuad> into)
            where TSphere : class
        {
            into.Clear();
            for (int i = 0; i < storage.Count; i++)
                if (ReferenceEquals(rootOf(storage[i]), sphere)) into.Add(storage[i]);
        }
    }

    /*
     * Each key's items walked once and shared until the frame or `version`
     * changes, so every camera in a frame reads the one walk. What a camera
     * does with the items stays its own to undo.
     */
    internal sealed class SharedWalk<TKey, TItem> where TKey : class
    {
        private readonly Dictionary<TKey, List<TItem>> _lists = new Dictionary<TKey, List<TItem>>();
        private readonly HashSet<TKey> _walked = new HashSet<TKey>();
        private int _frame = int.MinValue;
        private int _version;

        /* How many walks have run, for tests. */
        public int Walks { get; private set; }

        /* `key`'s items for this frame, filled by `walk` the first time they are asked for since the frame or `version` changed. */
        public List<TItem> For(TKey key, int frame, int version, Action<TKey, List<TItem>> walk)
        {
            if (frame != _frame || version != _version)
            {
                _frame = frame;
                _version = version;
                _walked.Clear();
                foreach (var stale in _lists.Values) stale.Clear();
            }
            if (!_lists.TryGetValue(key, out var list)) _lists[key] = list = new List<TItem>();
            if (_walked.Add(key))
            {
                list.Clear();
                walk(key, list);
                Walks++;
            }
            return list;
        }
    }
}
