using System;

namespace Kerbcast
{
    /*
     * Stock's aerial perspective at a viewer, as PQSMod_AerialPerspectiveMaterial
     * computes it each frame from the main camera's altitude over the active
     * sphere and hands it to the terrain and ocean shaders as two globals.
     * Those shaders fog a fragment by its depth from whichever camera draws
     * it, scaled by the air's density at the viewer, so a camera high above
     * the main one needs the values for its own altitude: with the main
     * camera's, the sea it looks down on fogs solid with air as thick as at
     * the main camera.
     */
    internal static class AerialFog
    {
        /*
         * The viewer's altitude in atmosphere depths (`_cameraAltitude`) and
         * the air's density there (`_heightDensityAtViewer`) for a viewer
         * `distanceM` from the centre of a sphere of `radiusM`, with that
         * sphere's `atmosphereDepth` and `heightFalloff`, in stock's own
         * float steps. False, leaving stock's values alone, when either is
         * not a finite number.
         */
        public static bool TryAt(double distanceM, double radiusM, float atmosphereDepth, float heightFalloff, out float altitude, out float density)
        {
            altitude = (float)((distanceM - radiusM) / atmosphereDepth);
            density = (float)Math.Exp((0f - heightFalloff) * altitude);
            return !float.IsNaN(altitude) && !float.IsInfinity(altitude) && !float.IsNaN(density) && !float.IsInfinity(density);
        }
    }
}
