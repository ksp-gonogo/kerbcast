using System.Collections.Generic;
using Kerbcast.Api;
using UnityEngine;

namespace Kerbcast
{
    /* Immutable per-camera snapshot handed to the kOS add-on. Plain data +
       the owning Part; no kOS types (Kerbcast.dll must not link kOS). */
    [System.Obsolete("Use Kerbcast.Api.CameraView from Kerbcast.API.dll")]
    public sealed class KerbcastCameraView
    {
        public uint FlightId;
        /* "part", "kerbal" or "command", as the wire's CameraKind. */
        public string Kind;
        public uint PartFlightId;
        public string CameraName;
        public string PartName;
        public string PartTitle;
        public bool SupportsZoom;
        public bool SupportsPan;
        public float Fov, FovMin, FovMax;
        public float PanYaw, PanPitch;
        public float PanYawMin, PanYawMax, PanPitchMin, PanPitchMax;
        // World-space optical axis (unit forward) of the capture camera.
        public float BoresightX, BoresightY, BoresightZ;
        // Lens position relative to the vessel CoM, in Unity world axes — the
        // same frame kOS uses for TARGET:POSITION, so a script can do
        // (TARGET:POSITION - cam:POSITION) directly.
        public float PositionX, PositionY, PositionZ;
        // Current auto-track mode (0=none/1=active-vessel/2=target).
        public int TrackMode;
        public global::Part Part;
    }

    /* The in-process control seam that predates Kerbcast.API. The members
       Kerbcast.API covers forward to its implementation and are obsolete;
       the rest stay until the API covers them. */
    public static class KerbcastControl
    {
        [System.Obsolete("Use Kerbcast.Api.KerbcastApi.Instance != null from Kerbcast.API.dll")]
        public static bool IsActive => KerbcastApi.Instance != null;

        /* Whether the video sidecar process is currently alive. False out of
           flight (no host yet) and false once the process dies, which is the
           state where every feed goes black while the mod still looks healthy. */
        [System.Obsolete("Use Kerbcast.Api.KerbcastApi.Instance.SidecarAlive from Kerbcast.API.dll")]
        public static bool SidecarAlive => KerbcastApiImpl.SidecarAliveNow;

        /* Host the feeds and the signalling endpoint listen on: what the
           running sidecar was launched with, or what settings.cfg configures
           before the first flight starts it. */
        [System.Obsolete("Use Kerbcast.Api.KerbcastApi.Instance.BindAddress from Kerbcast.API.dll")]
        public static string BindAddress => KerbcastApiImpl.BindAddressNow;

        [System.Obsolete("Use Kerbcast.Api.KerbcastApi.Instance.Port from Kerbcast.API.dll")]
        public static int Port => KerbcastApiImpl.PortNow;

        /* True when only this computer can reach the feeds. */
        [System.Obsolete("Use Kerbcast.Api.KerbcastApi.Instance.BindIsLoopback from Kerbcast.API.dll")]
        public static bool BindIsLoopback => KerbcastSettings.IsLoopback(KerbcastApiImpl.BindAddressNow);

        [System.Obsolete("Use Kerbcast.Api.ICameras.CamerasFor(uint vesselPersistentId) from Kerbcast.API.dll")]
        public static IReadOnlyList<KerbcastCameraView> CamerasFor(global::Vessel vessel)
        {
            var result = new List<KerbcastCameraView>();
            var cams = KerbcastCore.Instance?.Cameras;
            if (cams == null || vessel == null) return result;
            for (int i = 0; i < cams.Count; i++)
                if (KerbcastApiImpl.IsPartCamera(cams[i], out var c) && c.Vessel == vessel)
                    result.Add(ToView(c));
            return result;
        }

        [System.Obsolete("Use Kerbcast.Api.ICameras.View from Kerbcast.API.dll")]
        public static KerbcastCameraView ViewOf(uint flightId)
        {
            var c = Find(flightId);
            return c == null ? null : ToView(c);
        }

        [System.Obsolete("Use Kerbcast.Api.ICameras.SetFov from Kerbcast.API.dll")]
        public static bool SetFov(uint flightId, float fov) => KerbcastApiImpl.SetFovCore(flightId, fov);

        [System.Obsolete("Use Kerbcast.Api.ICameras.SetPan from Kerbcast.API.dll")]
        public static bool SetPan(uint flightId, float yaw, float pitch) => KerbcastApiImpl.SetPanCore(flightId, yaw, pitch);

        /* aimPoint is a kOS-frame vector (ship-relative, e.g. TARGET:POSITION),
           matching what KerbcastCameraView.Position reports. */
        [System.Obsolete("Use Kerbcast.Api.ICameras.AimAt from Kerbcast.API.dll")]
        public static bool AimAt(uint flightId, Vector3 aimPoint) => KerbcastApiImpl.AimAtCore(flightId, aimPoint);

        /* mode: 0=none, 1=active-vessel, 2=target. Only pan+zoom cameras
           honour a set; an unknown camera reads 0. */
        [System.Obsolete("Use Kerbcast.Api.ICameras.SetTrackMode from Kerbcast.API.dll")]
        public static bool SetTrackMode(uint flightId, int mode) => KerbcastApiImpl.SetTrackModeCore(flightId, mode);

        [System.Obsolete("Use Kerbcast.Api.ICameras.GetTrackMode from Kerbcast.API.dll")]
        public static int GetTrackMode(uint flightId) => KerbcastApiImpl.GetTrackModeCore(flightId);

        /* Takes the lock on every command feed's aim in this scene for `owner`:
           the same lock as Kerbcast.API's ICommand.TakeLock, without its
           options. While it is held, SetCommandAim accepts only calls that pass
           `owner`; everyone else is refused, browser clients included. Taking
           it returns every feed to its default aim, and under the lock a
           feed's default has no subject: it holds pose. The lock holds until
           ClearCommandAuthority or a scene change, and never times out: take
           it again on each scene load. It fails closed: if `owner` makes no
           call (this, SetCommandAim with its owner, or SetCommandBlank) for 5 s
           of REAL time, every command feed blanks "Authority not responding", but
           the lock stays held and everyone else stays refused, so a stalled
           holder that delays commands can not be bypassed. Real time keeps
           counting while the game is paused or warping, so keep calling as a
           heartbeat then too. Calling again with the same owner is a
           heartbeat; a different owner is refused (false). False also when
           Kerbcast is not running in this scene. */
        public static bool SetCommandAuthority(string owner)
        {
            var auth = KerbcastCore.Instance?.CommandAuthority;
            if (auth == null || string.IsNullOrEmpty(owner) || !ReservedAssemblies.SafeValue(owner)) return false;
            /* A renewal keeps the options the holder took the lock with. */
            if (auth.Lock.Holds(owner))
            {
                auth.Heartbeat(Time.realtimeSinceStartup);
                return true;
            }
            var taken = TakeLockCore(owner, Api.LockOptions.None, null);
            return taken == Api.LockResult.Taken || taken == Api.LockResult.Renewed;
        }

        /* Takes or renews the scene's one lock. Taking it starts a new tenure,
           returns every feed to its default aim and leaves each default with
           no subject. */
        internal static Api.LockResult TakeLockCore(string owner, Api.LockOptions options, string silentText)
        {
            var core = KerbcastCore.Instance;
            if (core == null) return Api.LockResult.NotRunning;
            var result = core.CommandAuthority.Lock.Take(owner, options, silentText, Time.realtimeSinceStartup);
            if (result == Api.LockResult.HeldByOther) return result;
            /* A reserved scene starts with the silence text the owner last took the lock with. */
            LockReservationStore.Reservation.NoteSilentText(owner, silentText);
            if (result == Api.LockResult.Taken) ResetFeeds(core);
            return result;
        }

        /* Releases the lock if `owner` holds it: truth again, and every feed back to its default aim.
           The player's release logs its own lines, so `byPlayer` skips this one. */
        internal static bool ReleaseLockCore(string owner, bool byPlayer = false)
        {
            var core = KerbcastCore.Instance;
            var auth = core?.CommandAuthority;
            if (auth == null || !auth.Lock.Holds(owner)) return false;
            if (!byPlayer) Debug.Log($"[Kerbcast][CMDCAM] command lock released: {owner}");
            auth.Reset();
            ResetFeeds(core);
            return true;
        }

        /* The holder's signal speed, which the light-time history reads. */
        internal static bool SetSignalSpeedCore(string owner, double? metresPerSecond)
        {
            var auth = KerbcastCore.Instance?.CommandAuthority;
            if (auth == null) return false;
            if (!auth.Lock.SetSignalSpeed(owner, metresPerSecond, Time.realtimeSinceStartup)) return false;
            LightHistoryHost.History.SetSignalSpeed(metresPerSecond);
            return true;
        }

        /* Every aim dropped, and every feed's request resolved again for what the lock now allows. */
        static void ResetFeeds(KerbcastCore core)
        {
            var cams = core.Cameras;
            for (int i = 0; i < cams.Count; i++)
                if (cams[i] is KerbcastCamera c && c.Rig is CommandCameraRig gr) gr.SetAim(null, null);
            core.CommandProvider?.ClearPendingAims();
            core.CommandProvider?.RefreshSubjects();
        }

        /* Releases `owner`'s lock. Every command feed returns to its default
           aim: no aim set before or during the lock is restored. Ignored for
           any other owner. */
        public static void ClearCommandAuthority(string owner) => ReleaseLockCore(owner);

        /* The command feeds in this scene, empty when there are none. */
        public static IReadOnlyList<CommandFeedView> CommandFeeds()
        {
            var result = new List<CommandFeedView>();
            var cams = KerbcastCore.Instance?.Cameras;
            if (cams == null) return result;
            double ut = Planetarium.GetUniversalTime();
            int level = CommandCameraTargets.TrackingLevel();
            for (int i = 0; i < cams.Count; i++)
            {
                if (cams[i] is KerbcastCamera c && c.Rig is CommandCameraRig gr)
                {
#pragma warning disable CS0618 // StationId stays filled for callers built against 1.11
                    result.Add(new CommandFeedView
                    {
                        FlightId = c.FlightId,
                        StationNodeName = gr.StationNodeName,
                        StationId = CameraId.SiteHashKey(gr.StationNodeName),
                        SiteKey = gr.Site.NodeName,
                        SiteKind = gr.Site.Kind,
                        VantagePersistentId = gr.Site.VantagePersistentId,
                        DisplayName = gr.StationDisplayName,
                        Aim = gr.Aim,
                        SubjectPersistentId = gr.SubjectPersistentId,
                        RangeM = CommandCameraTargets.RangeTo(gr.Site, gr.SubjectPersistentId, ut),
                        TrackingLevel = level,
                        FramingFits = gr.FramingFits,
                        Created = KerbcastCore.Instance.CommandProvider?.IsCreated(c.FlightId) ?? false,
                    });
#pragma warning restore CS0618
                }
            }
            return result;
        }

        /* Every craft command feed `flightId` could watch, eligible first, then
           nearest first; empty when it is not a command feed. Eligibility only
           reports the tracking network's rules (career, with the tracking rules
           difficulty setting on); a Vessel aim accepts any of them. */
        public static IReadOnlyList<CommandTargetView> CommandTargets(uint flightId)
        {
            var c = Find(flightId);
            if (c == null || !(c.Rig is CommandCameraRig gr)) return new List<CommandTargetView>();
            return CommandCameraTargets.List(gr.Site, Planetarium.GetUniversalTime(), gr.FovMax);
        }

        /* Points command feed `flightId` by `aim`, or returns it to its default
           with null (the active craft in flight, the configured
           CommandCameraTargetName at the Space Center). The aim's subject is
           drawn wherever it truly is, so an aim off the craft shows it off
           centre; every camera-axis reading (occlusion, sunlight, range,
           bracket) is taken at the aimed point. Works on a created camera that
           is not built yet. While a lock is held (SetCommandAuthority) only
           calls passing its owner are accepted, and each is a heartbeat even
           when it returns false. False when the lock refuses the call, the
           feed is not a command feed, a craft it names does not exist (one
           docked into another no longer does), its body is unknown, or a
           number is not finite or out of range (a negative framing margin, an
           elevation outside -90..90). */
        public static bool SetCommandAim(uint flightId, CommandAim aim, string owner = null)
            => SetAimCore(flightId, aim, owner) == AimResult.Ok;

        /* SetCommandAim with the reason it refused. */
        internal static AimResult SetAimCore(uint flightId, CommandAim aim, string owner)
        {
            var core = KerbcastCore.Instance;
            if (core == null) return AimResult.NotRunning;
            var auth = core.CommandAuthority;
            if (auth != null && auth.Registered)
            {
                if (!string.Equals(owner, auth.Owner, System.StringComparison.Ordinal)) return AimResult.NotLockHolder;
                auth.Heartbeat(Time.realtimeSinceStartup);
            }
            var gr = Find(flightId)?.Rig as CommandCameraRig;
            var provider = core.CommandProvider;
            if (gr == null && (provider == null || !provider.IsPending(flightId))) return AimResult.NotCommandFeed;
            Orbit orbit = null;
            if (aim != null)
            {
                string siteKey = gr != null ? gr.Site.NodeName : provider.PendingSiteKey(flightId);
                var resolved = TryResolveAim(flightId, siteKey, gr, aim, out orbit);
                if (resolved != AimResult.Ok) return resolved;
            }
            if (gr != null) gr.SetAim(aim, orbit);
            else provider.SetPendingAim(flightId, aim, orbit);
            return AimResult.Ok;
        }

        /* Replaces `owner`'s anchors on camera `flightId`, or clears them with
           null or an empty list. Each anchor marks a part, a craft, a point on
           a body, a direction or a moving point (see AnchorSpec's factories); the game projects
           every one through the camera that rendered each frame and sends the
           results with that frame to every browser client that asked for
           anchors, tagged with `owner`. Anchors are display, not commands: the
           command lock does not apply. They last until replaced, cleared with
           ClearAnchors, or the scene changes. False, with nothing changed, when
           Kerbcast is not running here, the camera is unknown, `owner` is
           empty or reserved ("kerbcast", or starting "peer:"), there are more
           than 16, two share an id, or one is malformed (`error` says which). */
        public static bool SetAnchors(uint flightId, IList<AnchorSpec> anchors, string owner, out string error)
        {
            var core = KerbcastCore.Instance;
            if (core == null) { error = "Kerbcast is not running in this scene"; return false; }
            if (anchors != null && anchors.Count > 0 && Find(flightId) == null)
            {
                error = $"no camera with flightId {flightId}";
                return false;
            }
            return core.Anchors.Set(owner, flightId, anchors, out error);
        }

        public static bool SetAnchors(uint flightId, IList<AnchorSpec> anchors, string owner)
        {
            bool ok = SetAnchors(flightId, anchors, owner, out string error);
            if (!ok) Debug.LogWarning($"[Kerbcast][ANCHOR] anchors from {owner} for cam={flightId} refused: {error}");
            return ok;
        }

        /* While `owner` holds the command lock, whether command feeds send
           Kerbcast's subject anchor ("subject", owner "kerbcast"). It is on by
           default and is always observed-only: a position only while the
           picture shows the craft, never its range. Each call is a heartbeat.
           False when `owner` does not hold the lock. The setting goes with the
           lock. */
        public static bool SetSubjectAnchor(bool visible, string owner)
        {
            var auth = HeldBy(owner);
            if (auth == null) return false;
            auth.SubjectAnchorVisible = visible;
            return true;
        }

        /* While `owner` holds the command lock, how much of browser clients'
           anchor results every camera sends. ObservedOnly (the default under
           the lock) sends a position and size only while the anchor is in
           frame and not occluded, and never its range or angle off axis; Full
           sends everything. Replaces any per-camera policy. Anchors set in game
           are never limited. Each call is a heartbeat. False when `owner` does
           not hold the lock. Unrestricted again once the lock clears. */
        public static bool SetAnchorPolicy(AnchorPolicy policy, string owner)
        {
            var auth = HeldBy(owner);
            if (auth == null) return false;
            auth.SetAnchorPolicy(policy);
            return true;
        }

        /* SetAnchorPolicy for one camera. */
        public static bool SetAnchorPolicy(uint flightId, AnchorPolicy policy, string owner)
        {
            var auth = HeldBy(owner);
            if (auth == null) return false;
            auth.SetAnchorPolicy(flightId, policy);
            return true;
        }

        /* The authority when `owner` holds the lock, after counting the call as a heartbeat. */
        private static CommandAuthority HeldBy(string owner)
        {
            var auth = KerbcastCore.Instance?.CommandAuthority;
            if (auth == null || !auth.Registered || !string.Equals(auth.Owner, owner, System.StringComparison.Ordinal)) return null;
            auth.Heartbeat(Time.realtimeSinceStartup);
            return auth;
        }

        /* Forgets every anchor `owner` set, on every camera. */
        public static void ClearAnchors(string owner)
        {
            KerbcastCore.Instance?.Anchors.Clear(owner);
        }

        /* The holder of the command lock, or null when nobody holds it. */
        internal static string CommandAuthorityOwner => KerbcastCore.Instance?.CommandAuthority?.Owner;

        /* Creates a command camera standing at `vantage` for the rest of this
           scene (flight or Space Center), and returns the flightId its feed
           will have, or 0 when Kerbcast is not running here, the vantage is
           malformed, a surface vantage names an unknown body, a vessel vantage
           names a craft that no longer exists, or `aim` is unusable (as for
           SetCommandAim). The feed appears once the vantage resolves, normally
           on the next frame; a launch site, station or craft that is not
           placed within 30 s drops the camera instead of standing it anywhere
           else. A vessel vantage rides the combined craft after a dock or claw
           grab; if its craft is destroyed or recovered the camera is removed. `name` replaces the default feed name
           ("Launchpad camera", "<site> camera", "<station> tracking",
           "<craft> camera" or "<body> surface camera"). It points by `aim`,
           or at the default craft (the active one in flight) when null. While
           a lock is held the aim is not applied and the camera starts at its
           default; the holder aims it with SetCommandAim, which works before
           it is built. */
        public static uint CreateCommandCamera(CommandVantage vantage, string name = null, CommandAim aim = null)
        {
            string holder = CommandAuthorityOwner;
            if (holder != null)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] command camera at {vantage?.ToString() ?? "null"} refused: {holder} holds the command lock; it creates cameras with its owner");
                return 0;
            }
            uint id = Create(vantage, name, aim, null, out string error);
            if (id == 0) Debug.LogWarning($"[Kerbcast][CMDCAM] command camera at {vantage?.ToString() ?? "null"} refused: {error}");
            return id;
        }

        /* CreateCommandCamera for the lock holder, whose aim applies; 0 for
           anyone else while the lock is held. A heartbeat for the holder. */
        public static uint CreateCommandCamera(CommandVantage vantage, string name, CommandAim aim, string owner)
        {
            var auth = KerbcastCore.Instance?.CommandAuthority;
            if (auth != null && auth.Registered && HeldBy(owner) == null)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] command camera at {vantage?.ToString() ?? "null"} refused: {auth.Owner} holds the command lock");
                return 0;
            }
            uint id = Create(vantage, name, aim, null, out string error, holderCall: true);
            if (id == 0) Debug.LogWarning($"[Kerbcast][CMDCAM] command camera at {vantage?.ToString() ?? "null"} refused: {error}");
            return id;
        }

        internal static uint Create(CommandVantage vantage, string name, CommandAim aim, string token, out string error,
            bool holderCall = false)
        {
            var core = KerbcastCore.Instance;
            var provider = core?.CommandProvider;
            if (provider == null)
            {
                error = "Kerbcast is not running in this scene";
                return 0;
            }
            Orbit orbit = null;
            if (aim != null && TryResolveAim(0, CommandCameraSite.KeyFor(vantage), null, aim, out orbit) != AimResult.Ok)
            {
                error = $"aim {aim} is unusable";
                return 0;
            }
            var auth = core.CommandAuthority;
            if (aim != null && auth != null && auth.Registered && !holderCall)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] {auth.Owner} holds the command lock; new camera starts at its default, not {aim}");
                aim = null;
                orbit = null;
            }
            return provider.Create(vantage, name, aim, orbit, token, core.CameraFlightIds(), out error);
        }

        /* Removes a command camera CreateCommandCamera (or a client) made,
           built or not; its feed ends. False for the settings-driven camera or
           an unknown id, so a repeated call is harmless. */
        public static bool DestroyCommandCamera(uint flightId)
        {
            if (CommandAuthorityOwner != null) return false;
            return KerbcastCore.Instance?.DestroyCommandCamera(flightId) ?? false;
        }

        /* DestroyCommandCamera for the lock holder; false for anyone else while the lock is held. A heartbeat for the holder. */
        public static bool DestroyCommandCamera(uint flightId, string owner)
        {
            var auth = KerbcastCore.Instance?.CommandAuthority;
            if (auth != null && auth.Registered && HeldBy(owner) == null) return false;
            return KerbcastCore.Instance?.DestroyCommandCamera(flightId) ?? false;
        }

        /* A client's aim as a CommandAim; call only once spec.Invalid() is null. */
        internal static CommandAim AimOf(CommandAimSpec spec)
        {
            switch (spec.Kind)
            {
                case "vessel":
                    return CommandAim.Vessel(spec.PersistentId, spec.FramingMarginM);
                case "orbit":
                    return CommandAim.Orbit(spec.Body, spec.Inclination, spec.Eccentricity, spec.SemiMajorAxis, spec.Lan,
                        spec.ArgumentOfPeriapsis, spec.MeanAnomalyAtEpoch, spec.Epoch, spec.SubjectPersistentId, spec.FramingMarginM);
                case "surface":
                    return CommandAim.Surface(spec.Body, spec.Latitude, spec.Longitude, spec.Altitude, spec.FramingMarginM);
                case "positionAt":
                    return CommandAim.PositionAt(spec.Body, spec.Ut,
                        new Vector3d(spec.Position[0], spec.Position[1], spec.Position[2]),
                        new Vector3d(spec.Velocity[0], spec.Velocity[1], spec.Velocity[2]),
                        spec.SubjectPersistentId, spec.FramingMarginM);
                case "direction":
                    return CommandAim.Direction(spec.Azimuth, spec.Elevation, spec.FramingMarginM);
            }
            return null;
        }

        /* Checks an aim's numbers, craft and body, building the conic for an
           Orbit aim. Its craft is checked against what the feed at `siteKey`
           knows, and against what `rig` (null for a camera not built yet)
           could frame. */
        static AimResult TryResolveAim(uint flightId, string siteKey, CommandCameraRig rig, CommandAim aim, out Orbit orbit)
        {
            orbit = null;
            if (!Finite(aim.FramingMarginM) || aim.FramingMarginM < 0) return AimResult.BadNumber;
            uint subject = aim.SubjectPersistentId;
            if (aim.Kind == CommandAimKind.Vessel && subject == 0) return AimResult.UnknownCraft;
            if (subject != 0)
            {
                var known = CommandCameraTargets.AimSubject(siteKey, rig?.Site, subject, aim.Kind == CommandAimKind.Vessel,
                    aim.AllowUncertain, aim.FramingMarginM, rig?.FovMax ?? 0);
                if (known != AimResult.Ok) return known;
            }
            switch (aim.Kind)
            {
                case CommandAimKind.Vessel:
                    return AimResult.Ok;
                case CommandAimKind.Direction:
                    return Finite(aim.Azimuth) && Finite(aim.Elevation) && aim.Elevation >= -90 && aim.Elevation <= 90
                        ? AimResult.Ok : AimResult.BadNumber;
                case CommandAimKind.PositionAt:
                    if (!(Finite(aim.Ut) && Finite(aim.BodyRelativePosition) && Finite(aim.BodyRelativeVelocity))) return AimResult.BadNumber;
                    break;
                case CommandAimKind.Surface:
                    if (!(Finite(aim.Latitude) && Finite(aim.Longitude) && Finite(aim.Altitude))) return AimResult.BadNumber;
                    break;
            }
            var body = FlightGlobals.GetBodyByName(aim.Body);
            if (body == null) return AimResult.UnknownBody;
            if (aim.Kind == CommandAimKind.PositionAt)
            {
                var p = aim.BodyRelativePosition;
                var v = aim.BodyRelativeVelocity;
                return KeplerPath.TryFromState(new DVec3(p.x, p.y, p.z), new DVec3(v.x, v.y, v.z), aim.Ut, body.gravParameter, out _)
                    ? AimResult.Ok : AimResult.BadNumber;
            }
            if (aim.Kind != CommandAimKind.Orbit) return AimResult.Ok;
            try
            {
                orbit = new Orbit(aim.Inclination, aim.Eccentricity, aim.SemiMajorAxis, aim.Lan,
                    aim.ArgumentOfPeriapsis, aim.MeanAnomalyAtEpoch, aim.Epoch, body);
                return AimResult.Ok;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] cam={flightId} aim {aim} rejected: {ex.Message}");
                return AimResult.BadNumber;
            }
        }

        /* Blanks a command feed with `reason` shown on it; null or empty clears.
           A blanked feed streams black frames, never the scene. Like the
           owner's other calls it is a heartbeat. False when `owner` does not
           hold the lock or the feed is not a command feed. */
        public static bool SetCommandBlank(uint flightId, string reason, string owner)
        {
            var auth = HeldBy(owner);
            if (auth == null || !IsCommandFeed(flightId)) return false;
            auth.OrdersFor(flightId).Blank = string.IsNullOrEmpty(reason) ? null : reason;
            return true;
        }

        /* Without an owner nobody can be the holder, so this always refuses, and says so once per session. */
        [System.Obsolete("Pass the lock holder's owner: SetCommandBlank(flightId, reason, owner)")]
        public static bool SetCommandBlank(uint flightId, string reason)
        {
            if (!_ownerlessBlankWarned)
            {
                _ownerlessBlankWarned = true;
                Debug.LogWarning("[Kerbcast][CMDCAM] SetCommandBlank without an owner is refused; call SetCommandBlank(flightId, reason, owner) as the lock holder");
            }
            return false;
        }

        static bool _ownerlessBlankWarned;

        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        static bool Finite(Vector3d v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        /* A built command feed, or a created one not built yet. */
        internal static bool IsCommandFeed(uint flightId)
        {
            var c = Find(flightId);
            if (c != null && c.Rig is CommandCameraRig) return true;
            return KerbcastCore.Instance?.CommandProvider?.IsPending(flightId) == true;
        }

        static KerbcastCamera Find(uint flightId) => KerbcastApiImpl.Find(flightId);

        [System.Obsolete("Builds the obsolete KerbcastCameraView")]
        static KerbcastCameraView ToView(KerbcastCamera c)
        {
            Vector3 fwd = c.BoresightWorld;
            // Lens position relative to the vessel CoM (falls back to raw world
            // if the vessel is gone), matching kOS's ship-relative frame.
            Vector3 pos = c.PositionWorld;
            var vessel = c.Mount.Vessel;
            if (vessel != null) pos -= vessel.CoM;
            var part = c.Mount.OwningPart;
            return new KerbcastCameraView
            {
                FlightId = c.FlightId,
                Kind = c.Rig is CommandCameraRig ? "command" : "part",
                PartFlightId = part != null ? part.flightID : 0u,
                CameraName = c.CameraName, PartName = c.PartName, PartTitle = c.PartTitle,
                SupportsZoom = c.SupportsZoom, SupportsPan = c.SupportsPan,
                Fov = c.Fov, FovMin = c.FovMin, FovMax = c.FovMax,
                PanYaw = c.PanYaw, PanPitch = c.PanPitch,
                PanYawMin = c.PanYawMin, PanYawMax = c.PanYawMax,
                PanPitchMin = c.PanPitchMin, PanPitchMax = c.PanPitchMax,
                BoresightX = fwd.x, BoresightY = fwd.y, BoresightZ = fwd.z,
                PositionX = pos.x, PositionY = pos.y, PositionZ = pos.z,
                TrackMode = c.GetTrackMode(),
                Part = part,
            };
        }
    }
}
