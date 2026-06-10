// Shared loader for the kerbcam-shaders AssetBundle (built per platform in CI
// by build-kerbcam-shaders.yml, shipped at GameData/Kerbcam/kerbcam-shaders
// for Linux plus kerbcam-shaders.windows / kerbcam-shaders.osx).
// FX effects fetch their shaders/materials through here. The bundle is loaded
// once and cached; a missing bundle, missing shader, or shader with no variant
// for the running graphics API returns null so callers degrade gracefully
// (FX simply doesn't appear) rather than throwing.

using System;
using System.IO;
using UnityEngine;

namespace Kerbcam
{
    internal static class KerbcamFxAssets
    {
        private static AssetBundle _bundle;
        private static bool _attempted;

        // Internal AssetBundle name, identical across the per-platform builds
        // (and the legacy unsuffixed Linux file on disk).
        private const string BundleName = "kerbcam-shaders";

        // Shader variants are compiled per build target, so each platform
        // ships its own bundle file. Linux keeps the unsuffixed name the
        // pre-multi-platform releases used; Windows/macOS follow the
        // HullcamShaders/shaders.linux platform-suffix precedent.
        private static string PlatformBundleFileName()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                    return BundleName + ".windows";
                case RuntimePlatform.OSXPlayer:
                    return BundleName + ".osx";
                default:
                    return BundleName;
            }
        }

        private static AssetBundle Bundle()
        {
            if (_attempted) return _bundle;
            _attempted = true;
            try
            {
                // Unity allows only one load per bundle file: a second
                // LoadFromFile on the same file returns null. Several callers
                // share this bundle (NightVision filter + FX effects), so reuse
                // an already-loaded instance before loading.
                foreach (var loaded in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (loaded != null && loaded.name == BundleName)
                    {
                        _bundle = loaded;
                        return _bundle;
                    }
                }
                var dir = Path.Combine(
                    KSPUtil.ApplicationRootPath, "GameData", "Kerbcam");
                var path = Path.Combine(dir, PlatformBundleFileName());
                if (!File.Exists(path))
                {
                    // Installs from before the per-platform bundles ship only
                    // the unsuffixed (Linux-built) file. Load it anyway: the
                    // isSupported check in LoadMaterial catches a cross-platform
                    // bundle, so this stays graceful rather than magenta.
                    var legacy = Path.Combine(dir, BundleName);
                    if (path != legacy && File.Exists(legacy))
                    {
                        Debug.LogWarning($"[Kerbcam] FX shader bundle {path} not found; falling back to legacy {legacy}");
                        path = legacy;
                    }
                    else
                    {
                        Debug.LogWarning($"[Kerbcam] FX shader bundle not found at {path}; atmospheric FX disabled");
                        return null;
                    }
                }
                _bundle = AssetBundle.LoadFromFile(path);
                if (_bundle == null)
                    Debug.LogWarning("[Kerbcam] AssetBundle.LoadFromFile returned null for kerbcam-shaders");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] FX shader bundle load failed: {ex.Message}");
            }
            return _bundle;
        }

        // Build a fresh Material from a shader in the bundle. Returns null if the
        // bundle or shader is missing — caller treats that as "effect unavailable".
        public static Material LoadMaterial(string shaderAssetName)
        {
            var bundle = Bundle();
            var shader = bundle != null ? bundle.LoadAsset<Shader>(shaderAssetName) : null;
            if (shader == null)
            {
                Debug.LogWarning($"[Kerbcam] FX shader '{shaderAssetName}' not found in kerbcam-shaders bundle");
                return null;
            }
            // A bundle built for another platform cross-loads with non-null
            // shaders that have no variant for the running graphics API; Unity
            // would render them as solid magenta. Treat them as unavailable.
            if (!shader.isSupported)
            {
                Debug.LogWarning(
                    $"[Kerbcam] FX shader '{shaderAssetName}' has no variant for " +
                    $"{Application.platform}/{SystemInfo.graphicsDeviceType}; " +
                    "the kerbcam-shaders bundle was likely built for another platform");
                return null;
            }
            return new Material(shader) { name = shaderAssetName };
        }
    }
}
