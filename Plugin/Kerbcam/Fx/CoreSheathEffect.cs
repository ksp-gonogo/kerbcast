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
        private static readonly int _PuffId = Shader.PropertyToID("_PuffDistance");

        // Max outward inflation (metres) at full intensity — tuned C#-side so
        // the puff amount can be adjusted without a CI shader rebuild.
        private const float _puffMeters = 0.18f;
        // Intensity used by ForceAtmosphericFx — a moderate, flight-like value
        // (not max) so the forced pad preview reads like real supersonic flight.
        private const float _forcedIntensity = 0.6f;

        // Diagnostics (gated on DebugCameraLogging): how many DrawRenderer calls
        // the CB holds + a throttle so the per-frame state log doesn't spam.
        private int _drawCount;
        private float _lastLogTime;

        // Intensity ramp (C#-side, fast loop — no CI rebuild to tune). Below
        // _minQ there's no meaningful atmosphere; intensity ramps with mach
        // from transonic up. Tuned low so the indicator shows across ordinary
        // ascent, not just extreme reentry.
        private const float _minQ = 0.1f;       // kPa
        private const float _machLow = 0.8f;
        private const float _machHigh = 5.0f;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;
            _material = KerbcamFxAssets.LoadMaterial("KerbcamPlasma");
            if (_material == null) return false; // bundle/shader missing → unavailable
            _cb = new CommandBuffer { name = "Kerbcam FX Core" };
            Debug.Log($"[Kerbcam] FX core initialized on {nearCam.name} (KerbcamPlasma material loaded)");
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

            float intensity = KerbcamSettings.ForceAtmosphericFx
                ? _forcedIntensity
                : ComputeIntensity(state.Mach, state.DynamicPressure);

            // Throttled state readout — logged even when intensity is 0, so a
            // missing effect can be diagnosed: flight regime (mach/q), whether
            // intensity ramps, CB draw count, attach state, and the rendering
            // path (AfterForwardAlpha only fires in Forward — Deferred = no FX).
            if (KerbcamSettings.DebugCameraLogging && Time.time - _lastLogTime > 1.5f)
            {
                _lastLogTime = Time.time;
                var v = state.Vessel;
                Debug.Log($"[Kerbcam-debug] FX core {_cam.name}: " +
                    $"vessel={(v != null ? v.vesselName : "null")} " +
                    $"srfSpd={state.VelocityWorld.magnitude:F0} mach={state.Mach:F2} " +
                    $"q={state.DynamicPressure:F2} alt={(v != null ? v.altitude : 0):F0} " +
                    $"sit={(v != null ? v.situation.ToString() : "?")} " +
                    $"intensity={intensity:F2} draws={_drawCount} attached={_attached} " +
                    $"path={_cam.actualRenderingPath}");
            }

            if (intensity <= 0.001f)
            {
                Detach(); // no heating → zero GPU cost
                return;
            }

            if (_cbDirty) RebuildCommandBuffer(state.Vessel);

            _material.SetFloat(_IntensityId, intensity);
            _material.SetFloat(_PuffId, _puffMeters * intensity); // puffs out as it heats
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
            _drawCount = 0;
            if (vessel == null) return;

            foreach (var part in vessel.parts)
            {
                if (part == null) continue;
                // Skip deployable appendages (solar panels, antennas, radiators):
                // their thin/flat geometry inflates into spikes and shouldn't
                // carry a sheath.
                if (part.FindModuleImplementing<ModuleDeployablePart>() != null) continue;

                var renderers = part.GetComponentsInChildren<Renderer>(includeInactive: false);
                for (int r = 0; r < renderers.Length; r++)
                {
                    var rend = renderers[r];
                    if (rend == null || !rend.enabled || !rend.gameObject.activeInHierarchy) continue;
                    // Mesh surfaces only — line/particle/trail/billboard renderers
                    // produce the stray-spike artifacts when re-drawn inflated.
                    if (!(rend is MeshRenderer || rend is SkinnedMeshRenderer)) continue;
                    // Re-draw every submesh of the part with our plasma material,
                    // additively over the part's normal render.
                    int subMeshes = rend.sharedMaterials.Length;
                    for (int s = 0; s < subMeshes; s++)
                    {
                        _cb.DrawRenderer(rend, _material, s);
                        _drawCount++;
                    }
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
