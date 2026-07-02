// Un-culls Scatterer's sunflare quad for a kerbcast near clone.
//
// Scatterer draws each sun flare as a layer-15 quad the flare shader culls
// unless its shared sunglareMaterial has renderOnCurrentCamera == 1. Scatterer
// raises that flag only inside its own SunflareCameraHook.OnPreRender, which
// runs during the REAL near camera's render window and lowers it again in
// OnPostRender. Our clone renders in a different window, so the flag is 0 while
// the clone renders layer 15 and the quad is culled: the stream shows a dark
// blob where the sun should be.
//
// This component (attached to the near clone) raises the same flag for the
// clone's render and lowers it again afterwards, writing only the values
// Scatterer itself writes. It deliberately does NOT call SunFlare.
// updateProperties() and never touches renderSunFlare. updateProperties() runs
// the occlusion raycast from Scatterer.Instance.nearCamera, which the per-clone
// ScattererCameraSwap has repointed at our on-vessel clone during this window;
// running it here would raycast from inside the ship, conclude the sun is
// occluded, and force the flare off (the failure that sank the reverted
// approach in 805c34b). Instead the flare's visibility stays Scatterer's own
// decision, computed by the real-camera hook from its external clear-view
// raycast.
//
// Camera renders are sequential on the main thread, so render windows never
// overlap; lowering the flag in OnPostRender only returns it to its resting
// state, exactly as Scatterer's hook does, so the player's main view is never
// corrupted.
//
// Reflection-only: no compile-time Scatterer reference. All handles optional;
// absent Scatterer or an unresolved flare list no-ops.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class ScattererSunflareUncull : MonoBehaviour
    {
        private const string LogTag = "[Kerbcast-Scatterer]";

        // Reflection handles into the live flare list, set by ScattererIntegration:
        //   InstanceProperty         Scatterer.Scatterer.Instance (static getter)
        //   SunflareManagerField     Instance.sunflareManager
        //   ScattererSunFlaresField  SunflareManager.scattererSunFlares (dict)
        //   SunglareMaterialField    SunFlare.sunglareMaterial (Material)
        public PropertyInfo InstanceProperty;
        public FieldInfo SunflareManagerField;
        public FieldInfo ScattererSunFlaresField;
        public FieldInfo SunglareMaterialField;

        private static readonly int RenderOnCurrentCamera = Shader.PropertyToID("renderOnCurrentCamera");
        private static readonly int UseDbufferOnCamera = Shader.PropertyToID("useDbufferOnCamera");

        private bool _raised;

        private void OnPreRender()
        {
            _raised = false;
            foreach (var mat in EnumerateFlareMaterials())
            {
                // useDbufferOnCamera = 1 is the near-camera value Scatterer uses;
                // the clone forces a depth pass (ScattererIntegration) so the flare
                // shader's soft occlusion against near geometry reads valid depth.
                mat.SetFloat(RenderOnCurrentCamera, 1.0f);
                mat.SetFloat(UseDbufferOnCamera, 1.0f);
                _raised = true;
            }
        }

        private void OnPostRender() => Lower();
        private void OnDisable() => Lower();

        private void Lower()
        {
            if (!_raised) return;
            _raised = false;
            try
            {
                foreach (var mat in EnumerateFlareMaterials())
                    mat.SetFloat(RenderOnCurrentCamera, 0.0f);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} sunflare uncull restore failed: {ex.Message}");
            }
        }

        // Live-resolves the flare materials from the singleton each render, so a
        // Scatterer whose sunflareManager inits after our clone is built (its
        // InitCoroutine spreads across frames) is still picked up. Skips any null
        // handle or flare so a partial version match simply no-ops.
        private IEnumerable<Material> EnumerateFlareMaterials()
        {
            if (InstanceProperty == null || SunflareManagerField == null ||
                ScattererSunFlaresField == null || SunglareMaterialField == null)
                yield break;

            IDictionary flares = null;
            try
            {
                var inst = InstanceProperty.GetValue(null, null);
                if (inst == null) yield break;
                var manager = SunflareManagerField.GetValue(inst);
                if (manager == null) yield break;
                flares = ScattererSunFlaresField.GetValue(manager) as IDictionary;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} sunflare enumerate failed: {ex.Message}");
                yield break;
            }
            if (flares == null) yield break;

            foreach (var flare in flares.Values)
            {
                Material mat = null;
                try
                {
                    if (flare != null)
                        mat = SunglareMaterialField.GetValue(flare) as Material;
                }
                catch { mat = null; }
                if (mat != null) yield return mat;
            }
        }
    }
}
