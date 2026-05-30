// Core atmospheric-FX layer: the windward plasma sheath + trailing plasma
// wings. Owns a CommandBuffer attached to the near camera at
// CameraEvent.AfterForwardAlpha that re-draws the vessel's own part renderers
// with our additive plasma material. The plasma shader runs a geometry-shader
// pass on those triangles, extruding each windward-facing triangle backward
// along the airflow direction to form trailing wings of plasma past the
// vessel silhouette — the structural look that vertex-displacement on a sealed
// proxy shell can't produce.
//
// Running inside the near render means the result composites against that
// render's depth — vessel parts in front correctly occlude the trail.
//
// Per-frame the effect writes two scalars to the material:
//   _Intensity   magnitude of the effect (0..1, ramped from mach/q like before)
//   _FxState     character of the effect (0..1, mach-blend between
//                Condensation and ReentryHeat presets). Separate from
//                intensity: a low-mach, high-q regime is intense-but-cool
//                (vapour); a high-mach regime is intense-and-hot (plasma).
//                Approximates KSP's stock state =
//                  Mathf.InverseLerp(AeroFXStartThermalFX,
//                                    AeroFXFullThermalFX, mach)
//                — exact PhysicsGlobals values aren't dumpable cheaply, so we
//                use mach 2 → mach 6 as a reasonable starting bracket.

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

        // Shader property IDs.
        private static readonly int _IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int _WindDirId = Shader.PropertyToID("_WindDirWorld");
        private static readonly int _FxStateId = Shader.PropertyToID("_FxState");

        // Intensity used by ForceAtmosphericFx — moderate, flight-like.
        private const float _forcedIntensity = 0.6f;
        // Intensity-gating thresholds (C#-side, fast to tune).
        private const float _minQ = 0.1f;       // kPa
        private const float _machLow = 0.8f;
        private const float _machHigh = 5.0f;
        // Mach bracket for the Condensation → ReentryHeat preset blend.
        // Approximates PhysicsGlobals.AeroFXStartThermalFX /
        // AeroFXFullThermalFX (not dumpable cheaply).
        private const float _fxStateMachStart = 2.0f;
        private const float _fxStateMachFull = 6.0f;

        // Diagnostics — throttle so the per-frame log doesn't spam.
        private int _drawCount;
        private float _lastLogTime;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;
            _material = KerbcamFxAssets.LoadMaterial("KerbcamPlasma");
            if (_material == null) return false;
            _cb = new CommandBuffer { name = "Kerbcam FX Core" };
            Debug.Log($"[Kerbcam] FX core initialized on {nearCam.name} (KerbcamPlasma material loaded; geometry-shader extrusion path)");
            return true;
        }

        public void OnVesselChanged(Vessel vessel)
        {
            _vessel = vessel;
            _cbDirty = true;
        }

        public void Render(in FxFrameState state)
        {
            if (_material == null || _cam == null || _cb == null) return;

            float intensity = KerbcamSettings.ForceAtmosphericFx
                ? _forcedIntensity
                : ComputeIntensity(state.Mach, state.DynamicPressure);
            float fxState = KerbcamSettings.ForceAtmosphericFx
                ? 0.5f
                : Mathf.Clamp01(Mathf.InverseLerp(_fxStateMachStart, _fxStateMachFull, state.Mach));

            if (KerbcamSettings.DebugCameraLogging && Time.time - _lastLogTime > 1.5f)
            {
                _lastLogTime = Time.time;
                var v = state.Vessel;
                Debug.Log($"[Kerbcam-debug] FX core {_cam.name}: " +
                    $"vessel={(v != null ? v.vesselName : "null")} " +
                    $"srfSpd={state.VelocityWorld.magnitude:F0} mach={state.Mach:F2} " +
                    $"q={state.DynamicPressure:F2} alt={(v != null ? v.altitude : 0):F0} " +
                    $"sit={(v != null ? v.situation.ToString() : "?")} " +
                    $"intensity={intensity:F2} fxState={fxState:F2} draws={_drawCount} " +
                    $"attached={_attached} path={_cam.actualRenderingPath}");
            }

            if (intensity <= 0.001f || state.Vessel == null)
            {
                Detach();
                return;
            }

            if (_cbDirty) RebuildCommandBuffer(state.Vessel);

            // Material uniforms: intensity drives brightness; fxState drives
            // the Condensation→ReentryHeat preset blend (colour, wobble amp,
            // scroll speed, wing length); wind dir is the C# fallback the
            // shader uses when _LightDirection0 is degenerate (pad).
            //
            // _WindDirWorld carries the VESSEL VELOCITY direction (i.e. the
            // direction the vessel is moving). The shader inverts it to get
            // the airflow direction (= trail extrusion direction). This
            // matches how the rest of the FX layers read state.VelocityWorld.
            _material.SetFloat(_IntensityId, intensity);
            _material.SetFloat(_FxStateId, fxState);
            _material.SetVector(_WindDirId, state.VelocityWorld.sqrMagnitude > 1e-4f
                ? state.VelocityWorld.normalized
                : (state.Vessel != null ? (Vector3)state.Vessel.transform.up : Vector3.up));

            Attach();
        }

        private static float ComputeIntensity(float mach, float dynamicPressure)
        {
            if (dynamicPressure < _minQ) return 0f;
            return Mathf.Clamp01(Mathf.InverseLerp(_machLow, _machHigh, mach));
        }

        // Rebuild the part-renderer draw list. ModuleDeployablePart is skipped
        // because solar panels / antennas / radiators have thin flat geometry
        // that extrudes into long ugly spikes when run through the
        // geometry-shader pass. Mesh / SkinnedMesh renderers only — line /
        // particle / trail / billboard renderers produce stray-spike artifacts.
        private void RebuildCommandBuffer(Vessel vessel)
        {
            _cb.Clear();
            _cbDirty = false;
            _drawCount = 0;
            if (vessel == null) return;

            foreach (var part in vessel.parts)
            {
                if (part == null) continue;
                if (part.FindModuleImplementing<ModuleDeployablePart>() != null) continue;

                var renderers = part.GetComponentsInChildren<Renderer>(includeInactive: false);
                for (int r = 0; r < renderers.Length; r++)
                {
                    var rend = renderers[r];
                    if (rend == null || !rend.enabled || !rend.gameObject.activeInHierarchy) continue;
                    if (!(rend is MeshRenderer || rend is SkinnedMeshRenderer)) continue;
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
