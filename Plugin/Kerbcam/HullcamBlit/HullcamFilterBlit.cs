/* Per-camera Hullcam filter blit mechanism, factored out of KerbcamCamera
   (c00fe16) so the headless determinism test in ci/kerbcam-shaders compiles
   the SAME source file via the Assets/Editor symlink pattern (like Fx/Core).
   KSP-free and Hullcam-free by construction: UnityEngine plus reflection
   only. The Hullcam CameraFilter type arrives as a System.Type, so the test
   can substitute its own static-holder type with the same field shape (the
   shape itself is pinned against the real DLL by HullcamContract.Tests).

   Why this exists: Hullcam's CameraFilter classes hardwire every uniform
   write and the filter Graphics.Blit to one protected static Material
   (mtShader) that is ALSO written every display frame by Hullcam's own
   MovieTimeFilter on the flight camera and by every other kerbcam camera's
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
   back to the legacy shared-static path rather than dropping the filter. */

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Kerbcam
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
        /// RenderImageWithFilter calls) with the holder type's mtShader
        /// static redirected to this camera's private material, restoring
        /// the shared material in a finally. Falls back to running the pass
        /// against the untouched shared static when the field, the shared
        /// material, or the private material is unavailable.
        /// </summary>
        public void Run(Action renderPass)
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
                    renderPass();
                }
                finally
                {
                    mtField.SetValue(null, sharedMt);
                }
            }
            else
            {
                renderPass();
            }
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
                        $"[Kerbcam] {_filterType.Name}.mtShader field not found; filter blits will use the shared material");
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
