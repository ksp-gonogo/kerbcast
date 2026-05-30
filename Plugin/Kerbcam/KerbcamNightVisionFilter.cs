using System;
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
                // Shares the one kerbcam-shaders bundle via KerbcamFxAssets — a
                // second independent AssetBundle.LoadFromFile on the same file
                // returns null ("already loaded"), so all callers route here.
                _material = KerbcamFxAssets.LoadMaterial("KerbcamNightVision");
                if (_material != null)
                {
                    _material.SetFloat("_Gain", 4.0f);
                    Debug.Log("[Kerbcam] KerbcamNightVision shader loaded from bundle");
                }
                else
                {
                    Debug.LogWarning("[Kerbcam] KerbcamNightVision unavailable; falling back to HullcamVDS filter");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Kerbcam] KerbcamNightVisionFilter.GetMaterial failed: " + ex.Message);
            }
            return _material;
        }
    }
}
