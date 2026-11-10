using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * A camera's aerial perspective for its local layers: the two globals
     * stock's PQSMod_AerialPerspectiveMaterial sets from the main camera's
     * altitude, set instead from this camera's eye (AerialFog) while its
     * layers render, then put back as stock had them. Only a value stock
     * itself wrote is replaced: when the globals hold anything else, another
     * mod owns them and they are left alone.
     */
    internal sealed class LensFog
    {
        private const string Tag = "[Kerbcast][PICTURE]";
        private static readonly int AltitudeId = Shader.PropertyToID("_cameraAltitude");
        private static readonly int DensityId = Shader.PropertyToID("_heightDensityAtViewer");
        private static readonly Dictionary<PQS, PQSMod_AerialPerspectiveMaterial[]> s_mods = new Dictionary<PQS, PQSMod_AerialPerspectiveMaterial[]>();

        private bool _set;
        private float _stockAltitude;
        private float _stockDensity;
        /* The body whose air the last frame gave the lens, or -1 for stock's values kept, for the log. */
        private int _logged = -2;

        public void Apply(Vector3 eye, string view)
        {
            _set = false;
            var body = FlightGlobals.currentMainBody;
            var pqs = body != null ? body.pqsController : null;
            float density = Shader.GetGlobalFloat(DensityId);
            var mod = pqs != null && pqs.isActive ? Writer(pqs, density) : null;
            var sphere = mod != null ? mod.sphere : null;
            if (sphere == null
                || !AerialFog.TryAt(((Vector3d)eye - (Vector3d)sphere.transform.position).magnitude, sphere.radius,
                    mod.atmosphereDepth, mod.heightFalloff, out var altitude, out var lensDensity))
            {
                Note(-1, view, 0f, density);
                return;
            }
            _stockAltitude = Shader.GetGlobalFloat(AltitudeId);
            _stockDensity = density;
            Shader.SetGlobalFloat(AltitudeId, altitude);
            Shader.SetGlobalFloat(DensityId, lensDensity);
            _set = true;
            Note(body.flightGlobalsIndex, view, altitude, lensDensity);
        }

        public void Restore()
        {
            if (!_set) return;
            _set = false;
            Shader.SetGlobalFloat(AltitudeId, _stockAltitude);
            Shader.SetGlobalFloat(DensityId, _stockDensity);
        }

        /*
         * The aerial perspective mod on `pqs` or one of its child spheres
         * (the ocean) whose last write is the `density` the global holds,
         * so the lens's values come from the same sphere's air.
         */
        private static PQSMod_AerialPerspectiveMaterial Writer(PQS pqs, float density)
        {
            if (!s_mods.TryGetValue(pqs, out var mods) || Destroyed(mods))
                s_mods[pqs] = mods = pqs.GetComponentsInChildren<PQSMod_AerialPerspectiveMaterial>(true);
            for (int i = 0; i < mods.Length; i++)
            {
                var m = mods[i];
                if (m.modEnabled && m.sphere != null && m.sphere.isActive && m.heightDensAtViewer == density) return m;
            }
            return null;
        }

        private static bool Destroyed(PQSMod_AerialPerspectiveMaterial[] mods)
        {
            for (int i = 0; i < mods.Length; i++)
                if (mods[i] == null) return true;
            return false;
        }

        /* Logs the lens taking its own air over a body, or keeping stock's, once per change. */
        private void Note(int body, string view, float altitude, float density)
        {
            if (body == _logged) return;
            _logged = body;
            Debug.Log(body < 0
                ? $"{Tag} view={view} event=aerial-fog state=stock"
                : $"{Tag} view={view} event=aerial-fog state=lens body={FlightGlobals.Bodies[body].bodyName} altitudeDepths={altitude:F3} density={density:G4}");
        }
    }
}
