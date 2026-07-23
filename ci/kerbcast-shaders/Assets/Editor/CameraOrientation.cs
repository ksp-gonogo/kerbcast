// Capture-blit orientation proof for a just-built kerbcast-shaders bundle.
//
// Invoked from CI (Linux bundle, glcore under xvfb):
//   xvfb-run -a "$UNITY_EDITOR_PATH" -batchmode -quit -projectPath "$(pwd)" \
//      -executeMethod KerbcastCI.CameraOrientation.RenderAll \
//      -buildTarget Linux64 -logFile -
// and by the Windows job (Windows bundle, d3d11) with
// KERBCAST_SMOKE_BUNDLE_DIR=Bundles-windows.
//
// A four-quadrant marker frame is CAMERA-RENDERED into an RT (matching the real
// capture path: the Direct3D internal flip only happens for camera-rendered
// textures, and it is that flip an uncompensated vert_img blit fails to undo;
// a Blit-populated source never triggers it, which is why the bug did not
// reproduce on WARP until this change). It is then blitted through each capture
// blit branch and read back; each quadrant must land where it started. The
// quadrants differ in LUMINANCE, not hue: the NightVision shader collapses
// every colour to green
// phosphor (v*0.15, v, v*0.05), so only brightness survives a filter blit. Four
// distinct brightness levels, judged by rank, still distinguish vertical flip,
// horizontal mirror and 180 rotation, not just the reported upside-down case.
// Levels are kept <= 63 so the shader's x4 gain cannot saturate them into one
// indistinguishable white.
//
// Branches proven:
//   plain        Unity's default blit. Reference AND the premise test: on d3d11
//                it is asserted upright, pinning the untested 71d883f "D3D11
//                reads upright, needs no flip" assumption.
//   nightvision  kerbcast's KerbcastNightVision shader via a custom-material
//                blit. vert_img does no UV-origin handling, so this inverts on
//                top-left-origin APIs (d3d11/metal): the GitHub #5 bug. Red on
//                d3d11 until the fix.
//   filter-probe the same NightVision material driven THROUGH
//                HullcamFilterBlit.Run, whose graphicsUVStartsAtTop-gated probe
//                measures and compensates the inversion. Exercises the probe's
//                d3d11 path (never covered before, since the determinism
//                harness's MovieTime bundle is glcore-only). Red on d3d11 only
//                if the probe defaults wrong.
//
// Needs a real graphics device: run under xvfb, never with -nographics.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KerbcastCI
{
    public static class CameraOrientation
    {
        // Even dimensions so the four quadrants are exact halves.
        private const int _w = 128;
        private const int _h = 72;

        /* Gray luminance per quadrant, keyed by position in the upright image:
           TL darkest .. BR brightest, ranks 0..3. Kept <= 63 so the NightVision
           x4 gain (61..252 out) stays unsaturated and the four stay distinct. */
        private static readonly byte[] _levels = { 16, 32, 48, 63 }; // TL, TR, BL, BR

        public static void RenderAll()
        {
            var failures = new List<string>();
            Debug.Log($"[Kerbcast-CI] CameraOrientation: device {SystemInfo.graphicsDeviceType}, "
                + $"uvStartsAtTop={SystemInfo.graphicsUVStartsAtTop}, bundle {BundleDir()}");

            RenderTexture src = MakeMarkerSource(1);
            RenderTexture srcMsaa = MakeMarkerSource(4);
            try
            {
                CheckBranch("plain", (s, d) => Graphics.Blit(s, d), src, failures);

                Material nv = LoadNightVisionMaterial();
                nv.SetFloat("_Gain", 4f); // explicit: the marker levels assume x4
                try
                {
                    CheckBranch("nightvision", (s, d) => Graphics.Blit(s, d, nv), src, failures);

                    /* Control probe: the same NightVision blit but from an
                       MSAA-resolved source. Per the Unity docs this is a
                       DOCUMENTED D3D11 flip trigger: an MSAA-resolved source's
                       _MainTex_TexelSize.y goes negative, so the uncompensated
                       vert_img SHOULD invert it on top-left-origin APIs while
                       staying upright on GL. This branch answers the load-
                       bearing question the plain branches cannot: can the CI
                       WARP device reproduce ANY real D3D11 orientation flip at
                       all? If nightvision-msaa is vflip on d3d11 and identity on
                       glcore, WARP is a viable red-test device and we chase
                       which trigger the launchcam hits. If it is identity on
                       BOTH, WARP cannot show D3D11 flips and real hardware is
                       required for anything orientation-related. */
                    CheckBranch("nightvision-msaa", (s, d) => Graphics.Blit(s, d, nv), srcMsaa, failures);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(nv);
                }

                CheckFilterBranch("filter-probe", src, failures);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(src);
                UnityEngine.Object.DestroyImmediate(srcMsaa);
            }

            Debug.Log($"[Kerbcast-CI] CameraOrientation: {failures.Count} failure(s)");
            if (failures.Count > 0)
                throw new Exception(
                    $"CameraOrientation: {failures.Count} orientation check(s) failed; see FAIL lines above");
            Debug.Log("[Kerbcast-CI] CameraOrientation: ALL BRANCHES UPRIGHT");
        }

        /* Which Bundles-<platform> dir to prove; the Windows job sets
           KERBCAST_SMOKE_BUNDLE_DIR=Bundles-windows, matching ShaderSmoke. */
        private static string BundleDir()
        {
            string dir = Environment.GetEnvironmentVariable("KERBCAST_SMOKE_BUNDLE_DIR");
            return string.IsNullOrEmpty(dir) ? "Bundles-linux" : dir;
        }

        /* Blit the marker src through pass into a fresh dst, read back, and
           assert the four quadrants are unmoved. Logs the measured layout on
           failure so the CI log names the error class. */
        private static void CheckBranch(
            string name, Action<RenderTexture, RenderTexture> pass,
            RenderTexture src, List<string> failures)
        {
            var dst = new RenderTexture(_w, _h, 0, RenderTextureFormat.ARGB32)
            {
                name = $"CameraOrientation_{name}_dst",
                antiAliasing = 1,
            };
            try
            {
                pass(src, dst);
                string layout = ClassifyCorners(ReadBack(dst));
                if (layout == "identity")
                    Debug.Log($"[Kerbcast-CI]   ok   {name}: corners upright (identity)");
                else
                {
                    Debug.LogError($"[Kerbcast-CI]   FAIL {name}: corners moved ({layout})");
                    failures.Add($"{name}: orientation {layout}, expected identity");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(dst);
            }
        }

        /* Filter-probe branch: run an inverting pass (NightVision material)
           THROUGH HullcamFilterBlit.Run, which on top-left-origin APIs probes
           the pass once and appends a compensating flip. Expected upright on
           BOTH APIs: on glcore the probe gate is closed and NightVision is
           already upright; on d3d11 the probe must measure the inversion and
           correct it. A non-identity result on d3d11 means the probe defaulted
           wrong (the MeasurePassInverted inconclusive/exception path), the
           second, latent bug this harness exists to surface. Rig construction
           mirrors HullcamBlitDeterminism. The private material Run builds keeps
           the shader default _Gain (4), matching the marker levels. */
        private static void CheckFilterBranch(string name, RenderTexture src, List<string> failures)
        {
            var dst = new RenderTexture(_w, _h, 0, RenderTextureFormat.ARGB32)
            {
                name = $"CameraOrientation_{name}_dst",
                antiAliasing = 1,
            };
            Material shared = LoadNightVisionMaterial();
            var rig = new Kerbcast.HullcamFilterBlit(typeof(FakeCameraFilterStatics));
            Material prevShared = FakeCameraFilterStatics.SharedMaterial;
            try
            {
                /* Run builds its own private material from this shared one's
                   shader and redirects the static to it for the pass; the pass
                   blits through whatever the static currently holds. */
                FakeCameraFilterStatics.SharedMaterial = shared;
                rig.Run((s, d) => Graphics.Blit(s, d, FakeCameraFilterStatics.SharedMaterial), src, dst);
                string layout = ClassifyCorners(ReadBack(dst));
                if (layout == "identity")
                    Debug.Log($"[Kerbcast-CI]   ok   {name}: probe kept corners upright (identity)");
                else
                {
                    Debug.LogError(
                        $"[Kerbcast-CI]   FAIL {name}: corners moved ({layout}); probe failed to compensate");
                    failures.Add($"{name}: orientation {layout}, expected identity (probe compensation)");
                }
            }
            finally
            {
                FakeCameraFilterStatics.SharedMaterial = prevShared;
                if (rig.Material != null) UnityEngine.Object.DestroyImmediate(rig.Material);
                UnityEngine.Object.DestroyImmediate(shared);
                UnityEngine.Object.DestroyImmediate(dst);
            }
        }

        /* Produce the marker source by rendering a full-frame quad with a
           CAMERA into the RT, not Graphics.Blit. This is load-bearing: on
           Direct3D Unity internally flips a texture a camera renders into (the
           thing that sets _MainTex_TexelSize.y < 0), and it is that flip a
           subsequent custom-material blit through vert_img fails to compensate.
           A Blit-populated source never gets the flip, so the NightVision
           inversion would not reproduce (it did not, on the WARP CI device,
           until this change). Depth 24 so a camera can render into it. The Quad
           primitive's front faces -Z, toward a camera on the -Z side, so
           Unlit/Texture (cull back) renders it; ClassifyCorners's degeneracy
           guard flags a blank frame if that ever culls. Ortho camera fills
           exactly. */
        private static RenderTexture MakeMarkerSource(int aa)
        {
            Texture2D marker = MakeMarkerTexture();
            var rt = new RenderTexture(_w, _h, 24, RenderTextureFormat.ARGB32)
            {
                name = $"CameraOrientation_src_aa{aa}",
                antiAliasing = aa,
            };
            var root = new GameObject("__cam_orient_marker");
            Material mat = null;
            try
            {
                float aspect = (float)_w / _h;
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.transform.SetParent(root.transform, false);
                quad.transform.localScale = new Vector3(aspect, 1f, 1f);
                UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
                mat = new Material(Shader.Find("Unlit/Texture"));
                mat.SetTexture("_MainTex", marker);
                quad.GetComponent<MeshRenderer>().sharedMaterial = mat;

                var camGo = new GameObject("__cam_orient_camera");
                camGo.transform.SetParent(root.transform, false);
                camGo.transform.localPosition = new Vector3(0f, 0f, -1f);
                camGo.transform.localRotation = Quaternion.identity; // forward +Z, onto the quad
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 0.5f; // quad is 1 unit tall
                cam.aspect = aspect;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 10f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.allowMSAA = aa > 1; // MSAA source is a documented D3D11 flip trigger
                cam.allowHDR = false;
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = null;
            }
            finally
            {
                if (mat != null) UnityEngine.Object.DestroyImmediate(mat);
                UnityEngine.Object.DestroyImmediate(marker);
                UnityEngine.Object.DestroyImmediate(root);
            }
            return rt;
        }

        /* Four-quadrant gray marker. Pixel rows run bottom-up, so the "top"
           quadrants live at high y. TL=_levels[0] (darkest) .. BR=_levels[3]
           (brightest); quad UVs map (0,0) to bottom-left, keeping BL at the
           frame's bottom-left when rendered upright. */
        private static Texture2D MakeMarkerTexture()
        {
            var px = new Color32[_w * _h];
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++)
                {
                    bool top = y >= _h / 2;
                    bool right = x >= _w / 2;
                    byte g = top ? (right ? _levels[1] : _levels[0]) : (right ? _levels[3] : _levels[2]);
                    px[y * _w + x] = new Color32(g, g, g, 255);
                }
            var tex = new Texture2D(_w, _h, TextureFormat.RGBA32, false)
            {
                name = "CameraOrientation_marker",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
            };
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        private static byte[] ReadBack(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            byte[] bytes = tex.GetRawTextureData();
            UnityEngine.Object.DestroyImmediate(tex);
            return bytes;
        }

        /* Sample each quadrant's brightness (green channel: gray marker g==luma,
           NightVision output g==phosphor value, both monotonic in the source
           level), rank the four, and match the rank tuple to a known transform.
           Rank-based so it survives the shader's brightness scaling; only the
           ORDER matters. Rows are bottom-up: y=_h-1 is the visual top. */
        private static string ClassifyCorners(byte[] rgba)
        {
            int inset = 4;
            int Green(int cx, int cy) => rgba[(cy * _w + cx) * 4 + 1];
            int tl = Green(inset, _h - 1 - inset);
            int tr = Green(_w - 1 - inset, _h - 1 - inset);
            int bl = Green(inset, inset);
            int br = Green(_w - 1 - inset, inset);

            /* Always log the corner values so identity is never ambiguous with a
               blank render. A near-uniform frame (marker never rendered, e.g. a
               culled quad) would otherwise tie-break into a false "identity". */
            int spread = Math.Max(Math.Max(tl, tr), Math.Max(bl, br))
                - Math.Min(Math.Min(tl, tr), Math.Min(bl, br));
            Debug.Log($"[Kerbcast-CI]     corners g=({tl},{tr},{bl},{br}) spread={spread}");
            if (spread < 20)
                return $"blank(g={tl},{tr},{bl},{br})";

            int[] vals = { tl, tr, bl, br };
            int[] rank = new int[4];
            for (int i = 0; i < 4; i++)
            {
                int r = 0;
                for (int j = 0; j < 4; j++)
                    if (vals[j] < vals[i] || (vals[j] == vals[i] && j < i)) r++;
                rank[i] = r;
            }
            // Expected upright ranks: TL=0 (darkest) TR=1 BL=2 BR=3.
            string key = $"{rank[0]}{rank[1]}{rank[2]}{rank[3]}";
            switch (key)
            {
                case "0123": return "identity";
                case "2301": return "vflip";
                case "1032": return "hmirror";
                case "3210": return "rot180";
                default:
                    return $"unknown(TL#{rank[0]},TR#{rank[1]},BL#{rank[2]},BR#{rank[3]}; "
                        + $"g={tl},{tr},{bl},{br})";
            }
        }

        /* The KerbcastNightVision material from the just-built bundle (not the
           project source), so the proof exercises the shader KSP actually
           loads. Same bundle path pattern as ShaderSmoke. */
        private static Material LoadNightVisionMaterial()
        {
            string path = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", BundleDir(), "kerbcast-shaders"));
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"kerbcast-shaders bundle not found at {path}; run BuildKerbcastShaders first");
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
                throw new Exception($"AssetBundle.LoadFromFile failed for {path}");
            try
            {
                foreach (var s in bundle.LoadAllAssets<Shader>())
                    if (s.name == "Kerbcast/NightVision")
                    {
                        if (!s.isSupported)
                            throw new Exception("Kerbcast/NightVision not supported on this device");
                        return new Material(s);
                    }
            }
            finally
            {
                bundle.Unload(false);
            }
            throw new Exception("Kerbcast/NightVision not present in the bundle");
        }
    }
}
