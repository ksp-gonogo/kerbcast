using System;
using System.Collections.Generic;
using Kerbcast.Api;

namespace Kerbcast
{
    /*
     * Which craft persistentId named which Vessel.id, kept while one save is
     * loaded so a craft KSP no longer has still resolves, and the identity
     * events built from KSP's coupling, splitting and loss callbacks. A craft
     * reports one end (merge, destroy or recover) at most, so KSP's several
     * callbacks for one loss raise one event; seeing the craft again (a revert
     * or quickload) clears that.
     */
    internal sealed class VesselIdentityLedger
    {
        readonly Dictionary<uint, Guid> ids = new Dictionary<uint, Guid>();
        /* The latest persistentId each craft had, so a renumber points at the new one. */
        readonly Dictionary<Guid, uint> persistentIds = new Dictionary<Guid, uint>();
        readonly HashSet<uint> ended = new HashSet<uint>();

        public int Count => ids.Count;

        public void Learn(uint persistentId, Guid vesselId)
        {
            if (persistentId == 0 || vesselId == Guid.Empty) return;
            ids[persistentId] = vesselId;
            persistentIds[vesselId] = persistentId;
            ended.Remove(persistentId);
        }

        public bool TryGet(uint persistentId, out Guid vesselId) => ids.TryGetValue(persistentId, out vesselId);

        public bool TryGetPersistentId(Guid vesselId, out uint persistentId) => persistentIds.TryGetValue(vesselId, out persistentId);

        /* A different save: its persistentIds name other craft. */
        public void Reset()
        {
            ids.Clear();
            persistentIds.Clear();
            ended.Clear();
        }

        /* `absorbed` joined `survivor`; null when it already ended or is unnamed. */
        public IdentityEvent Merge(uint absorbed, Guid absorbedId, uint survivor, Guid survivorId, double ut)
        {
            if (absorbed == 0 || absorbed == survivor) return null;
            Learn(survivor, survivorId);
            return End(IdentityChange.Merge, absorbed, absorbedId, ut, survivor);
        }

        /* KSP gave `from`'s craft the persistentId `to`. Null when `from`
           already ended, as after a coupling, which KSP also reports as a
           persistentId change. Null too for a `clash`: a craft arriving under
           an id another craft has gets `to` before anyone saw it, and the
           other craft keeps `from`, so nothing changed for any subscriber. */
        public IdentityEvent Renumber(uint from, uint to, Guid vesselId, double ut, bool clash = false)
        {
            if (from == 0 || to == 0 || from == to) return null;
            if (clash) return null;
            if (vesselId == Guid.Empty) ids.TryGetValue(from, out vesselId);
            var e = End(IdentityChange.Renumber, from, vesselId, ut, to);
            if (e == null) return null;
            Learn(to, vesselId);
            return new IdentityEvent(e.Change, e.Ut, e.PersistentId, e.VesselId, e.OtherPersistentId, vesselId);
        }

        /* `created` left `from`. */
        public IdentityEvent Split(uint created, Guid createdId, uint from, Guid fromId, double ut)
        {
            if (created == 0 || created == from) return null;
            Learn(created, createdId);
            Learn(from, fromId);
            return new IdentityEvent(IdentityChange.Split, ut, created, Resolve(created, createdId), from, Resolve(from, fromId));
        }

        /* A destroy or recover; null when the craft already ended. */
        public IdentityEvent Lost(IdentityChange change, uint persistentId, Guid vesselId, double ut) =>
            persistentId == 0 ? null : End(change, persistentId, vesselId, ut, 0);

        IdentityEvent End(IdentityChange change, uint persistentId, Guid vesselId, double ut, uint other)
        {
            if (vesselId != Guid.Empty && !ids.ContainsKey(persistentId))
            {
                ids[persistentId] = vesselId;
                if (!persistentIds.ContainsKey(vesselId)) persistentIds[vesselId] = persistentId;
            }
            if (!ended.Add(persistentId)) return null;
            return new IdentityEvent(change, ut, persistentId, Resolve(persistentId, vesselId),
                other, other == 0 ? Guid.Empty : Resolve(other, Guid.Empty));
        }

        Guid Resolve(uint persistentId, Guid given) =>
            given != Guid.Empty ? given : ids.TryGetValue(persistentId, out var id) ? id : Guid.Empty;
    }
}
