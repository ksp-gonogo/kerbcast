using System.Collections.Generic;

namespace Kerbcast
{
    /* The bodies as the planetarium walks them: each body's children, its parent, and whether its orbit driver is reversed (it places its parent from itself). */
    internal interface IBodyWalk
    {
        int ChildCount(int body);
        int Child(int body, int i);
        int Parent(int body);
        bool Reversed(int body);
    }

    /*
     * The order the planetarium writes body positions each tick, and the
     * rotation each was written under. Planetarium.UpdateCBsRecursive walks
     * the bodies depth first from a root it finds by following reversed orbit
     * drivers down from the Sun, skipping a reversed child (it is walked from
     * below), and climbing to the parent after a reversed body's own subtree.
     * A body under inverse rotation turns the planetarium's frame in its own
     * update, before it writes its own offset, so every body written before
     * it that tick holds its offset under the previous tick's rotation.
     */
    internal static class BodyUpdateOrder
    {
        private const int MaxBodies = 1024;

        /* The body the walk starts from: the first reversed child, followed down, from `sun`. */
        public static int Root<T>(ref T tree, int sun) where T : IBodyWalk
        {
            int body = sun;
            for (int guard = 0; guard < MaxBodies; guard++)
            {
                int next = -1;
                for (int i = 0; i < tree.ChildCount(body); i++)
                {
                    int c = tree.Child(body, i);
                    if (tree.Reversed(c)) { next = c; break; }
                }
                if (next < 0) return body;
                body = next;
            }
            return body;
        }

        /* Every body in the order the planetarium updates it, from the root `Root` finds. */
        public static void Walk<T>(ref T tree, int sun, List<int> into) where T : IBodyWalk
        {
            into.Clear();
            Visit(ref tree, Root(ref tree, sun), into);
        }

        private static void Visit<T>(ref T tree, int body, List<int> into) where T : IBodyWalk
        {
            if (body < 0 || into.Count >= MaxBodies) return;
            into.Add(body);
            for (int i = 0; i < tree.ChildCount(body); i++)
            {
                int c = tree.Child(body, i);
                if (tree.Reversed(c)) continue;
                Visit(ref tree, c, into);
            }
            if (tree.Reversed(body)) Visit(ref tree, tree.Parent(body), into);
        }

        /*
         * The planetarium rotation each body's offset about its parent was
         * written under, indexed by body: `previous` for the bodies walked
         * before `inverseBody`, `current` for it and every body after, and
         * `current` for all of them with no body under inverse rotation (-1).
         * False when that cannot be told: a body under inverse rotation the
         * walk does not reach, or no previous rotation to give the ones before it.
         */
        public static bool Rotations(List<int> order, int inverseBody, DQuat previous, bool havePrevious, DQuat current, DQuat[] into)
        {
            for (int i = 0; i < into.Length; i++) into[i] = current;
            if (inverseBody < 0) return true;
            int at = order.IndexOf(inverseBody);
            if (at < 0 || (at > 0 && !havePrevious)) return false;
            for (int i = 0; i < at; i++)
            {
                int b = order[i];
                if (b >= 0 && b < into.Length) into[b] = previous;
            }
            return true;
        }
    }
}
