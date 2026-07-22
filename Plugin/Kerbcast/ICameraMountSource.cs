using UnityEngine;

namespace Kerbcast
{
    /* Zoom as an optional capability. Core fetches the limits to clamp + present,
       then calls SetFov with a clamped target in the standard frame; the source
       applies it to its concrete camera. */
    public interface IZoomCapability
    {
        float FovMin { get; }
        float FovMax { get; }
        float Fov { get; }
        void SetFov(float fov);
    }

    /* Pan as an optional capability. Core fetches yaw/pitch limits to clamp +
       present, then calls Steer with a clamped target in the standard (visual)
       frame; the source maps it to its joints, absorbing flip/axis quirks. */
    public interface IPanCapability
    {
        float YawMin { get; }
        float YawMax { get; }
        float PitchMin { get; }
        float PitchMax { get; }
        float Yaw { get; }
        float Pitch { get; }
        void Steer(float yaw, float pitch);
    }

    /* A camera's placement + identity + optional capabilities, decoupled from
       MuMechModuleHullCamera. Zoom/Pan are null when the source can't do them;
       KerbcastCamera derives SupportsZoom/SupportsPan from that. */
    public interface ICameraMountSource
    {
        // Mount transform + local pose
        Transform PartTransform { get; }
        Transform FindModelTransform(string name);
        string CameraTransformName { get; }
        Vector3 CameraPosition { get; }
        Vector3 CameraForward { get; }
        Vector3 CameraUp { get; }

        /* Fixed render params. DefaultFieldOfView is the authored FoV every
           camera has (zoom or not); the ctor seeds its initial Fov from it and
           the Zoom capability drives it thereafter. */
        float DefaultFieldOfView { get; }
        float NearClip { get; }
        int FilterMode { get; }

        // Optional capabilities (null == unsupported)
        IZoomCapability Zoom { get; }
        IPanCapability Pan { get; }

        // Owning vessel (FX / integration frame-state / sun)
        Vessel Vessel { get; }

        // Identity (cached once at KerbcastCamera construction)
        string PartName { get; }
        string PartTitle { get; }
        string CameraName { get; }
        string VesselDisplayName { get; }

        // Liveness + ownership, used by KerbcastCore churn
        bool IsAlive { get; }
        bool OwnsPart(Part part);

        /* Owning part, surfaced for the kOS control facade's camera view (which
           hands a bare Part handle to the addon). Null once the part is gone. */
        Part OwningPart { get; }
    }
}
