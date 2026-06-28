using System;
using UnityEngine;

namespace Kerbcast
{
    // Loads the kerbcast-built NightVision shader from GameData/Kerbcast/kerbcast-shaders
    // and provides a pre-configured Material for use in KerbcastCamera's blit pass.
    // Lazy singleton: bundle is opened once on first GetMaterial() call and kept
    // alive for the session. Falls back silently if the bundle is absent (dev
    // workflow without a built bundle) — KerbcastCamera then uses HullcamVDS's
    // filter as a graceful degradation.
    internal static class KerbcastNightVisionFilter
    {
        private static Material _material;
        private static bool _attempted;

        public static Material GetMaterial()
        {
            if (_attempted) return _material;
            _attempted = true;
            try
            {
                // Shares the one kerbcast-shaders bundle via KerbcastFxAssets — a
                // second independent AssetBundle.LoadFromFile on the same file
                // returns null ("already loaded"), so all callers route here.
                _material = KerbcastFxAssets.LoadMaterial("KerbcastNightVision");
                if (_material != null)
                {
                    _material.SetFloat("_Gain", 4.0f);
                    Debug.Log("[Kerbcast] KerbcastNightVision shader loaded from bundle");
                }
                else
                {
                    Debug.LogWarning("[Kerbcast] KerbcastNightVision unavailable; falling back to HullcamVDS filter");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Kerbcast] KerbcastNightVisionFilter.GetMaterial failed: " + ex.Message);
            }
            return _material;
        }
    }
}
