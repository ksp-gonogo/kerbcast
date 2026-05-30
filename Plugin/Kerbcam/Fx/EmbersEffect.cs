// Embers atmospheric-FX layer: sparks/embers shedding off the hot vessel into
// the wake at heavy reentry. Unlike CoreSheathEffect (which owns a
// CommandBuffer that re-draws the vessel's part renderers), this layer owns a
// Unity ParticleSystem GameObject parented into the near camera's scene. The
// system is World-space, so emitted particles detach from the moving vessel
// and trail behind it.
//
// Activation is restricted to *heavy* reentry only — embers should not appear
// at ordinary supersonic flight. The intensity gate combines a mach ramp
// (2.5 → 5) with a q ramp (0.5 → 5 kPa) and takes the min so both conditions
// must hold. At ~zero intensity emission is forced to 0 (cheap no-op) and the
// GameObject is left positioned wherever it last was.

using UnityEngine;

namespace Kerbcam
{
    internal sealed class EmbersEffect : IAtmoFxEffect
    {
        public AtmoFxLayers Layer => AtmoFxLayers.Embers;

        private Camera _cam;
        private Material _material;
        private GameObject _root;
        private ParticleSystem _ps;
        private ParticleSystemRenderer _psRenderer;
        private Vessel _vessel;

        private static readonly int _IntensityId = Shader.PropertyToID("_Intensity");

        // Intensity ramps. Heavy-reentry only — below mach 2.5 / q 0.5 kPa
        // there should be no embers at all. By mach 5 / q 5 kPa we're at full.
        // Combined as min(machRamp, qRamp) so both conditions must hold; e.g.
        // mach 6 in thin air still produces nothing because q gates it out.
        private const float _machLow = 2.5f;
        private const float _machHigh = 5.0f;
        private const float _qLow = 0.5f;     // kPa
        private const float _qHigh = 5.0f;    // kPa

        // Forced-preview intensity for ForceAtmosphericFx (mirrors CoreSheath's
        // 0.6 — a flight-like value, not the absolute max).
        private const float _forcedIntensity = 0.6f;

        // Peak emission rate (particles/sec) at intensity 1.0. With a max
        // lifetime of ~1.2 s and maxParticles=256 this gives steady-state
        // headroom (80 * 1.2 ≈ 96 in flight at full intensity).
        private const float _peakRate = 80f;

        // Hard particle cap.
        private const int _maxParticles = 256;

        // Diagnostics throttle (mirrors CoreSheath).
        private float _lastLogTime;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;

            // Load the material first so we don't leak a GameObject if the
            // bundle/shader is missing.
            _material = KerbcamFxAssets.LoadMaterial("KerbcamEmber");
            if (_material == null) return false;

            // Parent under the camera's parent so the particles live in the
            // scene the cam renders (not on the cam itself, which would drag
            // them along and defeat the World simulation space). Fall back to
            // the cam transform if the cam has no parent.
            var parent = nearCam.transform.parent != null
                ? nearCam.transform.parent
                : nearCam.transform;

            _root = new GameObject("Kerbcam FX Embers");
            // KSP's main flight cameras don't cull AtmoFxConstants.Layer, so
            // pinning the particles to it confines them to kerbcam streams
            // (the near cam's mask is OR'd with this layer in SetCameras).
            _root.layer = AtmoFxConstants.Layer;
            _root.transform.SetParent(parent, worldPositionStays: false);
            _root.transform.localPosition = Vector3.zero;
            _root.transform.localRotation = Quaternion.identity;
            _root.transform.localScale = Vector3.one;

            _ps = _root.AddComponent<ParticleSystem>();
            _psRenderer = _root.GetComponent<ParticleSystemRenderer>();

            ConfigureParticleSystem(_ps);
            ConfigureRenderer(_psRenderer, _material);

            Debug.Log($"[Kerbcam] FX embers initialized on {nearCam.name}");
            return true;
        }

        // All ParticleSystem module properties are returned by *value* from
        // their getters — you must assign the local back via the property
        // (e.g. `main.startLifetime = ...` mutates a temporary unless you go
        // `var main = ps.main; main.startLifetime = ...;`). Each module is
        // configured in its own scope below to make that pattern obvious.
        private void ConfigureParticleSystem(ParticleSystem ps)
        {
            // Stop the system before configuring so the initial 0-rate state
            // takes effect cleanly.
            ps.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);

            {
                var main = ps.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.2f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.12f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(5f, 15f);
                main.gravityModifier = 0f;
                main.maxParticles = _maxParticles;
                main.loop = true;
                main.playOnAwake = false;
                main.startColor = new ParticleSystem.MinMaxGradient(Color.white);
            }

            {
                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = 0f; // gated per-frame in Render
            }

            {
                var shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 25f;
                shape.radius = 1.0f;
                shape.position = Vector3.zero;
            }

            // Velocity-over-lifetime drift in World space. The world-space
            // wind direction is updated per frame in Render so embers always
            // trail backward relative to the vessel's current motion.
            {
                var vel = ps.velocityOverLifetime;
                vel.enabled = true;
                vel.space = ParticleSystemSimulationSpace.World;
                vel.x = new ParticleSystem.MinMaxCurve(0f);
                vel.y = new ParticleSystem.MinMaxCurve(0f);
                vel.z = new ParticleSystem.MinMaxCurve(0f);
            }

            // ColorOverLifetime: hot white-orange birth → red → fade to dark
            // transparent. This is what reads as "ember cooling".
            {
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var gradient = new Gradient();
                gradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(new Color(1.0f, 0.95f, 0.75f), 0.0f), // white-hot
                        new GradientColorKey(new Color(1.0f, 0.55f, 0.15f), 0.35f), // orange
                        new GradientColorKey(new Color(0.8f, 0.15f, 0.05f), 0.75f), // red
                        new GradientColorKey(new Color(0.1f, 0.02f, 0.0f), 1.0f),   // ash
                    },
                    new[]
                    {
                        new GradientAlphaKey(1.0f, 0.0f),
                        new GradientAlphaKey(0.8f, 0.4f),
                        new GradientAlphaKey(0.3f, 0.8f),
                        new GradientAlphaKey(0.0f, 1.0f),
                    });
                col.color = new ParticleSystem.MinMaxGradient(gradient);
            }

            // SizeOverLifetime taper from 1 → 0.3 as the spark cools.
            {
                var size = ps.sizeOverLifetime;
                size.enabled = true;
                var curve = new AnimationCurve(
                    new Keyframe(0.0f, 1.0f),
                    new Keyframe(0.6f, 0.7f),
                    new Keyframe(1.0f, 0.3f));
                size.size = new ParticleSystem.MinMaxCurve(1.0f, curve);
            }

            ps.Play();
        }

        private static void ConfigureRenderer(ParticleSystemRenderer r, Material mat)
        {
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = mat;
            r.sortingFudge = 0f;
            r.alignment = ParticleSystemRenderSpace.View;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        public void OnVesselChanged(Vessel vessel)
        {
            _vessel = vessel;
            // Clear any in-flight embers from the previous vessel so the new
            // one starts cleanly (otherwise old World-space sparks linger).
            if (_ps != null)
            {
                _ps.Clear(false);
            }
        }

        public void Render(in FxFrameState state)
        {
            if (_material == null || _ps == null) return;

            float intensity = KerbcamSettings.ForceAtmosphericFx
                ? _forcedIntensity
                : ComputeIntensity(state.Mach, state.DynamicPressure);

            bool emitting = intensity > 0.001f;

            if (KerbcamSettings.DebugCameraLogging && Time.time - _lastLogTime > 1.5f)
            {
                _lastLogTime = Time.time;
                var v = state.Vessel;
                Debug.Log($"[Kerbcam-debug] FX embers {_cam.name}: " +
                    $"vessel={(v != null ? v.vesselName : "null")} " +
                    $"srfSpd={state.VelocityWorld.magnitude:F0} mach={state.Mach:F2} " +
                    $"q={state.DynamicPressure:F2} alt={(v != null ? v.altitude : 0):F0} " +
                    $"sit={(v != null ? v.situation.ToString() : "?")} " +
                    $"intensity={intensity:F2} emitting={emitting} " +
                    $"path={_cam.actualRenderingPath}");
            }

            // Cheap no-op path: at zero intensity force emission to 0 and
            // bail. Existing in-flight particles continue to fade naturally.
            if (!emitting)
            {
                var em = _ps.emission;
                em.rateOverTime = 0f;
                _material.SetFloat(_IntensityId, 0f);
                return;
            }

            // Position the emitter at the vessel's world location, nudged
            // slightly backward along -wind so embers shed from "behind" the
            // hot zone rather than dead-centre.
            Vector3 windDir = state.VelocityWorld.sqrMagnitude > 1e-4f
                ? state.VelocityWorld.normalized
                : Vector3.up;
            if (state.Vessel != null && state.Vessel.transform != null && _root != null)
            {
                _root.transform.position =
                    state.Vessel.transform.position - windDir * 0.5f;
                // Orient the cone so its axis points backward (with the wake).
                // When -windDir is nearly parallel to world-up the implicit
                // up-axis goes degenerate and Unity logs a warning; switch
                // helper to world-right in that case (same trick as
                // KerbcamPlasma's wind frame).
                Vector3 lookUp = Mathf.Abs(windDir.y) < 0.99f ? Vector3.up : Vector3.right;
                _root.transform.rotation = Quaternion.LookRotation(-windDir, lookUp);
            }

            // Drift velocity along -wind (backward wake). Magnitude is on the
            // same order as startSpeed so it dominates the initial cone spray
            // a few hundred ms in. A small per-axis random component (±2 m/s)
            // breaks up the otherwise uniform sheet of particles.
            {
                var vel = _ps.velocityOverLifetime;
                vel.enabled = true;
                vel.space = ParticleSystemSimulationSpace.World;
                Vector3 drift = -windDir * 8f;
                const float jitter = 2f;
                vel.x = new ParticleSystem.MinMaxCurve(drift.x - jitter, drift.x + jitter);
                vel.y = new ParticleSystem.MinMaxCurve(drift.y - jitter, drift.y + jitter);
                vel.z = new ParticleSystem.MinMaxCurve(drift.z - jitter, drift.z + jitter);
            }

            {
                var em = _ps.emission;
                em.rateOverTime = Mathf.Lerp(0f, _peakRate, intensity);
            }

            _material.SetFloat(_IntensityId, intensity);
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

        public void Dispose()
        {
            if (_ps != null)
            {
                _ps.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }
            _ps = null;
            _psRenderer = null;
            if (_material != null)
            {
                Object.Destroy(_material);
                _material = null;
            }
        }
    }
}
