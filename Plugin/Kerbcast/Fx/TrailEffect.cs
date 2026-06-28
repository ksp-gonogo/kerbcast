// Trail atmospheric-FX layer: the plasma wake streaming behind a supersonic
// vessel along -velocity. Owns its own procedural tapered-tube mesh (built once
// in TryInitialize) and a CommandBuffer that draws it additively via the
// KerbcastTrail material. Like CoreSheathEffect the CB is attached at
// CameraEvent.AfterForwardAlpha so it composites against the near render's
// depth — correct occlusion against the vessel itself, no second camera.
//
// The mesh is a hollow tapered cylinder built along local +Z: z=0 at the
// vessel end (uv.y=0, radius ~_tubeStartRadius), z=_tubeLength at the tail
// (uv.y=1, radius collapses to ~0). No end caps; Cull Off in the shader so
// both inside and outside read. Per-frame Render builds a TRS that puts the
// vessel end at the CoM and orients +Z along -velocity (i.e. the tube extends
// astern).

using UnityEngine;
using UnityEngine.Rendering;

namespace Kerbcast
{
    internal sealed class TrailEffect : IAtmoFxEffect
    {
        public AtmoFxLayers Layer => AtmoFxLayers.Trail;

        private Camera _cam;
        private Material _material;
        private CommandBuffer _cb;
        private Mesh _mesh;
        private Vessel _vessel;
        private bool _attached;

        private static readonly int _IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int _WindDirId = Shader.PropertyToID("_WindDirWorld");

        // Tube geometry — built once via the shared FxMeshes. 24 length
        // segments × 12 radial segments; natural start radius 4 m (the
        // shared FxPlacement.Trail normalises by it, so the world-space
        // wake size is mesh-independent).
        private const int _lengthSegments = 24;
        private const int _radialSegments = 12;
        private const float _tubeStartRadius = 4f; // metres at the vessel end
        private const float _tubeLength = 20f;     // metres in local space

        // Intensity used by ForceAtmosphericFx — a moderate, flight-like value
        // (not max) so the forced pad preview reads like real supersonic flight.
        private const float _forcedIntensity = 0.6f;

        // Diagnostics (gated on DebugCameraLogging): throttle so the per-frame
        // state log doesn't spam.
        private float _lastLogTime;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;
            _material = KerbcastFxAssets.LoadMaterial("KerbcastTrail");
            if (_material == null) return false; // bundle/shader missing → unavailable
            _cb = new CommandBuffer { name = "Kerbcast FX Trail" };
            _mesh = FxMeshes.BuildTaperedTube(_tubeStartRadius, _tubeLength, _lengthSegments, _radialSegments);
            Debug.Log($"[Kerbcast] FX trail initialized on {nearCam.name}");
            return true;
        }

        public void OnVesselChanged(Vessel vessel)
        {
            // Mesh is vessel-agnostic — placement happens per-frame via TRS —
            // so OnVesselChanged just records the new vessel; no rebuild.
            _vessel = vessel;
        }

        public void Render(in FxFrameState state)
        {
            if (_material == null || _cam == null || _mesh == null) return;

            float intensity = KerbcastSettings.ForceAtmosphericFx
                ? _forcedIntensity
                : ComputeIntensity(state.Mach, state.DynamicPressure);

            Vector3 vel = state.VelocityWorld;
            float velSqr = vel.sqrMagnitude;

            // Throttled state readout — logged even when intensity is 0 or
            // velocity is too low, so a missing trail can be diagnosed the
            // same way as the core sheath: flight regime, intensity ramp, CB
            // attach state, and rendering path (AfterForwardAlpha only fires
            // in Forward — Deferred = no FX).
            if (KerbcastSettings.DebugCameraLogging && Time.time - _lastLogTime > 1.5f)
            {
                _lastLogTime = Time.time;
                var v = state.Vessel;
                Debug.Log($"[Kerbcast-debug] FX trail {_cam.name}: " +
                    $"vessel={(v != null ? v.vesselName : "null")} " +
                    $"srfSpd={Mathf.Sqrt(velSqr):F0} mach={state.Mach:F2} " +
                    $"q={state.DynamicPressure:F2} alt={(v != null ? v.altitude : 0):F0} " +
                    $"sit={(v != null ? v.situation.ToString() : "?")} " +
                    $"intensity={intensity:F2} attached={_attached} " +
                    $"path={_cam.actualRenderingPath}");
            }

            // No usable velocity vector → no wake direction → bail.
            if (velSqr < 1f)
            {
                Detach();
                return;
            }

            if (intensity <= 0.001f)
            {
                Detach(); // no heating → zero GPU cost
                return;
            }

            if (state.Vessel == null)
            {
                Detach();
                return;
            }

            // Place the tube: head at the vessel's AFT windward edge (pulled
            // IN slightly so the cylinder/parts occlude the top ring of the
            // tube — combined with the shader's headFade this hides the
            // hard mesh edge), +Z aligned with -velocity so the tube
            // streams astern. Radius scales with the vessel's WINDWARD
            // PROFILE so the wake matches the actual cross-section the
            // vessel presents to the airflow (broadside flight → wide
            // wake; end-on → narrower).
            Vector3 windDir = vel / Mathf.Sqrt(velSqr);
            // Silhouette + placement come from the shared FX core (also
            // compiled by the CI render harness): head buried at the aft
            // windward edge, elliptical wake matching the silhouette the
            // vessel presents — broadside drags a wide flat ribbon, not
            // the circular tube a nose-first vessel sheds.
            var silhouette = WindwardProfile.Compute(state.Vessel, windDir);
            var pose = FxPlacement.Trail(silhouette, windDir, state.Vessel.CoM, _tubeStartRadius);
            Matrix4x4 m = Matrix4x4.TRS(pose.Position, pose.Rotation, pose.Scale);

            _material.SetFloat(_IntensityId, intensity);
            _material.SetVector(_WindDirId, windDir);

            // Rebuild every frame — only one draw call, and the matrix changes
            // per frame anyway, so caching it would be pointless.
            _cb.Clear();
            _cb.DrawMesh(_mesh, m, _material);

            Attach();
        }

        // Shared FxRamps — mach gates the physics, q ramps the visibility
        // (never a binary gate; see the reentry pop-in note in FxCore).
        private static float ComputeIntensity(float mach, float dynamicPressure)
        {
            return FxRamps.Trail(mach, dynamicPressure);
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
