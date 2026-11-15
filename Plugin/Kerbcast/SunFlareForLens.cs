using UnityEngine;

namespace Kerbcast
{
    /*
     * The Sun's lens flare as a lens far from the active craft sees it. Stock
     * parks the flare a short way out from its own scaled camera, along the
     * Sun's direction, which works only because that camera is where the
     * flare was aimed from. A lens on the ground with the active craft in
     * orbit stands a planet's width away, so the same flare lands somewhere
     * unrelated to where the Sun is drawn and the glare that hides the disc in
     * the game's own view never reaches the feed. This puts the flare the same
     * short way out from the lens along the lens's own Sun direction for the
     * length of one scaled render, switches it on for that render, and puts it
     * back. Unity then draws stock's flare asset at stock's brightness.
     *
     * Stock switches the flare off only for a body that hides the Sun from the
     * map camera, which says nothing about a lens on the ground, and relies on
     * the flare's occlusion ray against the planet's scaled collider to hide
     * it at night. The ray starts a hair above that collider and could just as
     * well start inside it, so the lens's own planet is checked here: a Sun
     * below its horizon leaves the flare off for the render.
     */
    internal static class SunFlareForLens
    {
        private const float FallbackDistance = 20f;
        private const int LogLimit = 3;

        private static Transform s_moved;
        private static Vector3 s_was;
        private static LensFlare s_flare;
        private static bool s_enabledWas;
        private static int s_logged;

        /* Moves the Sun's flare out from `lens`, a scaled-space position, toward the Sun, ready for the render that follows. */
        public static void Begin(Vector3 lens, Vector3 forward, Vector3 lensLocal)
        {
            End();
            try
            {
                var flare = SunFlare.Instance != null ? SunFlare.Instance.sunFlare : null;
                var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
                if (flare == null || sun == null) return;
                Vector3d sunAt = ScaledSpace.LocalToScaledSpace(sun.position);
                var to = new Vector3((float)(sunAt.x - lens.x), (float)(sunAt.y - lens.y), (float)(sunAt.z - lens.z));
                if (to.sqrMagnitude < 1e-6f) return;
                var dir = to.normalized;
                var t = flare.transform;
                var was = t.position;
                float dist = ScaledCamera.Instance != null ? Mathf.Max(1f, (was - ScaledCamera.Instance.transform.position).magnitude) : FallbackDistance;
                s_moved = t;
                s_was = was;
                s_flare = flare;
                s_enabledWas = flare.enabled;
                t.position = lens + dir * dist;
                bool hidden = HiddenByOwnPlanet(lens, dir, lensLocal);
                flare.enabled = !hidden;
                if (s_logged < LogLimit)
                {
                    s_logged++;
                    Debug.Log($"[Kerbcast] event=sun-glare placed flare='{flare.name}' enabledWas={s_enabledWas} brightness={flare.brightness:G4} hiddenByPlanet={hidden} dist={dist:G4} offAxisDeg={Vector3.Angle(forward, dir):F1}");
                }
            }
            catch (System.Exception e)
            {
                End();
                if (s_logged < LogLimit)
                {
                    s_logged++;
                    Debug.Log($"[Kerbcast] event=sun-glare failed: {e.Message}");
                }
            }
        }

        /* Whether the lens's own planet stands between the lens and the Sun: the ray along `dir` from `lens` enters the planet's scaled sphere. */
        private static bool HiddenByOwnPlanet(Vector3 lens, Vector3 dir, Vector3 lensLocal)
        {
            var body = FlightGlobals.getMainBody(lensLocal);
            if (body == null || body.isStar) return false;
            Vector3d centre = ScaledSpace.LocalToScaledSpace(body.position);
            double radius = body.Radius * ScaledSpace.InverseScaleFactor;
            double ox = lens.x - centre.x, oy = lens.y - centre.y, oz = lens.z - centre.z;
            double c = ox * ox + oy * oy + oz * oz - radius * radius;
            if (c <= 0) return false;
            double b = ox * dir.x + oy * dir.y + oz * dir.z;
            return b < 0 && b * b - c > 0;
        }

        /* Puts the flare back where and as stock had it. A no-op when nothing was moved. */
        public static void End()
        {
            if (s_moved == null) return;
            try
            {
                s_moved.position = s_was;
                if (s_flare != null) s_flare.enabled = s_enabledWas;
            }
            finally
            {
                s_moved = null;
                s_flare = null;
            }
        }
    }
}
