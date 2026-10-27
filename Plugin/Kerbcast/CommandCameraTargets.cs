using System;
using System.Collections.Generic;
using Kerbcast.Api;
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
        /* "ok", "tooFar", "levelTooLow", "notTrackable", or under a world model "notPositioned", "refused", "stale" or "tooUncertain". */
        public string Eligibility;
        public string EligibilityText;
        /* The world model's own refusal, when Eligibility is "refused"; Kerbcast.API reports them. */
        internal string RefusalCode;
        internal string RefusalText;
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

        /* Scratch for List; every caller is on the main thread. */
        private static readonly List<CraftFix> Craft = new List<CraftFix>();

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

        /* `fovMaxDeg` is the feed's widest field, for tooUncertain. */
        public static List<CommandTargetView> List(CommandCameraSite site, double ut, double fovMaxDeg)
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

            var source = FixSources.For(site);
            Craft.Clear();
            source.List(ut, site.VantagePersistentId, hasStation, Craft);
            foreach (var c in Craft)
            {
                double range = hasStation ? RangeFrom(station, c) : double.NaN;
                Add(result, c, range, source.ListsUnplacedByCareerRules, gated, trackedLimit, maxRange, fovMaxDeg);
            }
            Craft.Clear();

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
            return RangeFrom(station, FixSources.For(site).Find(persistentId, ut));
        }

        /*
         * Whether the feed at `siteKey` (null when not known yet) may name this
         * craft in an aim. An unknown craft never; for a Vessel aim, which
         * points at the craft itself, a world model's listed craft is not
         * positioned, a refused one is refused or stale, and one too uncertain
         * to frame from `site` (when it is known) with the aim's margin in
         * the feed's widest field is refused unless the aim allows it. Under
         * truth a known craft is always accepted, as 1.11 did.
         */
        public static AimResult AimSubject(string siteKey, CommandCameraSite site, uint persistentId, bool vesselAim,
            bool allowUncertain, double marginM, double fovMaxDeg)
        {
            var source = FixSources.ForKey(siteKey);
            if (!source.Knows(persistentId)) return AimResult.UnknownCraft;
            if (!vesselAim || source.ListsUnplacedByCareerRules) return AimResult.Ok;
            double ut = Planetarium.GetUniversalTime();
            var craft = source.Find(persistentId, ut);
            switch (craft.State)
            {
                case CraftFixState.Listed: return AimResult.NotPositioned;
                case CraftFixState.Refused:
                    return craft.RefusalCode == CommandCameraMath.RefusalStale ? AimResult.Stale : AimResult.Refused;
            }
            if (allowUncertain || site == null || !TryStation(site, ut, out Vector3d station)) return AimResult.Ok;
            return TooUncertain(craft, RangeFrom(station, craft), marginM, fovMaxDeg) ? AimResult.TooUncertain : AimResult.Ok;
        }

        private static bool TooUncertain(CraftFix craft, double range, double marginM, double fovMaxDeg) =>
            craft.Positioned && Kinematics.TooUncertain(craft.UncertaintyM,
                double.IsNaN(craft.RadiusM) ? CommandCameraMath.NominalCraftRadiusM : craft.RadiusM, marginM, range, fovMaxDeg);

        private static double RangeFrom(Vector3d station, CraftFix craft) =>
            craft.Positioned ? (craft.World - new DVec3(station.x, station.y, station.z)).Magnitude : double.NaN;

        /*
         * One row, or none for an undiscovered or unknown craft. The career
         * rules read the Tracking Station from KSP (its level, the network's
         * range, the unowned-object limit), which describe the player's
         * facility rather than any craft. Discovery is a fact about the craft:
         * truth gives it, and a world model does not (null), so under a model
         * every record counts as owned. notTrackable and levelTooLow, which
         * hinge on an unowned or untracked craft, then never apply: the
         * model's Listed and Refused records say what it does not know.
         * tooFar applies to the modelled range, and tooUncertain to the
         * model's uncertainty with no margin.
         */
        private static void Add(List<CommandTargetView> into, CraftFix craft, double range, bool truthListing,
            bool gated, int trackedLimit, double maxRange, double fovMaxDeg)
        {
            var level = craft.Discovery ?? DiscoveryLevels.Owned;
            bool owned = level == DiscoveryLevels.Owned;
            if (!owned && (level & DiscoveryLevels.Presence) == 0) return;
            bool unowned = !owned;
            bool trackable = owned || (level & DiscoveryLevels.StateVectors) != 0;
            string career = CommandCameraMath.Eligibility(gated, trackable, unowned, trackedLimit, range, maxRange);
            string code = CommandCameraMath.TargetEligibility(craft.State, craft.RefusalCode, truthListing, career,
                TooUncertain(craft, range, 0, fovMaxDeg));
            if (code == null) return;
            into.Add(new CommandTargetView
            {
                PersistentId = craft.PersistentId,
                VesselName = craft.Name ?? "",
                VesselType = craft.Kind ?? "",
                RangeM = range,
                Eligibility = code,
                EligibilityText = code == CommandCameraMath.EligibleRefused && !string.IsNullOrEmpty(craft.RefusalText)
                    ? craft.RefusalText
                    : CommandCameraMath.EligibilityText(code),
                RefusalCode = code == CommandCameraMath.EligibleRefused ? craft.RefusalCode : null,
                RefusalText = code == CommandCameraMath.EligibleRefused ? craft.RefusalText : null,
            });
        }

        private static bool TryStation(CommandCameraSite site, double ut, out Vector3d world)
        {
            world = default;
            if (site.HasVantage) return site.TryVantagePosition(ut, out world, out _);
            if (site.Body == null) return false;
            world = site.Body.GetWorldSurfacePosition(site.Latitude, site.Longitude, site.Altitude);
            return true;
        }
    }
}
