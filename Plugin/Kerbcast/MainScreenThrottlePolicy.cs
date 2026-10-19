using System;

namespace Kerbcast
{
    /* How a KSP camera is made nearly free while "throttle main render" is on.

       Local cameras (Camera 00, Camera 01) draw thousands of part and terrain
       objects, so their cull mask goes to 0. Scaled and galaxy cameras draw a
       handful of bodies and their mask is left alone entirely: KSP's MapView
       toggles layer 31 on the scaled camera relative to its live mask, and
       Scatterer's reflection probe fixer reads the galaxy camera's mask, so
       zeroing or restoring either would leak into state that outlives the
       throttle. */
    public enum ThrottledCameraRole
    {
        Local,
        Scaled,
    }

    /* Unity-free decisions for the main-screen throttle, kept apart so they are
       unit-testable. The throttle keeps KSP's cameras enabled, because Scatterer
       and EVE update their sky, haze and cloud state from render callbacks on
       those cameras, and instead shrinks their viewport to a few pixels. */
    public static class MainScreenThrottlePolicy
    {
        /* Viewport edge in pixels. Large enough that no image effect downsample
           chain (bloom, SMAA, TAA) reaches a zero-sized target, small enough that
           fill cost is noise. */
        public const int CheapViewportPixels = 8;

        private const float RectEpsilon = 1e-6f;

        public static bool ManagesCullingMask(ThrottledCameraRole role) => role == ThrottledCameraRole.Local;

        /* Normalized viewport width and height covering CheapViewportPixels at the
           given screen size, anchored bottom-left. Never zero (a zero-area camera
           skips its callbacks entirely) and never above the full screen. */
        public static void CheapViewport(int screenWidth, int screenHeight, out float width, out float height)
        {
            width = Fraction(screenWidth);
            height = Fraction(screenHeight);
        }

        private static float Fraction(int screenPixels)
        {
            if (screenPixels <= CheapViewportPixels) return 1f;
            return (float)CheapViewportPixels / screenPixels;
        }

        /* True when KSP or a mod has written the camera's viewport, or the mask
           of a camera whose mask the throttle owns, since the throttle applied. */
        public static bool NeedsReapply(
            bool managesMask, int currentMask,
            float currentX, float currentY, float currentWidth, float currentHeight,
            float cheapWidth, float cheapHeight)
        {
            if (managesMask && currentMask != 0) return true;
            return !Near(currentX, 0f) || !Near(currentY, 0f)
                || !Near(currentWidth, cheapWidth) || !Near(currentHeight, cheapHeight);
        }

        private static bool Near(float a, float b) => Math.Abs(a - b) <= RectEpsilon;
    }
}
