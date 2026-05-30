// Core atmospheric-FX layer: a procedural plasma sheath SHELL around the
// vessel.
//
// Previously this re-drew the vessel's own part renderers with the plasma
// material, which bound the sheath shape to the vessel silhouette no matter
// how much we inflated the vertices. The look-feedback called for "more
// points, doesn't look like the vessel," which the part-renderer source can't
// deliver. So we render our own mesh instead: a procedural UV-sphere (~800
// vertices) scaled into an ellipsoid along the wind axis and centred on the
// vessel CoM. The shader then has plenty of independent vertices to displace
// per-vertex by noise, giving an irregular cloudy shape that doesn't read
// as "the vessel." Vessel parts still occlude the shell where they're in
// front of it (ZTest LEqual on the near render's depth), so the effect
// reads as a halo around the visible parts and into the empty space around
// them — not a glow on them.

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
        private Mesh _proxyMesh;
        private Vessel _vessel;
        private float _vesselExtent = 5f;
        private bool _attached;

        // Shader property IDs.
        private static readonly int _IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int _WindDirId = Shader.PropertyToID("_WindDirWorld");
        private static readonly int _NoiseAmountId = Shader.PropertyToID("_NoiseAmount");

        // Max vertex displacement (metres) at full intensity. Drives how far
        // each shell vertex can deform off the smooth sphere/ellipsoid base.
        private const float _maxNoiseMeters = 1.8f;
        // Intensity used by ForceAtmosphericFx — moderate, flight-like.
        private const float _forcedIntensity = 0.6f;
        // Intensity-gating thresholds.
        private const float _minQ = 0.1f;       // kPa
        private const float _machLow = 0.8f;
        private const float _machHigh = 5.0f;

        // Diagnostics — throttle so the per-frame state log doesn't spam.
        private float _lastLogTime;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;
            _material = KerbcamFxAssets.LoadMaterial("KerbcamPlasma");
            if (_material == null) return false;
            _cb = new CommandBuffer { name = "Kerbcam FX Core" };
            _proxyMesh = BuildUvSphere(24, 32);
            Debug.Log($"[Kerbcam] FX core initialized on {nearCam.name} (KerbcamPlasma material loaded; proxy mesh {_proxyMesh.vertexCount} verts)");
            return true;
        }

        public void OnVesselChanged(Vessel vessel)
        {
            _vessel = vessel;
            _vesselExtent = 5f;
            if (vessel != null && vessel.parts != null)
            {
                Vector3 com = vessel.CoM;
                foreach (var part in vessel.parts)
                {
                    if (part == null) continue;
                    float d = Vector3.Distance(part.transform.position, com);
                    if (d > _vesselExtent) _vesselExtent = d;
                }
            }
        }

        public void Render(in FxFrameState state)
        {
            if (_material == null || _cam == null || _cb == null) return;

            float intensity = KerbcamSettings.ForceAtmosphericFx
                ? _forcedIntensity
                : ComputeIntensity(state.Mach, state.DynamicPressure);

            if (KerbcamSettings.DebugCameraLogging && Time.time - _lastLogTime > 1.5f)
            {
                _lastLogTime = Time.time;
                var v = state.Vessel;
                Debug.Log($"[Kerbcam-debug] FX core {_cam.name}: " +
                    $"vessel={(v != null ? v.vesselName : "null")} " +
                    $"srfSpd={state.VelocityWorld.magnitude:F0} mach={state.Mach:F2} " +
                    $"q={state.DynamicPressure:F2} alt={(v != null ? v.altitude : 0):F0} " +
                    $"sit={(v != null ? v.situation.ToString() : "?")} " +
                    $"intensity={intensity:F2} extent={_vesselExtent:F1} " +
                    $"attached={_attached} path={_cam.actualRenderingPath}");
            }

            if (intensity <= 0.001f || state.Vessel == null)
            {
                Detach();
                return;
            }

            // Build the proxy shell transform: centred on the vessel CoM,
            // oriented so +Z points along the wind, scaled to an ellipsoid
            // that encompasses the vessel (longer along the wind axis).
            Vector3 windDir = state.VelocityWorld.sqrMagnitude > 1e-4f
                ? state.VelocityWorld.normalized
                : state.Vessel.transform.up;
            // Offset the shell forward (along wind) so its bulk sits in front
            // of the vessel — visibly telegraphs the direction of motion.
            // Loose extents so the shell stands clearly off the hull rather
            // than hugging it.
            Vector3 com = state.Vessel.CoM + windDir * _vesselExtent * 0.25f;
            float rLateral = _vesselExtent * 1.7f;
            float rAxial = _vesselExtent * 2.4f;
            Vector3 scale = new Vector3(rLateral, rLateral, rAxial);
            Quaternion rot = Quaternion.LookRotation(windDir);
            Matrix4x4 m = Matrix4x4.TRS(com, rot, scale);

            _cb.Clear();
            _cb.DrawMesh(_proxyMesh, m, _material);

            _material.SetFloat(_IntensityId, intensity);
            _material.SetFloat(_NoiseAmountId, _maxNoiseMeters * intensity);
            _material.SetVector(_WindDirId, windDir);

            Attach();
        }

        private static float ComputeIntensity(float mach, float dynamicPressure)
        {
            if (dynamicPressure < _minQ) return 0f;
            return Mathf.Clamp01(Mathf.InverseLerp(_machLow, _machHigh, mach));
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
            if (_proxyMesh != null) Object.Destroy(_proxyMesh);
            _proxyMesh = null;
        }

        // Procedural UV sphere — unit radius, dense enough that vertex
        // displacement reads as a noisy cloud rather than a faceted shell.
        // (24 latitude × 32 longitude rings = 825 vertices, 1536 triangles.)
        // The transform matrix in Render scales it into a wind-aligned ellipsoid.
        private static Mesh BuildUvSphere(int latSegs, int lonSegs)
        {
            int stride = lonSegs + 1;
            int vc = (latSegs + 1) * stride;
            var verts = new Vector3[vc];
            var normals = new Vector3[vc];

            int idx = 0;
            for (int i = 0; i <= latSegs; i++)
            {
                float phi = (float)i / latSegs * Mathf.PI;
                float y = Mathf.Cos(phi);
                float r = Mathf.Sin(phi);
                for (int j = 0; j <= lonSegs; j++)
                {
                    float theta = (float)j / lonSegs * Mathf.PI * 2f;
                    var p = new Vector3(r * Mathf.Cos(theta), y, r * Mathf.Sin(theta));
                    verts[idx] = p;
                    normals[idx] = p.normalized;
                    idx++;
                }
            }

            var tris = new int[latSegs * lonSegs * 6];
            int t = 0;
            for (int i = 0; i < latSegs; i++)
            {
                for (int j = 0; j < lonSegs; j++)
                {
                    int a = i * stride + j;
                    int b = a + 1;
                    int c = a + stride;
                    int d = c + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }

            var mesh = new Mesh { name = "Kerbcam FX Core Shell" };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.triangles = tris;
            // Generous bounds so frustum culling on the scaled instance is safe
            // at any vessel size; per-instance scale is applied via the TRS.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 4f);
            return mesh;
        }
    }
}
