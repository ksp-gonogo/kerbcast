// Headless regression test for the per-camera Hullcam filter blit (c00fe16).
//
// Invoked from CI by:
//   xvfb-run -a "$UNITY_EDITOR_PATH" -batchmode -quit \
//      -projectPath . \
//      -executeMethod KerbcastCI.HullcamBlitDeterminism.RunAll \
//      -buildTarget Linux64 -logFile -
//
// The bug class under test: kerbcast cameras used to blit through HullcamVDS's
// shared static Material (CameraFilter.mtShader), so any filter class that
// does not rewrite a uniform itself (the reticle _Title/_TitleTex on
// ColorHiResTV and friends, the NightVision overlay slots) inherited whatever
// another writer (Hullcam's per-frame MovieTimeFilter, other kerbcast cameras)
// last left there, producing flickering overlays. The fix redirects the
// static to a per-camera private Material for the duration of each filter
// pass; that mechanism is HullcamFilterBlit, compiled here from the SAME
// source file as the plugin via the Assets/Editor/SharedHullcamBlit symlink.
//
// What runs: the real MovieTime shader is loaded from the committed
// GameData/Kerbcast/HullcamShaders/shaders.linux bundle (the bundle kerbcast
// actually swaps in at runtime). A fake static-holder type stands in for
// HullcamVDS.CameraFilter (same field shape; the real DLL is pinned to that
// shape by Plugin/HullcamContract.Tests). For each of the nine filter modes,
// a mirrored uniform policy (the exact uniform SET the decompiled Hullcam
// class writes per blit, with deterministic stand-ins for its jittered
// values) blits a synthetic frame through HullcamFilterBlit.Run N times,
// with a hostile writer clobbering the shared material with garbage between
// every blit. Asserted:
//   (a) all N outputs per mode are byte-identical (the flicker bug,
//       mechanically);
//   (b) reticle policy: modes whose Hullcam class rewrites _TitleTex
//       (BWLoResTV, BWHiResTV, NightVision) plus Normal match the no-title
//       baseline exactly; the five reticle-showing modes differ from the
//       baseline, and only inside the reticle band;
//   (c) sanity: every filtered mode differs from a plain unfiltered blit
//       (the shader actually ran, no silent magenta/no-op pass);
//   (d) control: the LEGACY shared-static path, with the same hostile
//       writes interleaved, is NOT stable for ColorHiResTV or NightVision,
//       proving this harness detects the bug it guards against;
//   (e) orientation: a vertically asymmetric frame (top red, bottom blue)
//       through a reticle-showing and a reticle-suppressing class keeps
//       its top half on top, and the orientation probe in
//       HullcamFilterBlit reads the pass as upright. glcore here is the
//       parity-neutral bottom-left-UV-origin case; the top-left-origin
//       (D3D11/Metal) inversion is what Run's
//       SystemInfo.graphicsUVStartsAtTop-gated probe compensates at
//       runtime.
// Pass/fail report via Debug.Log; throws on failure so the editor exits
// non-zero (the BuildKerbcastShaders pattern).
//
// Byte-identity caveat: the MovieTime shader samples _Time for its *Speed
// uniforms. Every policy here holds all speeds at 0, and the whole run
// executes inside a single editor update (one executeMethod call), so
// _Time is constant across blits either way.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace KerbcastCI
{
    /* Stand-in for HullcamVDS.CameraFilter's static surface: the field
       names and kinds HullcamFilterBlit reflects over. Must stay in step
       with the contract HullcamContract.Tests pins on the real DLL. */
    public class FakeCameraFilterStatics
    {
        protected static Material mtShader = null;
        protected static Texture2D filmVignette = null;
        protected static Texture2D nvMesh = null;
        protected static Texture2D noise = null;

        public static Material SharedMaterial
        {
            get { return mtShader; }
            set { mtShader = value; }
        }

        public static void SeedTextures(Texture2D vignette, Texture2D mesh, Texture2D noiseTex)
        {
            filmVignette = vignette;
            nvMesh = mesh;
            noise = noiseTex;
        }
    }

    public static class HullcamBlitDeterminism
    {
        private const int _width = 256;
        private const int _height = 144;
        private const int _titledRuns = 5;
        private const int _baselineRuns = 2;
        private const int _legacyRuns = 4;
        /* Reticle alpha lives in rows [0.45, 0.55) of the title texture;
           policies offset the main UV by at most 0.05, so any titled vs
           baseline difference must stay inside this generous band. */
        private const float _reticleBandLo = 0.25f;
        private const float _reticleBandHi = 0.75f;

        private delegate void Policy(Material mt, Tex tex);

        private sealed class Mode
        {
            public string Name;
            public Policy Apply;        // null = Normal (plain blit, no material)
            public bool ShowsReticle;
        }

        private sealed class Tex
        {
            public Texture2D FilmVignette, Scratches, Dust, Noise, CrtMesh, NvMesh, VHold;
            public Texture2D NoneTX;    // fully transparent: the upstream suppressor
            public Texture2D Reticle;   // kerbcast's dockingdisplay stand-in
            public Texture2D[] Garbage; // hostile-writer payloads
        }

        public static void RunAll()
        {
            var failures = new List<string>();
            void Check(bool cond, string msg)
            {
                if (cond) Debug.Log("[Kerbcast-CI]   ok   " + msg);
                else { Debug.LogError("[Kerbcast-CI]   FAIL " + msg); failures.Add(msg); }
            }

            Shader movieTime = LoadMovieTimeShader();
            var tex = BuildTextures();
            FakeCameraFilterStatics.SeedTextures(tex.FilmVignette, tex.NvMesh, tex.Noise);

            var sharedMat = new Material(movieTime);
            FakeCameraFilterStatics.SharedMaterial = sharedMat;

            var src = MakeSourceRT();
            var dst = new RenderTexture(_width, _height, 0, RenderTextureFormat.ARGB32)
            {
                name = "HullcamBlitDeterminism_dst",
                antiAliasing = 1,
            };

            // Plain unfiltered copy of the synthetic frame, for sanity (c).
            Graphics.Blit(src, dst);
            byte[] plain = ReadBack(dst);

            foreach (var mode in Modes())
            {
                Debug.Log($"[Kerbcast-CI] mode {mode.Name}:");

                var titled = RunSeries(mode, true, _titledRuns, sharedMat, src, dst, tex);
                Check(AllIdentical(titled),
                    $"{mode.Name}: {_titledRuns} hostile-interleaved blits byte-identical");

                var baseline = RunSeries(mode, false, _baselineRuns, sharedMat, src, dst, tex);
                Check(AllIdentical(baseline),
                    $"{mode.Name}: baseline (no-title) blits byte-identical");

                bool matchesBaseline = SameBytes(titled[0], baseline[0]);
                if (mode.ShowsReticle)
                {
                    Check(!matchesBaseline,
                        $"{mode.Name}: titled output differs from no-title baseline (reticle present)");
                    string detail;
                    Check(DiffConfinedToBand(titled[0], baseline[0], out detail),
                        $"{mode.Name}: titled vs baseline difference confined to the reticle band{detail}");
                }
                else
                {
                    Check(matchesBaseline,
                        $"{mode.Name}: titled output identical to no-title baseline (reticle suppressed)");
                }

                if (mode.Apply != null)
                    Check(!SameBytes(titled[0], plain),
                        $"{mode.Name}: filtered output differs from a plain blit (shader ran)");
                else
                    Check(SameBytes(titled[0], plain),
                        $"{mode.Name}: passthrough output identical to a plain blit");
            }

            /* Control: the legacy shared-static path must NOT survive the
               same hostile interleaving, for a class that never rewrites
               _TitleTex (ColorHiResTV, the AerocamUP flicker) and for the
               NightVision fallback (unwritten overlay slots). If these ever
               pass, the harness has gone blind, not the bug fixed by
               accident: the redirect is the only thing the fixed path adds. */
            foreach (var name in new[] { "ColorHiResTV", "NightVision" })
            {
                var mode = FindMode(name);
                var legacy = RunLegacySeries(mode, _legacyRuns, sharedMat, src, dst, tex);
                Check(!AllIdentical(legacy),
                    $"{name}: LEGACY shared-static path flickers under hostile writes (harness can see the bug)");
            }

            /* Orientation: a vertically asymmetric frame (top half red,
               bottom half blue) through a reticle-showing and a
               reticle-suppressing class must come out with the input's top
               half still on top. glcore (this runner, and the tier-1 Deck)
               is the bottom-left-UV-origin case where the chain is
               parity-neutral as-is and Run's probe gate stays closed; the
               top-left-origin (D3D11/Metal) case is the asymmetric one the
               SystemInfo.graphicsUVStartsAtTop-gated probe in
               HullcamFilterBlit.Run measures and compensates at runtime.
               MeasurePassInverted is also asserted directly so the probe's
               own read of an upright pass is pinned here. */
            foreach (var name in new[] { "ColorHiResTV", "BWHiResTV" })
            {
                var mode = FindMode(name);
                bool upright;
                string orientDetail;
                bool measured = RunOrientation(mode, sharedMat, tex, out upright, out orientDetail);
                Check(upright,
                    $"{name}: asymmetric frame keeps its top half on top through Run{orientDetail}");
                Check(!measured,
                    $"{name}: MeasurePassInverted reports upright on a bottom-left-origin API");
            }

            Debug.Log($"[Kerbcast-CI] HullcamBlitDeterminism: {failures.Count} failure(s)");
            if (failures.Count > 0)
                throw new Exception(
                    $"HullcamBlitDeterminism: {failures.Count} assertion(s) failed; see FAIL lines above");
            Debug.Log("[Kerbcast-CI] HullcamBlitDeterminism: ALL CHECKS PASSED");
        }

        // ------------------------------------------------------------------
        // Series runners
        // ------------------------------------------------------------------

        /* One fixed-path series: a fresh HullcamFilterBlit rig (fresh private
           material, exactly like a freshly attached kerbcast camera), N
           passes through Run with the hostile writer clobbering the shared
           material before AND after every pass. Inside Run the static reads
           back the redirected private material, mirroring how the real
           CameraFilter class reaches mtShader. */
        private static List<byte[]> RunSeries(
            Mode mode, bool title, int n,
            Material sharedMat, RenderTexture src, RenderTexture dst, Tex tex)
        {
            var rig = new Kerbcast.HullcamFilterBlit(typeof(FakeCameraFilterStatics));
            var outputs = new List<byte[]>();
            for (int i = 0; i < n; i++)
            {
                Clobber(sharedMat, tex, i);
                rig.Run((s, d) =>
                {
                    var mt = FakeCameraFilterStatics.SharedMaterial;
                    TitlePage(mt, title, tex.Reticle);
                    if (mode.Apply == null)
                    {
                        Graphics.Blit(s, d);
                    }
                    else
                    {
                        mode.Apply(mt, tex);
                        Graphics.Blit(s, d, mt);
                    }
                }, src, dst);
                Clobber(sharedMat, tex, i + 7);
                outputs.Add(ReadBack(dst));
            }
            if (rig.Material != null)
                UnityEngine.Object.DestroyImmediate(rig.Material);
            if (!ReferenceEquals(FakeCameraFilterStatics.SharedMaterial, sharedMat))
                throw new Exception("HullcamFilterBlit.Run failed to restore the shared static");
            return outputs;
        }

        /* The pre-c00fe16 path: title write and filter blit both against the
           shared material, with the hostile write landing in between (the
           in-game interleaving that caused the flicker). */
        private static List<byte[]> RunLegacySeries(
            Mode mode, int n,
            Material sharedMat, RenderTexture src, RenderTexture dst, Tex tex)
        {
            var outputs = new List<byte[]>();
            for (int i = 0; i < n; i++)
            {
                TitlePage(sharedMat, true, tex.Reticle);
                Clobber(sharedMat, tex, i);
                mode.Apply(sharedMat, tex);
                Graphics.Blit(src, dst, sharedMat);
                outputs.Add(ReadBack(dst));
            }
            return outputs;
        }

        /* Orientation check: top-red/bottom-blue frame through a fresh rig
           (title on, exactly the plugin's call shape). Returns via out
           params whether the output kept the red half on top and what
           MeasurePassInverted said about the same pass; the caller asserts
           upright=true and measured=false on this bottom-left-origin
           runner. Uprightness is judged by red-vs-blue channel dominance
           per half, falling back to luminance for monochrome classes
           (red's 0.299 luma beats blue's 0.114 through every monotonic
           MovieTime transform). */
        private static bool RunOrientation(
            Mode mode, Material sharedMat, Tex tex,
            out bool upright, out string detail)
        {
            var asymSrc = MakeOrientationSourceRT();
            var asymDst = new RenderTexture(_width, _height, 0, RenderTextureFormat.ARGB32)
            {
                name = "HullcamBlitDeterminism_orient_dst",
                antiAliasing = 1,
            };
            var rig = new Kerbcast.HullcamFilterBlit(typeof(FakeCameraFilterStatics));
            Action<RenderTexture, RenderTexture> pass = (s, d) =>
            {
                var mt = FakeCameraFilterStatics.SharedMaterial;
                TitlePage(mt, true, tex.Reticle);
                mode.Apply(mt, tex);
                Graphics.Blit(s, d, mt);
            };
            try
            {
                rig.Run(pass, asymSrc, asymDst);
                byte[] outBytes = ReadBack(asymDst);

                /* Raw RGBA32 rows are bottom-up; compare the outer row
                   quarters (clear of the central reticle band). */
                int bytesPerRow = _width * 4;
                int quarter = _height / 4;
                double topR = 0, topB = 0, topLum = 0, botR = 0, botB = 0, botLum = 0;
                for (int y = 0; y < quarter; y++)
                {
                    int bot = y * bytesPerRow;
                    int top = (_height - 1 - y) * bytesPerRow;
                    for (int x = 0; x < _width; x++)
                    {
                        int bo = bot + x * 4, to = top + x * 4;
                        botR += outBytes[bo]; botB += outBytes[bo + 2];
                        botLum += outBytes[bo] + outBytes[bo + 1] + outBytes[bo + 2];
                        topR += outBytes[to]; topB += outBytes[to + 2];
                        topLum += outBytes[to] + outBytes[to + 1] + outBytes[to + 2];
                    }
                }
                double texels = (double)quarter * _width;
                double channelScore = ((topR - topB) - (botR - botB)) / texels;
                double lumScore = (topLum - botLum) / texels;
                const double eps = 5.0;
                upright = Math.Abs(channelScore) >= eps
                    ? channelScore > 0
                    : lumScore > eps;
                detail = $" (channelScore={channelScore:F1}, lumScore={lumScore:F1})";

                return Kerbcast.HullcamFilterBlit.MeasurePassInverted(pass);
            }
            finally
            {
                if (rig.Material != null)
                    UnityEngine.Object.DestroyImmediate(rig.Material);
                if (!ReferenceEquals(FakeCameraFilterStatics.SharedMaterial, sharedMat))
                    throw new Exception("orientation Run failed to restore the shared static");
                asymSrc.Release();
                UnityEngine.Object.DestroyImmediate(asymSrc);
                asymDst.Release();
                UnityEngine.Object.DestroyImmediate(asymDst);
            }
        }

        // Top half red, bottom half blue: the vertically asymmetric frame
        // for the orientation assertion.
        private static RenderTexture MakeOrientationSourceRT()
        {
            var px = new Color32[_width * _height];
            for (int y = 0; y < _height; y++)
                for (int x = 0; x < _width; x++)
                    px[y * _width + x] = y >= _height / 2
                        ? new Color32(255, 0, 0, 255)   // pixel rows run bottom-up
                        : new Color32(0, 0, 255, 255);
            var tex = MakeTexture("orient_frame", _width, _height, px);
            var rt = new RenderTexture(_width, _height, 0, RenderTextureFormat.ARGB32)
            {
                name = "HullcamBlitDeterminism_orient_src",
                antiAliasing = 1,
            };
            Graphics.Blit(tex, rt);
            UnityEngine.Object.DestroyImmediate(tex);
            return rt;
        }

        // Mirrors CameraFilter.RenderTitlePage: kerbcast calls it with
        // title=true and its dockingdisplay copy before every filter blit.
        private static void TitlePage(Material mt, bool title, Texture2D titleTex)
        {
            if (mt == null) return;
            mt.SetFloat("_Title", (title && titleTex != null) ? 1f : 0f);
            if (title && titleTex != null)
                mt.SetTexture("_TitleTex", titleTex);
        }

        /* The hostile writer: everything Hullcam's own MovieTimeFilter or
           another (legacy) kerbcast camera could leave on the shared
           material, varied per call so any leak into our output breaks
           byte-identity: a dockingdisplay-like crosshair, a none-like
           transparent texture, random colors and junk floats. */
        private static void Clobber(Material shared, Tex tex, int i)
        {
            var g = tex.Garbage[i % tex.Garbage.Length];
            shared.SetFloat("_Title", i % 2);
            shared.SetTexture("_TitleTex", g);
            shared.SetTexture("_VignetteTex", tex.Garbage[(i + 1) % tex.Garbage.Length]);
            shared.SetTexture("_Overlay1Tex", tex.Garbage[(i + 2) % tex.Garbage.Length]);
            shared.SetTexture("_Overlay2Tex", g);
            shared.SetFloat("_Monochrome", (i + 1) % 2);
            shared.SetColor("_MonoColor", new Color(0.13f * (i % 7), 0.29f * (i % 3), 0.41f * (i % 2), 1f));
            shared.SetFloat("_ColorJitter", 0.2f + 0.3f * (i % 3));
            shared.SetFloat("_Contrast", 0.5f + 0.7f * (i % 4));
            shared.SetFloat("_ContrastJitter", 0.1f * (i % 5));
            shared.SetFloat("_Brightness", 0.3f + 0.1f * (i % 6));
            shared.SetFloat("_BrightnessJitter", 0.05f * (i % 4));
            shared.SetFloat("_MainOffsetX", 0.31f * (i % 3));
            shared.SetFloat("_MainOffsetY", 0.17f * (i % 4));
            shared.SetFloat("_MainSpeedX", i % 2);
            shared.SetFloat("_MainSpeedY", i % 3);
            shared.SetFloat("_VignetteAmount", 0.5f * (i % 3));
            shared.SetFloat("_VignetteOffsetX", 0.23f * (i % 2));
            shared.SetFloat("_VignetteOffsetY", 0.37f * (i % 3));
            shared.SetFloat("_VignetteSpeedX", i % 2);
            shared.SetFloat("_VignetteSpeedY", i % 2);
            shared.SetFloat("_Overlay1Amount", 0.4f * (i % 3));
            shared.SetFloat("_Overlay1OffsetX", 0.11f * (i % 5));
            shared.SetFloat("_Overlay1OffsetY", 0.07f * (i % 4));
            shared.SetFloat("_Overlay1SpeedX", i % 3);
            shared.SetFloat("_Overlay1SpeedY", -10f * (i % 2));
            shared.SetFloat("_Overlay2Amount", 0.6f * (i % 2));
            shared.SetFloat("_Overlay2OffsetX", 0.19f * (i % 3));
            shared.SetFloat("_Overlay2OffsetY", 0.43f * (i % 2));
            shared.SetFloat("_Overlay2SpeedX", i % 2);
            shared.SetFloat("_Overlay2SpeedY", i % 3);
        }

        // ------------------------------------------------------------------
        // The nine mirrored filter policies. Each writes the exact uniform
        // SET the decompiled HullcamVDS class writes per blit (pinned by
        // HullcamContract.Tests), with deterministic constants standing in
        // for its jittered values. What matters for the bug class is which
        // uniforms a mode does NOT write: those are the slots a shared
        // material leaks through.
        // ------------------------------------------------------------------

        private static List<Mode> Modes()
        {
            return new List<Mode>
            {
                new Mode { Name = "Normal", Apply = null, ShowsReticle = false },
                new Mode
                {
                    Name = "DockingCam", ShowsReticle = true,
                    Apply = (mt, tex) =>
                    {
                        mt.SetTexture("_VignetteTex", tex.FilmVignette);
                        mt.SetTexture("_Overlay1Tex", tex.FilmVignette);
                        mt.SetTexture("_Overlay2Tex", tex.FilmVignette);
                        FloatBlock(mt, 1f, new Color(0.5f, 0.5f, 0.5f, 0.5f), 1.5f, 0.55f,
                            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
                    },
                },
                new Mode
                {
                    Name = "BWFilm", ShowsReticle = true,
                    Apply = (mt, tex) =>
                    {
                        mt.SetTexture("_VignetteTex", tex.FilmVignette);
                        mt.SetTexture("_Overlay1Tex", tex.Scratches);
                        mt.SetTexture("_Overlay2Tex", tex.Dust);
                        FloatBlock(mt, 1f, new Color(0.5f, 0.5f, 0.5f, 1f), 1.6f, 0.55f,
                            0.01f, 0.6f, 0.01f, 0.35f, -50f, 0.35f, 0.02f, 0.03f);
                    },
                },
                new Mode
                {
                    Name = "BWLoResTV", ShowsReticle = false,
                    Apply = (mt, tex) =>
                    {
                        mt.SetTexture("_VignetteTex", tex.VHold);
                        mt.SetTexture("_Overlay1Tex", tex.CrtMesh);
                        mt.SetTexture("_Overlay2Tex", tex.Noise);
                        mt.SetTexture("_TitleTex", tex.NoneTX); // the suppressor
                        FloatBlock(mt, 1f, new Color(0.5f, 0.5f, 0.5f, 1f), 1.6f, 0.55f,
                            0.05f, 0.8f, 0.05f, 0.4f, 0f, 0.4f, 0.01f, 0.02f);
                    },
                },
                new Mode
                {
                    Name = "BWHiResTV", ShowsReticle = false,
                    Apply = (mt, tex) =>
                    {
                        mt.SetTexture("_VignetteTex", tex.VHold);
                        mt.SetTexture("_Overlay1Tex", tex.CrtMesh);
                        mt.SetTexture("_Overlay2Tex", tex.Noise);
                        mt.SetTexture("_TitleTex", tex.NoneTX); // the suppressor
                        FloatBlock(mt, 1f, new Color(0.5f, 0.5f, 0.5f, 1f), 1.7f, 0.6f,
                            0.04f, 0.6f, 0.04f, 0.25f, 0f, 0.25f, 0.02f, 0.01f);
                    },
                },
                new Mode
                {
                    Name = "ColorFilm", ShowsReticle = true,
                    Apply = (mt, tex) =>
                    {
                        mt.SetTexture("_VignetteTex", tex.FilmVignette);
                        mt.SetTexture("_Overlay1Tex", tex.Scratches);
                        mt.SetTexture("_Overlay2Tex", tex.Dust);
                        FloatBlock(mt, 0f, new Color(0.5f, 0.5f, 0.5f, 1f), 2f, 0.55f,
                            0.01f, 0.6f, 0.01f, 0.35f, -50f, 0.35f, 0.02f, 0.03f);
                    },
                },
                new Mode
                {
                    Name = "ColorLoResTV", ShowsReticle = true,
                    Apply = (mt, tex) =>
                    {
                        mt.SetTexture("_VignetteTex", tex.VHold);
                        mt.SetTexture("_Overlay1Tex", tex.CrtMesh);
                        mt.SetTexture("_Overlay2Tex", tex.Noise);
                        FloatBlock(mt, 0f, new Color(0.5f, 0.5f, 0.5f, 1f), 2f, 0.55f,
                            0.05f, 0.8f, 0.05f, 0.4f, 0f, 0.4f, 0.01f, 0.02f);
                    },
                },
                new Mode
                {
                    Name = "ColorHiResTV", ShowsReticle = true,
                    Apply = (mt, tex) =>
                    {
                        // The AerocamUP class: writes everything EXCEPT
                        // _Title/_TitleTex, which is why it flickered.
                        mt.SetTexture("_VignetteTex", tex.VHold);
                        mt.SetTexture("_Overlay1Tex", tex.CrtMesh);
                        mt.SetTexture("_Overlay2Tex", tex.Noise);
                        FloatBlock(mt, 0f, new Color(0.5f, 0.5f, 0.5f, 1f), 2f, 0.6f,
                            0.04f, 0.5f, 0.04f, 0.3f, 0f, 0.3f, 0.02f, 0.01f);
                    },
                },
                new Mode
                {
                    Name = "NightVision", ShowsReticle = false,
                    Apply = (mt, tex) =>
                    {
                        /* The mtShader fallback: rewrites _TitleTex but
                           NEVER the three overlay slots; the private
                           material's once-seeded textures are what keep
                           this deterministic. */
                        mt.SetTexture("_TitleTex", tex.NoneTX);
                        FloatBlock(mt, 1f, new Color(0f, 0.5f, 0f, 1f), 1.8f, 0.6f,
                            0f, 1f, 0f, 0.3f, 0f, 0.3f, 0.01f, 0.02f);
                    },
                },
            };
        }

        // The float/color block every non-Normal Hullcam filter class writes
        // in full on each blit, in the upstream order. Speeds and the X
        // offsets the classes hold at zero stay zero here.
        private static void FloatBlock(Material mt, float monochrome, Color monoColor,
            float contrast, float brightness, float mainOffsetY,
            float vignetteAmount, float vignetteOffsetY,
            float overlay1Amount, float overlay1SpeedY,
            float overlay2Amount, float overlay2OffsetX, float overlay2OffsetY)
        {
            mt.SetFloat("_Monochrome", monochrome);
            mt.SetColor("_MonoColor", monoColor);
            mt.SetFloat("_ColorJitter", 1f);
            mt.SetFloat("_Contrast", contrast);
            mt.SetFloat("_ContrastJitter", 1f);
            mt.SetFloat("_Brightness", brightness);
            mt.SetFloat("_BrightnessJitter", 1f);
            mt.SetFloat("_MainOffsetX", 0f);
            mt.SetFloat("_MainOffsetY", mainOffsetY);
            mt.SetFloat("_MainSpeedX", 0f);
            mt.SetFloat("_MainSpeedY", 0f);
            mt.SetFloat("_VignetteAmount", vignetteAmount);
            mt.SetFloat("_VignetteOffsetX", 0f);
            mt.SetFloat("_VignetteOffsetY", vignetteOffsetY);
            mt.SetFloat("_VignetteSpeedX", 0f);
            mt.SetFloat("_VignetteSpeedY", 0f);
            mt.SetFloat("_Overlay1Amount", overlay1Amount);
            mt.SetFloat("_Overlay1OffsetX", 0f);
            mt.SetFloat("_Overlay1OffsetY", 0f);
            mt.SetFloat("_Overlay1SpeedX", 0f);
            mt.SetFloat("_Overlay1SpeedY", overlay1SpeedY);
            mt.SetFloat("_Overlay2Amount", overlay2Amount);
            mt.SetFloat("_Overlay2OffsetX", overlay2OffsetX);
            mt.SetFloat("_Overlay2OffsetY", overlay2OffsetY);
            mt.SetFloat("_Overlay2SpeedX", 0f);
            mt.SetFloat("_Overlay2SpeedY", 0f);
        }

        private static Mode FindMode(string name)
        {
            foreach (var m in Modes())
                if (m.Name == name) return m;
            throw new Exception($"unknown mode {name}");
        }

        // ------------------------------------------------------------------
        // Assets
        // ------------------------------------------------------------------

        /* The committed bundle the plugin swaps in at runtime
           (HullcamShaderBundleSwap), so the test exercises the shipped
           MovieTime shader, not a hand-copied source. Repo root is three
           levels above Assets/. */
        private static Shader LoadMovieTimeShader()
        {
            string path = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "..",
                "GameData", "Kerbcast", "HullcamShaders", "shaders.linux"));
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"shaders.linux bundle not found at {path}; is the repo checkout intact?");
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
                throw new Exception($"AssetBundle.LoadFromFile failed for {path}");
            try
            {
                foreach (var shader in bundle.LoadAllAssets<Shader>())
                {
                    if (shader.name == "Custom/MovieTime")
                    {
                        if (!shader.isSupported)
                            throw new Exception(
                                "Custom/MovieTime loaded but is not supported on this graphics device");
                        return shader;
                    }
                }
            }
            finally
            {
                bundle.Unload(false);
            }
            throw new Exception("Custom/MovieTime not present in shaders.linux");
        }

        private static RenderTexture MakeSourceRT()
        {
            // Deterministic synthetic frame: colour bars over a vertical
            // gradient, enough structure that every filter term shows.
            var px = new Color32[_width * _height];
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int bar = (x * 8) / _width;
                    byte v = (byte)((y * 255) / (_height - 1));
                    byte r = (byte)((bar & 1) != 0 ? 230 : 30);
                    byte g = (byte)((bar & 2) != 0 ? 230 : 30);
                    byte b = (byte)((bar & 4) != 0 ? 230 : 30);
                    px[y * _width + x] = new Color32(
                        (byte)((r + v) / 2), (byte)((g + v) / 2), (byte)((b + v) / 2), 255);
                }
            }
            var tex = MakeTexture("src_frame", _width, _height, px);
            var rt = new RenderTexture(_width, _height, 0, RenderTextureFormat.ARGB32)
            {
                name = "HullcamBlitDeterminism_src",
                antiAliasing = 1,
            };
            Graphics.Blit(tex, rt);
            return rt;
        }

        private static Tex BuildTextures()
        {
            var rng = new System.Random(20260610);
            var tex = new Tex
            {
                FilmVignette = Radial("filmVignette", 128, 1.0f),
                Scratches = Pattern("scratches", 128, (x, y) => x % 17 == 0 ? 0.9f : 0f),
                Dust = Noise2D("dust", 128, rng, 0.08f),
                Noise = Noise2D("noise", 128, rng, 1f),
                CrtMesh = Pattern("crtMesh", 128, (x, y) => y % 3 == 0 ? 0.8f : 0.1f),
                NvMesh = Pattern("nvMesh", 128, (x, y) => (x + y) % 5 == 0 ? 0.7f : 0.2f),
                VHold = Pattern("vHold", 128, (x, y) => y < 8 ? 1f : 0f),
                NoneTX = Solid("noneTX", new Color32(0, 0, 0, 0)),
                Reticle = ReticleBand("reticle"),
                Garbage = new[]
                {
                    Solid("garbage_magenta", new Color32(255, 0, 255, 255)),
                    Noise2D("garbage_confetti", 64, rng, 1f),
                    Crosshair("garbage_crosshair"),
                },
            };
            return tex;
        }

        private static Texture2D MakeTexture(string name, int w, int h, Color32[] px)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
            };
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        private static Texture2D Solid(string name, Color32 c)
        {
            var px = new Color32[16];
            for (int i = 0; i < 16; i++) px[i] = c;
            return MakeTexture(name, 4, 4, px);
        }

        private static Texture2D Pattern(string name, int size, Func<int, int, float> f)
        {
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    byte v = (byte)(Mathf.Clamp01(f(x, y)) * 255);
                    px[y * size + x] = new Color32(v, v, v, 255);
                }
            return MakeTexture(name, size, size, px);
        }

        private static Texture2D Radial(string name, int size, float strength)
        {
            var px = new Color32[size * size];
            float half = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half;
                    byte v = (byte)(Mathf.Clamp01(1f - d * strength) * 255);
                    px[y * size + x] = new Color32(v, v, v, 255);
                }
            return MakeTexture(name, size, size, px);
        }

        private static Texture2D Noise2D(string name, int size, System.Random rng, float density)
        {
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++)
            {
                bool on = rng.NextDouble() < density;
                byte v = on ? (byte)rng.Next(64, 256) : (byte)0;
                px[i] = new Color32(v, v, v, 255);
            }
            return MakeTexture(name, size, size, px);
        }

        // Title texture with alpha only in the central horizontal band
        // (rows 0.45..0.55), so the reticle's footprint in the output is
        // known and the band-confinement assertion has teeth.
        private static Texture2D ReticleBand(string name)
        {
            const int size = 128;
            var px = new Color32[size * size];
            int lo = (int)(size * 0.45f), hi = (int)(size * 0.55f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool on = y >= lo && y < hi && (x / 8) % 2 == 0; // dashed
                    px[y * size + x] = on
                        ? new Color32(40, 255, 60, 255)
                        : new Color32(0, 0, 0, 0);
                }
            return MakeTexture(name, size, size, px);
        }

        // Dockingdisplay-like full-frame crosshair, alpha 1 everywhere it
        // draws: the worst-case garbage payload for the legacy path.
        private static Texture2D Crosshair(string name)
        {
            const int size = 128;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool on = Mathf.Abs(x - size / 2) < 3 || Mathf.Abs(y - size / 2) < 3;
                    px[y * size + x] = on
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(0, 0, 0, 0);
                }
            return MakeTexture(name, size, size, px);
        }

        // ------------------------------------------------------------------
        // Readback + comparison
        // ------------------------------------------------------------------

        private static byte[] ReadBack(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            byte[] raw = tex.GetRawTextureData();
            var copy = new byte[raw.Length];
            Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
            UnityEngine.Object.DestroyImmediate(tex);
            return copy;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            using (var sha = SHA256.Create())
            {
                var ha = sha.ComputeHash(a);
                var hb = sha.ComputeHash(b);
                for (int i = 0; i < ha.Length; i++)
                    if (ha[i] != hb[i]) return false;
            }
            return true;
        }

        private static bool AllIdentical(List<byte[]> outputs)
        {
            for (int i = 1; i < outputs.Count; i++)
                if (!SameBytes(outputs[0], outputs[i])) return false;
            return true;
        }

        /* Titled vs baseline may differ only inside the reticle band (rows
           are bottom-up in raw RGBA32 data; the band is symmetric about the
           centre so orientation does not matter). Reports the first
           offending row for diagnosis. */
        private static bool DiffConfinedToBand(byte[] titled, byte[] baseline, out string detail)
        {
            detail = "";
            int bytesPerRow = _width * 4;
            int loRow = (int)(_height * _reticleBandLo);
            int hiRow = (int)(_height * _reticleBandHi);
            for (int r = 0; r < _height; r++)
            {
                if (r >= loRow && r < hiRow) continue;
                int start = r * bytesPerRow;
                for (int i = 0; i < bytesPerRow; i++)
                {
                    if (titled[start + i] != baseline[start + i])
                    {
                        detail = $" (unexpected diff at row {r}, outside [{loRow},{hiRow}))";
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
