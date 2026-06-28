// Harmony prefix patch that transparently substitutes kerbcast's rebuilt
// shaders.linux bundle for HullcamVDS's broken upstream one on Linux.
//
// Background: HullcamVDS ships a `shaders.linux` built years ago against
// BuildTarget.StandaloneLinuxUniversal, an enum removed in Unity 2019.2.
// It fails silently on modern Mesa OpenGL — shaders load but render black.
// Upstream issue linuxgurugamer/HullcamVDSContinued#27, open since 2021.
//
// kerbcast ships a rebuilt bundle at GameData/Kerbcast/HullcamShaders/shaders.linux
// (built in CI via .github/workflows/build-hullcam-shaders.yml using Unity
// 2019.4.40f1 + BuildTarget.StandaloneLinux64). When KSP is running on
// Linux and HullcamVDS is installed, this patch intercepts CameraFilter.LoadBundle
// before it executes and loads our bundle instead. Windows and macOS users
// are unaffected — the patch no-ops on non-Linux platforms, so HullcamVDS'
// own bundles handle those.
//
// The patch is applied at KSPAddon.Startup.Instantly (once:true) so it runs
// well before Flight scene entry, where MovieTime.Awake → CameraFilter.InitializeAssets
// → CameraFilter.LoadShaderFile → CameraFilter.LoadBundle would fire.
//
// Graceful degradation:
//   - Non-Linux platform: patch is never applied.
//   - HullcamVDS not installed: assembly lookup returns null; no patch applied.
//   - EnableHullcamLinuxShaderSwap = false in settings.cfg: same.
//   - Our bundle file missing from GameData: original LoadBundle runs as normal.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Kerbcast
{
    /// <summary>
    /// KSPAddon lifecycle host for the Harmony patch. Runs at Startup.Instantly
    /// (once:true) so the patch is in place before HullcamVDS's Flight-scene
    /// MonoBehaviours start up and call CameraFilter.LoadBundle.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, once: true)]
    internal sealed class HullcamShaderBundleSwap : MonoBehaviour
    {
        private const string HarmonyId = "kerbcast.hullcam.shaderbundleswap";

        private static Harmony _harmony;

        private void Awake()
        {
            if (Application.platform != RuntimePlatform.LinuxPlayer)
            {
                Debug.Log("[Kerbcast-ShaderSwap] non-Linux platform; skipping HullcamVDS shader bundle swap");
                return;
            }

            if (!KerbcastSettings.EnableHullcamLinuxShaderSwap)
            {
                Debug.Log("[Kerbcast-ShaderSwap] EnableHullcamLinuxShaderSwap=false; skipping");
                return;
            }

            try
            {
                ApplyPatch();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast-ShaderSwap] failed to apply Harmony patch: {ex}");
            }
        }

        private static void ApplyPatch()
        {
            // Resolve HullcamVDS.CameraFilter via AssemblyLoader rather than
            // `typeof(HullcamVDS.CameraFilter)` — the latter would JIT-compile
            // a TypeLoadException if HullcamVDS isn't installed, even in code
            // paths that are never reached. Using AccessTools.TypeByName
            // (which calls Type.GetType via AssemblyLoader under the hood)
            // is safe: returns null if the assembly isn't loaded.
            var cameraFilterType = AccessTools.TypeByName("HullcamVDS.CameraFilter");
            if (cameraFilterType == null)
            {
                Debug.Log("[Kerbcast-ShaderSwap] HullcamVDS not loaded; skipping shader bundle swap");
                return;
            }

            var loadBundleMethod = cameraFilterType.GetMethod(
                "LoadBundle",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (loadBundleMethod == null)
            {
                Debug.LogWarning("[Kerbcast-ShaderSwap] CameraFilter.LoadBundle not found; skipping");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            var prefix = new HarmonyMethod(
                typeof(CameraFilterLoadBundlePatch),
                nameof(CameraFilterLoadBundlePatch.Prefix));
            _harmony.Patch(loadBundleMethod, prefix: prefix);
            Debug.Log("[Kerbcast-ShaderSwap] Harmony prefix applied to CameraFilter.LoadBundle");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;
        }
    }

    /// <summary>
    /// The actual Harmony prefix. Loads kerbcast's rebuilt shaders.linux bundle,
    /// populates CameraFilter's private static LoadedShaders dictionary and
    /// BundleLoaded flag via reflection, then returns false to skip the original
    /// LoadBundle implementation.
    ///
    /// Returns true (run original) if anything goes wrong or our bundle file
    /// isn't present — so HullcamVDS degrades to its usual behaviour rather
    /// than silently missing shaders.
    /// </summary>
    internal static class CameraFilterLoadBundlePatch
    {
        // Cached reflection state. Populated lazily on first invocation so
        // the patch class itself is safe to load even if something is odd
        // about the HullcamVDS assembly at class-load time.
        private static Type _cameraFilterType;
        private static FieldInfo _bundleLoadedField;
        private static FieldInfo _loadedShadersField;
        private static bool _reflectionReady;

        public static bool Prefix()
        {
            try
            {
                return RunPrefix();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast-ShaderSwap] prefix threw; falling back to original LoadBundle: {ex}");
                return true; // run original
            }
        }

        private static bool RunPrefix()
        {
            // Lazy-init reflection. We grab CameraFilter's private static
            // BundleLoaded and LoadedShaders fields so we can populate them
            // without going through the original method.
            if (!_reflectionReady)
            {
                _cameraFilterType = AccessTools.TypeByName("HullcamVDS.CameraFilter");
                if (_cameraFilterType == null)
                {
                    Debug.LogWarning("[Kerbcast-ShaderSwap] CameraFilter type gone at prefix time; running original");
                    return true;
                }

                _bundleLoadedField = _cameraFilterType.GetField(
                    "BundleLoaded",
                    BindingFlags.NonPublic | BindingFlags.Static);
                _loadedShadersField = _cameraFilterType.GetField(
                    "LoadedShaders",
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (_bundleLoadedField == null || _loadedShadersField == null)
                {
                    Debug.LogWarning("[Kerbcast-ShaderSwap] BundleLoaded or LoadedShaders field not found; running original");
                    return true;
                }

                _reflectionReady = true;
            }

            // Respect the upstream early-exit guard: if another code path
            // already loaded the bundle, don't double-load.
            var alreadyLoaded = (bool)_bundleLoadedField.GetValue(null);
            if (alreadyLoaded)
            {
                Debug.Log("[Kerbcast-ShaderSwap] bundle already loaded; skipping prefix");
                return false; // skip original too (already done)
            }

            // Locate our replacement bundle.
            var bundlePath = System.IO.Path.Combine(
                KSPUtil.ApplicationRootPath,
                "GameData", "Kerbcast", "HullcamShaders", "shaders.linux");

            if (!System.IO.File.Exists(bundlePath))
            {
                Debug.LogWarning($"[Kerbcast-ShaderSwap] replacement bundle not found at {bundlePath}; running original LoadBundle");
                return true; // let original run
            }

            Debug.Log($"[Kerbcast-ShaderSwap] loading replacement shaders.linux from {bundlePath}");

            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Debug.LogWarning("[Kerbcast-ShaderSwap] AssetBundle.LoadFromFile returned null; running original LoadBundle");
                return true;
            }

            // Extract shaders and populate the upstream dictionary so
            // CameraFilter.LoadShaderFile works exactly as intended.
            var shaders = bundle.LoadAllAssets<Shader>();
            var loadedShaders = (Dictionary<string, Shader>)_loadedShadersField.GetValue(null);
            if (loadedShaders == null)
            {
                loadedShaders = new Dictionary<string, Shader>();
                _loadedShadersField.SetValue(null, loadedShaders);
            }

            int count = 0;
            foreach (var shader in shaders)
            {
                if (shader == null) continue;
                Debug.Log($"[Kerbcast-ShaderSwap] loaded shader: {shader.name}");
                loadedShaders[shader.name] = shader;
                count++;
            }

            // Release the bundle file handle; shaders survive because
            // AssetBundle.Unload(false) keeps already-loaded Unity objects.
            bundle.Unload(false);

            _bundleLoadedField.SetValue(null, true);
            Debug.Log($"[Kerbcast-ShaderSwap] replacement bundle loaded; {count} shader(s) registered");

            // Return false = skip CameraFilter.LoadBundle entirely.
            return false;
        }
    }
}
