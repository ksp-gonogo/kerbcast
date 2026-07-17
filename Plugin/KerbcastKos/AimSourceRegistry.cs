using System;
using System.Collections.Generic;

namespace Kerbcast.Kos
{
    /* One active AIM callback for a camera. Abstracted behind an interface so
       the registry's poll/re-arm lifecycle is unit-testable without a live kOS
       runtime (UserDelegateAimLease is the real, kOS-backed implementation). */
    public interface IAimLease
    {
        /* True once the last scheduled evaluation has returned (or when nothing
           has been scheduled yet), i.e. it is safe to apply + re-arm. */
        bool Finished { get; }

        /* Read the vector the callback last returned. False when there is no
           usable result yet (nothing scheduled, or a non-Vector return). */
        bool TryResult(out double x, out double y, out double z);

        /* Schedule the next evaluation. */
        void Rearm();
    }

    /* Unity-free lifecycle core for the AIM feature. Holds one lease per
       flightId; the pump drives Tick() once per Unity frame. Mirrors
       kOS Label.ScheduleTextUpdate: only apply the result and re-arm when the
       lease is Finished; a still-pending lease is left untouched so calls
       never stack up faster than they execute. */
    public sealed class AimSourceRegistry
    {
        readonly Dictionary<uint, IAimLease> sources = new Dictionary<uint, IAimLease>();

        /* Add, replace, or (lease == null) clear the source for a camera. */
        public void SetSource(uint id, IAimLease lease)
        {
            if (lease == null) sources.Remove(id);
            else sources[id] = lease;
        }

        public void Clear(uint id) => sources.Remove(id);

        public bool HasSource(uint id) => sources.ContainsKey(id);

        /* For each source that has finished its last evaluation: apply the
           returned vector (if any) via applyAim, then schedule the next one. A
           freshly-added lease reports Finished with no result, so its first
           Tick simply arms it. */
        public void Tick(Action<uint, double, double, double> applyAim)
        {
            foreach (var kv in sources)
            {
                var lease = kv.Value;
                if (!lease.Finished) continue;
                if (lease.TryResult(out var x, out var y, out var z))
                    applyAim(kv.Key, x, y, z);
                lease.Rearm();
            }
        }
    }
}
