// EVE (Environmental Visual Enhancements) capture for kerbcast's cloned cameras.
//
// EVE's clouds and atmosphere reach a kerbcast clone automatically: EVE attaches
// a DeferredRenderer to each PQS terrain quad whose OnWillRenderObject fires for
// any camera whose cullingMask includes the terrain layer, and our clones inherit
// that mask from CopyFrom. So no buffer copy is needed for clouds. What does NOT
// carry over is (1) the depth texture EVE's soft-cloud-edge shader keyword reads,
// and (2) the two per-camera local components EVE adds to the stock near camera
// (city lights, celestial shadows), which do not self-replicate.
//
// Reflection-only: no compile-time EVE reference. Absent or disabled EVE no-ops.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class EVEIntegration : ICameraModIntegration
    {
        private const string LogTag = "[Kerbcast-EVE]";

        private bool _probed;
        private bool _ready;
        private Type _localCityType;    // CityLights.LocalCityComponent
        private Type _localShadowType;  // CelestialShadows.LocalShadowComponent

        // Per-clone bookkeeping so RemoveFromLayer pulls exactly what we added.
        private readonly Dictionary<Camera, List<Component>> _added =
            new Dictionary<Camera, List<Component>>();
        private readonly HashSet<Camera> _depthSet = new HashSet<Camera>();

        public string Name => "EVE";
        public bool ForcesNoMsaa => false;
        public bool NeedsPerFrame => false;

        // Local components live on the near camera; the depth texture is needed on
        // near, far, and scaled (clouds render on all three). Galaxy is untouched.
        public CameraLayers AppliesToLayers =>
            CameraLayers.Near | CameraLayers.Far | CameraLayers.Scaled;

        public bool IsAvailable { get { Probe(); return _ready; } }

        private void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                if (!KerbcastSettings.EnableEVE)
                {
                    Debug.Log($"{LogTag} disabled by settings; EVE capture off");
                    return;
                }
                // EVE ships its shader loader under the ShaderLoader assembly; its
                // presence is the install signal across both EVE generations. We probe
                // ShaderLoaderClass (EVE-owned, not stock KSP) rather than the sheet's
                // Atmosphere.CloudsObject because ShaderLoaderClass also exposes a
                // "loaded" readiness flag and the ShaderLoader assembly name is
                // unambiguously EVE's; the cloud types still resolve below as needed.
                var shaderLoader = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name == "ShaderLoader")?.assembly;
                if (shaderLoader == null ||
                    shaderLoader.GetType("ShaderLoader.ShaderLoaderClass") == null)
                {
                    Debug.Log($"{LogTag} EVE not installed; cloud passthrough disabled");
                    return;
                }
                // Local-effect components are optional polish: resolve if present.
                _localCityType = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name == "CityLights")?.assembly
                    ?.GetType("CityLights.LocalCityComponent");
                _localShadowType = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name == "CelestialShadows")?.assembly
                    ?.GetType("CelestialShadows.LocalShadowComponent");

                _ready = true;
                Debug.Log($"{LogTag} integration enabled " +
                    $"(cityLights={_localCityType != null} celestialShadows={_localShadowType != null})");
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
                // Soft-depth cloud blending (SOFT_DEPTH_ON) reads _CameraDepthTexture.
                // EVE sets this on the stock near/far/scaled cameras; our clones need
                // it set explicitly.
                cam.depthTextureMode |= DepthTextureMode.Depth;
                _depthSet.Add(cam);

                // The two local components live on the stock near camera only.
                if (layer == CameraLayers.Near)
                {
                    var stock = Camera.allCameras.FirstOrDefault(c => c.name == "Camera 00");
                    if (stock != null)
                    {
                        ReplicateComponents(stock, cam, _localCityType);
                        ReplicateComponents(stock, cam, _localShadowType);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} apply to {cam.name} ({layer}) failed: {ex.Message}");
                RemoveFromLayer(cam, layer); // never leave a clone half-wired
            }
        }

        // For each instance of `type` on the stock camera, add one to the clone and
        // copy its declared fields (the material/light/body references EVE set after
        // AddComponent). The component's OnPreCull then runs during the clone's
        // Render() exactly as it does for the stock camera; the writes it makes are
        // global, idempotent values (sun direction), so the player's main view is
        // unaffected.
        private void ReplicateComponents(Camera stock, Camera clone, Type type)
        {
            if (type == null) return;
            var sources = stock.gameObject.GetComponents(type);
            foreach (var src in sources)
            {
                var dst = clone.gameObject.AddComponent(type);
                ComponentClone.CopyDeclaredFields(src, dst);
                Track(clone, dst);
            }
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
                        if (c != null) UnityEngine.Object.Destroy(c);
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
