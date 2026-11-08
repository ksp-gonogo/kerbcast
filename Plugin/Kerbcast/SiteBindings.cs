using System;
using System.Collections.Generic;
using System.Threading;

namespace Kerbcast
{
    /*
     * Which world-model scope each command site reads, as the holder's world
     * model last bound them. There is no default scope: a site with no
     * binding reads no scope, so every craft is unknown to it, rather than
     * silently borrowing another site's knowledge. Goes with the lock.
     *
     * Replace may run on the publisher's thread: it swaps in a new immutable
     * map with a volatile write. ScopeFor and FirstUnbound read on the main
     * thread, which alone owns the record of feeds already reported. Each
     * accepted world-model snapshot calls Replace; the governed read path
     * calls FirstUnbound to log each unbound feed once.
     */
    internal sealed class SiteBindings
    {
        static readonly Dictionary<string, string> None = new Dictionary<string, string>();

        Dictionary<string, string> bound = None;
        int version;

        readonly HashSet<uint> warned = new HashSet<uint>();
        int warnedVersion;

        public int Count => Volatile.Read(ref bound).Count;

        /* Replaces every binding. The same set again changes nothing, so a
           world model republishing its bindings does not re-log every feed. */
        public void Replace(IEnumerable<KeyValuePair<string, string>> bindings)
        {
            var next = new Dictionary<string, string>(StringComparer.Ordinal);
            if (bindings != null)
                foreach (var kv in bindings)
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value)) next[kv.Key] = kv.Value;
            Swap(next);
        }

        public void Clear() => Swap(None);

        /* The scope `siteKey` reads, null when it is unbound. Any thread. */
        public string ScopeFor(string siteKey) =>
            !string.IsNullOrEmpty(siteKey) && Volatile.Read(ref bound).TryGetValue(siteKey, out var scope) ? scope : null;

        /* True the first time feed `flightId` is found unbound since the
           bindings last changed, so each unbound feed is logged once. Main
           thread only. */
        public bool FirstUnbound(uint flightId, string siteKey)
        {
            int current = Volatile.Read(ref version);
            if (current != warnedVersion)
            {
                warned.Clear();
                warnedVersion = current;
            }
            return ScopeFor(siteKey) == null && warned.Add(flightId);
        }

        void Swap(Dictionary<string, string> next)
        {
            if (SameAs(Volatile.Read(ref bound), next)) return;
            Volatile.Write(ref bound, next);
            Interlocked.Increment(ref version);
        }

        static bool SameAs(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
                if (!b.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
