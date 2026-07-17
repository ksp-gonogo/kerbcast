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

        public KerbcastAddon(SharedObjects shared) : base(shared)
        {
            if (Control == null) Control = new RealKerbcastControl();
            AddSuffix("CAMERAS", new Suffix<ListValue>(GetCameras));
            AddSuffix("CAMERA", new OneArgsSuffix<KerbcastCameraStruct, StringValue>(GetCamera));
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
