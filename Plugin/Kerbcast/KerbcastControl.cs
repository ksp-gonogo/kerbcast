using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /* Immutable per-camera snapshot handed to the kOS add-on. Plain data +
       the owning Part; no kOS types (Kerbcast.dll must not link kOS). */
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

    /* In-process control seam the KerbcastKos add-on calls. Resolves cameras
       through the live KerbcastCore and applies FOV/pan; the plugin already
       feeds resulting state back to the sidecar via global.status.json. */
    public static class KerbcastControl
    {
        public static bool IsActive => KerbcastCore.Instance != null;

        /* Whether the video sidecar process is currently alive. False out of
           flight (no host yet) and false once the process dies, which is the
           state where every feed goes black while the mod still looks healthy. */
        public static bool SidecarAlive => KerbcastSidecarHost.Instance?.SidecarRunning ?? false;

        /* Host the feeds and the signalling endpoint listen on: what the
           running sidecar was launched with, or what settings.cfg configures
           before the first flight starts it. */
        public static string BindAddress => BindSettings.BindAddress;

        public static int Port => BindSettings.Port;

        /* True when only this computer can reach the feeds. */
        public static bool BindIsLoopback => KerbcastSettings.IsLoopback(BindSettings.BindAddress);

        private static KerbcastSettings _configured;

        private static KerbcastSettings BindSettings =>
            KerbcastSidecarHost.Instance?.LaunchSettings ?? (_configured ?? (_configured = KerbcastSettings.Load()));

        public static IReadOnlyList<KerbcastCameraView> CamerasFor(global::Vessel vessel)
        {
            var result = new List<KerbcastCameraView>();
            var cams = KerbcastCore.Instance?.Cameras;
            if (cams == null || vessel == null) return result;
            for (int i = 0; i < cams.Count; i++)
            {
                // Kerbal cameras aren't exposed to kOS this stage.
                // Nor are command cameras, whose Vessel is just the tracked craft.
                if (cams[i] is KerbcastCamera c && c.Rig == null && c.Vessel == vessel)
                    result.Add(ToView(c));
            }
            return result;
        }

        public static KerbcastCameraView ViewOf(uint flightId)
        {
            var c = Find(flightId);
            return c == null ? null : ToView(c);
        }

        public static bool SetFov(uint flightId, float fov)
        {
            var c = Find(flightId); if (c == null) return false; c.SetFov(fov); return true;
        }

        public static bool SetPan(uint flightId, float yaw, float pitch)
        {
            var c = Find(flightId); if (c == null) return false; c.SetPanTarget(yaw, pitch); return true;
        }

        /* aimPoint is a kOS-frame vector (ship-relative, e.g. TARGET:POSITION),
           matching what KerbcastCameraView.Position reports. Convert to a Unity
           world point via the vessel CoM before handing it to the camera, whose
           AimAt works in world space. */
        public static bool AimAt(uint flightId, Vector3 aimPoint)
        {
            var c = Find(flightId); if (c == null) return false;
            var vessel = c.Mount.Vessel;
            Vector3 worldPoint = vessel != null ? (Vector3)vessel.CoM + aimPoint : aimPoint;
            c.AimAt(worldPoint);
            return true;
        }

        /* Auto-track (issue #6) as a SYNCHRONOUS kOS function: set/get the
           camera's track mode in-process, immediate, no sidecar round-trip.
           Set is optimistic-local (the camera aims at once) and stages an
           up-report the sidecar adopts as authoritative (so every browser
           reflects it); get returns the applied value now. Only pan+zoom
           cameras honour a set (mirrors the browser gate); a non-pan+zoom or
           unknown camera returns none (0) from get and false from set.
           mode: 0=none, 1=active-vessel, 2=target. */
        public static bool SetTrackMode(uint flightId, int mode)
        {
            var c = Find(flightId);
            if (c == null || !(c.SupportsPan && c.SupportsZoom)) return false;
            c.RequestTrackMode(mode);
            return true;
        }

        public static int GetTrackMode(uint flightId)
        {
            var c = Find(flightId);
            return c != null ? c.GetTrackMode() : 0;
        }

        /* Takes the lock on every command feed's aim in this scene for `owner`
           (gonogo). While it is held, SetCommandAim accepts only calls that pass
           `owner`; everyone else is refused, browser clients included. Aims
           already set stay until the owner changes them. The lock holds until
           ClearCommandAuthority or a scene change, and never times out: take
           it again on each scene load. It fails closed: if `owner` makes no
           call (this, SetCommandAim with its owner, or SetCommandBlank) for 5 s
           of REAL time, every command feed blanks "gonogo not responding", but
           the lock stays held and everyone else stays refused, so a stalled
           holder that delays commands can not be bypassed. Real time keeps
           counting while the game is paused or warping, so keep calling as a
           heartbeat then too. Calling again with the same owner is a
           heartbeat; a different owner is refused (false). False also when
           Kerbcast is not running in this scene. */
        public static bool SetCommandAuthority(string owner)
        {
            var auth = KerbcastCore.Instance?.CommandAuthority;
            return auth != null && auth.Register(owner, Time.realtimeSinceStartup);
        }

        /* Releases `owner`'s lock. Every command feed returns to its default
           aim: no aim set before or during the lock is restored. Ignored for
           any other owner. */
        public static void ClearCommandAuthority(string owner)
        {
            var core = KerbcastCore.Instance;
            var auth = core?.CommandAuthority;
            if (auth == null || !auth.Registered || !string.Equals(auth.Owner, owner, System.StringComparison.Ordinal)) return;
            auth.Clear(owner);
            var cams = core.Cameras;
            for (int i = 0; i < cams.Count; i++)
                if (cams[i] is KerbcastCamera c && c.Rig is CommandCameraRig gr && gr.Aim != null) gr.SetAim(null, null);
            core.CommandProvider?.ClearPendingAims();
        }

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
                    result.Add(new CommandFeedView
                    {
                        FlightId = c.FlightId,
                        StationNodeName = gr.StationNodeName,
                        StationId = CameraId.CommandCentreId(gr.StationNodeName),
                        DisplayName = gr.StationDisplayName,
                        Aim = gr.Aim,
                        SubjectPersistentId = gr.SubjectPersistentId,
                        RangeM = CommandCameraTargets.RangeTo(gr.Site, gr.SubjectPersistentId, ut),
                        TrackingLevel = level,
                        FramingFits = gr.FramingFits,
                        Created = KerbcastCore.Instance.CommandProvider?.IsCreated(c.FlightId) ?? false,
                    });
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
            return CommandCameraTargets.List(gr.Site, Planetarium.GetUniversalTime());
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
        {
            var core = KerbcastCore.Instance;
            if (core == null) return false;
            var auth = core.CommandAuthority;
            if (auth != null && auth.Registered)
            {
                if (!string.Equals(owner, auth.Owner, System.StringComparison.Ordinal)) return false;
                auth.Heartbeat(Time.realtimeSinceStartup);
            }
            var gr = Find(flightId)?.Rig as CommandCameraRig;
            var provider = core.CommandProvider;
            if (gr == null && (provider == null || !provider.IsPending(flightId))) return false;
            Orbit orbit = null;
            if (aim != null && !TryResolveAim(flightId, aim, out orbit)) return false;
            if (gr != null) gr.SetAim(aim, orbit);
            else provider.SetPendingAim(flightId, aim, orbit);
            return true;
        }

        /* Replaces `owner`'s anchors on camera `flightId`, or clears them with
           null or an empty list. Each anchor marks a part, a craft, a point on
           a body or a direction (see AnchorSpec's factories); the game projects
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
            uint id = Create(vantage, name, aim, null, out string error);
            if (id == 0) Debug.LogWarning($"[Kerbcast][CMDCAM] command camera at {vantage?.ToString() ?? "null"} refused: {error}");
            return id;
        }

        internal static uint Create(CommandVantage vantage, string name, CommandAim aim, string token, out string error)
        {
            var core = KerbcastCore.Instance;
            var provider = core?.CommandProvider;
            if (provider == null)
            {
                error = "Kerbcast is not running in this scene";
                return 0;
            }
            Orbit orbit = null;
            if (aim != null && !TryResolveAim(0, aim, out orbit))
            {
                error = $"aim {aim} is unusable";
                return 0;
            }
            var auth = core.CommandAuthority;
            if (aim != null && auth != null && auth.Registered)
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

        /* Checks an aim's numbers, craft and body, building the conic for an Orbit aim. */
        static bool TryResolveAim(uint flightId, CommandAim aim, out Orbit orbit)
        {
            orbit = null;
            if (!Finite(aim.FramingMarginM) || aim.FramingMarginM < 0) return false;
            uint subject = aim.SubjectPersistentId;
            if (aim.Kind == CommandAimKind.Vessel && subject == 0) return false;
            if (subject != 0 && !CommandCameraTargets.Exists(subject)) return false;
            switch (aim.Kind)
            {
                case CommandAimKind.Vessel:
                    return true;
                case CommandAimKind.Direction:
                    return Finite(aim.Azimuth) && Finite(aim.Elevation) && aim.Elevation >= -90 && aim.Elevation <= 90;
                case CommandAimKind.PositionAt:
                    if (!(Finite(aim.Ut) && Finite(aim.BodyRelativePosition) && Finite(aim.BodyRelativeVelocity))) return false;
                    break;
                case CommandAimKind.Surface:
                    if (!(Finite(aim.Latitude) && Finite(aim.Longitude) && Finite(aim.Altitude))) return false;
                    break;
            }
            var body = FlightGlobals.GetBodyByName(aim.Body);
            if (body == null) return false;
            if (aim.Kind != CommandAimKind.Orbit) return true;
            try
            {
                orbit = new Orbit(aim.Inclination, aim.Eccentricity, aim.SemiMajorAxis, aim.Lan,
                    aim.ArgumentOfPeriapsis, aim.MeanAnomalyAtEpoch, aim.Epoch, body);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] cam={flightId} aim {aim} rejected: {ex.Message}");
                return false;
            }
        }

        /* Blanks a command feed with `reason` shown on it; null or empty clears.
           A blanked feed streams black frames, never the scene. Like the
           owner's other calls it is a heartbeat. False when no lock is held or
           the feed is not a command feed. */
        public static bool SetCommandBlank(uint flightId, string reason)
        {
            var auth = Governing(flightId);
            if (auth == null) return false;
            auth.OrdersFor(flightId).Blank = string.IsNullOrEmpty(reason) ? null : reason;
            return true;
        }

        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        static bool Finite(Vector3d v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        /* The registered authority after a heartbeat, when `flightId` is a command feed. */
        static CommandAuthority Governing(uint flightId)
        {
            var auth = KerbcastCore.Instance?.CommandAuthority;
            if (auth == null || !auth.Registered) return null;
            auth.Heartbeat(Time.realtimeSinceStartup);
            var c = Find(flightId);
            if (c != null && c.Rig is CommandCameraRig) return auth;
            return KerbcastCore.Instance.CommandProvider?.IsPending(flightId) == true ? auth : null;
        }

        static KerbcastCamera Find(uint flightId)
        {
            var cams = KerbcastCore.Instance?.Cameras;
            if (cams == null) return null;
            for (int i = 0; i < cams.Count; i++)
                if (cams[i] is KerbcastCamera kc && kc.FlightId == flightId) return kc;
            return null;
        }

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
