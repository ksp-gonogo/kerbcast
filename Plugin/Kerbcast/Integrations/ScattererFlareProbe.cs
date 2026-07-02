// Diagnostic-only probe for the Scatterer sunflare on a kerbcast clone. Attached
// to the near clone by ScattererIntegration when DebugCameraLogging is on, it
// reads Scatterer's OWN flare state during the clone's manual render and logs it,
// so we work from Scatterer's numbers instead of a reconstruction.
//
// OnPreRender (fires after the camera swaps run in OnPreCull) replicates the gate
// math Scatterer's updateProperties uses - the sun's viewport via the current
// Instance.scaledSpaceCamera - so we can see the real val.z and which camera is
// actually backing the swap. OnPostRender logs the resulting flare state.
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
        public PropertyInfo InstanceProp;     // Scatterer.Scatterer.Instance
        public FieldInfo NearField;           // Instance.nearCamera
        public FieldInfo ScaledField;         // Instance.scaledSpaceCamera
        public FieldInfo SourceScaledTransformField; // SunFlare.sourceScaledTransform
        public FieldInfo SourceField;         // SunFlare.source (CelestialBody)
        public FieldInfo CbmField;            // Scatterer.scattererCelestialBodiesManager
        public FieldInfo UnderwaterField;     // <cbm>.underwater

        private int _preCull;
        private int _preRender;
        private int _postRender;

        private void OnPreCull() => _preCull++;

        private void OnPreRender()
        {
            _preRender++;
            if (_preRender % 120 != 0) return;
            try
            {
                var inst = InstanceProp?.GetValue(null, null);
                var nearCam = inst != null ? NearField?.GetValue(inst) as Camera : null;
                var scaledCam = inst != null ? ScaledField?.GetValue(inst) as Camera : null;

                var hook = HookType != null ? GetComponent(HookType) : null;
                var flare = hook != null ? HookFlareField?.GetValue(hook) : null;
                var sst = flare != null ? SourceScaledTransformField?.GetValue(flare) as Transform : null;

                string valStr = "n/a"; string angStr = "n/a";
                if (scaledCam != null && sst != null)
                {
                    Vector3 val = scaledCam.WorldToViewportPoint(sst.position);
                    valStr = $"({val.x:F2},{val.y:F2},z{(val.z >= 0 ? "+" : "-")}{val.z:F0})";
                    angStr = Vector3.Angle(scaledCam.transform.forward,
                        sst.position - scaledCam.transform.position).ToString("F1");
                }

                // Replicate Scatterer's two occlusion raycasts (updateProperties):
                // near-space toward the CelestialBody on layers 0|15, then, if clear,
                // scaled-space toward the scaled sun on layer 10. Either hit -> flare
                // gated off. No 10km filter (unlike our flarelog): any hit counts.
                string r1 = "n/a";
                var src = SourceField?.GetValue(flare) as CelestialBody;
                if (nearCam != null && src != null && src.transform != null)
                {
                    Vector3 o = nearCam.transform.position;
                    Vector3 d = (src.transform.position - o).normalized;
                    r1 = Physics.Raycast(o, d, out var h1, Mathf.Infinity, 32769)
                        ? $"HIT {h1.collider?.name}@{h1.distance:F0}m" : "clear";
                }
                string r2 = "n/a";
                if (scaledCam != null && sst != null)
                {
                    Vector3 o = scaledCam.transform.position;
                    Vector3 d = (sst.position - o).normalized;
                    r2 = Physics.Raycast(o, d, out var h2, Mathf.Infinity, 1024)
                        ? $"HIT {h2.collider?.name}@{h2.distance:F0}m" : "clear";
                }
                string uw = "?";
                if (inst != null && CbmField != null && UnderwaterField != null)
                {
                    var cbm = CbmField.GetValue(inst);
                    if (cbm != null) uw = UnderwaterField.GetValue(cbm).ToString();
                }

                Debug.Log(
                    $"[Kerbcast-flareprobe-in] cam={name} " +
                    $"Instance.near='{(nearCam != null ? nearCam.name : "null")}' " +
                    $"Instance.scaled='{(scaledCam != null ? scaledCam.name : "null")}' " +
                    $"scaledCamSunAngle={angStr} val={valStr} " +
                    $"ray1(near|0,15)={r1} ray2(scaled|10)={r2} underwater={uw}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[Kerbcast-flareprobe-in] cam={name} error: {ex.Message}");
            }
        }

        private void OnPostRender()
        {
            _postRender++;
            if (_postRender % 120 != 0) return;
            try
            {
                var hook = HookType != null ? GetComponent(HookType) : null;
                if (hook == null)
                {
                    Debug.Log($"[Kerbcast-flareprobe] cam={name} hookPresent=False");
                    return;
                }
                bool hookEnabled = hook is Behaviour b && b.enabled;
                var flare = HookFlareField?.GetValue(hook);
                bool rendering = flare != null && FlareRenderingProp != null
                    && (bool)FlareRenderingProp.GetValue(flare, null);
                Debug.Log($"[Kerbcast-flareprobe] cam={name} hookEnabled={hookEnabled} " +
                    $"FlareRendering={rendering}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[Kerbcast-flareprobe] cam={name} error: {ex.Message}");
            }
        }
    }
}
