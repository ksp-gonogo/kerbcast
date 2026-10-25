using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /* A craft a command camera could watch. Eligibility only reports what the
       tracking network would allow; a Vessel aim takes any of them. */
    public sealed class CommandTargetView
    {
        public uint PersistentId;
        public string VesselName;
        /* KSP's VesselType name, e.g. "Ship", "Probe", "Debris". */
        public string VesselType;
        /* Metres from the station; NaN when the craft cannot be placed. */
        public double RangeM;
        /* "ok", "tooFar", "levelTooLow" or "notTrackable". */
        public string Eligibility;
        public string EligibilityText;
    }

    /*
     * The craft a command camera could watch, and the Tracking Station rules
     * that decide which of them its network allows.
     *
     * Listed: every vessel except flags, Unknown, dropped parts and the parts of
     * deployed ground experiments (their controller is listed), and the
     * vantage vessel the camera rides. An unowned object (asteroid, comet,
     * unclaimed rescue craft) is listed once discovered; until the player
     * tracks it, it is notTrackable in every game mode.
     *
     * Career only, with the KerbcastGameParameters tracking rules on: a craft
     * beyond GameVariables.GetDSNRange(level) is tooFar, and an unowned object
     * is levelTooLow while GameVariables.GetTrackedObjectLimit(level) is 0
     * (stock level 1). Both go through GameVariables so a mod that overrides it
     * (RP-1) sets the limits.
     */
    internal static class CommandCameraTargets
    {
        private const int RangeSignificantFigures = 3;

        /* Career with the tracking rules on. */
        public static bool Gated()
        {
            var game = HighLogic.CurrentGame;
            if (game == null || game.Mode != Game.Modes.CAREER || GameVariables.Instance == null) return false;
            try
            {
                var p = game.Parameters?.CustomParams<KerbcastGameParameters>();
                return p == null || p.CommandCameraTrackingRules;
            }
            catch (Exception) { return true; }
        }

        /* The Tracking Station's normalised level; 1 (the top) outside career. */
        public static float NormLevel()
        {
            var game = HighLogic.CurrentGame;
            if (game == null || game.Mode != Game.Modes.CAREER) return 1f;
            try { return ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.TrackingStation); }
            catch (Exception) { return 1f; }
        }

        public static int TrackingLevel()
        {
            int maxIndex;
            try { maxIndex = ScenarioUpgradeableFacilities.GetFacilityLevelCount(SpaceCenterFacility.TrackingStation); }
            catch (Exception) { maxIndex = -1; }
            return CommandCameraMath.TrackingLevel(NormLevel(), maxIndex);
        }

        public static double RoundRange(double rangeM) =>
            CommandCameraMath.RoundSignificant(rangeM, RangeSignificantFigures);

        public static List<CommandTargetView> List(CommandCameraSite site, double ut)
        {
            var result = new List<CommandTargetView>();
            if (site == null) return result;
            bool hasStation = TryStation(site, ut, out Vector3d station);
            bool gated = Gated();
            float norm = NormLevel();
            double maxRange = double.PositiveInfinity;
            int trackedLimit = int.MaxValue;
            if (gated)
            {
                maxRange = GameVariables.Instance.GetDSNRange(norm);
                trackedLimit = GameVariables.Instance.GetTrackedObjectLimit(norm);
            }

            var seen = new HashSet<uint>();
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                foreach (var v in vessels)
                {
                    if (v == null || v.persistentId == site.VantagePersistentId || !Listed(v.vesselType)) continue;
                    var level = v.DiscoveryInfo != null ? v.DiscoveryInfo.Level : DiscoveryLevels.Owned;
                    if (!seen.Add(v.persistentId)) continue;
                    double range = hasStation && TryPosition(v, ut, out Vector3d at) ? (at - station).magnitude : double.NaN;
                    Add(result, v.persistentId, v.GetDisplayName(), v.vesselType, level, range, gated, trackedLimit, maxRange);
                }
            }
            /* The Space Center may not have built Vessel objects; the save's records cover it. */
            var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protos != null)
            {
                foreach (var pv in protos)
                {
                    if (!RecordCounts(pv) || !Listed(pv.vesselType) || seen.Contains(pv.persistentId)) continue;
                    if (pv.persistentId == site.VantagePersistentId) continue;
                    seen.Add(pv.persistentId);
                    double range = hasStation && TryPosition(pv, ut, out Vector3d at) ? (at - station).magnitude : double.NaN;
                    Add(result, pv.persistentId, Localised(pv.vesselName), pv.vesselType, ProtoLevel(pv), range,
                        gated, trackedLimit, maxRange);
                }
            }

            result.Sort((a, b) =>
            {
                bool aOk = a.Eligibility == CommandCameraMath.EligibleOk, bOk = b.Eligibility == CommandCameraMath.EligibleOk;
                if (aOk != bOk) return aOk ? -1 : 1;
                bool aNaN = double.IsNaN(a.RangeM), bNaN = double.IsNaN(b.RangeM);
                if (aNaN != bNaN) return aNaN ? 1 : -1;
                int byRange = aNaN ? 0 : a.RangeM.CompareTo(b.RangeM);
                return byRange != 0 ? byRange : string.CompareOrdinal(a.VesselName, b.VesselName);
            });
            return result;
        }

        /* Station to one craft in metres; NaN when either end cannot be placed. */
        public static double RangeTo(CommandCameraSite site, uint persistentId, double ut)
        {
            if (site == null || persistentId == 0 || !TryStation(site, ut, out Vector3d station)) return double.NaN;
            if (FlightGlobals.FindVessel(persistentId, out Vessel v) && v != null)
                return TryPosition(v, ut, out Vector3d at) ? (at - station).magnitude : double.NaN;
            var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protos != null)
                foreach (var pv in protos)
                    if (RecordCounts(pv) && pv.persistentId == persistentId)
                        return TryPosition(pv, ut, out Vector3d at) ? (at - station).magnitude : double.NaN;
            return double.NaN;
        }

        /* Whether a craft with this persistentId exists now, live or, in a
           scene that has not built it, as a saved record. */
        public static bool Exists(uint persistentId)
        {
            if (persistentId == 0) return false;
            if (FlightGlobals.FindVessel(persistentId, out Vessel v) && v != null && v.state != Vessel.State.DEAD) return true;
            var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protos == null) return false;
            for (int i = 0; i < protos.Count; i++)
                if (RecordCounts(protos[i]) && protos[i].persistentId == persistentId) return true;
            return false;
        }

        /* Whether a saved record still names a real craft (see
           CommandCameraMath.SavedRecordCounts). A Vessel built from the record
           and since destroyed reads as Unity-null while still referenced. */
        public static bool RecordCounts(ProtoVessel pv)
        {
            if (pv == null) return false;
            var vessels = FlightGlobals.Vessels;
            bool authoritative = HighLogic.LoadedSceneIsFlight && vessels != null && vessels.Count > 0;
            bool built = !ReferenceEquals(pv.vesselRef, null);
            return CommandCameraMath.SavedRecordCounts(authoritative, built, built && pv.vesselRef == null);
        }

        private static void Add(List<CommandTargetView> into, uint pid, string name, VesselType type,
            DiscoveryLevels level, double range, bool gated, int trackedLimit, double maxRange)
        {
            bool owned = level == DiscoveryLevels.Owned;
            if (!owned && (level & DiscoveryLevels.Presence) == 0) return;
            bool unowned = !owned;
            bool trackable = owned || (level & DiscoveryLevels.StateVectors) != 0;
            string code = CommandCameraMath.Eligibility(gated, trackable, unowned, trackedLimit, range, maxRange);
            into.Add(new CommandTargetView
            {
                PersistentId = pid,
                VesselName = name ?? "",
                VesselType = type.ToString(),
                RangeM = range,
                Eligibility = code,
                EligibilityText = CommandCameraMath.EligibilityText(code),
            });
        }

        private static bool Listed(VesselType t) =>
            t != VesselType.Flag && t != VesselType.Unknown && t != VesselType.DroppedPart
            && t != VesselType.DeployedSciencePart && t != VesselType.DeployedGroundPart;

        private static bool TryStation(CommandCameraSite site, double ut, out Vector3d world)
        {
            world = default;
            if (site.HasVantage) return site.TryVantagePosition(ut, out world, out _);
            if (site.Body == null) return false;
            world = site.Body.GetWorldSurfacePosition(site.Latitude, site.Longitude, site.Altitude);
            return true;
        }

        private static bool TryPosition(Vessel v, double ut, out Vector3d world)
        {
            world = default;
            if (v.mainBody == null) return false;
            if (v.loaded) world = v.packed ? v.GetWorldPos3D() : v.CoMD;
            else if (IsSurface(v.situation)) world = v.mainBody.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
            else if (v.orbit != null) world = v.orbit.getPositionAtUT(ut);
            else return false;
            return true;
        }

        private static bool TryPosition(ProtoVessel pv, double ut, out Vector3d world)
        {
            world = default;
            var snap = pv.orbitSnapShot;
            if (snap == null) return false;
            var bodies = FlightGlobals.Bodies;
            int idx = snap.ReferenceBodyIndex;
            if (bodies == null || idx < 0 || idx >= bodies.Count || bodies[idx] == null) return false;
            try
            {
                if (IsSurface(pv.situation))
                    world = bodies[idx].GetWorldSurfacePosition(pv.latitude, pv.longitude, pv.altitude);
                else
                    world = snap.Load().getPositionAtUT(ut);
                return true;
            }
            catch (Exception) { return false; }
        }

        /* A saved craft's discovery state; Owned when the record has none. */
        private static DiscoveryLevels ProtoLevel(ProtoVessel pv)
        {
            var state = pv.discoveryInfo?.GetValue("state");
            return int.TryParse(state, out int level) ? (DiscoveryLevels)level : DiscoveryLevels.Owned;
        }

        private static string Localised(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith("#", StringComparison.Ordinal)) return name ?? "";
            try { return KSP.Localization.Localizer.Format(name); }
            catch (Exception) { return name; }
        }

        private static bool IsSurface(Vessel.Situations s) =>
            s == Vessel.Situations.LANDED || s == Vessel.Situations.SPLASHED || s == Vessel.Situations.PRELAUNCH;
    }
}
