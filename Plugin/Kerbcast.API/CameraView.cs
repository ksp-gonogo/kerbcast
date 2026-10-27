using System;

namespace Kerbcast.Api
{
    /**
     * <summary>
     * One camera as it was when the view was taken. Each call returns new views, so changing one
     * changes nothing in the game.
     * </summary>
     */
    public sealed class CameraView
    {
        public uint FlightId { get; set; }

        /** <summary>A part camera, or a command feed (controlled only through <see cref="ICommand"/>).</summary> */
        public CameraKind Kind { get; set; }

        public string Name { get; set; }

        /** <summary>The persistentId of the craft a part camera rides; 0 for a command feed.</summary> */
        public uint VesselPersistentId { get; set; }

        /** <summary>The Vessel.id of the craft a part camera rides; Guid.Empty for a command feed.</summary> */
        public Guid VesselId { get; set; }

        /** <summary>The camera part's flightID, 0 without a part.</summary> */
        public uint PartFlightId { get; set; }

        /** <summary>The camera part's persistentId, 0 without a part.</summary> */
        public uint PartPersistentId { get; set; }

        /** <summary>The part's internal name.</summary> */
        public string PartName { get; set; }

        public string PartTitle { get; set; }

        /** <summary>Whether the camera's part has a docking node: null when it could not be read, false when it has none.</summary> */
        public bool? HasDockingNode { get; set; }

        /** <summary>The docking node's KSP nodeType ("size1"), null without one.</summary> */
        public string DockingNodeType { get; set; }

        /** <summary>The docking node's KSP state ("Ready", "Docked (docker)"), null without one.</summary> */
        public string DockingNodeState { get; set; }

        public bool SupportsZoom { get; set; }
        public bool SupportsPan { get; set; }

        /** <summary>Field of view in degrees; the range below bounds what <see cref="ICameras.SetFov"/> reaches.</summary> */
        public double Fov { get; set; }
        public double FovMin { get; set; }
        public double FovMax { get; set; }

        /** <summary>Pan in degrees; the ranges below bound what <see cref="ICameras.SetPan"/> reaches.</summary> */
        public double PanYaw { get; set; }
        public double PanPitch { get; set; }
        public double PanYawMin { get; set; }
        public double PanYawMax { get; set; }
        public double PanPitchMin { get; set; }
        public double PanPitchMax { get; set; }

        /** <summary>Unit optical axis in KSP's world axes.</summary> */
        public Vec3d Boresight { get; set; }

        /**
         * <summary>
         * The lens relative to its craft's centre of mass, in KSP's world axes: the frame kOS's
         * TARGET:POSITION uses, so a target minus this is the lens-to-target vector. The world position
         * for a command feed.
         * </summary>
         */
        public Vec3d Position { get; set; }

        public TrackMode TrackMode { get; set; }

        /** <summary>A copy of this view.</summary> */
        public CameraView Clone() => (CameraView)MemberwiseClone();
    }

    /**
     * <summary>
     * A change to which craft exist, as KSP reports it. Guids are Guid.Empty only when Kerbcast never saw
     * the craft. Immutable, so every subscriber can keep the one it was handed.
     * </summary>
     */
    public sealed class IdentityEvent
    {
        public IdentityEvent(IdentityChange change, double ut, uint persistentId, Guid vesselId,
            uint otherPersistentId = 0, Guid otherVesselId = default)
        {
            Change = change;
            Ut = ut;
            PersistentId = persistentId;
            VesselId = vesselId;
            OtherPersistentId = otherPersistentId;
            OtherVesselId = otherVesselId;
        }

        public IdentityChange Change { get; }

        /** <summary>Game time it happened.</summary> */
        public double Ut { get; }

        public uint PersistentId { get; }
        public Guid VesselId { get; }

        /**
         * <summary>
         * The craft a merge carries on as, the one a split left, or a renumbered craft's new id; 0 and
         * Guid.Empty for a destroy or recover.
         * </summary>
         */
        public uint OtherPersistentId { get; }
        public Guid OtherVesselId { get; }

        public override string ToString() =>
            OtherPersistentId == 0
                ? $"{Change} {PersistentId} at {Ut}"
                : $"{Change} {PersistentId} / {OtherPersistentId} at {Ut}";
    }

    /**
     * <summary>
     * Kerbcast's per-save main-render throttle: while on, KSP's own flight cameras stop rendering so the
     * feeds get the GPU. Only Kerbcast implements this.
     * </summary>
     */
    public abstract class ThrottleSettings
    {
        internal ThrottleSettings() { }

        /** <summary>The loaded save's setting, null with no save loaded.</summary> */
        public abstract bool? MainScreen { get; }

        /** <summary>Sets the loaded save's setting, which persists with the save and applies at once. False with no save loaded.</summary> */
        public abstract bool SetMainScreen(bool on);
    }
}
