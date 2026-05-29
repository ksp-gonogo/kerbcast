// Bowshock atmospheric-FX layer: the luminous shock cone visible IN FRONT of
// the vessel along the velocity vector — the compressed-air shock you see
// ahead of a supersonic body. Owns a CommandBuffer attached to the near camera
// at CameraEvent.AfterForwardAlpha that draws a single procedural hollow cone
// mesh through the KerbcamBowshock additive material. Running inside the near
// render means it composites against that render's depth buffer (correct
// occlusion against the hull, no second camera). The mesh is generated once
// in TryInitialize and re-used; placement is per-frame via a Matrix4x4 built
// from the vessel CoM, the velocity vector and a crude size proxy.

using UnityEngine;
using UnityEngine.Rendering;

namespace Kerbcam
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

        // Cone geometry constants. 16 radial segments around the cone surface,
        // each face emits two flat-shaded triangles sharing the apex copy and
        // a unique base-vertex pair — 32 verts (1 apex copy + 1 base copy per
        // face), 16 triangles, no end caps. Per-face flat normals are crucial
        // for the shader's fresnel: a shared-apex mesh has a degenerate normal
        // at the apex and gives a muddy rim. Local +Z is the apex direction
        // (apex points along velocity); the wide end is at -Z.
        private const int _radialSegments = 16;
        private const float _baseRadius = 3f;     // m
        private const float _length = 6f;          // m, apex(+Z) to base(-Z)

        // Intensity used by ForceAtmosphericFx — a moderate, flight-like value
        // (not max) so the forced pad preview reads like real supersonic flight.
        private const float _forcedIntensity = 0.6f;

        // Intensity ramp (C#-side, fast loop — no CI rebuild to tune). Below
        // _minQ there's no meaningful atmosphere; intensity ramps with mach
        // from transonic onset up. Tuned per the bowshock spec: gate at mach
        // 1.0 (transonic), full by mach 2.5.
        private const float _minQ = 0.1f;       // kPa
        private const float _machLow = 1.0f;
        private const float _machHigh = 2.5f;

        // Throttled state log gate so the per-frame diagnostic doesn't spam.
        private float _lastLogTime;

        public bool TryInitialize(Camera nearCam)
        {
            _cam = nearCam;
            _material = KerbcamFxAssets.LoadMaterial("KerbcamBowshock");
            if (_material == null) return false; // bundle/shader missing → unavailable
            _cb = new CommandBuffer { name = "Kerbcam FX Bowshock" };
            _mesh = BuildConeMesh();
            Debug.Log($"[Kerbcam] FX bowshock initialized on {nearCam.name}");
            return true;
        }

        public void OnVesselChanged(Vessel vessel)
        {
            // Bowshock placement reads state.Vessel each Render, and the mesh
            // is vessel-independent — no per-vessel rebuild needed.
            _vessel = vessel;
        }

        public void Render(in FxFrameState state)
        {
            if (_material == null || _cam == null) return;

            float intensity = KerbcamSettings.ForceAtmosphericFx
                ? _forcedIntensity
                : ComputeIntensity(state.Mach, state.DynamicPressure);

            // Throttled state readout — logged even when intensity is 0, so a
            // missing effect can be diagnosed: flight regime (mach/q), whether
            // intensity ramps, attach state, and the rendering path (the CB
            // only fires in Forward — Deferred = no FX).
            if (KerbcamSettings.DebugCameraLogging && Time.time - _lastLogTime > 1.5f)
            {
                _lastLogTime = Time.time;
                var v = state.Vessel;
                Debug.Log($"[Kerbcam-debug] FX bowshock {_cam.name}: " +
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
            // Crude size proxy: more parts → larger vessel → place the cone
            // farther ahead so it doesn't visually intersect the hull. Capped
            // at 30 parts so the shock doesn't wander to infinity for big stacks.
            float offset = 8f + Mathf.Min(state.Vessel.parts.Count, 30) * 0.5f;
            Vector3 worldPos = state.Vessel.CoM + windDir * offset;
            Quaternion rot = Quaternion.LookRotation(windDir); // apex (+Z) points along velocity
            Matrix4x4 m = Matrix4x4.TRS(worldPos, rot, Vector3.one);

            _material.SetFloat(_IntensityId, intensity);

            // Single DrawMesh — the mesh is immutable, only the matrix moves.
            // No _cbDirty machinery needed (cf. CoreSheath, which rebuilds when
            // the vessel's renderer set changes).
            _cb.Clear();
            _cb.DrawMesh(_mesh, m, _material);

            Attach();
        }

        private static float ComputeIntensity(float mach, float dynamicPressure)
        {
            if (dynamicPressure < _minQ) return 0f;
            return Mathf.Clamp01(Mathf.InverseLerp(_machLow, _machHigh, mach));
        }

        // Procedural hollow cone, flat-shaded per face. 16 radial segments × 2
        // verts/face (apex copy + base copy) = 32 verts, 16 triangles, no caps.
        // Each face carries a single outward-radial normal so fresnel reads
        // cleanly at the silhouette; a shared-vertex/averaged-normal cone would
        // smear the rim into a muddy gradient.
        private static Mesh BuildConeMesh()
        {
            int faces = _radialSegments;
            var verts = new Vector3[faces * 2];
            var normals = new Vector3[faces * 2];
            var tris = new int[faces * 3];

            // Apex sits at local +Z = _length/2, base ring at -Z = -_length/2.
            // Splitting half-and-half so the matrix anchor sits roughly mid-cone.
            float apexZ = _length * 0.5f;
            float baseZ = -_length * 0.5f;
            Vector3 apexPos = new Vector3(0f, 0f, apexZ);

            for (int f = 0; f < faces; f++)
            {
                // Midpoint angle of this face — used to compute the face's
                // outward-radial normal direction in XY.
                float angMid = (f + 0.5f) / faces * Mathf.PI * 2f;
                float angA = (float)f / faces * Mathf.PI * 2f;
                float angB = (float)(f + 1) / faces * Mathf.PI * 2f;

                Vector3 baseA = new Vector3(Mathf.Cos(angA) * _baseRadius, Mathf.Sin(angA) * _baseRadius, baseZ);
                Vector3 baseB = new Vector3(Mathf.Cos(angB) * _baseRadius, Mathf.Sin(angB) * _baseRadius, baseZ);

                // Per-face outward-radial normal. Tilted slightly along +Z
                // because the slant surface leans toward the apex; computed
                // from the actual face geometry via a cross product so the
                // shader gets a geometrically accurate normal.
                Vector3 edge1 = baseA - apexPos;
                Vector3 edge2 = baseB - apexPos;
                Vector3 faceNormal = Vector3.Cross(edge1, edge2).normalized;
                // Make sure the normal points outward (radially away from the
                // cone axis). The midpoint XY direction is the canonical
                // outward direction; flip if the cross product came out the
                // wrong way.
                Vector3 outwardXY = new Vector3(Mathf.Cos(angMid), Mathf.Sin(angMid), 0f);
                if (Vector3.Dot(faceNormal, outwardXY) < 0f) faceNormal = -faceNormal;

                int vBase = f * 2;
                // Two unique verts per face: apex copy + one base vert. The
                // adjacent face owns the next base vert, so each face shares
                // the apex/base seam with its neighbour only by position, not
                // by index — keeps the per-face normal crisp.
                verts[vBase + 0] = apexPos;
                verts[vBase + 1] = baseA;
                normals[vBase + 0] = faceNormal;
                normals[vBase + 1] = faceNormal;

                // Triangle: apex → baseA → baseB. baseB is owned by face f+1
                // (its baseA), so we index across into the next face's vert
                // slot. Wraps cleanly back to face 0 on the seam.
                int next = (f + 1) % faces;
                int tBase = f * 3;
                tris[tBase + 0] = vBase + 0;          // apex
                tris[tBase + 1] = vBase + 1;          // this face's baseA
                tris[tBase + 2] = next * 2 + 1;       // next face's baseA = this face's baseB
            }

            var mesh = new Mesh { name = "Kerbcam Bowshock Cone" };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
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
