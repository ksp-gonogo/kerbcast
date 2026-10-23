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

        /* Registers `owner` (gonogo) as the authority over every command feed in
           this scene. From then on each command feed points where the authority
           says and fails closed: no aim yet blanks it "Waiting for gonogo", and
           if no setter (this, SetCommandAim, SetCommandFraming, SetCommandBlank)
           has been called for 5 s of REAL time every command feed blanks "gonogo
           not responding". Real time keeps counting while the game is paused or
           warping, so the authority must keep calling a setter as a heartbeat
           then too. Registration lasts until ClearCommandAuthority or a scene
           change, so re-register on each scene load. Calling again with the
           same owner is a heartbeat; a different owner is refused (false).
           False also when Kerbcast is not running in this scene. */
        public static bool SetCommandAuthority(string owner)
        {
            var auth = KerbcastCore.Instance?.CommandAuthority;
            return auth != null && auth.Register(owner, Time.realtimeSinceStartup);
        }

        /* Ends `owner`'s registration; command feeds return to the perfect
           standalone camera. Ignored for any other owner. */
        public static void ClearCommandAuthority(string owner)
        {
            KerbcastCore.Instance?.CommandAuthority?.Clear(owner);
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
                        TargetPersistentId = gr.TargetPersistentId,
                        RangeM = CommandCameraTargets.RangeTo(gr.Site, gr.TargetPersistentId, ut),
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
           difficulty setting on); SetCommandTarget accepts any of them. */
        public static IReadOnlyList<CommandTargetView> CommandTargets(uint flightId)
        {
            var c = Find(flightId);
            if (c == null || !(c.Rig is CommandCameraRig gr)) return new List<CommandTargetView>();
            return CommandCameraTargets.List(gr.Site, Planetarium.GetUniversalTime());
        }

        /* The craft command feed `flightId` watches, by Vessel.persistentId, or 0
           to return to the default (the active craft in flight, the configured
           CommandCameraTargetName at the Space Center). Any craft is accepted,
           eligible or not. Under an authority SetCommandAim still points the
           camera; the target only decides which craft is the true craft. False
           when the feed is not a command feed or no craft has that id. */
        public static bool SetCommandTarget(uint flightId, uint persistentId)
        {
            var c = Find(flightId);
            var gr = c?.Rig as CommandCameraRig;
            var provider = KerbcastCore.Instance?.CommandProvider;
            bool pending = gr == null && provider != null && provider.IsPending(flightId);
            if (gr == null && !pending) return false;
            if (persistentId != 0 && !VesselExists(persistentId)) return false;
            if (gr != null) gr.SetTarget(persistentId);
            else provider.SetPendingTarget(flightId, persistentId);
            return true;
        }

        /* Creates a command camera standing at `vantage` for the rest of this
           scene (flight or Space Center), and returns the flightId its feed
           will have, or 0 when Kerbcast is not running here, the vantage is
           malformed, or a surface vantage names an unknown body. The feed
           appears once the vantage resolves, normally on the next frame; a
           launch site, station or craft that is not placed within 30 s drops
           the camera instead of standing it anywhere else. `name` replaces the
           default feed name ("Launchpad camera", "<site> camera",
           "<station> tracking", "<craft> camera" or "<body> surface camera").
           It watches the default craft (the active one in flight) until
           `targetPersistentId` or SetCommandTarget picks another; every
           command setter works on it, including before it is built. */
        public static uint CreateCommandCamera(CommandVantage vantage, string name = null, uint targetPersistentId = 0)
        {
            uint id = Create(vantage, name, targetPersistentId, null, out string error);
            if (id == 0) Debug.LogWarning($"[Kerbcast][CMDCAM] command camera at {vantage?.ToString() ?? "null"} refused: {error}");
            return id;
        }

        internal static uint Create(CommandVantage vantage, string name, uint targetPersistentId, string token, out string error)
        {
            var core = KerbcastCore.Instance;
            var provider = core?.CommandProvider;
            if (provider == null)
            {
                error = "Kerbcast is not running in this scene";
                return 0;
            }
            if (targetPersistentId != 0 && !VesselExists(targetPersistentId))
            {
                error = $"no craft with persistentId={targetPersistentId}";
                return 0;
            }
            return provider.Create(vantage, name, targetPersistentId, token, core.CameraFlightIds(), out error);
        }

        /* Removes a command camera CreateCommandCamera (or a client) made,
           built or not; its feed ends. False for the settings-driven camera or
           an unknown id, so a repeated call is harmless. */
        public static bool DestroyCommandCamera(uint flightId)
        {
            return KerbcastCore.Instance?.DestroyCommandCamera(flightId) ?? false;
        }

        /* Points command feed `flightId` at `aim` instead of its craft while no
           authority is registered; the craft is still drawn wherever it truly
           is. `framingMarginM` widens the framing around the point the way
           SetCommandFraming does. Null clears it, back to the craft. Under an
           authority SetCommandAim decides instead, and this resumes when the
           authority clears. False when the feed is not a command feed, the
           margin is negative or not finite, or the aim is unusable (as for
           SetCommandAim). */
        public static bool SetCommandLookAt(uint flightId, CommandAim aim, double framingMarginM = 0)
        {
            if (!Finite(framingMarginM) || framingMarginM < 0) return false;
            var gr = Find(flightId)?.Rig as CommandCameraRig;
            var provider = KerbcastCore.Instance?.CommandProvider;
            if (gr == null && (provider == null || !provider.IsPending(flightId))) return false;
            Orbit orbit = null;
            if (aim != null && !TryResolveAim(flightId, aim, out orbit)) return false;
            if (gr != null) gr.SetLookAt(aim, orbit, framingMarginM);
            else provider.SetPendingLookAt(flightId, aim, orbit, framingMarginM);
            return true;
        }

        internal static CommandAim AimOf(CommandLookAtSpec spec) => spec.Kind == "vessel"
            ? CommandAim.Vessel(spec.PersistentId)
            : CommandAim.Surface(spec.Body, spec.Latitude, spec.Longitude, spec.Altitude);

        static bool VesselExists(uint persistentId)
        {
            if (FlightGlobals.FindVessel(persistentId, out Vessel v) && v != null) return true;
            var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protos == null) return false;
            for (int i = 0; i < protos.Count; i++)
                if (protos[i] != null && protos[i].persistentId == persistentId) return true;
            return false;
        }

        /* Points a command feed at `aim`. Under an authority every camera-axis reading (occlusion,
           sunlight, range, bracket size) is taken at the aim point, while the
           real craft is still drawn where it truly is, so a wrong aim shows as
           a craft off centre. False when no authority is registered, the feed
           is not a command feed, the aim is null, or its body is unknown. A
           call is a heartbeat even when it returns false for a bad aim. */
        public static bool SetCommandAim(uint flightId, CommandAim aim)
        {
            var auth = Governing(flightId);
            if (auth == null || aim == null) return false;
            if (!TryResolveAim(flightId, aim, out Orbit orbit)) return false;
            var orders = auth.OrdersFor(flightId);
            orders.Aim = aim;
            orders.AimOrbit = orbit;
            return true;
        }

        /* Checks an aim's numbers and body, building the conic for an Orbit aim. */
        static bool TryResolveAim(uint flightId, CommandAim aim, out Orbit orbit)
        {
            orbit = null;
            if (aim.Kind == CommandAimKind.PositionAt && !(Finite(aim.Ut) && Finite(aim.BodyRelativePosition)
                && Finite(aim.BodyRelativeVelocity))) return false;
            if (aim.Kind == CommandAimKind.Surface && !(Finite(aim.Latitude) && Finite(aim.Longitude) && Finite(aim.Altitude)))
                return false;
            if (aim.Kind == CommandAimKind.Vessel) return true;
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

        /* Widens a command feed's framing by `marginM` metres around the aim
           point, added to the craft's own radius for the field of view and for
           the bracket. FramingFits on CommandFeeds reports whether the result
           still fits the widest view. Until the first call the margin is 0.
           Like every setter it is a heartbeat. False when no authority is
           registered, the feed is not a command feed, or `marginM` is negative
           or not finite. */
        public static bool SetCommandFraming(uint flightId, double marginM)
        {
            var auth = Governing(flightId);
            if (auth == null || !Finite(marginM) || marginM < 0) return false;
            auth.OrdersFor(flightId).FramingMarginM = marginM;
            return true;
        }

        /* Blanks a command feed with `reason` shown on it; null or empty clears.
           A blanked feed streams black frames, never the scene. False when no
           authority is registered or the feed is not a command feed. */
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
