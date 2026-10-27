/* Every enum here keeps its numbers for the life of the API: values are
   added, never renumbered or reused. A result's 0 is Unknown, so a value
   nobody set never reads as success. */
namespace Kerbcast.Api
{
    /** <summary>A group of API members, for <see cref="KerbcastApi.Supports"/>.</summary> */
    public enum ApiFeature
    {
        /** <summary>Camera views and part-camera controls on <see cref="ICameras"/>.</summary> */
        CameraControl = 0,
        /** <summary>Craft ids and docking facts on views, <see cref="ICameras.TryVessel"/> and <see cref="KerbcastApi.Identity"/>.</summary> */
        CraftIdentity = 1,
        /** <summary><see cref="ICameras.Throttle"/>.</summary> */
        Throttle = 2,
        /** <summary><see cref="ICommand.SetSignalSpeed"/>.</summary> */
        SignalSpeed = 3,
        /** <summary><see cref="ICommand.TakeLock"/>, <see cref="ICommand.ReleaseLock"/>, <see cref="ICommand.Reserve"/> and <see cref="KerbcastApi.LockLost"/>.</summary> */
        CommandLock = 4,
        /** <summary><see cref="ICommand.SetAim"/>.</summary> */
        CommandAim = 5,
        /** <summary><see cref="ICommand.Feeds"/> and <see cref="ICommand.Targets"/>.</summary> */
        CommandFeeds = 6,
        /** <summary><see cref="ICommand.AttachWorldModel"/> and the publisher.</summary> */
        WorldModel = 7,
    }

    public enum CameraKind
    {
        /** <summary>A camera on a craft's part, controlled through <see cref="ICameras"/>.</summary> */
        Part = 0,
        /** <summary>Reserved: kerbal face cameras are not listed yet.</summary> */
        Kerbal = 1,
        /** <summary>A command feed, controlled only through <see cref="ICommand"/>.</summary> */
        Command = 2,
    }

    public enum TrackMode
    {
        None = 0,
        ActiveVessel = 1,
        Target = 2,
    }

    /** <summary>What an <see cref="IdentityEvent"/> reports.</summary> */
    public enum IdentityChange
    {
        /**
         * <summary>
         * The craft stopped existing as itself and carries on as part of OtherPersistentId's: docking,
         * a claw grab, a kerbal boarding.
         * </summary>
         */
        Merge = 0,
        /** <summary>The craft is new and left OtherPersistentId's: undocking or decoupling.</summary> */
        Split = 1,
        /** <summary>The craft was destroyed or terminated.</summary> */
        Destroy = 2,
        /** <summary>The craft was recovered.</summary> */
        Recover = 3,
        /**
         * <summary>
         * The same craft now has persistentId OtherPersistentId: KSP gave a craft that already existed
         * under the old id a new one. VesselId and OtherVesselId name the same craft. A craft that arrives
         * under an id another craft already has (two craft saved under one id, say) gets a fresh id with
         * no event, and the craft already there keeps its id. A coupling's own id change is a Merge, not
         * a Renumber.
         * </summary>
         */
        Renumber = 4,
    }

    /** <summary>A craft's state in a world-model scope. No record reads as Unknown.</summary> */
    public enum CraftFixState : byte
    {
        Unknown = 0,
        Refused = 1,
        Positioned = 2,
        /** <summary>Known but not positioned: it lists, and Vessel aims at it are refused.</summary> */
        Listed = 3,
    }

    public enum PublishResult
    {
        /** <summary>Never returned: the value of a result nobody set.</summary> */
        Unknown = 0,
        Accepted = 1,
        NotLockHolder = 2,
        SceneChanged = 3,
        /** <summary>The sequence is not after the last accepted one.</summary> */
        OutOfOrder = 4,
        /** <summary>The epoch is lower than the last accepted one in this lock tenure.</summary> */
        OlderEpoch = 5,
        /** <summary>A value in the snapshot is malformed; the error names the first.</summary> */
        Invalid = 6,
        /** <summary>Kerbcast is not running: the scene the publisher attached in ended and none has started.</summary> */
        NotRunning = 7,
    }

    public enum SiteKind
    {
        Station = 0,
        LaunchSite = 1,
        Surface = 2,
        Vessel = 3,
    }

    [System.Flags]
    public enum LockOptions
    {
        None = 0,
        /** <summary>Until the holder publishes a world model, every craft reads as unknown.</summary> */
        WorldModelRequired = 1,
    }

    public enum LockResult
    {
        /** <summary>Never returned: the value of a result nobody set.</summary> */
        Unknown = 0,
        /** <summary>The caller now holds the lock.</summary> */
        Taken = 1,
        /** <summary>The caller already held it; the call counted as a heartbeat.</summary> */
        Renewed = 2,
        /** <summary>Another owner holds it.</summary> */
        HeldByOther = 3,
        /** <summary>Kerbcast is not running in this scene, so there is no lock to take.</summary> */
        NotRunning = 4,
    }

    /** <summary>Why a holder lost the command lock, for <see cref="KerbcastApi.LockLost"/>.</summary> */
    public enum LockLossReason
    {
        /** <summary>Never raised: the value nobody set.</summary> */
        Unknown = 0,
        /**
         * <summary>
         * The scene is ending; every lock does. Raised as the scene unloads, when a take is refused with
         * NotRunning, so take the lock again once the next scene has started.
         * </summary>
         */
        SceneEnded = 1,
        /** <summary>The player released it in game, which also cleared any reservation.</summary> */
        ReleasedByPlayer = 2,
    }

    public enum AimResult
    {
        /** <summary>Never returned: the value of a result nobody set.</summary> */
        Unknown = 0,
        Ok = 1,
        NotLockHolder = 2,
        NotCommandFeed = 3,
        UnknownCraft = 4,
        NotPositioned = 5,
        Refused = 6,
        Stale = 7,
        TooUncertain = 8,
        UnknownBody = 9,
        BadNumber = 10,
        /** <summary>Kerbcast is not running in this scene, so there is no feed to aim.</summary> */
        NotRunning = 11,
    }

    public enum AimKind
    {
        Vessel = 0,
        Orbit = 1,
        Surface = 2,
        PositionAt = 3,
        Direction = 4,
    }

    /** <summary>How a positioned world-model record moves.</summary> */
    public enum KinematicsKind
    {
        None = 0,
        Conic = 1,
        StateVector = 2,
        Surface = 3,
    }
}
