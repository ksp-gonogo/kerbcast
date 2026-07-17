namespace Kerbcast.Kos
{
    /* Unity-free DTO mirroring Kerbcast.KerbcastCameraView. Deliberately
       carries no UnityEngine/KSP type in its metadata (PartHandle is a bare
       object) so the addon's suffix paths stay loadable headless. */
    public sealed class KosCameraView
    {
        public uint FlightId;
        public uint PartFlightId;
        public string CameraName;
        public string PartName;
        public string PartTitle;
        public bool SupportsZoom;
        public bool SupportsPan;
        public float Fov, FovMin, FovMax;
        public float PanYaw, PanPitch;
        public float PanYawMin, PanYawMax, PanPitchMin, PanPitchMax;
        public object PartHandle;
    }
}
