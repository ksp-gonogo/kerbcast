/* Per-camera Hullcam filter blit mechanism, factored out of KerbcastCamera
   (c00fe16) so the headless determinism test in ci/kerbcast-shaders compiles
   the SAME source file via the Assets/Editor symlink pattern (like Fx/Core).
   KSP-free and Hullcam-free by construction: UnityEngine plus reflection
   only. The Hullcam CameraFilter type arrives as a System.Type, so the test
   can substitute its own static-holder type with the same field shape (the
   shape itself is pinned against the real DLL by HullcamContract.Tests).

   Why this exists: Hullcam's CameraFilter classes hardwire every uniform
   write and the filter Graphics.Blit to one protected static Material
   (mtShader) that is ALSO written every display frame by Hullcam's own
   MovieTimeFilter on the flight camera and by every other kerbcast camera's
   filter blit. Blitting through that shared material makes each camera's
   reticle and filter state depend on write/draw interleaving with all the
   other writers (seen as reticle flicker on ColorHiResTV, which never
   rewrites _TitleTex itself). Instead, each camera blits through its own
   Material on the same MovieTime shader: for the duration of Run() the
   static field is pointed at the private material, so the filter class's
   full per-blit uniform set AND the Graphics.Blit land on state only this
   camera ever writes; the finally restores the real static immediately, so
   Hullcam's own in-game rendering (OnRenderImage, later in the frame) is
   untouched. If reflection or the shared material is unavailable, Run falls
   back to the legacy shared-static path rather than dropping the filter.

   Orientation: the MovieTime shader has no UNITY_UV_STARTS_AT_TOP handling
   (Hullcam only ever ran it inside OnRenderImage, where Unity compensates
   the Direct3D-style top-left UV origin itself). Driven by a manual
   Graphics.Blit chain that compensation is absent, and on top-left-origin
   APIs (D3D11, Metal) the redirected pass can come out vertically inverted
   while bottom-left-origin GL is correct (the v0.19.0 Windows regression).
   Rather than hardcoding which APIs or filter classes invert, Run measures
   it once per camera: on SystemInfo.graphicsUVStartsAtTop platforms only,
   the first Run pushes a vertically asymmetric probe frame through the
   camera's own redirected pass and reads back which half landed on top. If
   the pass inverted, every subsequent Run appends one compensating vertical
   flip to the destination. On bottom-left-origin platforms (the tier-1
   Deck) the gate is false, the probe never executes and the chain is
   byte-identical to the unprobed path; the headless determinism harness
   pins that on glcore. */

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class HullcamFilterBlit
    {
        /* Reflection handles for each holder type's protected static
           mtShader field, resolved once per type. A null VALUE marks
           "tried and missing" (assembly shape changed): every Run on that
           type then keeps the legacy shared-static behaviour, with the
           warning logged once at resolution time. */
        private static readonly Dictionary<Type, FieldInfo> s_mtShaderFields =
            new Dictionary<Type, FieldInfo>();

        private readonly Type _filterType;
        private Material _material;
        /* Orientation state, resolved by the first redirected Run. True
           once the probe has run (or been skipped by the UV-origin gate);
           _compensateFlip then says whether this camera's pass needs the
           corrective vertical flip. Per camera, not static: the probe runs
           the camera's own filter class. */
        private bool _orientationKnown;
        private bool _compensateFlip;

        /// <param name="filterType">The type holding the static filter
        /// state: HullcamVDS.CameraFilter in the plugin, a same-shaped fake
        /// in the determinism test.</param>
        public HullcamFilterBlit(Type filterType)
        {
            _filterType = filterType;
        }

        /// <summary>This camera's private material, null until the first
        /// successful Run builds it from the shared mtShader's Shader.</summary>
        public Material Material => _material;

        /// <summary>
        /// Execute one filter pass (the caller's RenderTitlePage plus
        /// RenderImageWithFilter calls, parameterised by source and
        /// destination) with the holder type's mtShader static redirected
        /// to this camera's private material, restoring the shared material
        /// in a finally. On top-left-UV-origin graphics APIs the first call
        /// probes the pass's vertical orientation and, when the pass
        /// inverts, every call appends a compensating flip to dst. Falls
        /// back to running the pass against the untouched shared static
        /// when the field, the shared material, or the private material is
        /// unavailable.
        /// </summary>
        public void Run(Action<RenderTexture, RenderTexture> renderPass,
            RenderTexture src, RenderTexture dst)
        {
            var mtField = MtShaderField();
            var sharedMt = mtField != null ? mtField.GetValue(null) as Material : null;
            if (_material == null && sharedMt != null)
                _material = BuildMaterial(sharedMt);
            if (mtField != null && sharedMt != null && _material != null)
            {
                mtField.SetValue(null, _material);
                try
                {
                    if (!_orientationKnown)
                    {
                        /* Gate on actual platform semantics, never on
                           Application.platform: bottom-left-origin GL
                           (the Deck) short-circuits and never probes,
                           keeping this path identical to the unprobed
                           one. Probed inside the redirect window so the
                           measurement exercises exactly the pass the
                           camera will run. */
                        _compensateFlip = SystemInfo.graphicsUVStartsAtTop
                            && MeasurePassInverted(renderPass);
                        _orientationKnown = true;
                        if (_compensateFlip)
                            Debug.Log(
                                "[Kerbcast] filter blit measured vertically inverted on this graphics API; compensating");
                    }
                    renderPass(src, dst);
                    if (_compensateFlip)
                        FlipVertical(dst);
                }
                finally
                {
                    mtField.SetValue(null, sharedMt);
                }
            }
            else
            {
                renderPass(src, dst);
            }
        }

        /* One-time orientation probe: a white-top/black-bottom frame goes
           through the camera's own redirected pass; whichever half is
           brighter in the output says whether the pass kept or inverted
           vertical orientation. The white/black ordering survives every
           MovieTime transform the filter classes apply (monochrome dot,
           positive contrast scale, brightness add, non-negative vignette
           and overlay multiplies), and the title overlay cannot contaminate
           the read: suppressing classes rewrite _TitleTex to a transparent
           texture, and the dockingdisplay crosshair's alpha hugs the centre
           axes while the probe compares the outer row quarters. Ambiguous
           or failed reads default to upright (no compensation, the pre-fix
           behaviour) with a warning. Internal so the determinism harness
           can assert it reports upright on glcore. */
        internal static bool MeasurePassInverted(
            Action<RenderTexture, RenderTexture> renderPass)
        {
            const int size = 8;
            RenderTexture probeSrc = null, probeDst = null;
            Texture2D pattern = null, result = null;
            var prevActive = RenderTexture.active;
            try
            {
                /* Texture2D pixel rows run bottom-up: indices y >= size/2
                   are the TOP half of the image. */
                var px = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        px[y * size + x] = y >= size / 2
                            ? new Color32(255, 255, 255, 255)
                            : new Color32(0, 0, 0, 255);
                pattern = new Texture2D(size, size, TextureFormat.RGBA32, false);
                pattern.SetPixels32(px);
                pattern.Apply(false, false);

                probeSrc = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
                probeDst = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
                // Default-material blit: the one upload path Unity keeps
                // orientation-correct on every API.
                Graphics.Blit(pattern, probeSrc);
                renderPass(probeSrc, probeDst);

                result = new Texture2D(size, size, TextureFormat.RGBA32, false);
                RenderTexture.active = probeDst;
                result.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                result.Apply();
                var outPx = result.GetPixels32();

                float top = 0f, bottom = 0f;
                int quarter = size / 4;
                for (int y = 0; y < quarter; y++)
                    for (int x = 0; x < size; x++)
                    {
                        Color32 b = outPx[y * size + x];
                        Color32 t = outPx[(size - 1 - y) * size + x];
                        bottom += b.r + b.g + b.b;
                        top += t.r + t.g + t.b;
                    }
                // ~8/255 per channel per texel of separation required.
                float margin = 8f * 3f * quarter * size;
                if (Mathf.Abs(top - bottom) < margin)
                {
                    Debug.LogWarning(
                        "[Kerbcast] filter orientation probe inconclusive; assuming upright");
                    return false;
                }
                return bottom > top;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[Kerbcast] filter orientation probe failed ({ex.Message}); assuming upright");
                return false;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (probeSrc != null) RenderTexture.ReleaseTemporary(probeSrc);
                if (probeDst != null) RenderTexture.ReleaseTemporary(probeDst);
                if (pattern != null) UnityEngine.Object.DestroyImmediate(pattern);
                if (result != null) UnityEngine.Object.DestroyImmediate(result);
            }
        }

        /* In-place vertical mirror of rt via a temporary, the same
           scale/offset Blit technique as KerbcastCamera's horizontal-flip
           correction (default-material scale/offset blits are
           orientation-neutral on both UV conventions). */
        private static void FlipVertical(RenderTexture rt)
        {
            var tmp = RenderTexture.GetTemporary(rt.descriptor);
            Graphics.Blit(rt, tmp, new Vector2(1f, -1f), new Vector2(0f, 1f));
            Graphics.Blit(tmp, rt);
            RenderTexture.ReleaseTemporary(tmp);
        }

        /// <summary>Destroy the private material (camera teardown).</summary>
        public void DestroyMaterial()
        {
            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
                _material = null;
            }
        }

        // The holder type's protected static mtShader field, resolved once
        // per type (shared across cameras via the static cache).
        private FieldInfo MtShaderField()
        {
            FieldInfo field;
            if (!s_mtShaderFields.TryGetValue(_filterType, out field))
            {
                try
                {
                    field = _filterType.GetField(
                        "mtShader", BindingFlags.NonPublic | BindingFlags.Static);
                }
                catch (Exception)
                {
                    field = null;
                }
                s_mtShaderFields[_filterType] = field;
                if (field == null)
                    Debug.LogWarning(
                        $"[Kerbcast] {_filterType.Name}.mtShader field not found; filter blits will use the shared material");
            }
            return field;
        }

        /* Build the private material from the shared mtShader's Shader. A
           fresh material starts from the shader's property-block defaults
           (every texture slot "white", all floats at their declared
           defaults), so nothing stale is copied in. The three overlay
           texture slots are then seeded for the one filter class that never
           writes them itself (CameraFilterNightVision's mtShader fallback
           inherits _VignetteTex/_Overlay1Tex/_Overlay2Tex upstream, a
           pre-existing Hullcam bug); every other class overwrites all three
           on every blit, so the seed is inert there. */
        private Material BuildMaterial(Material sharedMtShader)
        {
            var mat = new Material(sharedMtShader.shader);
            var vignette = ReadStaticTexture("filmVignette");
            var nvMesh = ReadStaticTexture("nvMesh");
            var noise = ReadStaticTexture("noise");
            if (vignette != null) mat.SetTexture("_VignetteTex", vignette);
            if (nvMesh != null) mat.SetTexture("_Overlay1Tex", nvMesh);
            if (noise != null) mat.SetTexture("_Overlay2Tex", noise);
            return mat;
        }

        // One of the holder type's protected static Texture2D fields
        // (filmVignette, nvMesh, noise, ...), or null if unavailable.
        private Texture2D ReadStaticTexture(string fieldName)
        {
            try
            {
                var f = _filterType.GetField(
                    fieldName, BindingFlags.NonPublic | BindingFlags.Static);
                return f != null ? f.GetValue(null) as Texture2D : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
