using System;
using System.Collections.Generic;

/* Only Kerbcast implements these interfaces, so they grow between minors
   without breaking a consumer. */
namespace Kerbcast.Api
{
    /**
     * <summary>
     * Kerbcast in this scene, from <see cref="KerbcastApi.Instance"/>. Its own members are safe on any
     * thread; <see cref="ICameras"/> and <see cref="ICommand"/> are main thread only.
     * </summary>
     */
    public interface IKerbcast
    {
        ICameras Cameras { get; }
        ICommand Command { get; }

        /** <summary>Whether the video sidecar process is alive. False once it dies, which is when every feed goes black while the mod looks healthy.</summary> */
        bool SidecarAlive { get; }

        /** <summary>Host the feeds and the signalling endpoint listen on.</summary> */
        string BindAddress { get; }

        /** <summary>Port the feeds and the signalling endpoint listen on.</summary> */
        int Port { get; }

        /** <summary>True when only this computer can reach the feeds.</summary> */
        bool BindIsLoopback { get; }
    }

    /**
     * <summary>
     * Cameras and craft identity, with no Unity or KSP types. Main thread only: every member throws
     * InvalidOperationException on another thread. The controls act on part cameras only; command
     * feeds are controlled through <see cref="ICommand"/>.
     * </summary>
     */
    public interface ICameras
    {
        /** <summary>The part cameras on a craft by persistentId, empty when it has none or is unknown.</summary> */
        IReadOnlyList<CameraView> CamerasFor(uint vesselPersistentId);

        /** <summary>The part cameras on a craft by Vessel.id, empty when it has none or is unknown.</summary> */
        IReadOnlyList<CameraView> CamerasFor(Guid vesselId);

        /**
         * <summary>
         * A part camera or command feed by flightId (see <see cref="CameraView.Kind"/>), null when there
         * is none. Kerbal face cameras are not covered.
         * </summary>
         */
        CameraView View(uint flightId);

        /** <summary>Slews a part camera's field of view, clamped to its range. False for an unknown camera, a command feed, or one without zoom.</summary> */
        bool SetFov(uint flightId, double fov);

        /** <summary>Slews a part camera's pan, clamped to its range. False for an unknown camera, a command feed, or one without pan.</summary> */
        bool SetPan(uint flightId, double yaw, double pitch);

        /**
         * <summary>
         * Points a part camera at <paramref name="target"/>, relative to its craft's centre of mass in
         * KSP's world axes (the frame of <see cref="CameraView.Position"/>). False for an unknown camera
         * or a command feed.
         * </summary>
         */
        bool AimAt(uint flightId, Vec3d target);

        /**
         * <summary>
         * Sets a part camera's auto-track at once; every browser sees it. Only cameras that both pan and
         * zoom track. False otherwise, for an unknown camera or a command feed.
         * </summary>
         */
        bool SetTrackMode(uint flightId, TrackMode mode);

        /** <summary>A camera's auto-track, None when unknown.</summary> */
        TrackMode GetTrackMode(uint flightId);

        /**
         * <summary>
         * The Vessel.id of a craft by persistentId, including a craft KSP no longer has: the map only
         * grows while one save is loaded.
         * </summary>
         */
        bool TryVessel(uint persistentId, out Guid vesselId);

        /**
         * <summary>
         * The persistentId of a craft by Vessel.id, the inverse of <see cref="TryVessel"/>, including a craft
         * KSP no longer has. After a renumber it is the craft's latest persistentId.
         * </summary>
         */
        bool TryPersistentId(Guid vesselId, out uint persistentId);

        ThrottleSettings Throttle { get; }
    }

    /**
     * <summary>
     * The command lock, command feed aims and the world model. Main thread only: every member throws
     * InvalidOperationException on another thread, so off-thread callers cache what they need here first.
     * </summary>
     */
    public interface ICommand
    {
        /**
         * <summary>
         * Takes the lock on every command feed in this scene for <paramref name="owner"/>. While held, only
         * the owner aims command feeds; every owner call is a heartbeat, and 5 s of real time without one
         * blanks every governed feed with <paramref name="silentText"/> (or Kerbcast's default), never the
         * truth. NotRunning when Kerbcast is not running in this scene, as through a reference kept from an
         * earlier one. <see cref="KerbcastApi.LockLost"/> says when the lock goes without a release.
         * </summary>
         * <exception cref="ArgumentException"><paramref name="owner"/> is null or empty, or
         * <paramref name="owner"/> or <paramref name="silentText"/> contains a line break, '{', '}' or '//'
         * (both may be saved in Kerbcast's settings).</exception>
         */
        LockResult TakeLock(string owner, LockOptions options, string silentText = null);

        /** <summary>Releases the lock; every feed returns to truth and its default aim. Ignored for anyone but the holder.</summary> */
        void ReleaseLock(string owner);

        /**
         * <summary>
         * Starts every scene locked for <paramref name="owner"/>, with WorldModelRequired, while
         * <paramref name="ownerAssembly"/> is loaded. Saved in Kerbcast's settings until the owner or a player
         * clears it; it lapses when Kerbcast starts without that assembly. In a scene where the owner makes no
         * call (and attaches no world model) within 30 s of real time from its first frame, the reservation
         * lapses for that scene: the lock stays held and every governed feed stays blank, never the truth, but
         * browser clients may create and remove command cameras again, which are blank too. An owner call ends
         * the lapse. Only the player's in-game release returns a reserved scene to truth. The owner's later call
         * replaces the reservation.
         * Kerbcast checks that <paramref name="ownerAssembly"/> is the calling assembly.
         * </summary>
         * <exception cref="ArgumentException"><paramref name="owner"/> or <paramref name="ownerAssembly"/> is null
         * or empty or contains a line break, '{', '}' or '//', or <paramref name="ownerAssembly"/> is one that is always loaded (Kerbcast's own, the game's,
         * Unity's or the runtime's) or is not the calling assembly.</exception>
         * <exception cref="InvalidOperationException">Another owner holds the reservation.</exception>
         */
        void Reserve(string owner, string ownerAssembly);

        /**
         * <summary>
         * Clears <paramref name="owner"/>'s reservation, so the next scene starts unlocked. A lock already
         * held stays held. Ignored for anyone but the reservation's owner.
         * </summary>
         */
        void ClearReservation(string owner);

        /**
         * <summary>
         * How fast light travels in the picture, which sets the past instants feeds show and the live bound:
         * finite and positive, or null for no delay (no past instants, a live light time of 0). False, with nothing
         * changed, unless <paramref name="owner"/> holds the lock; the value lasts until the lock is
         * released or the scene changes.
         * </summary>
         * <exception cref="ArgumentOutOfRangeException">The speed is not finite, or not positive.</exception>
         */
        bool SetLightSpeed(string owner, double? metresPerSecond);

        AimResult SetAim(uint flightId, Aim aim, string owner);

        IReadOnlyList<CommandFeed> Feeds();

        /**
         * <summary>
         * Overrides a command feed's optics (see <see cref="OpticsSettings"/>), or returns it to Kerbcast's own
         * with null. While the lock is held only its owner may, and the call is a heartbeat; without the lock anyone
         * may. The values last until replaced, the lock is taken or released, or the scene changes. Check
         * <see cref="KerbcastApi.Supports"/> with <see cref="ApiFeature.Optics"/> first.
         * </summary>
         */
        OpticsResult SetOptics(uint flightId, OpticsSettings optics, string owner);

        /**
         * <summary>
         * Creates a tracking network camera: one command feed that follows its craft through <paramref name="vantages"/>
         * and shows whichever has the clearest view, cutting between them (see <see cref="NetworkOptions"/> for when).
         * Each vantage has the lens it would have standing alone, and under a world model is scored only on what its
         * own site knows. <paramref name="name"/> names the feed ("Tracking network" when null); its label, in every
         * frame, is the current vantage's. It aims by <paramref name="aim"/>, or at the default craft when null. The
         * feed appears in <see cref="Feeds"/> once a vantage resolves, normally on the next frame, and is dropped if
         * none has within 30 s. Like any created camera it lasts until destroyed or the scene ends, a revert or quickload
         * included, so a host makes it again in the next scene. There is no default list: the host passes every vantage. While the lock is held only
         * its owner may create one, and the call is a heartbeat; without the lock anyone may. Check
         * <see cref="KerbcastApi.Supports"/> with <see cref="ApiFeature.NetworkCameras"/> first.
         * </summary>
         */
        NetworkResult CreateNetworkCamera(IReadOnlyList<NetworkVantage> vantages, string name, Aim aim, NetworkOptions options,
            string owner, out uint flightId);

        /**
         * <summary>
         * Replaces a network camera's vantages, and its options unless <paramref name="options"/> is null. A vantage
         * kept in the list keeps its place; if the camera's current vantage is removed it cuts on its next pass. The
         * lock applies as for <see cref="CreateNetworkCamera"/>.
         * </summary>
         */
        NetworkResult SetNetworkVantages(uint flightId, IReadOnlyList<NetworkVantage> vantages, NetworkOptions options, string owner);

        /** <summary>Removes a network camera, built or not; its feed ends. The lock applies as for <see cref="CreateNetworkCamera"/>.</summary> */
        NetworkResult DestroyNetworkCamera(uint flightId, string owner);

        /** <summary>Every vantage of a network camera as its last pass scored them, in list order; empty when it is no network camera or not built yet.</summary> */
        IReadOnlyList<NetworkVantageStatus> NetworkStatus(uint flightId);

        /**
         * <summary>
         * Every ground station in the scene a command camera can stand at, once per node name, switched on or not, so
         * a host can build its own vantage list. Check <see cref="KerbcastApi.Supports"/> with
         * <see cref="ApiFeature.GroundStations"/> first.
         * </summary>
         */
        IReadOnlyList<GroundStationInfo> GroundStations();

        /**
         * <summary>
         * Whether a lens standing at <paramref name="vantage"/> could see a point at game time <paramref name="ut"/>:
         * the vantage, every body and the vantage's own skyline are placed at that one instant, and the line is tested
         * with the occlusion command cameras draw with. The point is the caller's, in the world model's state vector
         * frame (<see cref="WorldModelBuilder.StateVector"/>: about body <paramref name="bodyIndex"/>, z-up, KSP's fixed
         * inertial frame), with <paramref name="radiusM"/> letting a craft show over a skyline (NaN for a point). There is
         * no light time or delay in it: the caller picks the instant and the point. A craft vantage is placed on its
         * current orbit patch, as if nothing changed its course (no thrust, no change of sphere of influence), or carried
         * by its body when landed. A ground station the game has switched off answers Disabled. Known is false when the
         * vantage or the body cannot be placed, and whenever any part of the vantage's skyline the line needs is not
         * measured yet, so a Known answer is always whole. It is a read for the host alone: no lock applies,
         * nothing is shown to viewers and no camera uses it. Check <see cref="KerbcastApi.Supports"/> with
         * <see cref="ApiFeature.LineOfSight"/> first.
         * </summary>
         */
        LineOfSightResult LineOfSight(NetworkVantage vantage, double ut, int bodyIndex, Vec3d position, double radiusM = double.NaN);

        /**
         * <summary>
         * How fine a detail a command camera standing at <paramref name="vantage"/> could show now: its tightest zoom,
         * the frame it renders, and its lens when deep-space optics are drawn, by the rules a camera there would have.
         * For the blur toward one point, air included, read <see cref="LineOfSightResult.ResolutionRad"/>. A read for the
         * host alone: no lock applies and no camera uses it. Not Known for a null vantage or one that does not resolve.
         * Check <see cref="KerbcastApi.Supports"/> with <see cref="ApiFeature.VantageResolution"/> first.
         * </summary>
         */
        VantageResolution VantageResolution(NetworkVantage vantage);

        /** <summary>Every craft a command feed could watch, empty when it is not a command feed.</summary> */
        IReadOnlyList<CommandTarget> Targets(uint flightId);

        /** <summary>The publisher for <paramref name="owner"/>'s world model, null unless <paramref name="owner"/> holds the lock in this scene.</summary> */
        IWorldModelPublisher AttachWorldModel(string owner);
    }

    /**
     * <summary>
     * Publishes a world model. Safe on any thread. Disposing fails closed: governed feeds read every craft
     * as unknown until the lock is released or <see cref="Detach"/> returns to truth.
     * </summary>
     */
    public interface IWorldModelPublisher : IDisposable
    {
        /**
         * <summary>
         * A builder for one snapshot. The sequence rises with every publish; an epoch lower than the last
         * accepted one in this lock tenure is refused.
         * </summary>
         */
        WorldModelBuilder Begin(long sequence, long epoch, double knowledgeUt);

        /** <summary>Copies the builder into Kerbcast and invalidates it, whatever the result.</summary> */
        PublishResult Publish(WorldModelBuilder builder, out string error);

        /** <summary>Returns an unfinished builder.</summary> */
        void Abandon(WorldModelBuilder builder);

        /** <summary>Stops publishing; with <paramref name="returnToTruth"/>, governed feeds show the truth again while the lock stays held.</summary> */
        void Detach(bool returnToTruth);

        long LastAcceptedSequence { get; }
        bool Attached { get; }
    }
}
