// Bowshock atmospheric-FX layer: the luminous shock cone visible IN FRONT of
// the vessel along the velocity vector — the compressed-air shock you see
// ahead of a supersonic body. Owns a CommandBuffer attached to the near camera
// at CameraEvent.AfterForwardAlpha that draws a single procedural hollow cone
// mesh through the KerbcastBowshock additive material. Running inside the near
// render means it composites against that render's depth buffer (correct
// occlusion against the hull, no second camera). The mesh is generated once
// in TryInitialize and re-used; placement is per-frame via a Matrix4x4 built
// from the vessel CoM, the velocity vector and a crude size proxy.

using UnityEngine;
using UnityEngine.Rendering;

namespace Kerbcast
{
    internal sealed class BowshockEffect : IAtmoFxEffect
    {
        public AtmoFxLayers Layer => AtmoFxLayers.Bowshock;

        private Camera _cam;
        private Material _material;
        private CommandBuffer _cb;
        private Mesh _mesh;
        private Vessel _vessel;
        private bool _attached;

        private static readonly int _IntensityId = Shader.PropertyToID("_Intensity");

        // Intensity used by ForceAtmosphericFx — a moderate, flight-like value
        // (not max) so the forced pad preview reads like real supersonic flight.
        private const float _forcedIntensity = 0.6f;

        // Throttled state log gate so the per-frame diagnostic doesn't spam.
        private float _lastLogTime;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;
            _material = KerbcastFxAssets.LoadMaterial("KerbcastBowshock");
            if (_material == null) return false; // bundle/shader missing → unavailable
            _cb = new CommandBuffer { name = "Kerbcast FX Bowshock" };
            // Oblate dome (flat hemisphere) — replaces the original cone mesh.
            // The shader auto-detects which shape via length(localPos) < 1.3
            // and uses a spherical normal for the dome / cylindrical for the
            // cone. Dome matches real blunt-body bowshock physics better
            // than a cone (a paraboloidal/spherical-cap detached shock).
            // Built by the shared FxMeshes so CI renders the same geometry.
            _mesh = FxMeshes.BuildDome();
            Debug.Log($"[Kerbcast] FX bowshock initialized on {nearCam.name} (dome mesh)");
            return true;
        }

        public void OnVesselChanged(Vessel vessel)
        {
            // Bowshock sizing reads the WindwardProfile in Render() so it
            // adapts to vessel orientation per-frame — no cached per-vessel
            // state to compute here.
            _vessel = vessel;
        }

        public void Render(in FxFrameState state)
        {
            if (_material == null || _cam == null) return;

            float intensity = KerbcastSettings.ForceAtmosphericFx
                ? _forcedIntensity
                : ComputeIntensity(state.Mach, state.DynamicPressure);

            // Throttled state readout — logged even when intensity is 0, so a
            // missing effect can be diagnosed: flight regime (mach/q), whether
            // intensity ramps, attach state, and the rendering path (the CB
            // only fires in Forward — Deferred = no FX).
            if (KerbcastSettings.DebugCameraLogging && Time.time - _lastLogTime > 1.5f)
            {
                _lastLogTime = Time.time;
                var v = state.Vessel;
                Debug.Log($"[Kerbcast-debug] FX bowshock {_cam.name}: " +
                    $"vessel={(v != null ? v.vesselName : "null")} " +
                    $"srfSpd={state.VelocityWorld.magnitude:F0} mach={state.Mach:F2} " +
                    $"q={state.DynamicPressure:F2} alt={(v != null ? v.altitude : 0):F0} " +
                    $"sit={(v != null ? v.situation.ToString() : "?")} " +
                    $"intensity={intensity:F2} attached={_attached} " +
                    $"path={_cam.actualRenderingPath}");
            }

            if (intensity <= 0.001f)
            {
                Detach(); // no shock → zero GPU cost
                return;
            }

            // Need a velocity vector to point the cone; if the vessel is
            // effectively stationary, drop out without drawing.
            Vector3 vel = state.VelocityWorld;
            if (vel.sqrMagnitude < 1f)
            {
                Detach();
                return;
            }
            if (state.Vessel == null)
            {
                Detach();
                return;
            }

            Vector3 windDir = vel.normalized;
            // Silhouette + placement come from the shared FX core (also
            // compiled by the CI render harness): elliptical cross-section,
            // dome base at the windward extreme, broadside → canoe shock.
            var silhouette = WindwardProfile.Compute(state.Vessel, windDir);
            var pose = FxPlacement.Bowshock(silhouette, windDir, state.Vessel.CoM);
            Matrix4x4 m = Matrix4x4.TRS(pose.Position, pose.Rotation, pose.Scale);

            _material.SetFloat(_IntensityId, intensity);

            // Single DrawMesh — the mesh is immutable, only the matrix moves.
            // No _cbDirty machinery needed (cf. CoreSheath, which rebuilds when
            // the vessel's renderer set changes).
            _cb.Clear();
            _cb.DrawMesh(_mesh, m, _material);

            Attach();
        }

        // Shared FxRamps so the CI fade-strip renders the exact curve the
        // plugin runs: mach gates the physics, q ramps the visibility —
        // never a binary gate (reentry pop-in).
        private static float ComputeIntensity(float mach, float dynamicPressure)
        {
            return FxRamps.Bowshock(mach, dynamicPressure);
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
            if (_mesh != null) Object.Destroy(_mesh);
            _mesh = null;
            if (_material != null) Object.Destroy(_material);
            _material = null;
        }
    }
}
