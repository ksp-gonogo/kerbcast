/* When a camera can clear its galaxy layer instead of rendering it.

   KSP fades the galaxy cube to black by day in an atmosphere, by tinting every
   face through a property block: on the pad the fade reaches 1.5 and the tint
   is exactly black. A cube drawn in black over a black clear leaves the clear,
   so clearing the layer gives the same frame without a whole camera render.
   Anything that could put a lit pixel there keeps the render: a tint above
   zero, a clear that is not plain black, or anything else on the galaxy layer.

   Depends only on System so a standalone test project compiles it. */

namespace Kerbcast
{
    internal static class GalaxyBlackout
    {
        /// <summary>Whether clearing the galaxy layer to its background gives the
        /// frame a render would. <paramref name="solidClear"/> and the background
        /// describe the galaxy camera's own clear; <paramref name="tinted"/> says
        /// the cube's tint was read this frame; <paramref name="cubeAloneOnLayer"/>
        /// says nothing but the cube draws on the camera's layers.</summary>
        public static bool CanClear(
            bool solidClear, float bgR, float bgG, float bgB,
            bool cubeAloneOnLayer, bool tinted, float tintR, float tintG, float tintB)
        {
            if (!solidClear || !cubeAloneOnLayer || !tinted) return false;
            return IsBlack(bgR, bgG, bgB) && IsBlack(tintR, tintG, tintB);
        }

        /* Exact zero, not "dark": a faint tint over a bright texture still rounds up to a visible star. NaN reads as not black. */
        private static bool IsBlack(float r, float g, float b) => r == 0f && g == 0f && b == 0f;
    }
}
