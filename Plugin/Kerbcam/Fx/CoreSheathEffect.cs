// Core atmospheric-FX layer: the windward plasma sheath + streaks, the
// wind↔plasma continuum. Owns a CommandBuffer attached to the near camera at
// CameraEvent.AfterForwardAlpha that re-draws the vessel's own part renderers
// with our additive plasma material. Running inside the near render means it
// composites against that render's depth — correct occlusion, no second camera.
// (This is the mechanism Firefly uses and the one kerbcam's earlier CB attempt
// had right; the earlier attempt just hunted for nonexistent FX particles
// instead of re-drawing the parts.)

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kerbcam
{
    internal sealed class CoreSheathEffect : IAtmoFxEffect
    {
        public AtmoFxLayers Layer => AtmoFxLayers.Core;

        private Camera _cam;
        private Material _material;
        private CommandBuffer _cb;
        private Vessel _vessel;
        private bool _attached;
        private bool _cbDirty = true;

        private static readonly int _IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int _WindDirId = Shader.PropertyToID("_WindDirWorld");

        // Intensity ramp (C#-side, fast loop — no CI rebuild to tune). Below
        // _minQ there's no meaningful atmosphere; intensity ramps with mach
        // across the transonic→reentry band.
        private const float _minQ = 0.1f;       // kPa
        private const float _machLow = 1.5f;
        private const float _machHigh = 7.0f;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;
            _material = KerbcamFxAssets.LoadMaterial("KerbcamPlasma");
            if (_material == null) return false; // bundle/shader missing → unavailable
            _cb = new CommandBuffer { name = "Kerbcam FX Core" };
            return true;
        }

        public void OnVesselChanged(Vessel vessel)
        {
            _vessel = vessel;
            _cbDirty = true;
        }

        public void Render(in FxFrameState state)
        {
            if (_material == null || _cam == null) return;

            float intensity = ComputeIntensity(state.Mach, state.DynamicPressure);
            if (intensity <= 0.001f)
            {
                Detach(); // no heating → zero GPU cost
                return;
            }

            if (_cbDirty) RebuildCommandBuffer(state.Vessel);

            _material.SetFloat(_IntensityId, intensity);
            _material.SetVector(_WindDirId, state.VelocityWorld.sqrMagnitude > 1e-4f
                ? state.VelocityWorld.normalized
                : Vector3.up);

            Attach();
        }

        private static float ComputeIntensity(float mach, float dynamicPressure)
        {
            if (dynamicPressure < _minQ) return 0f;
            return Mathf.Clamp01(Mathf.InverseLerp(_machLow, _machHigh, mach));
        }

        private void RebuildCommandBuffer(Vessel vessel)
        {
            _cb.Clear();
            _cbDirty = false;
            if (vessel == null) return;

            foreach (var part in vessel.parts)
            {
                if (part == null) continue;
                var renderers = part.GetComponentsInChildren<Renderer>(includeInactive: false);
                for (int r = 0; r < renderers.Length; r++)
                {
                    var rend = renderers[r];
                    if (rend == null || !rend.enabled || !rend.gameObject.activeInHierarchy) continue;
                    // Re-draw every submesh of the part with our plasma material,
                    // additively over the part's normal render.
                    int subMeshes = rend.sharedMaterials.Length;
                    for (int s = 0; s < subMeshes; s++)
                        _cb.DrawRenderer(rend, _material, s);
                }
            }
        }

        private void Attach()
        {
            if (_attached || _cam == null) return;
            _cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _cb);
            _attached = true;
        }

        private void Detach()
        {
            if (!_attached || _cam == null) return;
            _cam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _cb);
            _attached = false;
        }

        public void Dispose()
        {
            Detach();
            _cb?.Release();
            _cb = null;
            if (_material != null) Object.Destroy(_material);
            _material = null;
        }
    }
}
