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
         * The speed signals travel, for the past instants feeds show and the live bound: finite and
         * positive, or null for no delay (no past instants, a live light time of 0). False, with nothing
         * changed, unless <paramref name="owner"/> holds the lock; the value lasts until the lock is
         * released or the scene changes.
         * </summary>
         * <exception cref="ArgumentOutOfRangeException">The speed is not finite, or not positive.</exception>
         */
        bool SetSignalSpeed(string owner, double? metresPerSecond);

        AimResult SetAim(uint flightId, Aim aim, string owner);

        IReadOnlyList<CommandFeed> Feeds();

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
