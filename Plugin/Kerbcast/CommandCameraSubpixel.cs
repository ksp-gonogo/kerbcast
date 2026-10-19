using System;
using UnityEngine;
using UnityEngine.Rendering;
using Yangrc.OpenGLAsyncReadback;

namespace Kerbcast
{
    /*
     * Draws a craft smaller than a pixel the way a real sensor would: as the
     * light it reflects, spread over the pixels it touches. Without this a
     * sub-pixel model vanishes, because a triangle only rasterises when it
     * covers a pixel centre.
     *
     * The real model is rendered, lit by the scene, through a window of
     * WindowPx x WindowPx frame pixels on the view axis, at Supersample times
     * the frame's resolution with MSAA on top. The result is read back,
     * box-averaged to frame pixels and added onto the frame centre. The
     * readback goes through the same Unity-or-native path as the feeds and
     * lands a frame or two later, which is invisible for a target held at the
     * centre.
     */
    internal sealed class CommandCameraSubpixel : IDisposable
    {
        private const string Tag = "[Kerbcast][CMDCAM]";
        internal const int WindowPx = 16;
        private const int Supersample = 4;
        private const int Msaa = 8;
        private const int RtSize = WindowPx * Supersample;

        private readonly Camera _cam;
        private readonly RenderTexture _rt;
        private readonly RenderTexture _resolve;
        private readonly Material _add;
        private readonly Color[] _px = new Color[WindowPx * WindowPx];
        private bool _has;
        private bool _inFlight;
        private UniversalAsyncGPUReadbackRequest _req;
        private readonly uint _flightId;
        private bool _sizeWarned;
        private bool _discardPending;
        private bool _disposed;

        internal Camera Camera => _cam;
        /* Sum of the last window's light, for the status log. */
        internal float LastEnergy { get; private set; }
        /* Frame pixels' worth of supersamples the model touched, and the brightest one, to tell thin geometry from dim lighting. */
        internal float LastCoverage { get; private set; }
        internal float LastPeak { get; private set; }

        private CommandCameraSubpixel(Camera source, uint flightId, Material add)
        {
            var go = new GameObject($"Kerbcast_{flightId}_Subpixel");
            _cam = go.AddComponent<Camera>();
            _cam.CopyFrom(source);
            _cam.name = go.name;
            go.transform.parent = source.transform.parent;
            go.transform.localPosition = source.transform.localPosition;
            go.transform.localRotation = source.transform.localRotation;
            _cam.enabled = false;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cam.allowMSAA = true;
            _cam.aspect = 1f;
            go.AddComponent<CanvasHack>();

            _rt = new RenderTexture(RtSize, RtSize, 24, RenderTextureFormat.ARGBHalf) { antiAliasing = Msaa, name = go.name + "_MSAA" };
            _rt.Create();
            _resolve = new RenderTexture(RtSize, RtSize, 0, RenderTextureFormat.ARGBFloat) { name = go.name + "_Resolve" };
            _resolve.Create();
            _cam.targetTexture = _rt;
            _add = add;
            _flightId = flightId;
        }

        public static CommandCameraSubpixel Create(Camera source, uint flightId)
        {
            if (source == null) return null;
            if (!ReadbackGate.EnsureReady())
            {
                Debug.LogWarning($"{Tag} cam={flightId} sub-pixel model off: no GPU readback path");
                return null;
            }
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                Debug.LogWarning($"{Tag} cam={flightId} sub-pixel model off: Hidden/Internal-Colored not found");
                return null;
            }
            var add = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            add.SetInt("_SrcBlend", (int)BlendMode.One);
            add.SetInt("_DstBlend", (int)BlendMode.One);
            add.SetInt("_Cull", (int)CullMode.Off);
            add.SetInt("_ZWrite", 0);
            add.SetInt("_ZTest", (int)CompareFunction.Always);
            var s = new CommandCameraSubpixel(source, flightId, add);
            Debug.Log($"{Tag} cam={flightId} sub-pixel model ready: window={WindowPx}px supersample={Supersample}x msaa={Msaa}x "
                + $"source='{source.name}' mask=0x{source.cullingMask:X8} readback={(SystemInfo.supportsAsyncGPUReadback ? "unity" : "native")}");
            return s;
        }

        /* Forget the last window so a stale dot never shows after a mode change. */
        public void Reset()
        {
            _has = false;
            LastEnergy = 0f;
            LastCoverage = 0f;
            LastPeak = 0f;
            _discardPending = _inFlight;
        }

        /* Renders the window around the view axis of `pose`, with the frame's
           vertical FOV and height, between `near` and `far`. */
        public void Render(Transform pose, float frameFovDeg, int frameHeight, float near, float far)
        {
            if (_disposed || pose == null || frameHeight <= 0) return;
            _cam.transform.SetPositionAndRotation(pose.position, pose.rotation);
            double half = Math.Tan(frameFovDeg * 0.5 * Math.PI / 180.0) * WindowPx / frameHeight;
            _cam.fieldOfView = (float)(2.0 * Math.Atan(half) * 180.0 / Math.PI);
            _cam.aspect = 1f;
            _cam.nearClipPlane = Mathf.Max(0.01f, near);
            _cam.farClipPlane = Mathf.Max(_cam.nearClipPlane * 1.001f, far);
            _cam.targetTexture = _rt;
            if (_inFlight && _req.done)
            {
                _inFlight = false;
                if (!_discardPending) Drain(_req);
                _discardPending = false;
            }
            _cam.Render();
            Graphics.Blit(_rt, _resolve);
            if (_inFlight) return;
            _req = UniversalAsyncGPUReadbackRequest.Request(_resolve, 0);
            _inFlight = true;
        }

        private void Drain(UniversalAsyncGPUReadbackRequest req)
        {
            if (_disposed || req.hasError) return;
            var data = req.GetData<Color>();
            if (data.Length < RtSize * RtSize)
            {
                if (!_sizeWarned)
                {
                    _sizeWarned = true;
                    Debug.LogWarning($"{Tag} cam={_flightId} sub-pixel readback gave {data.Length} RGBA-float pixels, expected {RtSize * RtSize}; window not drawn");
                }
                return;
            }
            const float norm = 1f / (Supersample * Supersample);
            float energy = 0f, peak = 0f;
            int lit = 0;
            for (int y = 0; y < WindowPx; y++)
            {
                for (int x = 0; x < WindowPx; x++)
                {
                    float r = 0f, g = 0f, b = 0f;
                    for (int sy = 0; sy < Supersample; sy++)
                    {
                        int row = (y * Supersample + sy) * RtSize + x * Supersample;
                        for (int sx = 0; sx < Supersample; sx++)
                        {
                            var c = data[row + sx];
                            r += c.r; g += c.g; b += c.b;
                            float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                            if (m > 1e-4f) lit++;
                            if (m > peak) peak = m;
                        }
                    }
                    var px = new Color(r * norm, g * norm, b * norm, 1f);
                    _px[y * WindowPx + x] = px;
                    energy += px.r + px.g + px.b;
                }
            }
            LastEnergy = energy;
            LastCoverage = lit * norm;
            LastPeak = peak;
            _has = true;
        }

        /* Adds the last window onto the centre of `target`. */
        public void Composite(RenderTexture target)
        {
            if (!_has || _disposed || target == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = target;
            try
            {
                _add.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, target.width, 0, target.height);
                GL.Begin(GL.QUADS);
                int x0 = target.width / 2 - WindowPx / 2;
                int y0 = target.height / 2 - WindowPx / 2;
                for (int y = 0; y < WindowPx; y++)
                {
                    for (int x = 0; x < WindowPx; x++)
                    {
                        var c = _px[y * WindowPx + x];
                        if (c.r + c.g + c.b <= 0f) continue;
                        GL.Color(c);
                        float px = x0 + x, py = y0 + y;
                        GL.Vertex3(px, py, 0f);
                        GL.Vertex3(px + 1f, py, 0f);
                        GL.Vertex3(px + 1f, py + 1f, 0f);
                        GL.Vertex3(px, py + 1f, 0f);
                    }
                }
                GL.End();
                GL.PopMatrix();
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_cam != null) UnityEngine.Object.Destroy(_cam.gameObject);
            if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); }
            if (_resolve != null) { _resolve.Release(); UnityEngine.Object.Destroy(_resolve); }
            if (_add != null) UnityEngine.Object.Destroy(_add);
        }
    }
}
