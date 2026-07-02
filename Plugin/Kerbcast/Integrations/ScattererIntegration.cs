// Scatterer capture for kerbcast's cloned cameras. Two parts:
//
// 1. A per-clone ScattererCameraSwap points Scatterer's singleton camera at the
//    clone during its render (then restores), so Scatterer's atmosphere /
//    scattering / ocean command buffers self-attach to the clone.
//
// 2. Copy Scatterer's SunflareCameraHook from the matching stock camera onto the
//    clone (with its fields) and force it enabled. The hook's OnPreRender sets
//    renderOnCurrentCamera=1, which stops the shared sunflare mesh (layer 15) being
//    culled for the clone - that is what makes the flare draw at all. Its
//    useDbufferOnCamera is carried over from the source hook as-is (near=1,
//    scaled=0); the working state captured on video used the source value, so we
//    do not override it. Scatterer's own line-of-sight raycast (in updateProperties)
//    gates the whole flare, so terrain and part occlusion is preserved.
//
// 3. Keep the copied hook's flare reference live. Scatterer's SunflareManager
//    DestroyImmediates every SunFlare on teardown (scene change, re-init) and
//    builds fresh ones; the copied hook would then hold a destroyed flare and
//    its OnPreRender silently no-ops (Unity null check), so the flare vanishes
//    from the stream. PerFrame re-points the hook at the current live SunFlare
//    (Scatterer.Instance.sunflareManager.scattererSunFlares) before each clone
//    render.
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

        // Sunflare reflection, resolved in Probe. The hook/flare/manager members
        // drive the per-frame live-flare refresh (PerFrame); the rest feed the
        // diagnostic ScattererFlareProbe when DebugCameraLogging is on.
        private Type _sunflareHookType;
        private FieldInfo _hookFlareField;      // SunflareCameraHook.flare
        private FieldInfo _hookDbufferField;    // SunflareCameraHook.useDbufferOnCamera
        private FieldInfo _sunflareManagerField; // Instance.sunflareManager
        private FieldInfo _flaresDictField;     // SunflareManager.scattererSunFlares
        private FieldInfo _flareSourceNameField; // SunFlare.sourceName
        private PropertyInfo _flareRenderingProp; // SunFlare.FlareRendering
        private FieldInfo _flareMaterialField;  // SunFlare.sunglareMaterial
        private FieldInfo _flareGoField;        // SunFlare.sunflareGameObject (nonpublic)
        private FieldInfo _flareSourceScaledField; // SunFlare.sourceScaledTransform
        private FieldInfo _flareSourceField;    // SunFlare.source (CelestialBody)
        private FieldInfo _cbmField;            // Scatterer.scattererCelestialBodiesManager
        private FieldInfo _underwaterField;     // <cbm>.underwater

        private readonly Dictionary<Camera, List<Component>> _added = new Dictionary<Camera, List<Component>>();

        // Copied SunflareCameraHook per clone, for the per-frame live-flare refresh.
        private readonly Dictionary<Camera, Behaviour> _flareHooks = new Dictionary<Camera, Behaviour>();

        public string Name => "Scatterer";
        public bool ForcesNoMsaa => true;
        // Per-frame work: keep each copied flare hook pointed at the live SunFlare
        // (Scatterer destroys and rebuilds its flares on scene change / re-init).
        public bool NeedsPerFrame => true;
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

                // Sunflare reflection: refresh members first, then probe-only ones
                // (all harmless if absent; the refresh just stays inert).
                _sunflareHookType = asm.GetType("Scatterer.SunflareCameraHook");
                var flareType = asm.GetType("Scatterer.SunFlare");
                if (_sunflareHookType != null && flareType != null)
                {
                    _hookFlareField = _sunflareHookType.GetField("flare", PubInst);
                    _hookDbufferField = _sunflareHookType.GetField("useDbufferOnCamera", PubInst);
                    _flareSourceNameField = flareType.GetField("sourceName", PubInst);
                    _sunflareManagerField = t.GetField("sunflareManager", PubInst);
                    if (_sunflareManagerField != null)
                        _flaresDictField = _sunflareManagerField.FieldType
                            .GetField("scattererSunFlares", PubInst);
                    _flareRenderingProp = flareType.GetProperty("FlareRendering", PubInst);
                    _flareMaterialField = flareType.GetField("sunglareMaterial", PubInst);
                    _flareGoField = flareType.GetField("sunflareGameObject",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    _flareSourceScaledField = flareType.GetField("sourceScaledTransform", PubInst);
                    _flareSourceField = flareType.GetField("source", PubInst);
                    _cbmField = t.GetField("scattererCelestialBodiesManager", PubInst);
                    if (_cbmField != null)
                        _underwaterField = _cbmField.FieldType.GetField("underwater", PubInst);
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
                    // Only nearCamera is swapped to the clone (above). We deliberately
                    // do NOT repoint scaledSpaceCamera here: Scatterer's sunflare gate
                    // (updateProperties) must run against the real scaled camera so its
                    // sun-occlusion self-reset works. Each near clone then draws the
                    // shared flare mesh at its OWN GPU-projected sun position, so the
                    // flare is per-camera for free. The gate itself follows the main
                    // view (shared renderSunFlare); that is a known limitation in
                    // unified-camera mode, not worth breaking the working flare over.
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
            probe.InstanceProp = _instanceProp;
            probe.NearField = _nearField;
            probe.ScaledField = _scaledField;
            probe.SourceScaledTransformField = _flareSourceScaledField;
            probe.SourceField = _flareSourceField;
            probe.CbmField = _cbmField;
            probe.UnderwaterField = _underwaterField;
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
                // In unified-camera mode Scatterer drives the flare through the
                // unified camera and leaves the stock near/scaled flare hooks
                // DISABLED; CopyPublicMembers carries that enabled=false across, so
                // the copy never runs on the clone (its OnPreRender never calls
                // updateProperties or sets renderOnCurrentCamera). Force it enabled -
                // Scatterer holds no reference to our copy, so it never toggles it
                // back off.
                if (dst is Behaviour hookBehaviour)
                {
                    hookBehaviour.enabled = true;
                    // Remember the flare hook so PerFrame can keep its flare
                    // reference live across Scatterer rebuilds.
                    if (hookType == _sunflareHookType)
                        _flareHooks[target] = hookBehaviour;
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
                _flareHooks.Remove(cam);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} remove from {cam.name} failed: {ex.Message}");
            }
        }

        // Called just before each clone layer renders. Scatterer's SunflareManager
        // DestroyImmediates its SunFlares on teardown / re-init and builds fresh
        // ones; a copied hook left holding the dead flare silently stops driving
        // the flare (its OnPreRender Unity-null-checks it). Re-point the hook at
        // the current live SunFlare whenever its reference has gone stale.
        public void PerFrame(Camera cam, CameraLayers layer, in IntegrationFrameState state)
        {
            if (!_ready || _hookFlareField == null || cam == null) return;
            if (!_flareHooks.TryGetValue(cam, out var hook) || hook == null) return;
            try
            {
                var current = _hookFlareField.GetValue(hook) as UnityEngine.Object;
                if (current != null) return; // flare still live (Unity liveness check)

                var live = ResolveLiveFlare(current);
                if (live == null) return; // Scatterer mid-rebuild; retry next frame

                _hookFlareField.SetValue(hook, live);
                // Scatterer never touches our copy, but re-assert enabled so a
                // refresh always leaves a working hook.
                if (!hook.enabled) hook.enabled = true;
                var liveName = _flareSourceNameField?.GetValue(live) as string ?? "?";
                Debug.Log($"{LogTag} re-pointed flare hook on '{cam.name}' to live SunFlare '{liveName}'");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} flare refresh on {cam.name} failed: {ex.Message}");
            }
        }

        // Resolve the current live SunFlare from Scatterer's sunflare manager,
        // preferring the entry whose key matches the stale flare's sourceName
        // (multi-star systems), else the first live entry (stock: the Sun).
        private object ResolveLiveFlare(object staleFlare)
        {
            if (_sunflareManagerField == null || _flaresDictField == null || _instanceProp == null)
                return null;
            var inst = _instanceProp.GetValue(null, null);
            if (inst == null) return null;
            var mgr = _sunflareManagerField.GetValue(inst) as UnityEngine.Object;
            if (mgr == null) return null; // manager absent or torn down; rebuild pending
            var dict = _flaresDictField.GetValue(mgr) as System.Collections.IDictionary;
            if (dict == null) return null;

            // sourceName is a plain managed field, still readable on a destroyed flare.
            string wantName = null;
            if (staleFlare != null && _flareSourceNameField != null)
                wantName = _flareSourceNameField.GetValue(staleFlare) as string;

            object first = null;
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                var f = e.Value as UnityEngine.Object;
                if (f == null) continue; // destroyed entry
                if (wantName != null && (e.Key as string) == wantName) return f;
                if (first == null) first = f;
            }
            return first;
        }
    }
}
