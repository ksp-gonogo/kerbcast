// Diagnostic-only probe for the Scatterer sunflare on a kerbcast clone. Attached
// to the near clone by ScattererIntegration when DebugCameraLogging is on, it
// reads Scatterer's OWN flare state during the clone's manual render and logs it,
// so we work from Scatterer's numbers instead of a reconstruction.
//
// Read in OnPostRender, which fires at the end of the clone's Camera.Render():
//   - FlareRendering (SunFlare field, set in updateProperties/OnPreRender and not
//     reset) and renderSunFlare (material float, likewise) are reliable here.
//   - renderOnCurrentCamera is reset to 0 in the hook's OnPostRender, so it is not
//     read; the hook's presence on the clone is logged instead.
//
// Reflection-only and inert unless configured. Never shipped-on: gated by the
// caller on the debug flag.

using System;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class ScattererFlareProbe : MonoBehaviour
    {
        // Configured by ScattererIntegration after AddComponent.
        public Type HookType;                 // Scatterer.SunflareCameraHook
        public FieldInfo HookFlareField;      // SunflareCameraHook.flare -> SunFlare
        public PropertyInfo FlareRenderingProp; // SunFlare.FlareRendering (bool)
        public FieldInfo MaterialField;       // SunFlare.sunglareMaterial (Material)
        public FieldInfo FlareGoField;        // SunFlare.sunflareGameObject (GameObject)

        private int _frame;

        private void OnPostRender()
        {
            _frame++;
            if (_frame % 120 != 0) return; // once every ~120 renders
            try
            {
                var hook = HookType != null ? GetComponent(HookType) : null;
                if (hook == null)
                {
                    Debug.Log($"[Kerbcast-flareprobe] cam={name} hookPresent=False (flare hook was never copied onto this clone)");
                    return;
                }

                var flare = HookFlareField?.GetValue(hook);
                if (flare == null)
                {
                    Debug.Log($"[Kerbcast-flareprobe] cam={name} hookPresent=True flare=null");
                    return;
                }

                bool rendering = FlareRenderingProp != null && (bool)FlareRenderingProp.GetValue(flare, null);
                var mat = MaterialField?.GetValue(flare) as Material;
                float rsf = mat != null && mat.HasProperty("renderSunFlare") ? mat.GetFloat("renderSunFlare") : -1f;
                float dbuf = mat != null && mat.HasProperty("useDbufferOnCamera") ? mat.GetFloat("useDbufferOnCamera") : -1f;
                var go = FlareGoField?.GetValue(flare) as GameObject;
                Debug.Log(
                    $"[Kerbcast-flareprobe] cam={name} hookPresent=True FlareRendering={rendering} " +
                    $"renderSunFlare={rsf:F0} useDbufferOnCamera={dbuf:F0} " +
                    $"flareGO.layer={(go != null ? go.layer.ToString() : "?")} " +
                    $"flareGO.active={(go != null ? go.activeInHierarchy.ToString() : "?")}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[Kerbcast-flareprobe] cam={name} error: {ex.Message}");
            }
        }
    }
}
