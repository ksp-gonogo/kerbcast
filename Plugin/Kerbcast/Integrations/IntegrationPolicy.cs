// Unity-free process-global decisions for the visual-mod integration host.
//
// These run BEFORE any integration is applied to a camera (at render-target
// creation and at camera creation), so they cannot touch a built camera and
// must derive purely from each integration's one-shot availability probe.
// Today the only such decision is the MSAA format lever: Scatterer's
// depth-based effects break under MSAA, so when Scatterer (or any future
// integration that forces it) is installed, every cloned camera and the
// capture RT must run with MSAA off.

using System.Collections.Generic;

namespace Kerbcast
{
    // A snapshot of one integration's process-global facts, taken from its
    // one-shot reflection probe. Unity-free so the policy is unit-testable.
    internal readonly struct IntegrationFact
    {
        public readonly string Name;
        public readonly bool Available;
        public readonly bool ForcesNoMsaa;

        public IntegrationFact(string name, bool available, bool forcesNoMsaa)
        {
            Name = name;
            Available = available;
            ForcesNoMsaa = forcesNoMsaa;
        }
    }

    internal static class IntegrationPolicy
    {
        // MSAA is forced off iff at least one AVAILABLE integration requires it.
        public static bool ForceNoMsaa(IEnumerable<IntegrationFact> facts)
        {
            foreach (var f in facts)
                if (f.Available && f.ForcesNoMsaa) return true;
            return false;
        }
    }
}
