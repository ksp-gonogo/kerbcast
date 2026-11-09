using UnityEngine;

namespace Kerbcast
{
    /*
     * The stars as a lens away from the main camera sees them. KSP fades the
     * galaxy cube once a frame for the main camera alone: its air, its Sun
     * and its view. A command camera at KSC with the active craft at Duna
     * would show stars through a daytime sky. This works out the fade stock
     * would give at the lens instead.
     */
    internal static class GalaxyForLens
    {
        /* Nearer the main camera than this, the lens keeps KSP's own fade, so a camera beside the active craft looks as it always has. */
        public const double OwnFadeBeyondM = 10000.0;

        private const double KpaToAtm = 0.009869232667160128;

        /* The cube's tint for a lens at world position `eye` on `body`, looking along `forward` through `fovDeg`; null leaves KSP's tint. */
        public static Color? Tint(CelestialBody body, DVec3 eye, DVec3 forward, double fovDeg, out double fade)
        {
            fade = double.NaN;
            var main = HighLogic.LoadedSceneIsFlight && FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (main == null || body == null || sun == null) return null;
            var m = main.transform.position;
            if ((new DVec3(m.x, m.y, m.z) - eye).Magnitude < OwnFadeBeyondM) return null;
            if (!GalaxySky.Limits(out float atmos, out float daytime, out float airPressure, out float glare)) return null;

            var centre = body.position;
            var fromCentre = eye - new DVec3(centre.x, centre.y, centre.z);
            var up = fromCentre.Normalized;
            var s = sun.position;
            var toSun = new DVec3(s.x, s.y, s.z) - eye;
            double zenith = CommandCameraMath.AngleBetween(up, toSun) * (180.0 / System.Math.PI);
            double offAxis = CommandCameraMath.AngleBetween(forward, toSun) * (180.0 / System.Math.PI);
            double altitude = fromCentre.Magnitude - body.Radius;
            double pressure = body.atmosphere && altitude < body.atmosphereDepth
                ? FlightGlobals.getStaticPressure(altitude, body) * KpaToAtm : 0.0;
            fade = CommandCameraMath.GalaxyFade(pressure, zenith, zenith < 90.0, offAxis, fovDeg, atmos, daytime, airPressure, glare);
            return GalaxySky.TintFor((float)fade);
        }
    }
}
