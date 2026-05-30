// Trail atmospheric-FX layer: the plasma wake streaming behind a supersonic
// vessel along -velocity. Owns its own procedural tapered-tube mesh (built once
// in TryInitialize) and a CommandBuffer that draws it additively via the
// KerbcamTrail material. Like CoreSheathEffect the CB is attached at
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

namespace Kerbcam
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

        // Tube geometry — built once. 24 length segments × 12 radial segments,
        // 25 rings × 13 verts (duplicate at the radial seam so uv.x runs 0→1
        // cleanly without a seam vert needing two uv.x values).
        private const int _lengthSegments = 24;
        private const int _radialSegments = 12;
        private const float _tubeStartRadius = 4f; // metres at the vessel end
        private const float _tubeLength = 20f;     // metres in local space

        // Intensity ramp — per spec, more aggressive than CoreSheath: trail
        // turns on a bit later (mach 0.9) and saturates earlier (mach 3.0).
        private const float _minQ = 0.1f;       // kPa
        private const float _machLow = 0.9f;
        private const float _machHigh = 3.0f;

        // Intensity used by ForceAtmosphericFx — a moderate, flight-like value
        // (not max) so the forced pad preview reads like real supersonic flight.
        private const float _forcedIntensity = 0.6f;

        // Diagnostics (gated on DebugCameraLogging): throttle so the per-frame
        // state log doesn't spam.
        private float _lastLogTime;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;
            _material = KerbcamFxAssets.LoadMaterial("KerbcamTrail");
            if (_material == null) return false; // bundle/shader missing → unavailable
            _cb = new CommandBuffer { name = "Kerbcam FX Trail" };
            _mesh = BuildTaperedTube();
            Debug.Log($"[Kerbcam] FX trail initialized on {nearCam.name}");
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

            float intensity = KerbcamSettings.ForceAtmosphericFx
                ? _forcedIntensity
                : ComputeIntensity(state.Mach, state.DynamicPressure);

            Vector3 vel = state.VelocityWorld;
            float velSqr = vel.sqrMagnitude;

            // Throttled state readout — logged even when intensity is 0 or
            // velocity is too low, so a missing trail can be diagnosed the
            // same way as the core sheath: flight regime, intensity ramp, CB
            // attach state, and rendering path (AfterForwardAlpha only fires
            // in Forward — Deferred = no FX).
            if (KerbcamSettings.DebugCameraLogging && Time.time - _lastLogTime > 1.5f)
            {
                _lastLogTime = Time.time;
                var v = state.Vessel;
                Debug.Log($"[Kerbcam-debug] FX trail {_cam.name}: " +
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

            // Place the tube: vessel end at CoM, +Z aligned with -velocity so
            // the tube streams astern. LookRotation aligns its forward (+Z)
            // with the given direction.
            Vector3 windDir = vel / Mathf.Sqrt(velSqr);
            Vector3 worldPos = state.Vessel.CoM;
            Quaternion rot = Quaternion.LookRotation(-windDir);
            Matrix4x4 m = Matrix4x4.TRS(worldPos, rot, Vector3.one);

            _material.SetFloat(_IntensityId, intensity);
            _material.SetVector(_WindDirId, windDir);

            // Rebuild every frame — only one draw call, and the matrix changes
            // per frame anyway, so caching it would be pointless.
            _cb.Clear();
            _cb.DrawMesh(_mesh, m, _material);

            Attach();
        }

        private static float ComputeIntensity(float mach, float dynamicPressure)
        {
            if (dynamicPressure < _minQ) return 0f;
            return Mathf.Clamp01(Mathf.InverseLerp(_machLow, _machHigh, mach));
        }

        // Build a hollow tapered cylinder along local +Z. uv.y = z / _tubeLength
        // (0 at vessel end, 1 at tail); uv.x = radial fraction (0→1 around the
        // ring, with seam duplication). Radius tapers linearly from
        // _tubeStartRadius at z=0 to 0 at z=_tubeLength. No end caps.
        private static Mesh BuildTaperedTube()
        {
            int rings = _lengthSegments + 1;       // 25
            int vertsPerRing = _radialSegments + 1; // 13 (seam duplicated)
            int vertCount = rings * vertsPerRing;   // 325
            int triCount = _lengthSegments * _radialSegments * 2; // 576 tris
            int idxCount = triCount * 3;            // 1728 indices

            var verts = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var tris = new int[idxCount];

            for (int r = 0; r < rings; r++)
            {
                float vy = (float)r / _lengthSegments;             // 0..1 along length
                float z = vy * _tubeLength;
                float radius = Mathf.Lerp(_tubeStartRadius, 0f, vy);
                for (int s = 0; s < vertsPerRing; s++)
                {
                    float ux = (float)s / _radialSegments;         // 0..1 around ring
                    float angle = ux * Mathf.PI * 2f;
                    float cx = Mathf.Cos(angle);
                    float cy = Mathf.Sin(angle);
                    int i = r * vertsPerRing + s;
                    verts[i] = new Vector3(cx * radius, cy * radius, z);
                    // Outward normal in local space — ignores the taper tilt,
                    // close enough for a thin radial gradient in the shader.
                    normals[i] = new Vector3(cx, cy, 0f);
                    uvs[i] = new Vector2(ux, vy);
                }
            }

            int t = 0;
            for (int r = 0; r < _lengthSegments; r++)
            {
                int row0 = r * vertsPerRing;
                int row1 = (r + 1) * vertsPerRing;
                for (int s = 0; s < _radialSegments; s++)
                {
                    int a = row0 + s;
                    int b = row0 + s + 1;
                    int c = row1 + s;
                    int d = row1 + s + 1;
                    // CCW when viewed from outside (+normal) — Cull Off in the
                    // shader so winding doesn't matter for visibility, but keep
                    // it consistent anyway.
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }

            var mesh = new Mesh { name = "KerbcamTrailTube" };
            // 325 verts is comfortably under the 16-bit index limit; explicit
            // for clarity.
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            // Huge bounds so it never frustum-culls when streaming behind a
            // fast vessel — the matrix can fling it far from its origin.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
            return mesh;
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
