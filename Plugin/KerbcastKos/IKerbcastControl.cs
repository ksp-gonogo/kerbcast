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
    }
}
