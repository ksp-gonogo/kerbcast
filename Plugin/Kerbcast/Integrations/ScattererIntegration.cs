// Scatterer capture for kerbcast's cloned cameras. Two parts:
//
// 1. A per-clone ScattererCameraSwap points Scatterer's singleton camera at the
//    clone during its render (then restores), so Scatterer's atmosphere /
//    scattering / ocean command buffers self-attach to the clone.
//
// 2. Copy Scatterer's SunflareCameraHook from the matching stock camera onto the
//    clone (with its fields). The hook's OnPreRender sets renderOnCurrentCamera=1,
//    which stops the shared sunflare mesh (layer 15) being culled for the clone -
//    that is what makes the flare draw at all. We then force the copied hook's
//    useDbufferOnCamera to 0. The near hook ships with it at 1, which depth-occludes
//    the flare CORE against a depth buffer that belongs to the main camera, not the
//    clone, so the core reads as blocked and only the ghosts survive. Scatterer's
//    own line-of-sight raycast (in updateProperties) still gates the whole flare, so
//    gross occlusion by terrain and parts is kept; only per-pixel core occlusion is
//    dropped, which a stream does not need.
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
        private Type[] _scattererHookTypes;     // CameraRenderingHook + SunflareCameraHook types

        private readonly Dictionary<Camera, List<Component>> _added = new Dictionary<Camera, List<Component>>();

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

                // Per-camera hook types we clone onto each camera (name-matched so
                // a version rename is tolerated). SunflareCameraHook draws the lens
                // flare; CameraRenderingHook (absent in unified-camera builds) would
                // carry per-camera scattering/depth setup where a build has it.
                _scattererHookTypes = asm.GetTypes()
                    .Where(x => !x.IsAbstract && typeof(MonoBehaviour).IsAssignableFrom(x)
                        && (x.Name.Contains("CameraRenderingHook") || x.Name.Contains("SunflareCameraHook")))
                    .ToArray();

                _ready = true;
                Debug.Log($"{LogTag} integration enabled " +
                    $"(scaled={_scaledField != null} far={_farField != null} " +
                    $"unified={_unifiedField != null} hooks={_scattererHookTypes.Length})");
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

                // Copy Scatterer's per-camera hooks from the matching stock camera
                // so the clone renders the full per-camera look (flare + occlusion),
                // not just the swapped atmosphere. Near <- Camera 00, Scaled <-
                // Camera ScaledSpace. Far reuses the near swap and needs no hooks.
                if (layer == CameraLayers.Near)
                    CopyRenderingHooks("Camera 00", cam);
                else if (layer == CameraLayers.Scaled)
                    CopyRenderingHooks("Camera ScaledSpace", cam);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} apply to {cam.name} ({layer}) failed: {ex.Message}");
                RemoveFromLayer(cam, layer);
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

        // Clone Scatterer's per-camera hooks (CameraRenderingHook,
        // SunflareCameraHook) from the named stock camera onto the target clone,
        // copying their public fields/properties (flare, useDbufferOnCamera, etc.).
        // Each added hook is tracked so RemoveFromLayer tears it down.
        private void CopyRenderingHooks(string sourceCameraName, Camera target)
        {
            if (_scattererHookTypes == null || _scattererHookTypes.Length == 0) return;
            var source = Camera.allCameras.FirstOrDefault(c => c.name == sourceCameraName);
            if (source == null) return;
            foreach (var hookType in _scattererHookTypes)
            {
                if (target.gameObject.GetComponent(hookType) != null) continue;
                var src = source.gameObject.GetComponent(hookType);
                if (src == null) continue;
                var dst = target.gameObject.AddComponent(hookType);
                if (dst == null) continue;
                CopyPublicMembers(src, dst);
                // The near hook copies useDbufferOnCamera=1, which depth-occludes
                // the flare core against the main camera's depth buffer (not the
                // clone's) and kills the core on the stream. Force it off so the
                // core draws; Scatterer's own raycast still gates the flare.
                if (hookType.Name.Contains("SunflareCameraHook"))
                {
                    var dbuffer = hookType.GetField("useDbufferOnCamera",
                        BindingFlags.Public | BindingFlags.Instance);
                    dbuffer?.SetValue(dst, 0f);
                }
                Track(target, dst);
            }
        }

        // Copy public instance fields and read/write properties from one component
        // to another. Skips the Unity identity members that must not be reassigned.
        private static void CopyPublicMembers(Component source, Component target)
        {
            var type = source.GetType();
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                try { f.SetValue(target, f.GetValue(source)); } catch { }
            }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.Name == "name" || p.Name == "tag" || p.Name == "hideFlags") continue;
                try { p.SetValue(target, p.GetValue(source, null), null); } catch { }
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
                        if (c != null) UnityEngine.Object.Destroy(c); // OnDisable restores
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
