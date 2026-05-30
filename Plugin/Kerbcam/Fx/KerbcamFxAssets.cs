// Shared loader for the kerbcam-shaders AssetBundle (built in CI by
// build-kerbcam-shaders.yml, shipped at GameData/Kerbcam/kerbcam-shaders).
// FX effects fetch their shaders/materials through here. The bundle is loaded
// once and cached; a missing bundle or shader returns null so callers degrade
// gracefully (FX simply doesn't appear) rather than throwing.

using System;
using System.IO;
using UnityEngine;

namespace Kerbcam
{
    internal static class KerbcamFxAssets
    {
        private static AssetBundle _bundle;
        private static bool _attempted;

        private const string BundleName = "kerbcam-shaders";

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
                var path = Path.Combine(
                    KSPUtil.ApplicationRootPath, "GameData", "Kerbcam", BundleName);
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"[Kerbcam] FX shader bundle not found at {path}; atmospheric FX disabled");
                    return null;
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
            return new Material(shader) { name = shaderAssetName };
        }
    }
}
