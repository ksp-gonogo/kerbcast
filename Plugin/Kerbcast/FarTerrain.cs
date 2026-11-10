using System;
using System.Collections.Generic;

namespace Kerbcast
{
    /* Which local-space terrain a camera leaves out of its local layers, apart from Unity so it can be tested. */
    internal static class FarTerrain
    {
        /*
         * Whether a lens `distanceM` from a body's centre, `altitudeM` above
         * its sea level, sees that body's local-space terrain, reaching at
         * most `terrainRadiusM` from the centre, as stock draws it: within the
         * local layers' native `nativeFarM`, and no higher than
         * `stockTerrainToM` (StockTerrainTo), above which stock switches the
         * terrain off and the scaled sphere stands for the body. Stock sets
         * the terrain's and ocean's materials for the main camera, so from
         * higher than stock ever draws them the ocean near the active craft
         * shades as a flat sky colour.
         */
        public static bool SeenAsStock(double distanceM, double terrainRadiusM, double nativeFarM, double altitudeM, double stockTerrainToM) =>
            PictureBoundary.TerrainInReach(distanceM, terrainRadiusM, nativeFarM) && !(altitudeM > stockTerrainToM);

        /*
         * Whether a camera leaves a body's local-space terrain out of its
         * local layers: when its layers, their far plane stretched to
         * `reachFarM`, reach it but the lens does not see it as stock does
         * (SeenAsStock). From beyond the native reach the stretched layer's
         * depth cannot part the ocean from the land under it, and from above
         * stock's terrain altitude its shading no longer holds, so the scaled
         * sphere stands for the body there, light speed or not. Range and
         * height alone decide it, never the field, so a zoom never switches
         * it.
         */
        public static bool LeftOut(double distanceM, double terrainRadiusM, double nativeFarM, double reachFarM, double altitudeM, double stockTerrainToM) =>
            PictureBoundary.TerrainInReach(distanceM, terrainRadiusM, reachFarM)
            && !SeenAsStock(distanceM, terrainRadiusM, nativeFarM, altitudeM, stockTerrainToM);

        /*
         * The altitude above which stock switches a body's terrain off, as
         * its PQSMod_CelestialBodyTransform decides: `highQualityM` when the
         * terrain shader quality is 2 or more and it is set (0 then never
         * switches off), else `deactivateM`.
         */
        public static double StockTerrainTo(double deactivateM, double highQualityM, int shaderQuality) =>
            shaderQuality >= 2 && highQualityM >= 0 ? (highQualityM > 0 ? highQualityM : double.PositiveInfinity) : deactivateM;

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
