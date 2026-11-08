using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kerbcast
{
    /* One ground station a command camera can stand at, as the game has it now. */
    internal struct GroundStation
    {
        /* The CommNet node name, which a station vantage takes. */
        public string NodeName;
        public string DisplayName;
        public string Body;
        public int BodyIndex;
        public double Latitude;
        public double Longitude;
        /* The ground (or the sea) under the station, as a lens standing there measures from. */
        public double Altitude;
        /* The game has it switched on (CommandCameraSite.StationEnabled). */
        public bool Enabled;
        /* Why not, when it is off (NetworkMath.StationOffReason). */
        public string DisabledReason;
    }

    /* One craft the game knows, for building vantage lists and watches. */
    internal struct CatalogVessel
    {
        public uint PersistentId;
        public string Name;
        /* KSP's VesselType and Vessel.Situations names. */
        public string VesselType;
        public string Body;
        public string Situation;
        public bool Loaded;
        public bool Active;

        /* Debris and space objects are cut first when the list is too long. */
        public bool CutFirst => VesselType == "Debris" || VesselType == "SpaceObject";
    }

    /*
     * The ground stations and craft the plugin publishes to the sidecar in
     * global.sites.json. Unity-free, so the file's shape is tested without
     * the game.
     */
    internal static class GroundStationList
    {
        public const string FileName = "global.sites.json";

        /* The most craft listed: the sidecar sends the list in one message, which must stay well under a data channel's limit. */
        public const int MaxVessels = 300;

        /* The file's body: every station with a finite place, in the order given. */
        public static string Json(IList<GroundStation> stations) => Json(stations, null);

        /*
         * Stations as above, then craft: the active one first, the rest in
         * the order given, and past MaxVessels debris and space objects are
         * cut before anything else, with vesselsOmitted counting the cut.
         */
        public static string Json(IList<GroundStation> stations, IList<CatalogVessel> vessels)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(64 + stations.Count * 160);
            sb.Append("{\"stations\":[");
            bool first = true;
            for (int i = 0; i < stations.Count; i++)
            {
                var s = stations[i];
                if (string.IsNullOrEmpty(s.NodeName) || !Finite(s.Latitude) || !Finite(s.Longitude) || !Finite(s.Altitude)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"nodeName\":\"").Append(Esc(s.NodeName))
                    .Append("\",\"displayName\":\"").Append(Esc(string.IsNullOrEmpty(s.DisplayName) ? s.NodeName : s.DisplayName))
                    .Append("\",\"body\":\"").Append(Esc(s.Body ?? ""))
                    .Append("\",\"latitude\":").Append(s.Latitude.ToString("R", inv))
                    .Append(",\"longitude\":").Append(s.Longitude.ToString("R", inv))
                    .Append(",\"altitude\":").Append(s.Altitude.ToString("R", inv))
                    .Append(",\"enabled\":").Append(s.Enabled ? "true" : "false");
                if (!s.Enabled && !string.IsNullOrEmpty(s.DisabledReason))
                    sb.Append(",\"disabledReason\":\"").Append(Esc(s.DisabledReason)).Append('"');
                sb.Append('}');
            }
            sb.Append(']');
            if (vessels != null)
            {
                var kept = Kept(vessels, out int omitted);
                sb.Append(",\"vessels\":[");
                for (int i = 0; i < kept.Count; i++)
                {
                    var v = kept[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"persistentId\":").Append(v.PersistentId.ToString(CultureInfo.InvariantCulture))
                        .Append(",\"name\":\"").Append(Esc(v.Name ?? ""))
                        .Append("\",\"vesselType\":\"").Append(Esc(v.VesselType ?? ""))
                        .Append("\",\"body\":\"").Append(Esc(v.Body ?? ""))
                        .Append("\",\"situation\":\"").Append(Esc(v.Situation ?? ""))
                        .Append("\",\"loaded\":").Append(v.Loaded ? "true" : "false")
                        .Append(",\"active\":").Append(v.Active ? "true" : "false")
                        .Append('}');
                }
                sb.Append(']');
                if (omitted > 0) sb.Append(",\"vesselsOmitted\":").Append(omitted.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static List<CatalogVessel> Kept(IList<CatalogVessel> vessels, out int omitted)
        {
            var ordered = new List<CatalogVessel>(vessels.Count);
            foreach (var v in vessels) if (v.Active) ordered.Add(v);
            foreach (var v in vessels) if (!v.Active && !v.CutFirst) ordered.Add(v);
            foreach (var v in vessels) if (!v.Active && v.CutFirst) ordered.Add(v);
            omitted = Math.Max(0, ordered.Count - MaxVessels);
            if (omitted > 0) ordered.RemoveRange(MaxVessels, omitted);
            return ordered;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
