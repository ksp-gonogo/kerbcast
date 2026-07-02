// Diagnostic-only probe for the Scatterer sunflare on a kerbcast clone. Attached
// to the near clone by ScattererIntegration when DebugCameraLogging is on, it
// reads Scatterer's OWN flare state during the clone's manual render and logs it,
// so we work from Scatterer's numbers instead of a reconstruction.
//
// It also counts which camera messages fire during a manual Camera.Render()
// (OnPreCull / OnPreRender / OnPostRender) and reads the copied hook's enabled
// flag and useDbufferOnCamera field, to tell apart "hook is disabled" from
// "OnPreRender does not fire on a manual render" - the two reasons the copied
// SunflareCameraHook would never run on the clone.
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
        public FieldInfo HookDbufferField;    // SunflareCameraHook.useDbufferOnCamera (float)

        private int _frame;
        private int _preCull;
        private int _preRender;
        private int _postRender;

        private void OnPreCull() => _preCull++;
        private void OnPreRender() => _preRender++;

        private void OnPostRender()
        {
            _postRender++;
            _frame++;
            if (_frame % 120 != 0) return; // once every ~120 renders
            try
            {
                var hook = HookType != null ? GetComponent(HookType) : null;
                if (hook == null)
                {
                    Debug.Log($"[Kerbcast-flareprobe] cam={name} hookPresent=False " +
                        $"fires(cull/pre/post)={_preCull}/{_preRender}/{_postRender}");
                    return;
                }

                bool hookEnabled = hook is Behaviour b && b.enabled;
                float hookDbuf = HookDbufferField != null ? (float)HookDbufferField.GetValue(hook) : -1f;

                var flare = HookFlareField?.GetValue(hook);
                bool rendering = false;
                float rsf = -1f, matDbuf = -1f;
                int goLayer = -1; bool goActive = false; bool haveGo = false;
                if (flare != null)
                {
                    rendering = FlareRenderingProp != null && (bool)FlareRenderingProp.GetValue(flare, null);
                    var mat = MaterialField?.GetValue(flare) as Material;
                    if (mat != null)
                    {
                        if (mat.HasProperty("renderSunFlare")) rsf = mat.GetFloat("renderSunFlare");
                        if (mat.HasProperty("useDbufferOnCamera")) matDbuf = mat.GetFloat("useDbufferOnCamera");
                    }
                    var go = FlareGoField?.GetValue(flare) as GameObject;
                    if (go != null) { goLayer = go.layer; goActive = go.activeInHierarchy; haveGo = true; }
                }

                Debug.Log(
                    $"[Kerbcast-flareprobe] cam={name} hookPresent=True hookEnabled={hookEnabled} " +
                    $"hookDbufField={hookDbuf:F0} fires(cull/pre/post)={_preCull}/{_preRender}/{_postRender} " +
                    $"FlareRendering={rendering} mat.renderSunFlare={rsf:F0} mat.useDbuffer={matDbuf:F0} " +
                    $"flareGO.layer={(haveGo ? goLayer.ToString() : "?")} " +
                    $"flareGO.active={(haveGo ? goActive.ToString() : "?")}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[Kerbcast-flareprobe] cam={name} error: {ex.Message}");
            }
        }
    }
}
