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

        /**
         * <summary>
         * Beside a launch site ("LaunchPad", "Runway" or a modded site), as the Launchpad camera stands. Null or empty
         * <paramref name="site"/> takes the launch site of the active craft in flight, or of the craft the camera
         * watches at the Space Center, and the stock launch pad when that craft has none.
         * </summary>
         */
        public static NetworkVantage LaunchSite(string site, string label = null, double distance = DefaultLaunchSiteDistance,
            double bearing = DefaultLaunchSiteBearing, double height = DefaultLaunchSiteHeight) =>
            new NetworkVantage(SiteKind.LaunchSite, site, label, false, height, distance, bearing);

        /** <summary>Any point on a body's surface.</summary> */
        public static NetworkVantage Surface(int bodyIndex, double latitude, double longitude, string label = null,
            double height = DefaultSurfaceHeight) =>
            new NetworkVantage(SiteKind.Surface, null, label, false, height, body: bodyIndex, lat: latitude, lon: longitude);

        /** <summary>Riding a craft. While the craft is in the scene its own parts are hidden from this camera alone. Skipped while it is the craft being watched.</summary> */
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

    /** <summary>One ground station a command camera can stand at, from <see cref="ICommand.GroundStations"/>.</summary> */
    public sealed class GroundStationInfo
    {
        /** <summary>The station's CommNet node name: the key <see cref="NetworkVantage.Station"/> takes, and its site key.</summary> */
        public string NodeName { get; set; }

        /** <summary>The name the game shows for it.</summary> */
        public string DisplayName { get; set; }

        /** <summary>The body it stands on, as KSP's flightGlobalsIndex.</summary> */
        public int BodyIndex { get; set; }

        public double Latitude { get; set; }
        public double Longitude { get; set; }

        /** <summary>Metres above sea level of the ground (or the sea) at the station, which a station vantage's height is added to.</summary> */
        public double Altitude { get; set; }

        /** <summary>The game has the station switched on. A vantage at one switched off reads "disabled".</summary> */
        public bool Enabled { get; set; }

        /** <summary>Why the game has it switched off, a short text fit to show a player; null while it is on. Added in API 1.5.</summary> */
        public string DisabledReason { get; set; }

        public override string ToString() => $"GroundStation({NodeName} '{DisplayName}' body {BodyIndex} {Latitude}, {Longitude}, {Altitude} m{(Enabled ? "" : " off")})";
    }

    /** <summary>Whether a vantage can see a point at one instant, from <see cref="ICommand.LineOfSight"/>.</summary> */
    public readonly struct LineOfSightResult
    {
        public LineOfSightResult(bool known, bool visible, string occluder, bool belowHorizon, double rangeM, bool disabled = false,
            double resolutionRad = double.NaN)
        {
            Known = known;
            Visible = visible;
            Occluder = occluder;
            BelowHorizon = belowHorizon;
            RangeM = rangeM;
            Disabled = disabled;
            ResolutionRad = resolutionRad;
        }

        /** <summary>False when the vantage cannot be placed at the instant, or the point's body is unknown; nothing else is then meaningful.</summary> */
        public bool Known { get; }

        /** <summary>No body is in the way.</summary> */
        public bool Visible { get; }

        /** <summary>The first body in the way, by its bodyName, or null.</summary> */
        public string Occluder { get; }

        /** <summary>The body in the way is the one the vantage stands on or orbits: its own horizon or skyline.</summary> */
        public bool BelowHorizon { get; }

        /** <summary>Metres from the vantage to the point; NaN when not Known.</summary> */
        public double RangeM { get; }

        /**
         * <summary>
         * The vantage is a ground station the game has switched off, so it sees nothing (Visible false, no Occluder),
         * as a network vantage there reads "disabled". Known stays true and RangeM is set.
         * </summary>
         */
        public bool Disabled { get; }

        /**
         * <summary>
         * The smallest angle, in radians, a command camera at the vantage resolves toward the point at its tightest
         * zoom: one pixel there (see <see cref="VantageResolution.PixelRad"/>), or, when deep-space optics are drawn,
         * the blur of its lens and of the air between, if wider. The air is taken at the vantage and toward the point,
         * as the camera's own optics take it. A feed's <see cref="ICommand.SetOptics"/> override is not in it.
         * RangeM times this is the smallest size on the point the vantage could tell apart. NaN when not Known.
         * Added in API 1.5.
         * </summary>
         */
        public double ResolutionRad { get; }

        public override string ToString() =>
            !Known ? "LineOfSight(unknown)" : Disabled ? $"LineOfSight(station switched off, {RangeM} m)" : Visible ? $"LineOfSight(visible, {RangeM} m, resolves {ResolutionRad} rad)" : $"LineOfSight(blocked by {Occluder}{(BelowHorizon ? ", below horizon" : "")}, {RangeM} m)";
    }

    /**
     * <summary>
     * How fine a detail a command camera at one vantage can show, from <see cref="ICommand.VantageResolution"/>:
     * the limits it renders with, by the same rules as a camera standing there. They can change within a scene (a
     * Tracking Station upgrade), so read them again rather than keeping them.
     * </summary>
     */
    public sealed class VantageResolution
    {
        /** <summary>False when the vantage does not resolve to a place now; nothing else is then meaningful.</summary> */
        public bool Known { get; set; }

        /**
         * <summary>
         * The narrowest vertical field of view the camera there zooms to, degrees. With OpticsOn the lens sets it, as
         * in a telescope: no narrower than where its own blur spans 2 pixels of the frame, the best case with no air in
         * the way (a camera looking through air stops a little wider, by the seeing toward its aim). NaN when not Known.
         * </summary>
         */
        public double FovMinDeg { get; set; } = double.NaN;

        /** <summary>The frame a command camera renders at full quality, in pixels. Under load it may render smaller for a while.</summary> */
        public int FrameWidthPx { get; set; }
        public int FrameHeightPx { get; set; }

        /** <summary>The angle one pixel covers at the frame's centre at FovMinDeg, radians: FovMinDeg spans FrameHeightPx. NaN when not Known.</summary> */
        public double PixelRad { get; set; } = double.NaN;

        /** <summary>Deep-space optics are on and drawn: the player's setting is on and the game can draw them.</summary> */
        public bool OpticsOn { get; set; }

        /**
         * <summary>
         * The lens's own blur (FWHM) in arcseconds while OpticsOn, Kerbcast's own for the vantage now (the Tracking
         * Station level, or a craft's space lens, see LensSource), before any feed's <see cref="ICommand.SetOptics"/> override; NaN otherwise. Air adds to it
         * from the ground: <see cref="LineOfSightResult.ResolutionRad"/> has the whole blur toward a point.
         * </summary>
         */
        public double LensArcsec { get; set; } = double.NaN;

        /**
         * <summary>
         * The smallest angle it resolves with no air in the way, radians: PixelRad, or the lens blur when OpticsOn and
         * wider. No sight line from the vantage resolves finer. NaN when not Known.
         * </summary>
         */
        public double BestResolutionRad { get; set; } = double.NaN;

        /**
         * <summary>
         * Where the vantage's lens comes from: the Tracking Station, the launch site's fixed lens, or a craft's space
         * lens. Set whether or not OpticsOn; the lens changes the picture only while it is. Unknown when not Known.
         * </summary>
         */
        public LensSource LensSource { get; set; }

        /**
         * <summary>
         * The lens's level: the Tracking Station level (1 based) for TrackingStation and LaunchSite, or the space-lens
         * step (1 based, in Kerbcast's own order) for SpaceLens. 0 when not Known.
         * </summary>
         */
        public int LensLevel { get; set; }

        /**
         * <summary>
         * A short name for the lens a player would recognise: "Tracking Station level 2", or for a space lens the title
         * of the tech node that unlocked it, in the player's language. Null when not Known.
         * </summary>
         */
        public string LensName { get; set; }

        public override string ToString() =>
            !Known ? "VantageResolution(unknown)"
                : $"VantageResolution(fovMin={FovMinDeg} deg, {FrameWidthPx}x{FrameHeightPx}, pixel={PixelRad} rad, optics={(OpticsOn ? LensArcsec + " arcsec" : "off")}, best={BestResolutionRad} rad, lens={LensSource} {LensLevel} '{LensName}')";
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
         * not know where the craft is), "subject", "gone", "disabled" (a ground station the game has turned
         * off), "unresolved", "unplaced" (a ridden craft with no position now), "pending" (not scored yet) or
         * "noSubject". New codes may appear. The active vantage reading anything but "ok" means no vantage can see the
         * craft. When no vantage can even stand in, the feed is blank ("no vantage").
         * </summary>
         */
        public string State { get; set; }

        /** <summary>Picture quality in stops, higher is clearer; NaN unless State is "ok".</summary> */
        public double Score { get; set; }

        /** <summary>The name of the body hiding the craft while State is "occluded", else null. Added in API 1.5.</summary> */
        public string Occluder { get; set; }
    }
}
