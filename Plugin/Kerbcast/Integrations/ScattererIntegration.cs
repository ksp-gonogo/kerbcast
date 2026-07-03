/* Scatterer capture for kerbcast's cloned cameras.

   1. A per-clone ScattererCameraSwap points Scatterer's singleton camera at
      the clone during its render (then restores), so Scatterer's atmosphere /
      scattering / ocean command buffers self-attach to the clone.

   2. Scatterer's SunflareCameraHook is copied (with its fields) from the
      matching stock camera onto the clone. Its OnPreRender sets
      renderOnCurrentCamera=1, which stops the shared sunflare mesh (layer 15)
      being culled for the clone; useDbufferOnCamera carries over from the
      source hook as-is. The scaled clone runs the copy directly; the near
      clone holds it disabled and a ScattererSunflareDriver performs its
      writes instead (see 4).

   3. PerFrame keeps each copied hook's flare reference live (Scatterer
      DestroyImmediates its SunFlares on scene change / re-init; a hook left
      holding a dead flare silently no-ops) and gates the hook (or driver) on
      the sun being inside that clone's own viewport. Scatterer's shared
      renderSunFlare float follows the MAIN view, so an always-enabled copy
      would draw the flare on every clone whenever the player faces the sun.

   4. On the near clone the ScattererSunflareDriver widens Scatterer's
      single-center-ray occlusion to a multi-point disk test, so the flare
      stays on until the sun is fully hidden instead of popping off when a
      part edge crosses the sun's center.

   Scatterer forces QualitySettings.antiAliasing = 0 and its depth-based
   effects break under MSAA, so ForcesNoMsaa = true; the host then drives
   every clone and the capture RT with MSAA off.

   Reflection-only: no compile-time Scatterer reference. Absent or disabled
   Scatterer no-ops. */

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

        /* Sunflare reflection, resolved in Probe. Consumed by PerFrame's
           live-flare refresh + gate and by the ScattererSunflareDriver. */
        private Type _sunflareHookType;
        private FieldInfo _hookFlareField;      // SunflareCameraHook.flare
        private FieldInfo _hookDbufferField;    // SunflareCameraHook.useDbufferOnCamera
        private MethodInfo _flareUpdatePropsMethod;   // SunFlare.updateProperties()
        private MethodInfo _flareClearExtinctionMethod; // SunFlare.ClearExtinction()
        private FieldInfo _sunflareManagerField; // Instance.sunflareManager
        private FieldInfo _flaresDictField;     // SunflareManager.scattererSunFlares
        private FieldInfo _flareSourceNameField; // SunFlare.sourceName
        private PropertyInfo _flareRenderingProp; // SunFlare.FlareRendering
        private FieldInfo _flareMaterialField;  // SunFlare.sunglareMaterial
        private FieldInfo _flareSourceScaledField; // SunFlare.sourceScaledTransform
        private FieldInfo _cbmField;            // Scatterer.scattererCelestialBodiesManager
        private FieldInfo _underwaterField;     // <cbm>.underwater

        private readonly Dictionary<Camera, List<Component>> _added = new Dictionary<Camera, List<Component>>();

        // Copied SunflareCameraHook per clone, for the per-frame live-flare refresh.
        private readonly Dictionary<Camera, Behaviour> _flareHooks = new Dictionary<Camera, Behaviour>();

        /* Near clones get a ScattererSunflareDriver that owns the render-time
           flare writes (the copied hook stays disabled as a reference holder);
           PerFrame gates the driver instead of the hook where one exists. */
        private readonly Dictionary<Camera, ScattererSunflareDriver> _flareDrivers =
            new Dictionary<Camera, ScattererSunflareDriver>();

        public string Name => "Scatterer";
        public bool ForcesNoMsaa => true;
        // Per-frame work: keep each copied flare hook pointed at the live SunFlare
        // (Scatterer destroys and rebuilds its flares on scene change / re-init)
        // and gate the hook on the sun being in that clone's view.
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

                // Sunflare reflection: all harmless if absent; the refresh,
                // gate, and driver just stay inert.
                _sunflareHookType = asm.GetType("Scatterer.SunflareCameraHook");
                var flareType = asm.GetType("Scatterer.SunFlare");
                if (_sunflareHookType != null && flareType != null)
                {
                    _hookFlareField = _sunflareHookType.GetField("flare", PubInst);
                    _hookDbufferField = _sunflareHookType.GetField("useDbufferOnCamera", PubInst);
                    _flareUpdatePropsMethod = flareType.GetMethod("updateProperties", PubInst);
                    _flareClearExtinctionMethod = flareType.GetMethod("ClearExtinction", PubInst);
                    _flareSourceNameField = flareType.GetField("sourceName", PubInst);
                    _sunflareManagerField = t.GetField("sunflareManager", PubInst);
                    if (_sunflareManagerField != null)
                        _flaresDictField = _sunflareManagerField.FieldType
                            .GetField("scattererSunFlares", PubInst);
                    _flareRenderingProp = flareType.GetProperty("FlareRendering", PubInst);
                    _flareMaterialField = flareType.GetField("sunglareMaterial", PubInst);
                    _flareSourceScaledField = flareType.GetField("sourceScaledTransform", PubInst);
                    _cbmField = t.GetField("scattererCelestialBodiesManager", PubInst);
                    if (_cbmField != null)
                        _underwaterField = _cbmField.FieldType.GetField("underwater", PubInst);
                }

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

                AddSwap(cam, field);

                // Copy Scatterer's per-camera hooks from the matching stock camera
                // so the clone renders the full per-camera look (flare + occlusion),
                // not just the swapped atmosphere. Near <- Camera 00, Scaled <-
                // Camera ScaledSpace. Far reuses the near swap and needs no hooks.
                if (layer == CameraLayers.Near)
                {
                    /* Only nearCamera is swapped to the clone (above); we
                       deliberately do NOT repoint scaledSpaceCamera. Scatterer's
                       sunflare gate (updateProperties) must run against the real
                       scaled camera so its sun-occlusion self-reset works. Each
                       near clone still draws the shared flare mesh at its OWN
                       GPU-projected sun position, so the flare is per-camera for
                       free; PerFrame's sun-in-view gate covers the shared
                       renderSunFlare float following the main view. */
                    CopyRenderingHooks("Camera 00", cam);
                    AttachFlareDriver(cam);
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
        // field, tracked so RemoveFromLayer tears it down.
        private void AddSwap(Camera cam, FieldInfo field)
        {
            var swap = cam.gameObject.AddComponent<ScattererCameraSwap>();
            swap.InstanceProperty = _instanceProp;
            swap.CameraField = field;
            Track(cam, swap);
        }

        /* Give the near clone a driver that performs the copied hook's render-
           time writes itself and widens Scatterer's single-center-ray occlusion
           to a multi-point disk test, so the flare stays on until the sun is
           fully hidden behind parts/terrain instead of popping off the moment
           a part edge crosses the sun's center. The copied hook is disabled for
           good: OnPreRender order between two components is undocumented, so
           the driver must be the only writer. The hook remains the live-flare
           reference holder that PerFrame re-points. */
        private void AttachFlareDriver(Camera cam)
        {
            if (_flareUpdatePropsMethod == null || _hookFlareField == null
                || _flareMaterialField == null)
                return;
            if (_flareDrivers.TryGetValue(cam, out var existing) && existing != null) return;
            if (!_flareHooks.TryGetValue(cam, out var hook) || hook == null) return;

            var driver = cam.gameObject.AddComponent<ScattererSunflareDriver>();
            driver.Hook = hook;
            driver.HookFlareField = _hookFlareField;
            driver.UpdatePropertiesMethod = _flareUpdatePropsMethod;
            driver.ClearExtinctionMethod = _flareClearExtinctionMethod;
            driver.MaterialField = _flareMaterialField;
            driver.FlareRenderingProp = _flareRenderingProp;
            driver.SourceScaledTransformField = _flareSourceScaledField;
            driver.InstanceProp = _instanceProp;
            driver.ScaledField = _scaledField;
            driver.CbmField = _cbmField;
            driver.UnderwaterField = _underwaterField;
            driver.UseDbufferOnCamera = _hookDbufferField != null
                ? (float)_hookDbufferField.GetValue(hook) : 1f;
            driver.enabled = false; // PerFrame's sun-in-view gate owns it
            hook.enabled = false;
            _flareDrivers[cam] = driver;
            Track(cam, driver);
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
                // In unified-camera mode Scatterer drives the flare through the
                // unified camera and leaves the stock near/scaled flare hooks
                // DISABLED; CopyPublicMembers carries that enabled=false across, so
                // the copy never runs on the clone (its OnPreRender never calls
                // updateProperties or sets renderOnCurrentCamera). Force it enabled
                // here as the initial state; from then on PerFrame owns the flare
                // hook's enabled flag (sun-in-view gate). Scatterer holds no
                // reference to our copy, so it never toggles it back off.
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
                _flareDrivers.Remove(cam);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} remove from {cam.name} failed: {ex.Message}");
            }
        }

        /* Called just before each clone layer renders. For clones carrying a
           copied flare hook (near, scaled): re-point the hook's flare reference
           if Scatterer has rebuilt its SunFlares (runs regardless of the gate
           so the reference is live on re-enable), then enable the hook - or, on
           the near clone, its driver - only while the sun is in THIS clone's
           view. A disabled hook gets no OnPreRender, so renderOnCurrentCamera
           stays 0 and the shared flare mesh is culled for that clone. */
        public void PerFrame(Camera cam, CameraLayers layer, in IntegrationFrameState state)
        {
            if (!_ready || _hookFlareField == null || cam == null) return;
            if (!_flareHooks.TryGetValue(cam, out var hook) || hook == null) return;
            try
            {
                RefreshFlareReference(hook);
                /* The gate owns the enabled state from here on. Cheap: Unity only
                   fires OnEnable/OnDisable on an actual change. Where a driver
                   exists (near clone) it is gated instead and the copied hook is
                   held disabled: the driver replicates the hook's render-time
                   writes and then overrides the occlusion verdict, so a second
                   writer with undocumented OnPreRender order must never run. */
                bool sunInView = SunInView(cam);
                if (_flareDrivers.TryGetValue(cam, out var driver) && driver != null)
                {
                    hook.enabled = false;
                    driver.enabled = sunInView;
                }
                else
                {
                    hook.enabled = sunInView;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} flare refresh on {cam.name} failed: {ex.Message}");
            }
        }

        private void RefreshFlareReference(Behaviour hook)
        {
            var current = _hookFlareField.GetValue(hook) as UnityEngine.Object;
            if (current != null) return; // flare still live (Unity liveness check)

            var live = ResolveLiveFlare(current);
            if (live == null) return; // Scatterer mid-rebuild; retry next frame

            _hookFlareField.SetValue(hook, live);
        }

        // Sun inside this clone's viewport (in front of the camera, within the
        // frame plus a margin). The margin keeps the flare's ghosts and streaks
        // alive while the sun dot sits just off the frame edge. No occlusion
        // raycast here: Scatterer's own line-of-sight test in updateProperties
        // raycasts from Instance.nearCamera, which the swap points at the clone
        // during its render, so part/terrain blocking is already per-clone.
        private const float FlareViewMargin = 0.15f;

        private static bool SunInView(Camera cam)
        {
            if (Planetarium.fetch == null || Planetarium.fetch.Sun == null) return false;
            Vector3 sunPos = (Vector3)Planetarium.fetch.Sun.position;
            Vector3 vp = cam.WorldToViewportPoint(sunPos);
            return vp.z > 0f
                && vp.x >= -FlareViewMargin && vp.x <= 1f + FlareViewMargin
                && vp.y >= -FlareViewMargin && vp.y <= 1f + FlareViewMargin;
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
