using System;
using Kerbcast.Api;
using UnityEngine;

namespace Kerbcast
{
    /* Feeds the ledger from KSP's GameEvents for the rest of the process, in
       every scene, and raises its events through KerbcastApi.Identity. Main
       thread. */
    internal static class VesselIdentity
    {
        static readonly VesselIdentityLedger Ledger = new VesselIdentityLedger();
        static bool hooked;
        static string saveFolder;

        /* What the last full scan saw; a miss rescans only once it changes. */
        static GameScenes scannedScene = GameScenes.LOADING;
        static int scannedVessels = -1;
        static int scannedSaved = -1;

        public static void EnsureHooked()
        {
            if (hooked) return;
            hooked = true;
            saveFolder = HighLogic.SaveFolder;
            var h = new Handlers();
            GameEvents.onGameStateCreated.Add(h.OnGameStateCreated);
            GameEvents.onVesselCreate.Add(h.OnVesselCreate);
            GameEvents.onVesselLoaded.Add(h.OnVesselCreate);
            GameEvents.onPartCouple.Add(h.OnPartCouple);
            GameEvents.onCrewBoardVessel.Add(h.OnCrewBoardVessel);
            GameEvents.onCrewOnEva.Add(h.OnCrewOnEva);
            GameEvents.onVesselPersistentIdChanged.Add(h.OnPersistentIdChanged);
            GameEvents.onPartDeCoupleNewVesselComplete.Add(h.OnSplit);
            GameEvents.onVesselsUndocking.Add(h.OnSplit);
            GameEvents.onVesselWillDestroy.Add(h.OnVesselWillDestroy);
            GameEvents.onVesselRecovered.Add(h.OnVesselRecovered);
            GameEvents.onVesselTerminated.Add(h.OnVesselTerminated);
        }

        /* On a miss, learns every craft KSP has now, loaded or saved, at most
           once per scene and craft count. */
        public static bool TryGet(uint persistentId, out Guid vesselId)
        {
            if (Ledger.TryGet(persistentId, out vesselId)) return true;
            return Rescan() && Ledger.TryGet(persistentId, out vesselId);
        }

        public static bool TryGetPersistentId(Guid vesselId, out uint persistentId)
        {
            if (Ledger.TryGetPersistentId(vesselId, out persistentId)) return true;
            return Rescan() && Ledger.TryGetPersistentId(vesselId, out persistentId);
        }

        /* False when nothing changed since the last scan, so a miss stays a miss. */
        static bool Rescan()
        {
            /* FlightGlobals is absent in some scenes, such as the editor. */
            var vessels = FlightGlobals.fetch != null ? FlightGlobals.Vessels : null;
            var saved = HighLogic.CurrentGame?.flightState?.protoVessels;
            int vesselCount = vessels?.Count ?? 0, savedCount = saved?.Count ?? 0;
            if (HighLogic.LoadedScene == scannedScene && vesselCount == scannedVessels && savedCount == scannedSaved) return false;
            scannedScene = HighLogic.LoadedScene;
            scannedVessels = vesselCount;
            scannedSaved = savedCount;
            for (int i = 0; i < vesselCount; i++)
                if (vessels[i] != null) Ledger.Learn(vessels[i].persistentId, vessels[i].id);
            for (int i = 0; i < savedCount; i++)
                if (saved[i] != null && !Ledger.TryGet(saved[i].persistentId, out _))
                    Ledger.Learn(saved[i].persistentId, saved[i].vesselID);
            return true;
        }

        /* Fires on every load, a quickload of the same save included; only a
           different save forgets what the ledger knew. */
        static void OnGameStateCreated(Game game)
        {
            string folder = HighLogic.SaveFolder;
            if (string.Equals(folder, saveFolder, StringComparison.Ordinal)) return;
            saveFolder = folder;
            Ledger.Reset();
            scannedVessels = -1;
        }

        static void OnVesselCreate(Vessel v)
        {
            if (v != null) Ledger.Learn(v.persistentId, v.id);
        }

        /* Fires before the merge: `from`'s craft is absorbed into `to`'s. */
        static void OnPartCouple(GameEvents.FromToAction<Part, Part> action) => Merge(action.from?.vessel, action.to?.vessel);

        /* Fires before KerbalEVA calls Die on the boarding kerbal's craft, so
           the boarding reads as a merge and the Die as nothing new. */
        static void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> action) => Merge(action.from?.vessel, action.to?.vessel);

        /* A kerbal going on EVA leaves the craft, as boarding joins it: `to` is the kerbal's new craft. */
        static void OnCrewOnEva(GameEvents.FromToAction<Part, Part> action) => OnSplit(action.from?.vessel, action.to?.vessel);

        /* A coupling (already reported by OnPartCouple, so the ledger drops
           it) or KSP renumbering a craft whose persistentId collided. */
        static void OnPersistentIdChanged(uint from, uint to)
        {
            Guid id = Guid.Empty;
            bool clash = false;
            try
            {
                clash = IsClash(from, to);
                if (FlightGlobals.fetch != null && FlightGlobals.FindVessel(to, out Vessel v) && v != null) id = v.id;
            }
            catch (Exception)
            {
                /* The vessel may be mid-registration; the ledger knows the old id's guid. */
            }
            Raise(Ledger.Renumber(from, to, id, Now(), clash));
        }

        /*
         * Whether KSP's persistentId change `from` to `to` gives a craft
         * arriving under a taken id a fresh one, rather than a coupling. KSP
         * checks a loading craft's id before the craft exists, so the fresh id
         * names no craft yet; the craft that had `from` may not exist yet
         * either, while a save's craft are still loading. A coupling always
         * fires with its survivor in place at `to`.
         */
        internal static bool IsClash(uint from, uint to)
        {
            if (FlightGlobals.fetch == null) return false;
            return !FlightGlobals.FindVessel(to, out Vessel arriving) || arriving == null;
        }

        static void Merge(Vessel absorbed, Vessel survivor)
        {
            if (absorbed == null || survivor == null) return;
            Raise(Ledger.Merge(absorbed.persistentId, absorbed.id, survivor.persistentId, survivor.id, Now()));
        }

        static void OnSplit(Vessel from, Vessel created)
        {
            if (from == null || created == null) return;
            Raise(Ledger.Split(created.persistentId, created.id, from.persistentId, from.id, Now()));
        }

        static void OnVesselWillDestroy(Vessel v)
        {
            if (v != null) Raise(Ledger.Lost(IdentityChange.Destroy, v.persistentId, v.id, Now()));
        }

        static void OnVesselRecovered(ProtoVessel pv, bool quick)
        {
            if (pv != null) Raise(Ledger.Lost(IdentityChange.Recover, pv.persistentId, pv.vesselID, Now()));
        }

        static void OnVesselTerminated(ProtoVessel pv)
        {
            if (pv != null) Raise(Ledger.Lost(IdentityChange.Destroy, pv.persistentId, pv.vesselID, Now()));
        }

        /* KSP's EventData.Add reads the handler's Target to name it, so a
           static method throws there; these instance forwarders give it one. */
        sealed class Handlers
        {
            public void OnGameStateCreated(Game game) => VesselIdentity.OnGameStateCreated(game);
            public void OnVesselCreate(Vessel v) => VesselIdentity.OnVesselCreate(v);
            public void OnPartCouple(GameEvents.FromToAction<Part, Part> action) => VesselIdentity.OnPartCouple(action);
            public void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> action) => VesselIdentity.OnCrewBoardVessel(action);
            public void OnCrewOnEva(GameEvents.FromToAction<Part, Part> action) => VesselIdentity.OnCrewOnEva(action);
            public void OnPersistentIdChanged(uint from, uint to) => VesselIdentity.OnPersistentIdChanged(from, to);
            public void OnSplit(Vessel from, Vessel created) => VesselIdentity.OnSplit(from, created);
            public void OnVesselWillDestroy(Vessel v) => VesselIdentity.OnVesselWillDestroy(v);
            public void OnVesselRecovered(ProtoVessel pv, bool quick) => VesselIdentity.OnVesselRecovered(pv, quick);
            public void OnVesselTerminated(ProtoVessel pv) => VesselIdentity.OnVesselTerminated(pv);
        }

        static double Now() => Planetarium.fetch != null ? Planetarium.GetUniversalTime() : HighLogic.CurrentGame?.UniversalTime ?? 0;

        static void Raise(IdentityEvent e)
        {
            if (e != null) KerbcastApi.RaiseIdentity(e, Debug.LogException);
        }
    }
}
