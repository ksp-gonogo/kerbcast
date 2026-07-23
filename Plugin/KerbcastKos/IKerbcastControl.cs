using System.Collections.Generic;

namespace Kerbcast.Kos
{
    /* Unity-free control seam the addon calls instead of the static
       Kerbcast.KerbcastControl facade. RealKerbcastControl wraps the facade
       in-game; headless tests inject a fake. Keeping this interface free of
       UnityEngine/KSP types is what lets the suffix paths load headless. */
    public interface IKerbcastControl
    {
        bool IsActive { get; }
        IReadOnlyList<KosCameraView> CamerasFor(object vessel);
        KosCameraView ViewOf(uint flightId);
        bool SetFov(uint flightId, float fov);
        bool SetPan(uint flightId, float yaw, float pitch);
        bool AimAt(uint flightId, double x, double y, double z);
        /* Auto-track mode (0=none/1=active-vessel/2=target). Set is synchronous
           + optimistic-local (only pan+zoom cameras honour it; returns false
           otherwise); get returns the applied value now. */
        bool SetTrackMode(uint flightId, int mode);
        int GetTrackMode(uint flightId);
    }
}
