using System;
using System.Globalization;

namespace Kerbcast
{
    public enum CommandVantageKind
    {
        LaunchSite,
        Station,
        Surface,
        Vessel,
    }

    /* Where a created command camera stands. Build one with the factory
       methods; the kind says which fields are meaningful. Unity-free, so the
       sidecar request path and the tests share it. */
    public sealed class CommandVantage
    {
        public const double DefaultPadDistance = 500.0;
        public const double DefaultPadBearing = 180.0;
        public const double DefaultPadHeight = 5.0;
        public const double DefaultStationHeight = 30.0;
        public const double DefaultSurfaceHeight = 5.0;

        public CommandVantageKind Kind { get; }

        /* LaunchSite: the site's name ("LaunchPad", "Runway", a Making History
           or Kerbal Konstructs site), or empty for the active craft's launch
           site (the stock pad when it has none). Station: the CommNet home's
           nodeName, e.g. "KSC". */
        public string Name { get; }

        /* LaunchSite: metres from the site and the compass bearing from the site to the lens. */
        public double Distance { get; }
        public double Bearing { get; }

        /* Metres above the ground below the lens, or above the sea over water.
           For a station, above the station's own altitude. Unused for Vessel. */
        public double Height { get; }

        /* Surface: the body's bodyName and the lens position in degrees. */
        public string Body { get; }
        public double Latitude { get; }
        public double Longitude { get; }

        /* Vessel: the craft's Vessel.persistentId. */
        public uint PersistentId { get; }

        private CommandVantage(CommandVantageKind kind, string name = null, double distance = 0, double bearing = 0,
            double height = 0, string body = null, double lat = 0, double lon = 0, uint pid = 0)
        {
            Kind = kind;
            Name = name?.Trim() ?? "";
            Distance = distance;
            Bearing = bearing;
            Height = height;
            Body = body?.Trim() ?? "";
            Latitude = lat;
            Longitude = lon;
            PersistentId = pid;
        }

        /* Beside a launch site, the way the default Launchpad camera stands. */
        public static CommandVantage LaunchSite(string site = null, double distance = DefaultPadDistance,
            double bearing = DefaultPadBearing, double height = DefaultPadHeight) =>
            new CommandVantage(CommandVantageKind.LaunchSite, name: site, distance: distance, bearing: bearing, height: height);

        /* At a CommNet ground station, by its nodeName. */
        public static CommandVantage Station(string nodeName, double height = DefaultStationHeight) =>
            new CommandVantage(CommandVantageKind.Station, name: nodeName, height: height);

        /* Any point on a body's surface. */
        public static CommandVantage Surface(string body, double latitude, double longitude,
            double height = DefaultSurfaceHeight) =>
            new CommandVantage(CommandVantageKind.Surface, body: body, lat: latitude, lon: longitude, height: height);

        /* Riding a craft. Keep it unloaded (beyond physics range of the active
           craft), or the lens sits inside its parts. */
        public static CommandVantage Vessel(uint persistentId) =>
            new CommandVantage(CommandVantageKind.Vessel, pid: persistentId);

        /* Null when the vantage is well formed, else why not. Whether the named
           site, station, body or craft exists is checked in game. */
        public string Invalid()
        {
            switch (Kind)
            {
                case CommandVantageKind.LaunchSite:
                    if (!Finite(Distance) || Distance < 0) return "distance must be a finite number of metres, 0 or more";
                    if (!Finite(Bearing)) return "bearing must be finite";
                    if (!Finite(Height)) return "height must be finite";
                    return null;
                case CommandVantageKind.Station:
                    if (Name.Length == 0) return "a station vantage needs the station's node name";
                    if (!Finite(Height)) return "height must be finite";
                    return null;
                case CommandVantageKind.Surface:
                    if (Body.Length == 0) return "a surface vantage needs a body name";
                    if (!Finite(Latitude) || Latitude < -90 || Latitude > 90) return "latitude must be within -90..90";
                    if (!Finite(Longitude)) return "longitude must be finite";
                    if (!Finite(Height)) return "height must be finite";
                    return null;
                case CommandVantageKind.Vessel:
                    return PersistentId == 0 ? "a vessel vantage needs the craft's persistentId" : null;
            }
            return "unknown vantage kind";
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        public override string ToString()
        {
            var inv = CultureInfo.InvariantCulture;
            switch (Kind)
            {
                case CommandVantageKind.LaunchSite:
                    return string.Format(inv, "LaunchSite('{0}' distance={1:F0} bearing={2:F0} height={3:F1})",
                        Name.Length > 0 ? Name : "launch site of the active craft", Distance, Bearing, Height);
                case CommandVantageKind.Station:
                    return string.Format(inv, "Station('{0}' height={1:F1})", Name, Height);
                case CommandVantageKind.Surface:
                    return string.Format(inv, "Surface({0} {1:F4},{2:F4} height={3:F1})", Body, Latitude, Longitude, Height);
                default:
                    return string.Format(inv, "Vessel({0})", PersistentId);
            }
        }
    }

    /* Default feed names for created command cameras. */
    public static class CommandCameraNaming
    {
        public static string FeedName(string requested, CommandVantageKind kind, string siteNodeName, string siteDisplayName,
            string body)
        {
            if (!string.IsNullOrEmpty(requested) && requested.Trim().Length > 0) return requested.Trim();
            string display = string.IsNullOrEmpty(siteDisplayName) ? siteNodeName ?? "" : siteDisplayName;
            switch (kind)
            {
                case CommandVantageKind.LaunchSite:
                    return siteNodeName == "LaunchPad" ? "Launchpad camera" : $"{display} camera";
                case CommandVantageKind.Station:
                    return $"{display} tracking";
                case CommandVantageKind.Vessel:
                    return $"{display} camera";
                default:
                    return string.IsNullOrEmpty(body) ? "Surface camera" : $"{body} surface camera";
            }
        }
    }
}
