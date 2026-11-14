using UnityEngine;

namespace Kerbcast
{
    /*
     * The scaled planet shader lights its surface from the Sun but adds two
     * terms that ignore it: a rim colour that grows as the surface turns edge
     * on, and the environment's reflection, which grows the same way. Seen
     * from orbit they only touch the limb. A lens standing on that ground sees
     * every part of it edge on, so both run at full strength and the night
     * side looks like a daylit sea under a blue sky. For one scaled render
     * this scales the body material's rim colour and the scene's reflection
     * intensity by the daylight at the lens, then puts both back. The drawn
     * ground under the lens shares that material, so it is dimmed with the
     * mesh.
     */
    internal sealed class ScaledGroundDaylight
    {
        private static readonly int RimColorId = Shader.PropertyToID("_RimColor");

        private Material _material;
        private Color _rim;
        private bool _reflectionSet;
        private float _reflection;
        private string _logged;

        public void Begin(Renderer mesh, double daylight, double sunElevationDeg, string tag)
        {
            End();
            float k = Mathf.Clamp01((float)daylight);
            var material = mesh != null ? mesh.sharedMaterial : null;
            bool rim = material != null && material.HasProperty(RimColorId);
            Log(tag, k, sunElevationDeg, rim);
            if (k >= 1f) return;
            if (rim)
            {
                _material = material;
                _rim = material.GetColor(RimColorId);
                material.SetColor(RimColorId, new Color(_rim.r * k, _rim.g * k, _rim.b * k, _rim.a));
            }
            _reflection = RenderSettings.reflectionIntensity;
            _reflectionSet = true;
            RenderSettings.reflectionIntensity = _reflection * k;
        }

        public void End()
        {
            try
            {
                if (_material != null) _material.SetColor(RimColorId, _rim);
            }
            finally
            {
                _material = null;
                if (_reflectionSet) RenderSettings.reflectionIntensity = _reflection;
                _reflectionSet = false;
            }
        }

        /* One line when the daylight at the lens moves by a tenth, for checking a capture against how its ground was dimmed. */
        private void Log(string tag, float k, double sunElevationDeg, bool rim)
        {
            string state = $"daylight={Mathf.Round(k * 10f) / 10f:F1} rim={(rim ? "found" : "none")}";
            if (state == _logged) return;
            _logged = state;
            Debug.Log($"{tag} event=station-ground-daylight {state} sunElev={sunElevationDeg:F1} "
                + $"reflectionIntensity={RenderSettings.reflectionIntensity:F2} defaultReflection={RenderSettings.defaultReflectionMode}");
        }
    }
}
