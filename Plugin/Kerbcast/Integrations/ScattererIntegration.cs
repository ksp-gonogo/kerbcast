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

        // Diagnostic reflection, resolved in Probe, used only when
        // DebugCameraLogging is on (ScattererFlareProbe reads the live flare state).
        private Type _sunflareHookType;
        private FieldInfo _hookFlareField;      // SunflareCameraHook.flare
        private FieldInfo _hookDbufferField;    // SunflareCameraHook.useDbufferOnCamera
        private PropertyInfo _flareRenderingProp; // SunFlare.FlareRendering
        private FieldInfo _flareMaterialField;  // SunFlare.sunglareMaterial
        private FieldInfo _flareGoField;        // SunFlare.sunflareGameObject (nonpublic)

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

                // Diagnostic reflection for ScattererFlareProbe (harmless if absent).
                _sunflareHookType = asm.GetType("Scatterer.SunflareCameraHook");
                var flareType = asm.GetType("Scatterer.SunFlare");
                if (_sunflareHookType != null && flareType != null)
                {
                    _hookFlareField = _sunflareHookType.GetField("flare", PubInst);
                    _hookDbufferField = _sunflareHookType.GetField("useDbufferOnCamera", PubInst);
                    _flareRenderingProp = flareType.GetProperty("FlareRendering", PubInst);
                    _flareMaterialField = flareType.GetField("sunglareMaterial", PubInst);
                    _flareGoField = flareType.GetField("sunflareGameObject",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                }

                _ready = true;
                Debug.Log($"{LogTag} integration enabled " +
                    $"(scaled={_scaledField != null} far={_farField != null} " +
                    $"unified={_unifiedField != null} hooks={_scattererHookTypes.Length})");

                if (KerbcastSettings.DebugCameraLogging)
                {
                    var inst = _instanceProp.GetValue(null, null);
                    var nearCam = inst != null ? _nearField.GetValue(inst) as Camera : null;
                    var scaledCam = inst != null && _scaledField != null
                        ? _scaledField.GetValue(inst) as Camera : null;
                    Debug.Log($"{LogTag} real cameras near='{(nearCam != null ? nearCam.name : "null")}' " +
                        $"scaled='{(scaledCam != null ? scaledCam.name : "null")}'");
                }
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

                AddSwap(cam, field);

                // Copy Scatterer's per-camera hooks from the matching stock camera
                // so the clone renders the full per-camera look (flare + occlusion),
                // not just the swapped atmosphere. Near <- Camera 00, Scaled <-
                // Camera ScaledSpace. Far reuses the near swap and needs no hooks.
                if (layer == CameraLayers.Near)
                {
                    // Scatterer's sunflare reads Instance.scaledSpaceCamera in
                    // updateProperties for its render gate, scale and screen
                    // position, computing the sun's viewport from a SCALED-SPACE
                    // camera. The flare mesh renders on the near clone (layer 15),
                    // so during the near clone's render point scaledSpaceCamera at
                    // the matching SCALED clone (not the near clone: a local-space
                    // camera projects the scaled-space sun position to a bogus
                    // viewport, val.z<=0, and the gate never passes). This makes the
                    // flare track the clone's view instead of the player's.
                    if (_scaledField != null && _scaledField != field)
                    {
                        var scaledSibling = FindScaledSibling(cam);
                        AddSwap(cam, _scaledField, scaledSibling);
                        if (KerbcastSettings.DebugCameraLogging)
                            Debug.Log($"{LogTag} near clone {cam.name} scaled-sibling=" +
                                $"{(scaledSibling != null ? scaledSibling.name : "NOT FOUND")}");
                    }
                    CopyRenderingHooks("Camera 00", cam);
                    if (KerbcastSettings.DebugCameraLogging) AttachFlareProbe(cam);
                }
                else if (layer == CameraLayers.Scaled)
                    CopyRenderingHooks("Camera ScaledSpace", cam);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} apply to {cam.name} ({layer}) failed: {ex.Message}");
                RemoveFromLayer(cam, layer);
            }
        }

        // Attach a camera-reference swap to the clone for one Scatterer singleton
        // field, tracked so RemoveFromLayer tears it down. A clone can carry more
        // than one (the near clone swaps both nearCamera and scaledSpaceCamera).
        private void AddSwap(Camera cam, FieldInfo field, Camera swapIn = null)
        {
            var swap = cam.gameObject.AddComponent<ScattererCameraSwap>();
            swap.InstanceProperty = _instanceProp;
            swap.CameraField = field;
            swap.SwapInOverride = swapIn;
            Track(cam, swap);
        }

        // Find the scaled clone that pairs with a near clone. They are named
        // "Kerbcast_<id>_Near" / "Kerbcast_<id>_Scaled". Uses FindObjectsOfTypeAll so
        // a disabled clone (ours are enabled=false) is still found.
        private static Camera FindScaledSibling(Camera nearClone)
        {
            if (nearClone == null || nearClone.name == null || !nearClone.name.EndsWith("_Near"))
                return null;
            var scaledName = nearClone.name.Substring(0, nearClone.name.Length - "_Near".Length) + "_Scaled";
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
                if (c != null && c.name == scaledName) return c;
            return null;
        }

        // Diagnostic-only: attach a probe that logs Scatterer's live flare state on
        // the clone. Caller gates on DebugCameraLogging.
        private void AttachFlareProbe(Camera cam)
        {
            if (_sunflareHookType == null) return;
            var probe = cam.gameObject.AddComponent<ScattererFlareProbe>();
            probe.HookType = _sunflareHookType;
            probe.HookFlareField = _hookFlareField;
            probe.FlareRenderingProp = _flareRenderingProp;
            probe.MaterialField = _flareMaterialField;
            probe.FlareGoField = _flareGoField;
            probe.HookDbufferField = _hookDbufferField;
            Track(cam, probe);
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
            if (source == null)
            {
                if (KerbcastSettings.DebugCameraLogging)
                    Debug.Log($"{LogTag} copyhooks target={target.name} src='{sourceCameraName}' FOUND=false");
                return;
            }
            foreach (var hookType in _scattererHookTypes)
            {
                if (target.gameObject.GetComponent(hookType) != null) continue;
                var src = source.gameObject.GetComponent(hookType);
                if (KerbcastSettings.DebugCameraLogging)
                    Debug.Log($"{LogTag} copyhooks target={target.name} src='{sourceCameraName}' " +
                        $"{hookType.Name} srcExists={src != null}");
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
                // In unified-camera mode Scatterer drives the flare through the
                // unified camera and leaves the stock near/scaled flare hooks
                // DISABLED; CopyPublicMembers carries that enabled=false across, so
                // the copy never runs on the clone (its OnPreRender never calls
                // updateProperties or sets renderOnCurrentCamera). Force it enabled -
                // Scatterer holds no reference to our copy, so it never toggles it
                // back off.
                if (dst is Behaviour hookBehaviour)
                    hookBehaviour.enabled = true;
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
