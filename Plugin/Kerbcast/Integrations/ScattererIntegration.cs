// Scatterer capture for kerbcast's cloned cameras. Attaches a per-clone
// ScattererCameraSwap so Scatterer renders its atmosphere/scattering/ocean onto
// each clone during the clone's render, then restores the stock camera
// reference. Scatterer's screen-space scattering and ocean command buffers
// self-attach to the clone via their own OnWillRenderObject once the swap points
// the singleton at the clone, so no buffer copying is needed for the core look.
//
// The sunflare needs one more piece. Scatterer draws each flare as a layer-15
// quad the flare shader culls unless renderOnCurrentCamera == 1, a flag raised
// only by Scatterer's own SunflareCameraHook running on the real near camera. On
// the near clone we add a ScattererSunflareUncull that raises that same flag for
// the clone's render (and lowers it after), so the quad draws. It does NOT run
// the flare's occlusion raycast: that stays Scatterer's own decision, made by
// the real-camera hook from its external clear-view. The clone gets a forced
// depth pass so the flare shader's soft occlusion against near geometry reads
// valid depth; RemoveFromLayer clears exactly that.
//
// Scatterer forces QualitySettings.antiAliasing = 0 and its depth-based effects
// break under MSAA, so this integration reports ForcesNoMsaa = true; the host
// then drives every clone and the capture RT with MSAA off.
//
// Reflection-only: no compile-time Scatterer reference. Absent or disabled
// Scatterer no-ops.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class ScattererIntegration : ICameraModIntegration
    {
        private const string LogTag = "[Kerbcast-Scatterer]";

        private bool _probed;
        private bool _ready;
        private PropertyInfo _instanceProp;     // Scatterer.Scatterer.Instance (static)
        private FieldInfo _nearField;           // Instance.nearCamera
        private FieldInfo _scaledField;         // Instance.scaledSpaceCamera (fallback scaledCamera)
        private FieldInfo _farField;            // Instance.farCamera
        private FieldInfo _unifiedField;        // Instance.unifiedCameraMode (may be null on old versions)

        // Sunflare un-cull wiring, all optional: if any resolves to null the swap
        // still ships and the sunflare copy simply no-ops (dark blob, logged).
        private FieldInfo _sunflareManagerField;   // Instance.sunflareManager
        private FieldInfo _scattererSunFlaresField; // SunflareManager.scattererSunFlares (Dictionary<string,SunFlare>)
        private FieldInfo _sunglareMaterialField;   // SunFlare.sunglareMaterial (Material)
        private bool _sunflareReady;

        private readonly Dictionary<Camera, List<Component>> _added = new Dictionary<Camera, List<Component>>();
        // Cameras we forced depthTextureMode Depth on, so RemoveFromLayer clears
        // exactly what it set.
        private readonly HashSet<Camera> _depthSet = new HashSet<Camera>();

        public string Name => "Scatterer";
        public bool ForcesNoMsaa => true;
        public bool NeedsPerFrame => false;
        // Near, scaled, and (dual-cam only) far. Scatterer never touches galaxy.
        public CameraLayers AppliesToLayers =>
            CameraLayers.Near | CameraLayers.Scaled | CameraLayers.Far;

        public bool IsAvailable { get { Probe(); return _ready; } }

        private void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                if (!KerbcastSettings.EnableScatterer)
                {
                    Debug.Log($"{LogTag} disabled by settings; Scatterer capture off");
                    return;
                }
                // Assembly name casing varies by build ("scatterer" in the source
                // csproj, "Scatterer" as the installed DLL registers); match either.
                var asm = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => string.Equals(a.name, "scatterer", StringComparison.OrdinalIgnoreCase))?.assembly;
                var t = asm?.GetType("Scatterer.Scatterer");
                if (t == null)
                {
                    Debug.Log($"{LogTag} Scatterer not installed; capture disabled");
                    return;
                }

                const BindingFlags PubInst = BindingFlags.Public | BindingFlags.Instance;
                _instanceProp = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _nearField = t.GetField("nearCamera", PubInst);
                _scaledField = t.GetField("scaledSpaceCamera", PubInst) ?? t.GetField("scaledCamera", PubInst);
                _farField = t.GetField("farCamera", PubInst);
                _unifiedField = t.GetField("unifiedCameraMode", PubInst);

                if (_instanceProp == null || _nearField == null)
                {
                    Debug.LogWarning($"{LogTag} expected Scatterer members missing; unsupported version");
                    return;
                }

                // Sunflare un-cull wiring is optional: a null anywhere here only
                // drops the sunflare copy, never the swap.
                _sunflareManagerField = t.GetField("sunflareManager", PubInst);
                var managerType = _sunflareManagerField?.FieldType;
                _scattererSunFlaresField = managerType?.GetField("scattererSunFlares", PubInst);
                var sunFlareType = asm.GetType("Scatterer.SunFlare");
                _sunglareMaterialField = sunFlareType?.GetField("sunglareMaterial", PubInst);
                _sunflareReady = _sunflareManagerField != null && _scattererSunFlaresField != null
                    && _sunglareMaterialField != null;

                _ready = true;
                Debug.Log($"{LogTag} integration enabled " +
                    $"(scaled={_scaledField != null} far={_farField != null} " +
                    $"unified={_unifiedField != null} sunflare={_sunflareReady})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} probe failed: {ex.Message}");
                _ready = false;
            }
        }

        public void ApplyToLayer(Camera cam, CameraLayers layer)
        {
            if (cam == null) return;
            Probe();
            if (!_ready) return;
            try
            {
                FieldInfo field = null;
                if (layer == CameraLayers.Near) field = _nearField;
                else if (layer == CameraLayers.Scaled) field = _scaledField;
                else if (layer == CameraLayers.Far)
                {
                    // In unified-camera mode Camera 01 is absent and farCamera is
                    // aliased; swapping it could corrupt state, so skip the far swap.
                    if (_farField == null || IsUnifiedCameraMode()) return;
                    field = _farField;
                }
                if (field == null) return;

                var swap = cam.gameObject.AddComponent<ScattererCameraSwap>();
                swap.InstanceProperty = _instanceProp;
                swap.CameraField = field;
                Track(cam, swap);

                // The flare quad is layer 15 in flight, rendered only by the near
                // clone, and needs its cull flag raised for the clone (see header).
                if (layer == CameraLayers.Near && _sunflareReady)
                    AddSunflareUncull(cam);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} apply to {cam.name} ({layer}) failed: {ex.Message}");
                RemoveFromLayer(cam, layer);
            }
        }

        // Attach the flare un-cull to the near clone and force a depth pass so the
        // flare shader's soft occlusion (useDbufferOnCamera = 1) reads valid depth.
        // The component lowers the cull flag in its own OnPostRender/OnDisable, so
        // the player's main view is never left with the flag raised.
        private void AddSunflareUncull(Camera cam)
        {
            cam.depthTextureMode |= DepthTextureMode.Depth;
            _depthSet.Add(cam);

            var uncull = cam.gameObject.AddComponent<ScattererSunflareUncull>();
            uncull.InstanceProperty = _instanceProp;
            uncull.SunflareManagerField = _sunflareManagerField;
            uncull.ScattererSunFlaresField = _scattererSunFlaresField;
            uncull.SunglareMaterialField = _sunglareMaterialField;
            Track(cam, uncull);
        }

        private bool IsUnifiedCameraMode()
        {
            if (_unifiedField == null || _instanceProp == null) return false;
            try
            {
                var inst = _instanceProp.GetValue(null, null);
                return inst != null && (bool)_unifiedField.GetValue(inst);
            }
            catch { return false; }
        }

        private void Track(Camera cam, Component c)
        {
            if (!_added.TryGetValue(cam, out var list))
            {
                list = new List<Component>();
                _added[cam] = list;
            }
            list.Add(c);
        }

        public void RemoveFromLayer(Camera cam, CameraLayers layer)
        {
            if (cam == null) return;
            try
            {
                if (_added.TryGetValue(cam, out var list))
                {
                    foreach (var c in list)
                        if (c != null) UnityEngine.Object.Destroy(c); // OnDisable restores
                    _added.Remove(cam);
                }
                if (_depthSet.Remove(cam))
                    cam.depthTextureMode &= ~DepthTextureMode.Depth;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} remove from {cam.name} failed: {ex.Message}");
            }
        }

        public void PerFrame(Camera cam, CameraLayers layer, in IntegrationFrameState state) { }
    }
}
