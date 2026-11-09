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
        /** <summary><see cref="ICommand.SetLightSpeed"/>.</summary> */
        LightSpeed = 3,
        /** <summary><see cref="ICommand.TakeLock"/>, <see cref="ICommand.ReleaseLock"/>, <see cref="ICommand.Reserve"/> and <see cref="KerbcastApi.LockLost"/>.</summary> */
        CommandLock = 4,
        /** <summary><see cref="ICommand.SetAim"/>.</summary> */
        CommandAim = 5,
        /** <summary><see cref="ICommand.Feeds"/> and <see cref="ICommand.Targets"/>.</summary> */
        CommandFeeds = 6,
        /** <summary><see cref="ICommand.AttachWorldModel"/> and the publisher.</summary> */
        WorldModel = 7,
        /** <summary><see cref="ICommand.SetOptics"/>. Added in API 1.5.</summary> */
        Optics = 8,
        /** <summary><see cref="ICommand.CreateNetworkCamera"/>, <see cref="ICommand.SetNetworkVantages"/>, <see cref="ICommand.DestroyNetworkCamera"/> and <see cref="ICommand.NetworkStatus"/>. Added in API 1.5.</summary> */
        NetworkCameras = 9,
        /** <summary><see cref="ICommand.GroundStations"/>. Added in API 1.5.</summary> */
        GroundStations = 10,
        /** <summary><see cref="ICommand.LineOfSight"/>. Added in API 1.5.</summary> */
        LineOfSight = 11,
        /** <summary><see cref="ICommand.VantageResolution"/> and <see cref="LineOfSightResult.ResolutionRad"/>. Added in API 1.5.</summary> */
        VantageResolution = 12,
        /** <summary><see cref="ICommand.SetFov"/> and <see cref="Aim.WithFov"/>. Added in API 1.5.</summary> */
        CommandFov = 13,
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

    /** <summary>Where a command camera's lens comes from, in <see cref="VantageResolution.LensSource"/>. Added in API 1.5.</summary> */
    public enum LensSource
    {
        Unknown = 0,
        /** <summary>The Tracking Station's lens at its level now, or its top level's where the level sets nothing (outside career, or with the tracking rules off).</summary> */
        TrackingStation = 1,
        /** <summary>The camera beside the launch site, which always has the level 1 lens.</summary> */
        LaunchSite = 2,
        /** <summary>A craft's space lens: the craft is outside any atmosphere and a researched step gives a finer lens than the Tracking Station's.</summary> */
        SpaceLens = 3,
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

    public enum OpticsResult
    {
        /** <summary>Never returned: the value of a result nobody set.</summary> */
        Unknown = 0,
        Ok = 1,
        NotLockHolder = 2,
        NotCommandFeed = 3,
        /** <summary>A value is infinite, negative, or (BrightnessHold) above 1.</summary> */
        BadNumber = 4,
        /** <summary>Kerbcast is not running in this scene, so there is no feed to set.</summary> */
        NotRunning = 5,
    }

    public enum NetworkResult
    {
        /** <summary>Never returned: the value of a result nobody set.</summary> */
        Unknown = 0,
        Ok = 1,
        NotLockHolder = 2,
        /**
         * <summary>
         * The flightId is no tracking network camera this caller can change: not one made through the API, built or
         * waiting to be, or one another mod made under its own name, or one a player saved with the game
         * (<see cref="CommandFeed.Saved"/>). A saved network's vantages and its removal are the player's alone; its aim
         * and field of view still follow <see cref="ICommand.SetAim"/> and <see cref="ICommand.SetFov"/> under the lock rules.
         * </summary>
         */
        NotCommandFeed = 3,
        /** <summary>The list is null or empty: a network always has at least one vantage.</summary> */
        NoVantages = 4,
        /** <summary>Kerbcast is not running in this scene.</summary> */
        NotRunning = 5,
        /** <summary>A vantage is malformed (a missing name, a number out of range, an unknown body), or there are more than 64.</summary> */
        BadVantage = 6,
        /** <summary>The aim is unusable, as <see cref="ICommand.SetAim"/> would refuse it.</summary> */
        BadAim = 7,
        /** <summary>An option is negative, infinite or NaN.</summary> */
        BadNumber = 8,
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
