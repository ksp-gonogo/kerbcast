using System;
using kOS;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Utilities;

namespace Kerbcast.Kos
{
    /* The ADDONS:KERBCAST suffix surface. Registered by kOS via the
       [kOSAddon] attribute + AssemblyWalk. Reaches the plugin only through
       the Unity-free IKerbcastControl seam; Control defaults lazily to the
       in-game RealKerbcastControl, and a test presetting it never JITs the
       real adapter. */
    [kOS.AddOns.kOSAddon("KERBCAST")]
    [KOSNomenclature("KerbcastAddon")]
    public class KerbcastAddon : kOS.Suffixed.Addon
    {
        public static IKerbcastControl Control;   // test seam; defaulted lazily below

        /* Unity-free lifecycle core for AIM callbacks. The MonoBehaviour pump
           (created lazily by EnsurePump) drives it once per frame. */
        public static readonly AimSourceRegistry AimRegistry = new AimSourceRegistry();
        static KerbcastKosPump pump;

        public KerbcastAddon(SharedObjects shared) : base(shared)
        {
            if (Control == null) Control = new RealKerbcastControl();
            AddSuffix("CAMERAS", new Suffix<ListValue>(GetCameras));
            AddSuffix("CAMERA", new OneArgsSuffix<KerbcastCameraStruct, StringValue>(GetCamera));
        }

        /* Register/replace/clear the AIM source for a camera. A null or
           NoDelegate delegate clears it (the kerboscript "unset" idiom). Kept
           here (not in the struct) so the Unity/pump-touching code JITs only
           when AIM is actually used, keeping the struct headless-loadable. */
        public static void SetAim(uint id, UserDelegate del)
        {
            EnsurePump();
            AimRegistry.SetSource(
                id,
                del == null || del is NoDelegate ? null : new UserDelegateAimLease(del));
        }

        /* Create the single main-thread pump on first AIM use and wire it to
           the shared registry + control seam. DontDestroyOnLoad so it survives
           scene changes for the KSP session. */
        static void EnsurePump()
        {
            if (pump != null) return;
            var go = new UnityEngine.GameObject("KerbcastKosPump");
            UnityEngine.Object.DontDestroyOnLoad(go);
            pump = go.AddComponent<KerbcastKosPump>();
            pump.Registry = AimRegistry;
            pump.Control = Control;
        }

        public override BooleanValue Available() => Control.IsActive && shared.Vessel != null;

        ListValue GetCameras()
        {
            var list = new ListValue();
            foreach (var v in Control.CamerasFor(shared.Vessel))
                list.Add(new KerbcastCameraStruct(shared, v.FlightId, Control));
            return list;
        }

        KerbcastCameraStruct GetCamera(StringValue uid) =>
            new KerbcastCameraStruct(shared, Convert.ToUInt32((string)uid), Control);
    }
}
