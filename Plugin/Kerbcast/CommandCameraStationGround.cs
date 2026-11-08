using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * The ground under a command camera whose body has no local terrain. The
     * lens stands metres above a body drawn only as its scaled mesh, closer
     * than the scaled layer's near plane, so looking down shows through the
     * clipped near side and the culled far side. This draws a copy of that
     * mesh, smoothed to a sphere so the lens is always outside it, magnified
     * about the lens: every direction in the picture stays where it was and
     * the ground moves past the near plane. It uses the body's own material
     * and is enabled only around the scaled render it belongs to, so no other
     * camera ever sees it.
     */
    internal sealed class CommandCameraStationGround : IDisposable
    {
        private struct Sphere
        {
            public Mesh Mesh;
            public float LocalRadius;
        }

        /* Shared by every camera: one copy per scaled mesh, built the first time it is needed. */
        private static readonly Dictionary<Mesh, Sphere> Spheres = new Dictionary<Mesh, Sphere>();

        private readonly GameObject _go;
        private readonly MeshFilter _filter;
        private readonly MeshRenderer _renderer;
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();

        public CommandCameraStationGround(uint flightId)
        {
            _go = new GameObject($"Kerbcast_{flightId}_StationGround");
            _filter = _go.AddComponent<MeshFilter>();
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.enabled = false;
        }

        /* Readies the ground for the coming scaled render. Null on success, else why it cannot be drawn. */
        public string Show(CelestialBody body, Vector3 lens, Vector3 lensToCentre, double sphereRadiusUnits)
        {
            var scaledBody = body.scaledBody;
            var source = scaledBody != null ? scaledBody.GetComponent<Renderer>() : null;
            var filter = scaledBody != null ? scaledBody.GetComponent<MeshFilter>() : null;
            var mesh = filter != null ? filter.sharedMesh : null;
            if (source == null || mesh == null) return "no scaled mesh";
            if (!mesh.isReadable) return "scaled mesh not readable";
            if (!SphereFor(body, mesh, scaledBody.transform.lossyScale.x, out var sphere)) return "scaled mesh could not be copied";

            _filter.sharedMesh = sphere.Mesh;
            _renderer.sharedMaterial = source.sharedMaterial;
            source.GetPropertyBlock(_block);
            _renderer.SetPropertyBlock(_block);
            _go.layer = scaledBody.layer;
            var t = _go.transform;
            t.position = lens + lensToCentre;
            t.rotation = scaledBody.transform.rotation;
            t.localScale = Vector3.one * (float)(sphereRadiusUnits / sphere.LocalRadius);
            _renderer.enabled = true;
            return null;
        }

        public void Hide()
        {
            if (_renderer != null) _renderer.enabled = false;
        }

        public void Dispose()
        {
            if (_go != null) UnityEngine.Object.Destroy(_go);
        }

        private static bool SphereFor(CelestialBody body, Mesh mesh, float meshScale, out Sphere sphere)
        {
            if (Spheres.TryGetValue(mesh, out sphere) && sphere.Mesh != null) return true;
            if (!(meshScale > 0f)) return false;
            try
            {
                float radius = (float)(body.Radius / ScaledSpace.ScaleFactor / meshScale);
                var copy = UnityEngine.Object.Instantiate(mesh);
                copy.name = mesh.name + "_StationGround";
                var v = copy.vertices;
                for (int i = 0; i < v.Length; i++) v[i] = v[i].normalized * radius;
                copy.vertices = v;
                copy.RecalculateBounds();
                sphere = new Sphere { Mesh = copy, LocalRadius = radius };
                Spheres[mesh] = sphere;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] event=station-ground body={body.bodyName} copy threw: {ex}");
                return false;
            }
        }
    }
}
