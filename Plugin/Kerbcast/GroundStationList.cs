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
    }

    /*
     * The ground stations the plugin publishes to the sidecar in
     * global.sites.json. Unity-free, so the file's shape is tested without
     * the game.
     */
    internal static class GroundStationList
    {
        public const string FileName = "global.sites.json";

        /* The file's body: every station with a finite place, in the order given. */
        public static string Json(IList<GroundStation> stations)
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
                    .Append(",\"enabled\":").Append(s.Enabled ? "true" : "false")
                    .Append('}');
            }
            sb.Append("]}\n");
            return sb.ToString();
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
