using System;
using System.Collections.Generic;

namespace Kerbcast
{
    /* Which local-space terrain a camera leaves out of its local layers, apart from Unity so it can be tested. */
    internal static class FarTerrain
    {
        /*
         * Whether a camera leaves a body's local-space terrain, `distanceM`
         * from the lens and reaching at most `terrainRadiusM` from the body's
         * centre, out of its local layers: when only a far plane stretched to
         * `reachFarM` reaches it, not the layers' native `nativeFarM`. Stock
         * terrain is built to be seen from inside that native reach. From
         * farther, the stretched layer's depth cannot part the ocean from the
         * land under it and stock's near shading no longer holds, so the
         * scaled sphere stands for the body there, light speed or not. Range
         * alone decides it, never the field, so a zoom never switches it.
         */
        public static bool StretchedOnly(double distanceM, double terrainRadiusM, double nativeFarM, double reachFarM) =>
            !PictureBoundary.TerrainInReach(distanceM, terrainRadiusM, nativeFarM)
            && PictureBoundary.TerrainInReach(distanceM, terrainRadiusM, reachFarM);

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
