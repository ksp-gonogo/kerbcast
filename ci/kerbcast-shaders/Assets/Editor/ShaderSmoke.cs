// Per-shader render smoke test for a just-built kerbcast-shaders bundle.
//
// Invoked from CI by (Linux bundle, glcore under xvfb):
//   xvfb-run -a "$UNITY_EDITOR_PATH" -batchmode -quit \
//      -projectPath . \
//      -executeMethod KerbcastCI.ShaderSmoke.RenderAll \
//      -buildTarget Linux64 -logFile -
// and by the Windows job (Windows bundle, d3d11) with the env var
// KERBCAST_SMOKE_BUNDLE_DIR=Bundles-windows selecting which bundle to load.
//
// VALIDATION BLIND SPOT, stated honestly: a graphics device can only create
// and execute shader variants for its own API. The glcore run on the Linux
// runner proves the glcore variants; it CANNOT execute d3d11 blobs, and
// shader.isSupported is only a variant-presence check, so an invalid d3d11
// blob sails through the Linux run green. That is exactly how v0.19.0
// shipped a Windows bundle whose five d3d11 vertex shaders were all rejected
// by the D3D11 runtime at draw time (E_INVALIDARG, 0x80070057): every
// kerbcast-bundle camera streamed black and KSP.log gained 470k lines of
// "ShaderProgram is unsupported" spam. The Windows-runner run of this same
// test is the fix: D3D11 validates bytecode inside Create*Shader even on the
// hosted runner's WARP rasterizer, so a bad blob fails the render there.
// The plugin's KerbcastFxAssets render probe is the last-resort runtime net
// for any bundle that still slips through.
//
// Catches "bundle built but a shader is broken" inside the blocking
// build-kerbcast-shaders workflow, in seconds, instead of leaving it to the
// slow FX preview render (now the non-blocking fx-previews.yml workflow).
// For EVERY Shader asset in the freshly built bundle it asserts:
//   (a) shader.isSupported on this graphics device;
//   (b) the output of a small deterministic render through a material built
//       from the shader is not Unity's magenta error pattern (the pink
//       error shader replaces anything that failed to compile);
//   (c) the output is not all-zero (every stage of the shader actually
//       executed and wrote pixels).
//
// The render is a 2 m sphere through a 160x90 camera cleared to transparent
// black, with a deterministic colour-bar frame bound as _MainTex and
// _FXMainTex and the FX globals/uniforms set so the additive shaders emit.
// A plain fullscreen Graphics.Blit cannot drive the plasma/ember geometry
// stages (the blit quad carries no normals, so no triangle survives their
// windward gate); the sphere has normals and UVs, so the same rig exercises
// vertex, geometry and fragment stages of every shader uniformly, image
// filters included (vert_img reads the sphere's position + UV just fine).
//
// Per-shader pass/fail summary via Debug.Log; throws on any failure so the
// editor exits non-zero (the BuildKerbcastShaders pattern). Needs a real
// graphics device: run under xvfb, never with -nographics.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KerbcastCI
{
    public static class ShaderSmoke
    {
        private const int _width = 160;
        private const int _height = 90;
        /* Channel value a pixel must exceed to count as lit; LLVMpipe
           rounding noise stays below it. */
        private const byte _litFloor = 2;
        /* A healthy shader lights hundreds of pixels in this rig; demanding
           a handful keeps a single stray pixel from passing a dead one. */
        private const int _minLitPixels = 16;

        /* Shaders the bundle must contain. The smoke test also runs on any
           EXTRA shader it finds, so additions are covered without touching
           this list; the list only guards against a shader dropping out of
           the bundle (e.g. a lost assetBundleName meta tag). */
        private static readonly string[] _expectedShaders =
        {
            "Kerbcast/Plasma",
            "Kerbcast/Bowshock",
            "Kerbcast/Trail",
            "Kerbcast/Ember",
            "Kerbcast/NightVision",
        };

        public static void RenderAll()
        {
            var failures = new List<string>();

            List<Shader> shaders = LoadBundleShaders();
            Debug.Log($"[Kerbcast-CI] ShaderSmoke: {shaders.Count} shader(s) in {BundleDir()} "
                + $"(device: {SystemInfo.graphicsDeviceType})");

            var found = new HashSet<string>();
            foreach (var s in shaders) found.Add(s.name);
            foreach (var expected in _expectedShaders)
            {
                if (!found.Contains(expected))
                {
                    Debug.LogError($"[Kerbcast-CI]   FAIL {expected}: missing from the bundle");
                    failures.Add($"{expected}: missing from the bundle (lost assetBundleName meta tag?)");
                }
            }

            Texture2D frame = MakeTestFrame();
            SetFxGlobals(frame);

            foreach (var shader in shaders)
                SmokeOne(shader, frame, failures);

            Debug.Log($"[Kerbcast-CI] ShaderSmoke: {failures.Count} failure(s)");
            if (failures.Count > 0)
                throw new Exception(
                    $"ShaderSmoke: {failures.Count} shader check(s) failed; see FAIL lines above");
            Debug.Log("[Kerbcast-CI] ShaderSmoke: ALL SHADERS PASSED");
        }

        /* Which Bundles-<platform> dir to smoke. The Linux job tests
           Bundles-linux (glcore); the Windows job sets
           KERBCAST_SMOKE_BUNDLE_DIR=Bundles-windows to test the d3d11
           variants on a real D3D11 device. The loaded bundle's variants must
           match the editor's own graphics API or every shader reports
           unsupported, so each platform job tests its own bundle only. */
        private static string BundleDir()
        {
            string dir = Environment.GetEnvironmentVariable("KERBCAST_SMOKE_BUNDLE_DIR");
            return string.IsNullOrEmpty(dir) ? "Bundles-linux" : dir;
        }

        /* The bundle BuildKerbcastShaders just produced, not the project's
           Assets/Shaders sources: a shader can compile as a loose asset and
           still be broken or missing in the bundle KSP loads. */
        private static List<Shader> LoadBundleShaders()
        {
            string path = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", BundleDir(), "kerbcast-shaders"));
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"kerbcast-shaders bundle not found at {path}; run BuildKerbcastShaders.BuildAll first");
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
                throw new Exception($"AssetBundle.LoadFromFile failed for {path}");
            try
            {
                return new List<Shader>(bundle.LoadAllAssets<Shader>());
            }
            finally
            {
                /* false: keep the loaded Shader objects, drop the handle. */
                bundle.Unload(false);
            }
        }

        private static void SmokeOne(Shader shader, Texture2D frame, List<string> failures)
        {
            if (!shader.isSupported)
            {
                Debug.LogError($"[Kerbcast-CI]   FAIL {shader.name}: not supported on this graphics device");
                failures.Add($"{shader.name}: isSupported == false");
                return; // rendering it would only show the error shader
            }

            var root = new GameObject($"__shader_smoke_{shader.name.Replace('/', '_')}");
            Material mat = null;
            RenderTexture rt = null;
            try
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.SetParent(root.transform, false);
                sphere.transform.localScale = new Vector3(2f, 2f, 2f);
                UnityEngine.Object.DestroyImmediate(sphere.GetComponent<Collider>());

                mat = new Material(shader);
                /* Each Set* is a no-op on shaders without that property, so
                   one uniform block serves the whole bundle. _Intensity
                   defaults to 0 on the FX shaders (= emit nothing), so it
                   MUST be raised for the all-zero assertion to have teeth. */
                mat.SetTexture("_MainTex", frame);
                mat.SetFloat("_Intensity", 1f);
                mat.SetFloat("_FxState", 1f);
                mat.SetFloat("_FxRadiusMul", 1.6f);
                mat.SetVector("_WindDirWorld", new Vector4(0f, 1f, 0f, 0f));
                sphere.GetComponent<MeshRenderer>().sharedMaterial = mat;

                var camGo = new GameObject("__shader_smoke_camera");
                camGo.transform.SetParent(root.transform, false);
                camGo.transform.localPosition = new Vector3(3.2f, 0.8f, 0f);
                camGo.transform.localRotation = Quaternion.LookRotation(
                    -camGo.transform.localPosition.normalized, Vector3.up);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                /* Transparent black canvas: only the shader can light pixels,
                   so "any lit pixel" means "the shader ran". */
                cam.backgroundColor = Color.clear;
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 50f;
                cam.allowMSAA = false;
                cam.allowHDR = false;

                rt = new RenderTexture(_width, _height, 24, RenderTextureFormat.ARGB32)
                {
                    name = $"ShaderSmokeRT_{shader.name.Replace('/', '_')}",
                    antiAliasing = 1,
                };
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = null;

                Color32[] px = ReadBack(rt);
                int lit = 0, magenta = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    Color32 p = px[i];
                    if (p.r > _litFloor || p.g > _litFloor || p.b > _litFloor) lit++;
                    if (p.r >= 250 && p.g <= 8 && p.b >= 250) magenta++;
                }

                /* The error shader paints the sphere's whole footprint
                   (thousands of pixels) solid magenta; the FX palettes
                   (orange plasma, white wind, green NVG) cannot plausibly
                   saturate to pure magenta across 1% of the frame. */
                string stats = $"{lit}/{px.Length} lit px, {magenta} magenta px";
                if (magenta > px.Length / 100)
                {
                    Debug.LogError($"[Kerbcast-CI]   FAIL {shader.name}: magenta error pattern ({stats})");
                    failures.Add($"{shader.name}: rendered the magenta error pattern ({stats})");
                }
                else if (lit < _minLitPixels)
                {
                    Debug.LogError($"[Kerbcast-CI]   FAIL {shader.name}: output (nearly) all-zero ({stats})");
                    failures.Add($"{shader.name}: output (nearly) all-zero, shader did not visibly execute ({stats})");
                }
                else
                {
                    Debug.Log($"[Kerbcast-CI]   ok   {shader.name}: supported, {stats}");
                }
            }
            finally
            {
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                if (mat != null) UnityEngine.Object.DestroyImmediate(mat);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /* Deterministic test frame: colour bars over a vertical gradient
           (the HullcamBlitDeterminism pattern). Bound as _MainTex for the
           image-style shaders and as the global _FXMainTex the FX shaders
           sample for streak noise; its value range keeps every sharpen and
           contrast term nonzero. */
        private static Texture2D MakeTestFrame()
        {
            const int size = 128;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int bar = (x * 8) / size;
                    byte v = (byte)((y * 255) / (size - 1));
                    byte r = (byte)((bar & 1) != 0 ? 230 : 30);
                    byte g = (byte)((bar & 2) != 0 ? 230 : 30);
                    byte b = (byte)((bar & 4) != 0 ? 230 : 30);
                    px[y * size + x] = new Color32(
                        (byte)((r + v) / 2), (byte)((g + v) / 2), (byte)((b + v) / 2), 255);
                }
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ShaderSmoke_testFrame",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        /* The KSP-published globals the FX shaders consume, at the same
           fallback values RenderFxPreviews uses when a fixture omits them.
           The flat white depth map reads as "nothing upwind here", so the
           depth-wrap term is simply off rather than garbage. */
        private static void SetFxGlobals(Texture2D frame)
        {
            Shader.SetGlobalVector("_LightDirection0", new Vector4(0f, -1f, 0f, 0f));
            Shader.SetGlobalVector("_FXColor", new Vector4(1f, 0.5f, 0.2f, 1f));
            Shader.SetGlobalFloat("_FxLength", 2f);
            Shader.SetGlobalFloat("_FXWobble", 1f);
            Shader.SetGlobalFloat("_FXFalloff", 0.5f);
            Shader.SetGlobalMatrix("_FXDepthCamMatrix", Matrix4x4.identity);
            Shader.SetGlobalMatrix("_FXDepthProjMatrix", Matrix4x4.identity);
            Shader.SetGlobalFloat("_FXProjectionNear", 0.5f);
            Shader.SetGlobalFloat("_FXProjectionFar", 80f);
            Shader.SetGlobalTexture("_FXMainTex", frame);
            Shader.SetGlobalTexture("_FXDepthMap", MakeFlatDepthTexture());
        }

        private static Texture2D MakeFlatDepthTexture()
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "ShaderSmoke_flatDepth" };
            var px = new Color[16];
            for (int i = 0; i < 16; i++) px[i] = Color.white;
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Color32[] ReadBack(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            Color32[] px = tex.GetPixels32();
            UnityEngine.Object.DestroyImmediate(tex);
            return px;
        }
    }
}
