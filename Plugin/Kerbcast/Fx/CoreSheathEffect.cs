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

namespace Kerbcast
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
        private static readonly int _FxDepthMapId = Shader.PropertyToID("_FXDepthMap");

        // Intensity used by ForceAtmosphericFx — moderate, flight-like.
        private const float _forcedIntensity = 0.6f;
        // Intensity-gating thresholds (C#-side, fast to tune). _machLow
        // lowered to 0.3 (sub-transonic) so streaks appear as the vessel
        // first accelerates into the atmosphere proper; _activeIntensityFloor
        // keeps the low-mach output fainter than the mid-ramp.
        private const float _minQ = 0.1f;       // kPa
        private const float _machLow = 0.3f;
        private const float _machHigh = 6.0f;
        // Mach bracket for the Condensation → ReentryHeat preset blend.
        // Modest widening over the original 2.0–6.0 so the plasma colour
        // eases in across mid-ascent rather than slamming in.
        private const float _fxStateMachStart = 2.0f;
        private const float _fxStateMachFull = 7.0f;
        // Cold-condensation cap. White wind-streak output (fxState=0)
        // never exceeds this fraction of full intensity, so cold wind is
        // visible but never fully opaque. Plasma (fxState=1) has no cap —
        // at true reentry the effect is allowed to reach 1.0 (full
        // screen-fill). Linear lerp between them.
        private const float _coldIntensityCap = 0.5f;
        // Active-intensity floor. Once mach > _machLow and q > _minQ the
        // effect "activates"; raw intensity jumps to this floor immediately
        // instead of starting at zero, so the streaks are above the
        // shader's extrusion-length threshold even at sub-transonic
        // speeds. Lowered to 0.25 so the early streaks are visible but
        // very faint (the user wants wind coming in early and faint, then
        // brightening as the ramp continues).
        private const float _activeIntensityFloor = 0.25f;

        // Cold-condensation density fade (stock-equivalent). White wind
        // streaks fade out as atmDensity drops from this start → end,
        // which on Kerbin maps to roughly 5.5 km → 14 km altitude.
        // Values from PhysicsGlobals.AeroFXMachFXFadeStart / End.
        private const float _coldAtmFadeStart = 0.25f;
        private const float _coldAtmFadeEnd = 0.0875f;

        // Stock AerodynamicsFX FxScalar gate constants (Physics.cfg).
        // heatFlux below p0 → FxScalar=0 (effect off); above 6×p0 → 1.
        private const double _fxScalarP0 = 8_000_000.0;
        private const double _fxScalarRange = 5.0 * _fxScalarP0;

        // Diagnostics — throttle so the per-frame log doesn't spam.
        private int _drawCount;
        private float _lastLogTime;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;
            _material = KerbcastFxAssets.LoadMaterial("KerbcastPlasma");
            if (_material == null) return false;
            _cb = new CommandBuffer { name = "Kerbcast FX Core" };
            Debug.Log($"[Kerbcast] FX core initialized on {nearCam.name} (KerbcastPlasma material loaded; geometry-shader extrusion path)");
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

            /* KSP only publishes _FXDepthMap once its FXCamera renders
               (stock aero FX active). Until then the global is unset and the
               shader's wrap term samples garbage — the skin pass is pure
               wrap, so that read as glowing patches popping in and out
               behind the craft in cold flight. White = far plane = wrap 0;
               KSP's own SetGlobalTexture simply overwrites this later. */
            if (Shader.GetGlobalTexture(_FxDepthMapId) == null)
                Shader.SetGlobalTexture(_FxDepthMapId, Texture2D.whiteTexture);

            float intensity = KerbcastSettings.ForceAtmosphericFx
                ? _forcedIntensity
                : ComputeIntensity(state.Mach, state.DynamicPressure);
            float fxState = KerbcastSettings.ForceAtmosphericFx
                ? 0.5f
                : Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01(Mathf.InverseLerp(_fxStateMachStart, _fxStateMachFull, state.Mach)));

            // Stock-like fade-out: mirror AerodynamicsFX's FxScalar gate so
            // we fade out naturally at high altitude (heatFlux < 8e6 N/W
            // → FX off). Drives the 30 km ascent fade-out the user
            // observes in stock. See local_docs/kerbcast/stock_plasma_research.md.
            float fxScalar = ComputeStockFxScalar(state.Vessel);
            intensity *= fxScalar;

            // Stock-style condensation-only density fade: white wind streaks
            // fade out as atmDensity drops from 0.25 → 0.0875 (~5.5 km to
            // ~14 km on Kerbin), only orange plasma persists above. We bake
            // it into the cold cap so the cold-multiplier collapses to zero
            // at low density — high mach in thin air shows pure plasma.
            float coldDensityFade = state.Vessel != null
                ? Mathf.SmoothStep(_coldAtmFadeEnd, _coldAtmFadeStart, (float)state.Vessel.atmDensity)
                : 1f;

            // Cold wind streaks are capped at _coldIntensityCap × density fade;
            // plasma is uncapped (multiplier reaches 1.0 at fxState=1).
            intensity *= Mathf.Lerp(_coldIntensityCap * coldDensityFade, 1f, fxState);

            if (KerbcastSettings.DebugCameraLogging && Time.time - _lastLogTime > 1.5f)
            {
                _lastLogTime = Time.time;
                var v = state.Vessel;
                Debug.Log($"[Kerbcast-debug] FX core {_cam.name}: " +
                    $"vessel={(v != null ? v.vesselName : "null")} " +
                    $"srfSpd={state.VelocityWorld.magnitude:F0} mach={state.Mach:F2} " +
                    $"q={state.DynamicPressure:F2} alt={(v != null ? v.altitude : 0):F0} " +
                    $"atmRho={(v != null ? v.atmDensity : 0):F4} " +
                    $"fxScalar={fxScalar:F2} coldFade={coldDensityFade:F2} " +
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
            if (dynamicPressure < _minQ || mach < _machLow) return 0f;
            // SmoothStep for soft bracket edges, then Lerp from the
            // active-floor so intensity is visible from the moment the
            // effect activates rather than ramping from 0 (which would
            // leave geometry-shader extrusion length below threshold for
            // the first half of the bracket).
            float t = Mathf.SmoothStep(0f, 1f,
                Mathf.Clamp01(Mathf.InverseLerp(_machLow, _machHigh, mach)));
            return Mathf.Lerp(_activeIntensityFloor, 1f, t);
        }

        // Mirror AerodynamicsFX.LateUpdate's FxScalar gate (stock KSP).
        // Stock disables the FXCamera when this returns 0 → effect is off;
        // we use it as an intensity multiplier so our effect fades to zero
        // when stock would. Drives the natural ~30 km ascent fade-out and
        // correct reentry fade-in. Numeric values from PhysicsGlobals /
        // Physics.cfg; see local_docs/kerbcast/stock_plasma_research.md.
        private static float ComputeStockFxScalar(Vessel vessel)
        {
            if (vessel == null) return 0f;
            double rho = vessel.atmDensity;
            if (rho <= 0.0) return 0f;
            double densityFactor = rho;
            double fadeStart = PhysicsGlobals.AeroFXDensityFadeStart;
            if (fadeStart > 0.0 && densityFactor < fadeStart)
            {
                densityFactor = UtilMath.Lerp(0.0, densityFactor,
                    densityFactor * System.Math.Ceiling(1.0 / fadeStart));
            }
            densityFactor =
                System.Math.Pow(densityFactor, PhysicsGlobals.AeroFXDensityExponent1) * PhysicsGlobals.AeroFXDensityScalar1 +
                System.Math.Pow(densityFactor, PhysicsGlobals.AeroFXDensityExponent2) * PhysicsGlobals.AeroFXDensityScalar2;
            double heatFlux = 0.5 * densityFactor *
                System.Math.Pow(vessel.srfSpeed, PhysicsGlobals.AeroFXVelocityExponent);
            double scalar = (heatFlux - _fxScalarP0) / _fxScalarRange;
            return (float)System.Math.Max(0.0, System.Math.Min(1.0, scalar));
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
                    // Diagnostic for stray-flare investigation. The geom shader's
                    // side vector goes degenerate when a renderer triangle has an
                    // edge nearly parallel to airflow — long/thin renderers
                    // (engine nozzles, antennas, ladders, decoupler skirts) emit
                    // an off-to-the-side flare. Logging bounds aspect lets us
                    // correlate the offending part with the screenshot.
                    if (KerbcastSettings.DebugCameraLogging)
                    {
                        var b = rend.bounds.size;
                        float lo = Mathf.Max(0.01f, Mathf.Min(b.x, Mathf.Min(b.y, b.z)));
                        float hi = Mathf.Max(b.x, Mathf.Max(b.y, b.z));
                        if (hi / lo > 8f)
                        {
                            Debug.Log($"[Kerbcast-fx-geom] part={part.partInfo?.name} rend={rend.name} bounds={b.x:F2}x{b.y:F2}x{b.z:F2} aspect={hi / lo:F1}");
                        }
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
