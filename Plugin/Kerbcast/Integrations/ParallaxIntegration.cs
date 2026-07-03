// ParallaxContinued scatter capture for kerbcast's near and far terrain clones. Resolves
// Parallax's ScatterManager + ScatterRenderer by reflection, then attaches a
// ParallaxScatterDrive to each clone that re-submits Parallax's already-evaluated
// scatter draws to the clone. Also ORs the scatter layer (15) into the clone's
// cullingMask so the draws land. See ParallaxScatterDrive for why we re-submit
// rather than re-evaluate.
//
// Targets the maintained ParallaxContinued (Parallax.ScatterManager). Reflection
// -only; absent or disabled Parallax no-ops.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class ParallaxIntegration : ICameraModIntegration
    {
        private const string LogTag = "[Kerbcast-Parallax]";
        private const int ScatterLayer = 15;

        private bool _probed;
        private bool _ready;

        private FieldInfo _activeRenderersField;
        private FieldInfo _managerInstance;
        private MethodInfo _renderInCameras;

        private readonly Dictionary<Camera, List<Component>> _added = new Dictionary<Camera, List<Component>>();
        private readonly HashSet<Camera> _maskSet = new HashSet<Camera>();

        public string Name => "Parallax";
        public bool ForcesNoMsaa => false;
        public bool NeedsPerFrame => false;
        public CameraLayers AppliesToLayers => CameraLayers.Near | CameraLayers.Far;

        public bool IsAvailable { get { Probe(); return _ready; } }

        private void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                if (!KerbcastSettings.EnableParallax)
                {
                    Debug.Log($"{LogTag} disabled by settings; Parallax capture off");
                    return;
                }
                var asm = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name == "Parallax")?.assembly;
                var manager = asm?.GetType("Parallax.ScatterManager");
                var renderer = asm?.GetType("Parallax.ScatterRenderer");
                if (manager == null || renderer == null)
                {
                    Debug.Log($"{LogTag} ParallaxContinued not installed; capture disabled");
                    return;
                }

                const BindingFlags PubStat = BindingFlags.Public | BindingFlags.Static;
                const BindingFlags PubInst = BindingFlags.Public | BindingFlags.Instance;
                _managerInstance = manager.GetField("Instance", PubStat);
                _activeRenderersField = manager.GetField("activeScatterRenderers", PubInst);
                _renderInCameras = renderer.GetMethod("RenderInCameras", PubInst,
                    null, new[] { typeof(Camera).MakeArrayType() }, null);

                if (_managerInstance == null || _activeRenderersField == null
                    || _renderInCameras == null)
                {
                    Debug.LogWarning($"{LogTag} expected ParallaxContinued members missing; unsupported version");
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
            if (cam == null || (layer != CameraLayers.Near && layer != CameraLayers.Far)) return;
            Probe();
            if (!_ready) return;
            try
            {
                // Let the clone see the scatter layer so the draws land on it.
                if ((cam.cullingMask & (1 << ScatterLayer)) == 0)
                {
                    cam.cullingMask |= (1 << ScatterLayer);
                    _maskSet.Add(cam);
                }

                var drive = cam.gameObject.AddComponent<ParallaxScatterDrive>();
                drive.ManagerInstance = _managerInstance;
                drive.ActiveRenderersField = _activeRenderersField;
                drive.RenderInCamerasMethod = _renderInCameras;
                Track(cam, drive);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} apply to {cam.name} failed: {ex.Message}");
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
                if (_maskSet.Remove(cam))
                    cam.cullingMask &= ~(1 << ScatterLayer);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} remove from {cam.name} failed: {ex.Message}");
            }
        }

        public void PerFrame(Camera cam, CameraLayers layer, in IntegrationFrameState state) { }
    }
}
