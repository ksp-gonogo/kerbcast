using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KSP.Localization;

namespace Kerbcast
{
    /* The game's side of the space lens: which saves research tech, and which nodes are researched. */
    internal static class SpaceLensTech
    {
        /* Career and science saves research tech; sandbox and no game have every step. */
        public static bool Researches()
        {
            var game = HighLogic.CurrentGame;
            return game != null && (game.Mode == Game.Modes.CAREER || game.Mode == Game.Modes.SCIENCE_SANDBOX);
        }

        /* Whether `techId` is researched, true for every node outside career and science. */
        public static bool Researched(string techId)
        {
            if (!Researches()) return true;
            try
            {
                return ResearchAndDevelopment.Instance != null
                    && ResearchAndDevelopment.GetTechnologyState(techId) == RDTech.State.Available;
            }
            catch (Exception) { return false; }
        }

        /* Titles by node id for one save: the tree's titles do not change within it, and the status file asks every second. */
        private static readonly Dictionary<string, string> Titles = new Dictionary<string, string>();
        private static Game _titlesFor;

        /* The node's title in the player's language, its id when the tree has no such node. */
        public static string Title(string techId)
        {
            if (string.IsNullOrEmpty(techId)) return "";
            var game = HighLogic.CurrentGame;
            if (!ReferenceEquals(game, _titlesFor))
            {
                Titles.Clear();
                _titlesFor = game;
            }
            if (Titles.TryGetValue(techId, out var cached)) return cached;
            string found = techId;
            try
            {
                var title = ResearchAndDevelopment.GetTechnologyTitle(techId);
                if (!string.IsNullOrEmpty(title)) found = title.StartsWith("#") ? Localizer.Format(title) : title;
            }
            catch (Exception) { }
            Titles[techId] = found;
            return found;
        }

        /* The save's mode as the wire names it: "career", "science", "sandbox", or another mode lowercased; "" with no game. */
        public static string ModeName()
        {
            var game = HighLogic.CurrentGame;
            if (game == null) return "";
            switch (game.Mode)
            {
                case Game.Modes.CAREER: return "career";
                case Game.Modes.SCIENCE_SANDBOX: return "science";
                case Game.Modes.SANDBOX: return "sandbox";
                default: return game.Mode.ToString().ToLowerInvariant();
            }
        }
    }

    /*
     * The lens levels a save offers, for the status file: the Tracking
     * Station's lens at each level and the space-lens steps with whether each
     * is researched, each with the zoom limit it gives, so a page can show a
     * player what their research buys.
     */
    internal static class LensLevelsStatus
    {
        /* The "lensLevels" object, or null with no game loaded. */
        public static string Json(KerbcastSettings s)
        {
            if (s == null || HighLogic.CurrentGame == null) return null;
            bool optics = s.DeepSpaceOptics && CommandCameraOptics.Available();
            double top = s.CommandCameraFovMin;
            double middle = Math.Max(s.CommandCameraFovMinLevel2, top);
            double lowest = Math.Max(s.CommandCameraFovMinLevel1, middle);
            double l1 = s.OpticsResolutionLevel1, l2 = s.OpticsResolutionLevel2, l3 = s.OpticsResolutionLevel3;
            bool gated = CommandCameraTargets.Gated();
            int levels = CommandCameraTargets.TopTrackingLevel();
            int level = gated ? CommandCameraTargets.TrackingLevel() : levels;

            var sb = new StringBuilder(512);
            sb.Append("{\"gameMode\":\"").Append(Esc(SpaceLensTech.ModeName())).Append('"');
            sb.Append(",\"opticsOn\":").Append(optics ? "true" : "false");
            sb.Append(",\"trackingGated\":").Append(gated ? "true" : "false");
            sb.Append(",\"trackingLevel\":").Append(level);
            sb.Append(",\"trackingLevels\":[");
            double nowArcsec = double.NaN, nowFov = double.NaN;
            for (int i = 1; i <= levels; i++)
            {
                double norm = levels > 1 ? (i - 1) / (double)(levels - 1) : 1;
                double arcsec = OpticsModel.ResolutionForLevel(norm, l1, l2, l3);
                double fov = optics ? OpticsModel.OpticsFovFloor(top, arcsec, s.Height) : CommandCameraMath.LensFovMin(norm, lowest, middle, top);
                if (i == level)
                {
                    nowArcsec = arcsec;
                    nowFov = fov;
                }
                if (i > 1) sb.Append(',');
                sb.Append("{\"level\":").Append(i).Append(",\"lensArcsec\":").Append(Num(arcsec)).Append(",\"fovMinDeg\":").Append(Num(fov)).Append('}');
            }
            sb.Append(']');
            sb.Append(",\"trackingLensArcsec\":").Append(Num(nowArcsec));
            sb.Append(",\"trackingFovMinDeg\":").Append(Num(nowFov));
            sb.Append(",\"spaceSteps\":[");
            bool first = true;
            foreach (var step in s.SpaceLensSteps)
            {
                if (!step.Valid) continue;
                if (!first) sb.Append(',');
                first = false;
                double fov = optics ? SpaceLens.FovMin(top, step.Arcsec, s.Height) : top;
                sb.Append("{\"techId\":\"").Append(Esc(step.TechId)).Append('"');
                sb.Append(",\"techTitle\":\"").Append(Esc(SpaceLensTech.Title(step.TechId))).Append('"');
                sb.Append(",\"unlocked\":").Append(SpaceLensTech.Researched(step.TechId) ? "true" : "false");
                sb.Append(",\"lensArcsec\":").Append(Num(step.Arcsec));
                sb.Append(",\"fovMinDeg\":").Append(Num(fov)).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /* Four significant figures, so a steady value writes the same text each second; never NaN, which JSON lacks. */
        private static string Num(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "0" : v.ToString("G4", CultureInfo.InvariantCulture);

        private static string Esc(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            var sb = new StringBuilder(v.Length + 4);
            foreach (char c in v)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
