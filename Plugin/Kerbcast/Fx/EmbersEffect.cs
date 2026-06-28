// Embers atmospheric-FX layer: sparks/embers shedding off the heated
// vessel surfaces into the wake at heavy reentry.
//
// Architecture: same windward-extrusion pattern as CoreSheathEffect.
// A CommandBuffer at AfterForwardAlpha re-draws the vessel's own part
// renderers with the KerbcastEmber material; the shader's geom stage
// filters windward triangles and emits a small camera-aligned spark
// quad per triangle, positioned at a random point along the airflow
// extrusion. Sparks shed from the ACTUAL heated surface (heat shield,
// nose cone) rather than from an abstract emission region.
//
// Activation gate is unchanged (heavy reentry only): min(machRamp, qRamp)
// over mach 2.5–5.0 and q 0.5–5.0 kPa. The CB attaches/detaches via the
// usual cheap-no-op pattern when intensity is zero.

using UnityEngine;
using UnityEngine.Rendering;

namespace Kerbcast
{
    internal sealed class EmbersEffect : IAtmoFxEffect
    {
        public AtmoFxLayers Layer => AtmoFxLayers.Embers;

        private Camera _cam;
        private Material _material;
        private CommandBuffer _cb;
        private Vessel _vessel;
        private bool _attached;
        private bool _cbDirty = true;

        private static readonly int _IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int _WindDirId   = Shader.PropertyToID("_WindDirWorld");
        private static readonly int _FxStateId   = Shader.PropertyToID("_FxState");

        // Intensity ramps. Heavy-reentry only — below mach 2.5 / q 0.5 kPa
        // there should be no embers at all. By mach 5 / q 5 kPa we're at full.
        // Combined as min(machRamp, qRamp) so both conditions must hold.
        private const float _machLow = 2.5f;
        private const float _machHigh = 5.0f;
        private const float _qLow = 0.5f;     // kPa
        private const float _qHigh = 5.0f;    // kPa
        // FxState bracket — same as CoreSheath, drives the cool→hot colour
        // blend in the shader (white-yellow→orange-red sparks).
        private const float _fxStateMachStart = 2.0f;
        private const float _fxStateMachFull  = 7.0f;

        private const float _forcedIntensity = 0.6f;

        // Diagnostics throttle.
        private int _drawCount;
        private float _lastLogTime;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;
            _material = KerbcastFxAssets.LoadMaterial("KerbcastEmber");
            if (_material == null) return false;
            _cb = new CommandBuffer { name = "Kerbcast FX Embers" };
            Debug.Log($"[Kerbcast] FX embers initialized on {nearCam.name} (geom-shader path)");
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

            float intensity = KerbcastSettings.ForceAtmosphericFx
                ? _forcedIntensity
                : ComputeIntensity(state.Mach, state.DynamicPressure);
            float fxState = KerbcastSettings.ForceAtmosphericFx
                ? 0.5f
                : Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01(Mathf.InverseLerp(_fxStateMachStart, _fxStateMachFull, state.Mach)));

            if (KerbcastSettings.DebugCameraLogging && Time.time - _lastLogTime > 1.5f)
            {
                _lastLogTime = Time.time;
                var v = state.Vessel;
                Debug.Log($"[Kerbcast-debug] FX embers {_cam.name}: " +
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

            _material.SetFloat(_IntensityId, intensity);
            _material.SetFloat(_FxStateId, fxState);
            _material.SetVector(_WindDirId, state.VelocityWorld.sqrMagnitude > 1e-4f
                ? state.VelocityWorld.normalized
                : (state.Vessel != null ? (Vector3)state.Vessel.transform.up : Vector3.up));

            Attach();
        }

        // Heavy-reentry gate. Both axes must contribute — combining with
        // min() means embers won't appear at high mach in thin air or low
        // mach in thick air. Each axis is a clamped InverseLerp.
        private static float ComputeIntensity(float mach, float dynamicPressure)
        {
            float machRamp = Mathf.Clamp01(Mathf.InverseLerp(_machLow, _machHigh, mach));
            float qRamp = Mathf.Clamp01(Mathf.InverseLerp(_qLow, _qHigh, dynamicPressure));
            return Mathf.Min(machRamp, qRamp);
        }

        // Rebuild the part-renderer draw list. Same filtering rules as
        // CoreSheath — ModuleDeployablePart skipped (solar panels / antennas
        // are thin and emit degenerate-edge sparks), MeshRenderer /
        // SkinnedMeshRenderer only.
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
