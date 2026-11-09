using System;
using System.Collections.Generic;

namespace Kerbcast
{
    /* Which local-space terrain a camera leaves out of its local layers, apart from Unity so it can be tested. */
    internal static class FarTerrain
    {
        /* Whether a camera draws a body away from where its terrain stands in local space: moved to its past instant, left out, or only turned back. */
        public static bool DrawnAway(bool moves, bool hides, bool spins) => moves || hides || spins;

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
}
