using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /* A point of light standing in for an unloaded target: a camera-facing
       impostor on the true line of sight but only a few km out (see
       CommandCameraMath.ImpostorDistance), sized to subtend a few pixels. At
       that distance it sits inside the local layers' range with ordinary
       float precision, so terrain and buildings hide it by depth and other
       transparent effects draw over it. Planet occlusion and eclipse are the
       rig's flags. Its renderer is on only around this camera's local-layer
       renders, so no other view sees it. */
    internal sealed class CommandCameraMarker : IDisposable
    {
        private const string Tag = "[Kerbcast][CMDCAM]";
        private const float SizePixels = 3f;
        private const double PreferredDistance = 5000.0;
        private const double MinUlps = 4.0;
        private const int TexSize = 16;
        private const int LocalSceneryLayer = 15;

        private static readonly string[] ShaderNames =
        {
            "KSP/Particles/Additive",
            "Legacy Shaders/Particles/Additive",
            "Particles/Additive",
            "KSP/Unlit",
            "Unlit/Texture",
        };

        private static readonly Color Warm = new Color(1f, 0.94f, 0.82f, 1f);
        private static readonly int TintColorId = Shader.PropertyToID("_TintColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private readonly GameObject _go;
        private readonly MeshRenderer _renderer;
        private readonly Mesh _mesh;
        private readonly Material _material;
        private readonly Texture2D _texture;
        private readonly List<MarkerRenderHook> _hooks = new List<MarkerRenderHook>();
        private readonly uint _flightId;
        private bool _show;
        private bool _shownLogged;

        private CommandCameraMarker(Camera[] localLayers, int layer, uint flightId, Shader shader)
        {
            _flightId = flightId;
            _texture = BuildDot();
            _material = new Material(shader) { mainTexture = _texture };
            _mesh = BuildQuad();

            _go = new GameObject($"Kerbcast_{flightId}_TargetMarker") { layer = layer };
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.enabled = false;

            foreach (var cam in localLayers)
            {
                if (cam == null) continue;
                var hook = cam.gameObject.AddComponent<MarkerRenderHook>();
                hook.Marker = this;
                _hooks.Add(hook);
            }
        }

        /* Null without local layers or a usable shader; the camera then runs
           without a marker. */
        public static CommandCameraMarker Create(Camera[] localLayers, uint flightId)
        {
            int common = ~0;
            int count = 0;
            foreach (var cam in localLayers)
            {
                if (cam == null) continue;
                common &= cam.cullingMask;
                count++;
            }
            if (count == 0)
            {
                Debug.LogWarning($"{Tag} cam={flightId} target marker off: no local layers");
                return null;
            }
            int layer = PickLayer(common);
            if (layer < 0)
            {
                Debug.LogWarning($"{Tag} cam={flightId} target marker off: no layer every local layer draws (mask=0x{common:X8})");
                return null;
            }
            Shader shader = null;
            foreach (var name in ShaderNames)
            {
                shader = Shader.Find(name);
                if (shader != null) break;
            }
            if (shader == null)
            {
                Debug.LogWarning($"{Tag} cam={flightId} target marker off: none of {string.Join(", ", ShaderNames)} found");
                return null;
            }
            var m = new CommandCameraMarker(localLayers, layer, flightId, shader);
            Debug.Log($"{Tag} cam={flightId} target marker ready: impostor shader='{shader.name}' layer={layer} "
                + $"localMask=0x{common:X8} hooks={m._hooks.Count} size={SizePixels:F0}px preferredDistance={PreferredDistance:F0}m");
            return m;
        }

        private static int PickLayer(int mask)
        {
            if ((mask & (1 << LocalSceneryLayer)) != 0) return LocalSceneryLayer;
            if ((mask & 1) != 0) return 0;
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) return i;
            return -1;
        }

        /* Places and sizes the impostor for this tick. `show` false keeps it
           hidden (eclipse, occluded, a loaded target, no fix). */
        public void Place(bool show, DVec3 station, DVec3 towardTarget, double rangeM, Quaternion facing,
            float fovDeg, int renderHeight)
        {
            _show = show;
            if (!show || _go == null) return;
            double sizeAtUnit = CommandCameraMath.PixelSizeAtUnit(fovDeg, renderHeight) * SizePixels;
            double extent = station.Magnitude + PreferredDistance;
            double d = CommandCameraMath.ImpostorDistance(rangeM, PreferredDistance, sizeAtUnit, extent, MinUlps);
            var p = station + towardTarget * d;
            float size = (float)(sizeAtUnit * d);
            _go.transform.position = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            _go.transform.rotation = facing;
            _go.transform.localScale = new Vector3(size, size, size);

            /* Dimmer with range, gently: full at 100 km, about half by 10 Mm. */
            double k = 1.0 - 0.25 * Math.Log10(Math.Max(1.0, rangeM / 100000.0));
            float b = Mathf.Clamp((float)k, 0.5f, 1f);
            var c = new Color(Warm.r * b, Warm.g * b, Warm.b * b, 1f);
            if (_material.HasProperty(TintColorId)) _material.SetColor(TintColorId, c * 0.5f);
            if (_material.HasProperty(ColorId)) _material.SetColor(ColorId, c);

            if (!_shownLogged)
            {
                _shownLogged = true;
                Debug.Log($"{Tag} cam={_flightId} target marker first shown: proxyDistance={d:F0}m of range={rangeM:F0}m "
                    + $"size={size:G3}m ({SizePixels:F0}px at fov={fovDeg:F3} over {renderHeight} rows) stationExtent={station.Magnitude:F0}m");
            }
        }

        internal void SetVisible(bool on)
        {
            if (_renderer != null) _renderer.enabled = on && _show;
        }

        public void Dispose()
        {
            foreach (var h in _hooks) if (h != null) UnityEngine.Object.Destroy(h);
            _hooks.Clear();
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
        }

        private static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "Kerbcast_TargetMarkerQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            /* Both windings, so the quad shows whichever way culling runs. */
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            return mesh;
        }

        /* Bright centre with a soft edge, so a few pixels read as a point. */
        private static Texture2D BuildDot()
        {
            var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false)
            {
                name = "Kerbcast_TargetMarkerDot",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            float c = (TexSize - 1) * 0.5f;
            for (int y = 0; y < TexSize; y++)
            {
                for (int x = 0; x < TexSize; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float a = Mathf.Clamp01(1.4f - d);
                    tex.SetPixel(x, y, new Color(a, a, a, a));
                }
            }
            tex.Apply(false, true);
            return tex;
        }
    }

    /* On each local layer of the command camera; Unity calls back around each
       explicit Render(). */
    internal sealed class MarkerRenderHook : MonoBehaviour
    {
        internal CommandCameraMarker Marker;

        private void OnPreCull() => Marker?.SetVisible(true);
        private void OnPostRender() => Marker?.SetVisible(false);
    }
}
