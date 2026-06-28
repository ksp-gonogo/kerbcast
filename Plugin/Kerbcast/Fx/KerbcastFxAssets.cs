// Shared loader for the kerbcast-shaders AssetBundle (built per platform in CI
// by build-kerbcast-shaders.yml, shipped at GameData/Kerbcast/kerbcast-shaders
// for Linux plus kerbcast-shaders.windows / kerbcast-shaders.osx).
// FX effects fetch their shaders/materials through here. The bundle is loaded
// once and cached; a missing bundle, missing shader, shader with no variant
// for the running graphics API, or a bundle that fails the one-time render
// probe (variants present but rejected by the graphics driver at creation,
// the v0.19.0 Windows black-stream bug) returns null so callers degrade
// gracefully (FX simply doesn't appear) rather than throwing.

using System;
using System.IO;
using UnityEngine;

namespace Kerbcast
{
    internal static class KerbcastFxAssets
    {
        private static AssetBundle _bundle;
        private static bool _attempted;

        // Internal AssetBundle name, identical across the per-platform builds
        // (and the legacy unsuffixed Linux file on disk).
        private const string BundleName = "kerbcast-shaders";

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
                    KSPUtil.ApplicationRootPath, "GameData", "Kerbcast");
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
                        Debug.LogWarning($"[Kerbcast] FX shader bundle {path} not found; falling back to legacy {legacy}");
                        path = legacy;
                    }
                    else
                    {
                        Debug.LogWarning($"[Kerbcast] FX shader bundle not found at {path}; atmospheric FX disabled");
                        return null;
                    }
                }
                _bundle = AssetBundle.LoadFromFile(path);
                if (_bundle == null)
                    Debug.LogWarning("[Kerbcast] AssetBundle.LoadFromFile returned null for kerbcast-shaders");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast] FX shader bundle load failed: {ex.Message}");
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
                Debug.LogWarning($"[Kerbcast] FX shader '{shaderAssetName}' not found in kerbcast-shaders bundle");
                return null;
            }
            // A bundle built for another platform cross-loads with non-null
            // shaders that have no variant for the running graphics API; Unity
            // would render them as solid magenta. Treat them as unavailable.
            if (!shader.isSupported)
            {
                Debug.LogWarning(
                    $"[Kerbcast] FX shader '{shaderAssetName}' has no variant for " +
                    $"{Application.platform}/{SystemInfo.graphicsDeviceType}; " +
                    "the kerbcast-shaders bundle was likely built for another platform");
                return null;
            }
            if (!BundlePassesRenderProbe(bundle)) return null;
            return new Material(shader) { name = shaderAssetName };
        }

        /* Render-probe state: one verdict per session, shared by every
           LoadMaterial caller. */
        private static bool _probeAttempted;
        private static bool _probeFailed;

        /* The image-filter shader is the only bundle shader a fullscreen blit
           can fully exercise: the FX shaders gate their output on mesh
           normals and FX uniforms, so a healthy one legitimately blits zero. */
        private const string ProbeShaderName = "KerbcastNightVision";

        /* One-time bundle health check: blit a white frame through the
           bundle's NightVision shader and require visibly non-zero output.

           Why isSupported is not enough: it only checks that a variant for
           the running graphics API EXISTS in the bundle. v0.19.0's Windows
           bundle (d3d11 cross-compiled on the Linux editor) passed that
           check, then every vertex shader failed driver-side creation with
           E_INVALIDARG at first draw. Blits through such a shader silently
           output nothing, so the NightVision-class cameras streamed black
           while KSP.log filled with "ShaderProgram is unsupported" spam.

           Invalid-blob bundles fail as a class (all five shaders in that
           session), so one probed shader stands in for the bundle. A healthy
           NightVision turns a white input NVG green (g = 255); all-dark
           output means shader creation failed on this device, and every
           bundle material is withheld so the existing fallbacks engage
           (HullcamVDS filter for NightVision, FX absent). Warns once.

           An EXCEPTION while probing is not a failed probe: a probe that
           cannot run must not take a working platform's shaders down, so it
           reports inconclusive and lets materials through. */
        private static bool BundlePassesRenderProbe(AssetBundle bundle)
        {
            if (_probeAttempted) return !_probeFailed;
            _probeAttempted = true;

            var prevActive = RenderTexture.active;
            Material mat = null;
            Texture2D src = null;
            Texture2D read = null;
            RenderTexture rt = null;
            try
            {
                var shader = bundle.LoadAsset<Shader>(ProbeShaderName);
                if (shader == null || !shader.isSupported)
                    return true; // nothing probeable; per-shader guards above still apply

                mat = new Material(shader);

                src = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var white = new Color32[16];
                for (int i = 0; i < white.Length; i++)
                    white[i] = new Color32(255, 255, 255, 255);
                src.SetPixels32(white);
                src.Apply();

                rt = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGB32);
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
                Graphics.Blit(src, rt, mat);

                read = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                read.Apply();

                var px = read.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].r > 8 || px[i].g > 8 || px[i].b > 8)
                        return true;
                }

                _probeFailed = true;
                Debug.LogWarning(
                    "[Kerbcast] kerbcast-shaders render probe FAILED: shader " +
                    $"'{ProbeShaderName}' reports isSupported but a test blit produced no " +
                    $"output on {Application.platform}/{SystemInfo.graphicsDeviceType}. " +
                    "The bundle's shader variants are likely invalid for this graphics " +
                    "device (e.g. bad d3d11 blobs); disabling all kerbcast bundle shaders " +
                    "so cameras fall back instead of streaming black");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast] kerbcast-shaders render probe could not run ({ex.Message}); assuming shaders are usable");
                return true;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
                if (read != null) UnityEngine.Object.Destroy(read);
                if (src != null) UnityEngine.Object.Destroy(src);
                if (mat != null) UnityEngine.Object.Destroy(mat);
            }
        }
    }
}
