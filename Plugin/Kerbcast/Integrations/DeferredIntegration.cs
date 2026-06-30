// Deferred (deferred-rendering) support for kerbcast's cloned cameras. When the
// Deferred mod is installed it switches KSP to deferred shading; kerbcast's clones
// inherit renderingPath = DeferredShading via CopyFrom but not Deferred's
// ForwardRenderingCompatibility component, so a deferred clone renders KSP's
// forward-authored materials black. This integration adds Deferred's own
// ForwardRenderingCompatibility to each near/far clone that is in deferred and
// calls Init(15) (the local-layer index Deferred itself uses), reverting the clone
// to Forward if that fails so a clone is never left half-deferred and black.
//
// The scaled clone is deliberately untouched: kerbcast forces it to Forward to
// dodge the Mesa/OpenGL deferred-RT bug on the tier-1 Linux platform. Reflection
// -only; absent or disabled Deferred no-ops.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class DeferredIntegration : ICameraModIntegration
    {
        private const string LogTag = "[Kerbcast-Deferred]";
        private const int LocalLayer = 15; // Deferred's firstLocalCamera Init value

        private bool _probed;
        private bool _ready;
        private Type _compatType;     // Deferred.ForwardRenderingCompatibility
        private MethodInfo _initMethod; // ForwardRenderingCompatibility.Init(int)

        private readonly Dictionary<Camera, List<Component>> _added = new Dictionary<Camera, List<Component>>();

        public string Name => "Deferred";
        public bool ForcesNoMsaa => false;
        public bool NeedsPerFrame => false;
        // Near and far local clones. Scaled stays kerbcast's forced Forward; galaxy
        // is never deferred.
        public CameraLayers AppliesToLayers => CameraLayers.Near | CameraLayers.Far;

        public bool IsAvailable { get { Probe(); return _ready; } }

        private void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                if (!KerbcastSettings.EnableDeferred)
                {
                    Debug.Log($"{LogTag} disabled by settings; Deferred support off");
                    return;
                }
                var asm = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name == "Deferred")?.assembly;
                _compatType = asm?.GetType("Deferred.ForwardRenderingCompatibility");
                if (_compatType == null)
                {
                    Debug.Log($"{LogTag} Deferred not installed; support disabled");
                    return;
                }
                _initMethod = _compatType.GetMethod("Init",
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                if (_initMethod == null)
                {
                    Debug.LogWarning($"{LogTag} ForwardRenderingCompatibility.Init(int) missing; unsupported version");
                    return;
                }
                _ready = true;
                Debug.Log($"{LogTag} integration enabled");
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
            // Only act on a clone that actually inherited the deferred path.
            if (cam.renderingPath != RenderingPath.DeferredShading) return;
            try
            {
                var comp = cam.gameObject.AddComponent(_compatType);
                _initMethod.Invoke(comp, new object[] { LocalLayer });
                Track(cam, comp);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} apply to {cam.name} ({layer}) failed: {ex.Message}");
                // A half-deferred camera renders black; fall back to forward.
                cam.renderingPath = RenderingPath.Forward;
                RemoveFromLayer(cam, layer);
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
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} remove from {cam.name} failed: {ex.Message}");
            }
        }

        public void PerFrame(Camera cam, CameraLayers layer, in IntegrationFrameState state) { }
    }
}
