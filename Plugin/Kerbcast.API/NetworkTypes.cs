namespace Kerbcast.Api
{
    /**
     * <summary>
     * One place a tracking network camera may show its craft from (see <see cref="ICommand.CreateNetworkCamera"/>).
     * Immutable; build one with the factory methods. The vantage has the lens, zoom and optics a command camera
     * standing there alone would have: being in a network changes none of them.
     * </summary>
     */
    public sealed class NetworkVantage
    {
        public const double DefaultStationHeight = 30.0;
        public const double DefaultLaunchSiteDistance = 500.0;
        public const double DefaultLaunchSiteBearing = 180.0;
        public const double DefaultLaunchSiteHeight = 5.0;
        public const double DefaultSurfaceHeight = 5.0;

        public SiteKind Kind { get; }

        /** <summary>A station's CommNet node name ("KSC"), or a launch site's name; empty otherwise.</summary> */
        public string Name { get; }

        /** <summary>Replaces the vantage's own name wherever the camera names it. Null keeps the site's name. Cut to 64 characters, since it rides every frame.</summary> */
        public string Label { get; }

        /** <summary>Where the camera stands when no vantage can see the craft. The first one marked counts.</summary> */
        public bool Fallback { get; }

        /** <summary>Metres above the ground below the lens (for a station, above the station). Unused for a craft.</summary> */
        public double Height { get; }

        /** <summary>A launch site vantage's metres from the site, and the compass bearing from the site to the lens.</summary> */
        public double Distance { get; }
        public double Bearing { get; }

        /** <summary>A surface vantage's body (KSP's flightGlobalsIndex) and position in degrees.</summary> */
        public int BodyIndex { get; }
        public double Latitude { get; }
        public double Longitude { get; }

        /** <summary>A craft vantage's Vessel.persistentId.</summary> */
        public uint PersistentId { get; }

        NetworkVantage(SiteKind kind, string name, string label, bool fallback, double height,
            double distance = 0, double bearing = 0, int body = -1, double lat = 0, double lon = 0, uint pid = 0)
        {
            Kind = kind;
            Name = name ?? "";
            Label = label;
            Fallback = fallback;
            Height = height;
            Distance = distance;
            Bearing = bearing;
            BodyIndex = body;
            Latitude = lat;
            Longitude = lon;
            PersistentId = pid;
        }

        /** <summary>At a CommNet ground station, by its node name. A station the game has turned off reports "disabled".</summary> */
        public static NetworkVantage Station(string nodeName, string label = null, double height = DefaultStationHeight) =>
            new NetworkVantage(SiteKind.Station, nodeName, label, false, height);

        /** <summary>Beside a launch site ("LaunchPad", "Runway" or a modded site), as the Launchpad camera stands.</summary> */
        public static NetworkVantage LaunchSite(string site, string label = null, double distance = DefaultLaunchSiteDistance,
            double bearing = DefaultLaunchSiteBearing, double height = DefaultLaunchSiteHeight) =>
            new NetworkVantage(SiteKind.LaunchSite, site, label, false, height, distance, bearing);

        /** <summary>Any point on a body's surface.</summary> */
        public static NetworkVantage Surface(int bodyIndex, double latitude, double longitude, string label = null,
            double height = DefaultSurfaceHeight) =>
            new NetworkVantage(SiteKind.Surface, null, label, false, height, body: bodyIndex, lat: latitude, lon: longitude);

        /** <summary>Riding a craft. A craft loaded in the scene, or the craft being watched, cannot be used while it is.</summary> */
        public static NetworkVantage Vessel(uint persistentId, string label = null) =>
            new NetworkVantage(SiteKind.Vessel, null, label, false, 0, pid: persistentId);

        /** <summary>This vantage, marked as the network's fallback.</summary> */
        public NetworkVantage AsFallback() =>
            new NetworkVantage(Kind, Name, Label, true, Height, Distance, Bearing, BodyIndex, Latitude, Longitude, PersistentId);

        public override string ToString()
        {
            string what;
            switch (Kind)
            {
                case SiteKind.Station: what = $"Station({Name})"; break;
                case SiteKind.LaunchSite: what = $"LaunchSite({Name})"; break;
                case SiteKind.Surface: what = $"Surface(body {BodyIndex}, {Latitude}, {Longitude})"; break;
                default: what = $"Vessel({PersistentId})"; break;
            }
            return what + (Label != null ? $" as '{Label}'" : "") + (Fallback ? " fallback" : "");
        }
    }

    /** <summary>How a tracking network holds after a cut. Every value finite and 0 or more.</summary> */
    public sealed class NetworkOptions
    {
        /** <summary>Game seconds the camera stays at a vantage after cutting to it, before a clearer one may take over.</summary> */
        public double MinHoldUt { get; set; } = 10;

        /** <summary>Real seconds it stays as well, so time warp cannot make it cut on every frame.</summary> */
        public double MinHoldRealSeconds { get; set; } = 3;

        /** <summary>How much clearer, in stops (one stop is twice as sharp), another vantage must be to take over.</summary> */
        public double MarginStops { get; set; } = 0.5;

        public override string ToString() => $"NetworkOptions(hold={MinHoldUt}s, real={MinHoldRealSeconds}s, margin={MarginStops})";
    }

    /** <summary>One vantage of a tracking network as its last scoring pass left it, from <see cref="ICommand.NetworkStatus"/>.</summary> */
    public sealed class NetworkVantageStatus
    {
        /** <summary>The vantage's site key, as <see cref="CommandFeed.SiteKey"/> names it while the camera stands there.</summary> */
        public string SiteKey { get; set; }

        /** <summary>The vantage's label: the host's, or the site's own name.</summary> */
        public string Label { get; set; }

        /** <summary>What kind of place the vantage is, as <see cref="CommandFeed.SiteKind"/> would say were the camera there. Added in API 1.5.</summary> */
        public SiteKind SiteKind { get; set; }

        /**
         * <summary>
         * A craft vantage's Vessel.persistentId, following its craft through docking as <see cref="CommandFeed.VantagePersistentId"/>
         * does; 0 for any other kind. With <see cref="SiteKey"/> and <see cref="SiteKind"/> it lets a host bind every
         * vantage's site, not only the one on air. Added in API 1.5.
         * </summary>
         */
        public uint VantagePersistentId { get; set; }

        /** <summary>The camera stands here now.</summary> */
        public bool Active { get; set; }

        public bool Fallback { get; set; }

        /**
         * <summary>
         * "ok", or why the vantage cannot show the craft: "occluded", "belowHorizon", "tooFar", "unknown" (its site does
         * not know where the craft is), "loaded", "subject", "gone", "disabled" (a ground station the game has turned
         * off), "unresolved", "unplaced" (a ridden craft with no position now), "pending" (not scored yet) or
         * "noSubject". New codes may appear. The active vantage reading anything but "ok" means no vantage can see the
         * craft. When no vantage can even stand in, the feed is blank ("no vantage").
         * </summary>
         */
        public string State { get; set; }

        /** <summary>Picture quality in stops, higher is clearer; NaN unless State is "ok".</summary> */
        public double Score { get; set; }
    }
}
