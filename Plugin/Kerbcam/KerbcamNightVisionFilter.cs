using System;
using System.IO;
using UnityEngine;

namespace Kerbcam
{
    // Loads the kerbcam-built NightVision shader from GameData/Kerbcam/kerbcam-shaders
    // and provides a pre-configured Material for use in KerbcamCamera's blit pass.
    // Lazy singleton: bundle is opened once on first GetMaterial() call and kept
    // alive for the session. Falls back silently if the bundle is absent (dev
    // workflow without a built bundle) — KerbcamCamera then uses HullcamVDS's
    // filter as a graceful degradation.
    internal static class KerbcamNightVisionFilter
    {
        private static Material _material;
        private static bool _attempted;

        public static Material GetMaterial()
        {
            if (_attempted) return _material;
            _attempted = true;
            try
            {
                var bundlePath = Path.Combine(
                    KSPUtil.ApplicationRootPath,
                    "GameData", "Kerbcam", "kerbcam-shaders");
                if (!File.Exists(bundlePath))
                {
                    Debug.LogWarning("[Kerbcam] kerbcam-shaders bundle not found at " + bundlePath +
                        "; falling back to HullcamVDS NightVision filter");
                    return null;
                }
                var bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    Debug.LogWarning("[Kerbcam] AssetBundle.LoadFromFile returned null for kerbcam-shaders");
                    return null;
                }
                var shader = bundle.LoadAsset<Shader>("KerbcamNightVision");
                if (shader == null)
                {
                    Debug.LogWarning("[Kerbcam] KerbcamNightVision shader not found in bundle");
                    return null;
                }
                _material = new Material(shader) { name = "KerbcamNightVision" };
                _material.SetFloat("_Gain", 4.0f);
                Debug.Log("[Kerbcam] KerbcamNightVision shader loaded from bundle");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Kerbcam] KerbcamNightVisionFilter.GetMaterial failed: " + ex.Message);
            }
            return _material;
        }
    }
}
