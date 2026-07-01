// Scatterer capture for kerbcast's cloned cameras. Attaches a per-clone
// ScattererCameraSwap so Scatterer renders its atmosphere/scattering/ocean onto
// each clone during the clone's render, then restores the stock camera
// reference. Scatterer's screen-space scattering and ocean command buffers
// self-attach to the clone via their own OnWillRenderObject once the swap points
// the singleton at the clone, so no buffer copying is needed for the core look.
//
// The sunflare needs more than the swap. Scatterer draws each flare as a
// full-screen quad (layer 15 in flight) whose vertices the flare shader culls
// unless renderOnCurrentCamera == 1. That flag is raised only by a
// SunflareCameraHook.OnPreRender running on the camera doing the render, and the
// stock hook lives on Scatterer's own near camera, not on our clone. Without a
// driven hook on the near clone the flare quad is culled and the stream shows a
// dark blob where the sun should be. So on the near layer we add one
// SunflareCameraHook per live SunFlare, each wired to its flare with
// useDbufferOnCamera = 1 (the near-camera value Scatterer itself uses), and force
// depthTextureMode |= Depth so the hook's dbuffer occlusion check reads a valid
// _CameraDepthTexture. The hook resets renderOnCurrentCamera to 0 in its own
// OnPostRender, so the player's main view (driven by Scatterer's own hook) is
// unaffected. SkyNode uniforms and DepthToDistanceCommandBuffer are deliberately
// not replicated - see the notes in ApplyToLayer.
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

        // Sunflare hook wiring. All optional: if any resolve to null the swap
        // still ships and the sunflare copy simply no-ops.
        private FieldInfo _sunflareManagerField; // Instance.sunflareManager
        private FieldInfo _flaresField;          // SunflareManager.scattererSunFlares (Dictionary<string,SunFlare>)
        private Type _sunflareHookType;          // Scatterer.SunflareCameraHook
        private FieldInfo _hookFlareField;       // SunflareCameraHook.flare
        private FieldInfo _hookDbufferField;     // SunflareCameraHook.useDbufferOnCamera

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

                // Sunflare hook wiring is optional: resolve it, but a null anywhere
                // here only drops the sunflare copy, never the swap.
                _sunflareManagerField = t.GetField("sunflareManager", PubInst);
                _sunflareHookType = asm.GetType("Scatterer.SunflareCameraHook");
                if (_sunflareHookType != null)
                {
                    _hookFlareField = _sunflareHookType.GetField("flare", PubInst);
                    _hookDbufferField = _sunflareHookType.GetField("useDbufferOnCamera", PubInst);
                }
                var managerType = _sunflareManagerField?.FieldType;
                _flaresField = managerType?.GetField("scattererSunFlares", PubInst);

                _ready = true;
                bool sunflareReady = _sunflareManagerField != null && _flaresField != null
                    && _sunflareHookType != null && _hookFlareField != null && _hookDbufferField != null;
                Debug.Log($"{LogTag} integration enabled " +
                    $"(scaled={_scaledField != null} far={_farField != null} " +
                    $"unified={_unifiedField != null} sunflare={sunflareReady})");
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

                // The sunflare quad lives on layer 15 in flight, which only the near
                // clone renders, and it needs a driven SunflareCameraHook to un-cull
                // it (see the file header). SkyNode uniforms are not copied: SkyNode
                // is a per-body component, not a camera component, and it already
                // reads Instance.nearCamera / scaledSpaceCamera, which the swap above
                // redirects to this clone during its render, so the swap already
                // feeds SkyNode the clone camera. DepthToDistanceCommandBuffer is
                // also not copied: it is a !unified near-camera hook that writes to a
                // process-static RenderTexture sized to the stock camera's target,
                // so running it on our differently-sized clone RT would thrash that
                // shared texture and corrupt the player's ocean/flare depth. In the
                // unified mode this environment reports it is unused anyway.
                if (layer == CameraLayers.Near)
                    ReplicateSunflareHooks(cam);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} apply to {cam.name} ({layer}) failed: {ex.Message}");
                RemoveFromLayer(cam, layer);
            }
        }

        // Add one SunflareCameraHook per live SunFlare to the near clone, wired to
        // the flare with the near-camera dbuffer value, and force a depth pass so
        // the hook's occlusion check reads a valid _CameraDepthTexture. Mirrors what
        // SunFlare.start() does for Scatterer's own near camera. No-op if the
        // sunflare wiring did not fully resolve, or if no flares exist yet.
        private void ReplicateSunflareHooks(Camera cam)
        {
            if (_sunflareManagerField == null || _flaresField == null
                || _sunflareHookType == null || _hookFlareField == null || _hookDbufferField == null)
                return;

            var inst = _instanceProp.GetValue(null, null);
            var manager = inst == null ? null : _sunflareManagerField.GetValue(inst);
            var flares = manager == null ? null : _flaresField.GetValue(manager) as System.Collections.IDictionary;
            if (flares == null || flares.Count == 0) return;

            // The hook reads _CameraDepthTexture for its dbuffer occlusion test
            // (useDbufferOnCamera = 1); the clone must produce depth for that.
            cam.depthTextureMode |= DepthTextureMode.Depth;
            _depthSet.Add(cam);

            foreach (var flare in flares.Values)
            {
                if (flare == null) continue;
                var hook = cam.gameObject.AddComponent(_sunflareHookType);
                _hookFlareField.SetValue(hook, flare);
                _hookDbufferField.SetValue(hook, 1f);
                Track(cam, hook);
            }
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
