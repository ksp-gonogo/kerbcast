using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kerbcast
{
    /* One frame's optics as the rig settled them, in the frame's pixels. */
    internal struct OpticsFrame
    {
        public bool Active;
        /* The whole blur as a Gaussian. */
        public double SigmaPx;
        public double BlurFwhmPx;
        /* The lens's part and the air's part of the blur (FWHM). */
        public double DiffFwhmPx;
        public double SeeingFwhmPx;
        /* Seeing's shift of the whole image this frame. */
        public float OffsetX, OffsetY;
        /* Seeing's brightness flicker, 1 for none. */
        public float Twinkle;
        /* Light left per channel after extinction. */
        public Color Tint;
        public double BrightnessHold;
        public double DaySky;
        public double DaySkyStrength;
        /* Radians one pixel covers at the frame's centre (the tangent per pixel). */
        public double PixelRad;
        /* How far the effect has faded in (OpticsModel.Ramp): scales every gain, the day sky, the tint and seeing's motion. */
        public double Ramp;
    }

    /* How the point-of-light marker draws under the optics. */
    internal struct MarkerOptics
    {
        public bool Active;
        public Texture Texture;
        public float SizePx;
        /* Multiplies the marker's colour per channel. */
        public Color Scale;
        public float OffsetX, OffsetY;
    }

    /*
     * Deep-space optics for one command feed: one blur for everything the lens
     * sees in space, applied to the bodies and craft and never to the stars.
     *
     * The scaled layer (every body) draws into its own half-float target
     * instead of over the galaxy. It is blurred by a pyramid of plain blits,
     * halving with bilinear box averages and doubling back, with two levels
     * mixed so any width can be matched (OpticsModel.PyramidPlan). The galaxy
     * is then darkened under each body's blurred disc and the blurred bodies
     * are added over it. A craft too small to resolve becomes the blur's own
     * shape, with the lens's diffraction rings, holding the light it reflects.
     * Only shaders the game already ships are used.
     */
    internal sealed class CommandCameraOptics : IDisposable
    {
        private const string Tag = "[Kerbcast][CMDCAM]";
        /* Spacing of the grid the body masks are drawn on. */
        private const int GridCellPx = 12;
        /* Seconds without an active frame before the targets are released. */
        private const float IdleReleaseSeconds = 10f;
        /* The craft's blur reaches this many total FWHM from its centre, enough for two Airy rings. */
        private const double SpriteRadiusFwhm = 3.5;
        private const double MinSpriteRadiusPx = 1.5;
        private const double MaxSpriteRadiusPx = 600;
        private const int ProfileSamples = 96;
        private const int SpriteRings = 48;
        private const int SpriteSegments = 32;
        private const int MarkerTexels = 64;
        /* A boosted body's disc reaches this far past its limb, so the grid never leaves its edge unboosted. */
        private const double BoostPadPx = 3.0;
        /* What a daylit sky adds, before the strength knob. */
        private static readonly Color DaySkyColor = new Color(0.22f, 0.34f, 0.56f, 0f);

        private static readonly int TintColorId = Shader.PropertyToID("_TintColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        /* Shared across feeds, made once. */
        private static bool _materialsTried;
        private static Material _darken, _multiply, _forceAlpha, _add, _texAdd;
        /* What _texAdd actually adds per unit of tint times texture, measured once on this GPU. */
        private static float _texAddScale;
        private static bool _texAddReversed;

        private readonly uint _flightId;
        private RenderTexture _scaledRt, _work;
        private readonly RenderTexture[] _down = new RenderTexture[OpticsModel.MaxPyramidLevel + 2];
        private readonly RenderTexture[] _up = new RenderTexture[OpticsModel.MaxPyramidLevel + 1];
        private int _w, _h;
        private RenderTexture _savedTarget;
        private bool _redirected;
        private float _lastActive;

        private Mesh _grid;
        private int _gridCols, _gridRows;
        private Color32[] _gridColors;
        private double[] _rayX, _rayY, _rayZ, _keep;
        private readonly List<Disc> _discs = new List<Disc>();
        private readonly double[] _table = new double[64];

        /* The craft's blur: a polar mesh whose vertex colours carry the profile, and the marker's texture of it. */
        private Mesh _sprite;
        private Color[] _spriteColors;
        private double _spriteEnergy;
        private Texture2D _markerTex;
        private double _markerMean;
        /* The profile's shape in radians (seeing against lens), and a count bumped on each rebuild that the mesh and texture compare against. */
        private double _profileShape = double.NaN;
        private int _shape, _spriteShape = -1, _markerShape = -1;
        private const double ShapeStep = 0.05;
        private double[] _profile;
        private static double _dotEnergy = double.NaN;

        /* CPU time the last composite took, smoothed, for the status log. */
        internal double CpuMs { get; private set; }
        internal int LastLevel { get; private set; }
        internal int LastBodies { get; private set; }
        internal float LastCraftGain { get; private set; }

        private struct Disc
        {
            public double Cx, Cy, Cz;
            public double Radius;
            public double Gain;
        }

        private CommandCameraOptics(uint flightId)
        {
            _flightId = flightId;
        }

        /* Whether this GPU can draw the effect: the shaders exist and passed their test draw. Main thread. */
        public static bool Available() => EnsureMaterials();

        /* OpticsModel.AirFactor for a lens `fromCentreM` from `body`'s centre: 0 above its air or on an airless body. */
        public static double AirAt(CelestialBody body, double fromCentreM)
        {
            if (body == null || !body.atmosphere) return 0;
            double alt = fromCentreM - body.Radius;
            return alt < body.atmosphereDepth ? OpticsModel.AirFactor(body.GetPressure(Math.Max(0.0, alt))) : 0;
        }

        /* Null when this GPU cannot draw the effect; the feed then renders as it always has. */
        private static bool _unavailableLogged;

        public static CommandCameraOptics Create(uint flightId)
        {
            if (!EnsureMaterials())
            {
                /* On by default, so a GPU that cannot draw them says so once, not for every camera. */
                if (!_unavailableLogged)
                {
                    _unavailableLogged = true;
                    Debug.LogWarning($"{Tag} cam={flightId} deep-space optics off: the shaders it needs are missing or failed their test draw; command cameras keep their plain look");
                }
                return null;
            }
            Debug.Log($"{Tag} cam={flightId} deep-space optics ready: additive shader '{_texAdd.shader.name}' scale={_texAddScale:G3}"
                + (_texAddReversed ? " (reversed winding)" : ""));
            return new CommandCameraOptics(flightId);
        }

        private static Material Colored(Shader shader, BlendMode src, BlendMode dst)
        {
            var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m.SetInt("_SrcBlend", (int)src);
            m.SetInt("_DstBlend", (int)dst);
            m.SetInt("_Cull", (int)CullMode.Off);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_ZTest", (int)CompareFunction.Always);
            return m;
        }

        private static bool EnsureMaterials()
        {
            if (_materialsTried) return _texAdd != null;
            _materialsTried = true;
            var colored = Shader.Find("Hidden/Internal-Colored");
            Shader additive = null;
            foreach (var name in new[] { "KSP/Particles/Additive", "Legacy Shaders/Particles/Additive", "Particles/Additive" })
            {
                additive = Shader.Find(name);
                if (additive != null) break;
            }
            if (colored == null || additive == null) return false;
            _darken = Colored(colored, BlendMode.Zero, BlendMode.OneMinusSrcAlpha);
            _multiply = Colored(colored, BlendMode.DstColor, BlendMode.One);
            _forceAlpha = Colored(colored, BlendMode.OneMinusDstAlpha, BlendMode.One);
            _add = Colored(colored, BlendMode.One, BlendMode.One);
            _texAdd = new Material(additive) { hideFlags = HideFlags.HideAndDontSave };
            if (MeasureTexAdd(false, out _texAddScale)) return true;
            if (MeasureTexAdd(true, out _texAddScale))
            {
                _texAddReversed = true;
                return true;
            }
            UnityEngine.Object.Destroy(_texAdd);
            _texAdd = null;
            return false;
        }

        /* One test draw of a grey texture through the additive shader, to learn its scale on this GPU (a particle shader doubles its tint). */
        private static bool MeasureTexAdd(bool reversed, out float scale)
        {
            scale = 0f;
            var prev = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D src = null, read = null;
            try
            {
                src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var grey = new Color32(128, 128, 128, 255);
                src.SetPixels32(new[] { grey, grey, grey, grey });
                src.Apply();
                rt = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                rt.Create();
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
                const float tint = 0.25f;
                DrawTextured(rt, src, _texAdd, new Color(tint, tint, tint, 0.5f), 0, 0, rt.width, rt.height, reversed);
                read = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                read.Apply();
                float got = read.GetPixel(1, 1).g;
                scale = got / (tint * (128f / 255f));
                return got > 2f / 255f;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} deep-space optics test draw threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                if (src != null) UnityEngine.Object.Destroy(src);
                if (read != null) UnityEngine.Object.Destroy(read);
            }
        }

        /* Points the scaled layer at the optics target, cleared, for this frame's render. */
        public void BeginScaled(Camera scaled, int width, int height)
        {
            if (scaled == null || width <= 0 || height <= 0 || _redirected) return;
            EnsureTargets(width, height);
            _savedTarget = scaled.targetTexture;
            scaled.targetTexture = _scaledRt;
            _redirected = true;
            var prev = RenderTexture.active;
            RenderTexture.active = _scaledRt;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            RenderTexture.active = prev;
        }

        /*
         * Hands the scaled layer back and draws the blurred bodies over the
         * galaxy already in `capture`. `home` is the body the lens stands by,
         * `toHome` its centre from the lens in metres; null when the pose is
         * not known.
         */
        public void EndScaled(Camera scaled, RenderTexture capture, OpticsFrame f, CelestialBody home, DVec3 toHome, PicturePass picture)
        {
            if (!_redirected) return;
            _redirected = false;
            if (scaled != null) scaled.targetTexture = _savedTarget;
            _savedTarget = null;
            if (capture == null || capture.width != _w || capture.height != _h) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var prev = RenderTexture.active;
            try
            {
                _lastActive = Time.realtimeSinceStartup;
                Composite(scaled, capture, f, home, toHome, picture);
            }
            finally
            {
                RenderTexture.active = prev;
            }
            Took(t0);
        }

        private void Took(long t0)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            CpuMs = CpuMs <= 0 ? ms : CpuMs + (ms - CpuMs) * 0.1;
        }

        /* Frees the targets once the feed has gone a while without the effect. */
        public void Idle()
        {
            if (_scaledRt == null || Time.realtimeSinceStartup - _lastActive < IdleReleaseSeconds) return;
            ReleaseTargets();
            CpuMs = 0;
            Debug.Log($"{Tag} cam={_flightId} deep-space optics targets released (idle)");
        }

        private void Composite(Camera scaled, RenderTexture capture, OpticsFrame f, CelestialBody home, DVec3 toHome, PicturePass picture)
        {
            CollectBodies(scaled, f, home, toHome, picture);

            /* Bodies smaller than the blur get some of their spread light back, on their own disc before the blur. */
            if (BodyGains(f, out float maxGain))
            {
                Rays(scaled, f, 0f, 0f);
                BoostColors(f, maxGain);
                float k = maxGain - 1f;
                DrawGrid(_scaledRt, _multiply, new Color(k, k, k, k));
            }
            /* The scaled shaders' own alpha means nothing here: every pixel is light, weighted 1. */
            FullScreen(_scaledRt, _forceAlpha, new Color(0f, 0f, 0f, 1f));

            OpticsModel.PyramidPlan(f.SigmaPx, OpticsModel.MaxLevelFor(_w, _h), out int level, out double share);
            LastLevel = level;
            float a = (float)(1.0 - share), b = (float)share;
            /* A planet is too wide to twinkle; only craft flicker. */
            var tint = new Color(f.Tint.r, f.Tint.g, f.Tint.b, 1f);

            Graphics.Blit(capture, _work);
            if (_discs.Count > 0)
            {
                Rays(scaled, f, f.OffsetX, f.OffsetY);
                if (MaskColors(f)) DrawGrid(_work, _darken, Color.white);
            }

            Texture top = _scaledRt;
            for (int j = 1; j <= level + 1; j++)
            {
                Graphics.Blit(top, _down[j]);
                top = _down[j];
            }
            if (level == 0)
            {
                AddStretched(_work, _scaledRt, tint, a, f.OffsetX, f.OffsetY);
                AddStretched(_work, _down[1], tint, b, f.OffsetX, f.OffsetY);
            }
            else
            {
                /* Mix the two levels where the finer one lives, then double back up to the frame. */
                var mix = _up[level];
                RenderTexture.active = mix;
                GL.Clear(false, true, new Color(0f, 0f, 0f, 1f));
                AddStretched(mix, _down[level], Color.white, a, 0f, 0f);
                AddStretched(mix, _down[level + 1], Color.white, b, 0f, 0f);
                Texture up = mix;
                for (int j = level - 1; j >= 1; j--)
                {
                    Graphics.Blit(up, _up[j]);
                    up = _up[j];
                }
                AddStretched(_work, up, tint, 1f, f.OffsetX, f.OffsetY);
            }

            float day = (float)(f.DaySky * f.DaySkyStrength);
            if (day > 0f) FullScreen(_work, _add, DaySkyColor * day);
            Graphics.Blit(_work, capture);
        }

        /* Each body that could reach the frame, as a direction from the lens and an angular radius. */
        private void CollectBodies(Camera scaled, OpticsFrame f, CelestialBody home, DVec3 toHome, PicturePass picture)
        {
            _discs.Clear();
            LastBodies = 0;
            var bodies = FlightGlobals.Bodies;
            if (bodies == null || scaled == null) return;
            var t = scaled.transform;
            Vector3 eye = t.position, axis = t.forward;
            double sigma = f.SigmaPx * f.PixelRad;
            double halfDiag = Math.Atan(Math.Sqrt(_w * (double)_w + _h * (double)_h) * 0.5 * f.PixelRad);
            double inv = ScaledSpace.InverseScaleFactor;
            for (int i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                /* A body the picture leaves out has no disc to blur, and its transform is where the body is now. */
                if (body == null || body.scaledBody == null || (picture != null && picture.HidesBody(body))) continue;
                double d, cx, cy, cz, rho;
                if (body == home)
                {
                    /* A float scaled position rounds to hundreds of metres far from the origin, more than a lens's height above its ground, so the home body is placed in double. */
                    d = toHome.Magnitude;
                    if (!(d > 0)) continue;
                    cx = toHome.X / d;
                    cy = toHome.Y / d;
                    cz = toHome.Z / d;
                    rho = OpticsModel.HomeAngularRadius(d, body.Radius);
                }
                else
                {
                    var c = body.scaledBody.transform.position - eye;
                    d = c.magnitude;
                    double r = body.Radius * inv;
                    if (!(d > r * 1.0001)) continue;
                    cx = c.x / d;
                    cy = c.y / d;
                    cz = c.z / d;
                    rho = Math.Asin(r / d);
                }
                double off = Angle(cx, cy, cz, axis.x, axis.y, axis.z);
                if (off - rho - 6.0 * sigma > halfDiag) continue;
                _discs.Add(new Disc { Cx = cx, Cy = cy, Cz = cz, Radius = rho, Gain = 1.0 });
            }
            LastBodies = _discs.Count;
        }

        /* Angle between two unit vectors, accurate for tiny angles. */
        private static double Angle(double ax, double ay, double az, double bx, double by, double bz)
        {
            double x = ay * bz - az * by, y = az * bx - ax * bz, z = ax * by - ay * bx;
            return Math.Atan2(Math.Sqrt(x * x + y * y + z * z), ax * bx + ay * by + az * bz);
        }

        /* Squared chord between a vertex's ray and a body's centre: exact for tiny angles, unlike 1 - cos. */
        private double Chord2(int v, Disc d)
        {
            double x = _rayX[v] - d.Cx, y = _rayY[v] - d.Cy, z = _rayZ[v] - d.Cz;
            return x * x + y * y + z * z;
        }

        /* The squared chord of `angle` radians; 4 (opposite) at or past pi. */
        private static double Chord2(double angle)
        {
            if (angle >= Math.PI) return 4.0;
            double c = 2.0 * Math.Sin(Math.Max(0.0, angle) * 0.5);
            return c * c;
        }

        private bool BodyGains(OpticsFrame f, out float maxGain)
        {
            double sigma = f.SigmaPx * f.PixelRad;
            double product = 1.0;
            for (int i = 0; i < _discs.Count; i++)
            {
                var d = _discs[i];
                double g = OpticsModel.Gain(OpticsModel.DiscPeak(d.Radius, sigma), f.BrightnessHold, OpticsModel.BodyBoost, OpticsModel.BodyGainCap);
                d.Gain = 1.0 + (OpticsModel.DayGain(g, f.DaySky) - 1.0) * f.Ramp;
                _discs[i] = d;
                product *= d.Gain;
            }
            maxGain = (float)product;
            return product > 1.01;
        }

        /* Unit rays through every grid vertex, in the scaled camera's world axes, with seeing's shift taken out. */
        private void Rays(Camera scaled, OpticsFrame f, float dx, float dy)
        {
            var t = scaled.transform;
            Vector3 fw = t.forward, rt = t.right, up = t.up;
            double cellX = _w / (double)(_gridCols - 1), cellY = _h / (double)(_gridRows - 1);
            int v = 0;
            for (int j = 0; j < _gridRows; j++)
            {
                double sy = (j * cellY - _h * 0.5 - dy) * f.PixelRad;
                for (int i = 0; i < _gridCols; i++, v++)
                {
                    double sx = (i * cellX - _w * 0.5 - dx) * f.PixelRad;
                    double x = fw.x + rt.x * sx + up.x * sy, y = fw.y + rt.y * sx + up.y * sy, z = fw.z + rt.z * sx + up.z * sy;
                    double n = 1.0 / Math.Sqrt(x * x + y * y + z * z);
                    _rayX[v] = x * n;
                    _rayY[v] = y * n;
                    _rayZ[v] = z * n;
                }
            }
        }

        /* A body's blurred coverage against angle from its centre, tabulated over the band where it changes. */
        private void Tabulate(double radius, double sigma, out double lo, out double step)
        {
            lo = radius > 7.0 * sigma ? radius - 7.0 * sigma : 0.0;
            double hi = radius + 7.0 * sigma;
            step = (hi - lo) / (_table.Length - 1);
            for (int i = 0; i < _table.Length; i++) _table[i] = OpticsModel.DiscCoverage(lo + i * step, radius, sigma);
        }

        private double Lookup(double angle, double lo, double step)
        {
            double x = (angle - lo) / step;
            if (x <= 0) return _table[0];
            int i = (int)x;
            if (i >= _table.Length - 1) return 0.0;
            double t = x - i;
            return _table[i] + (_table[i + 1] - _table[i]) * t;
        }

        /* Alpha per vertex: how much of the galaxy every body hides there, blurred. False when nothing is hidden. */
        private bool MaskColors(OpticsFrame f)
        {
            double sigma = f.SigmaPx * f.PixelRad;
            int n = _gridColors.Length;
            var keep = _keep;
            for (int v = 0; v < n; v++) keep[v] = 1.0;
            bool any = false;
            foreach (var d in _discs)
            {
                Tabulate(d.Radius, sigma, out double lo, out double step);
                double hi = lo + step * (_table.Length - 1);
                double outer = Chord2(hi), inner = Chord2(lo);
                for (int v = 0; v < n; v++)
                {
                    double c2 = Chord2(v, d);
                    if (c2 >= outer) continue;
                    double cover = c2 <= inner ? _table[0] : Lookup(2.0 * Math.Asin(Math.Min(1.0, Math.Sqrt(c2) * 0.5)), lo, step);
                    if (cover > 0) { keep[v] *= 1.0 - cover; any = true; }
                }
            }
            for (int v = 0; v < n; v++)
                _gridColors[v] = new Color32(0, 0, 0, (byte)Math.Round(255.0 * (1.0 - keep[v])));
            return any;
        }

        /* Per vertex, each body's extra gain over its own disc (padded past the limb), as a share of `maxGain` - 1. */
        private void BoostColors(OpticsFrame f, float maxGain)
        {
            int n = _gridColors.Length;
            double pad = (BoostPadPx + GridCellPx * 1.5) * f.PixelRad;
            double inv = maxGain > 1.0001f ? 1.0 / (maxGain - 1.0) : 0.0;
            for (int v = 0; v < n; v++)
            {
                double factor = 1.0;
                foreach (var d in _discs)
                    if (d.Gain > 1.0 && Chord2(v, d) <= Chord2(d.Radius + pad)) factor *= d.Gain;
                byte c = (byte)Math.Round(255.0 * Math.Max(0.0, Math.Min(1.0, (factor - 1.0) * inv)));
                _gridColors[v] = new Color32(c, c, c, c);
            }
        }

        private void DrawGrid(RenderTexture dst, Material m, Color tint)
        {
            _grid.colors32 = _gridColors;
            RenderTexture.active = dst;
            m.SetColor(ColorId, tint);
            m.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, dst.width, 0, dst.height);
            Graphics.DrawMeshNow(_grid, Matrix4x4.identity);
            GL.PopMatrix();
        }

        private static void FullScreen(RenderTexture dst, Material m, Color c)
        {
            RenderTexture.active = dst;
            m.SetColor(ColorId, Color.white);
            m.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, dst.width, 0, dst.height);
            GL.Begin(GL.QUADS);
            GL.Color(c);
            GL.Vertex3(0, 0, 0);
            GL.Vertex3(dst.width, 0, 0);
            GL.Vertex3(dst.width, dst.height, 0);
            GL.Vertex3(0, dst.height, 0);
            GL.End();
            GL.PopMatrix();
        }

        /* Adds `src` stretched over all of `dst`, shifted by (dx, dy) pixels, times `tint` and `weight`. */
        private static void AddStretched(RenderTexture dst, Texture src, Color tint, float weight, float dx, float dy)
        {
            if (weight <= 0f) return;
            float k = weight / _texAddScale;
            OpticsModel.OverscanAxis(dx, dst.width, out float x0, out float x1, out float u0, out float u1);
            OpticsModel.OverscanAxis(dy, dst.height, out float y0, out float y1, out float v0, out float v1);
            DrawTextured(dst, src, _texAdd, new Color(tint.r * k, tint.g * k, tint.b * k, 0.5f),
                x0, y0, x1, y1, _texAddReversed, u0, v0, u1, v1);
        }

        private static void DrawTextured(RenderTexture dst, Texture src, Material m, Color tint, float x0, float y0, float x1, float y1,
            bool reversed, float u0 = 0f, float v0 = 0f, float u1 = 1f, float v1 = 1f)
        {
            RenderTexture.active = dst;
            m.mainTexture = src;
            m.SetColor(TintColorId, tint);
            m.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, dst.width, 0, dst.height);
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            if (!reversed)
            {
                GL.TexCoord2(u0, v0); GL.Vertex3(x0, y0, 0);
                GL.TexCoord2(u1, v0); GL.Vertex3(x1, y0, 0);
                GL.TexCoord2(u1, v1); GL.Vertex3(x1, y1, 0);
                GL.TexCoord2(u0, v1); GL.Vertex3(x0, y1, 0);
            }
            else
            {
                GL.TexCoord2(u0, v0); GL.Vertex3(x0, y0, 0);
                GL.TexCoord2(u0, v1); GL.Vertex3(x0, y1, 0);
                GL.TexCoord2(u1, v1); GL.Vertex3(x1, y1, 0);
                GL.TexCoord2(u1, v0); GL.Vertex3(x1, y0, 0);
            }
            GL.End();
            GL.PopMatrix();
        }

        /*
         * Draws a craft too small to resolve as the blur's own shape at the
         * frame's centre, onto `target`, holding `light` (each channel's sum
         * over the pixels it covered, `coverPx` of them). `acrossPx` widens the
         * blur by the craft's own size, so the handover to the drawn model
         * stays close.
         */
        public void DrawCraft(RenderTexture target, Color light, double coverPx, double acrossPx, OpticsFrame f)
        {
            if (target == null || !f.Active) return;
            if (light.r + light.g + light.b <= 0f) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double radius = SpriteRadius(f, acrossPx, out double total);
            EnsureProfile(f, acrossPx, total);
            if (_sprite == null || _spriteShape != _shape) BuildSprite();
            double area = _spriteEnergy * radius * radius;
            double ratio = Math.Max(1.0, coverPx) / area;
            double g = CraftGain(ratio, f);
            LastCraftGain = (float)g;
            float k = (float)(g / area) * f.Twinkle;
            var tint = new Color(light.r * k * f.Tint.r, light.g * k * f.Tint.g, light.b * k * f.Tint.b, 0f);
            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                _add.SetColor(ColorId, tint);
                _add.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, target.width, 0, target.height);
                var at = new Vector3(target.width * 0.5f + f.OffsetX, target.height * 0.5f + f.OffsetY, 0f);
                Graphics.DrawMeshNow(_sprite, Matrix4x4.TRS(at, Quaternion.identity, new Vector3((float)radius, (float)radius, 1f)));
                GL.PopMatrix();
            }
            finally
            {
                RenderTexture.active = prev;
                _add.SetColor(ColorId, Color.white);
            }
            Took(t0);
        }

        /* The point-of-light marker as the blur's shape, holding the light of its usual 3 pixel dot. */
        public MarkerOptics Marker(OpticsFrame f, float dotSizePx)
        {
            if (!f.Active) return default;
            double radius = SpriteRadius(f, 0, out double total);
            EnsureProfile(f, 0, total);
            if (_markerTex == null || _markerShape != _shape) BuildMarkerTexture();
            if (double.IsNaN(_dotEnergy)) _dotEnergy = CommandCameraMarker.DotEnergy();
            double size = 2.0 * radius;
            double ratio = dotSizePx * dotSizePx * _dotEnergy / (size * size * _markerMean);
            double g = CraftGain(ratio, f);
            LastCraftGain = (float)g;
            float s = (float)(Math.Min(1.0, ratio) * g) * f.Twinkle;
            return new MarkerOptics
            {
                Active = true,
                Texture = _markerTex,
                SizePx = (float)size,
                Scale = new Color(s * f.Tint.r, s * f.Tint.g, s * f.Tint.b, 1f),
                OffsetX = f.OffsetX,
                OffsetY = f.OffsetY,
            };
        }

        /* A craft's gain for a peak `ratio` of its sharp one, faded in with the effect. */
        private static double CraftGain(double ratio, OpticsFrame f)
        {
            double g = OpticsModel.DayGain(OpticsModel.Gain(ratio, f.BrightnessHold * OpticsModel.CraftHoldShare, 0.0, OpticsModel.CraftGainCap), f.DaySky);
            return 1.0 + (g - 1.0) * f.Ramp;
        }

        private static double SpriteRadius(OpticsFrame f, double acrossPx, out double total)
        {
            double seeing = Math.Sqrt(f.SeeingFwhmPx * f.SeeingFwhmPx + Math.Pow(0.59 * Math.Max(0.0, acrossPx), 2));
            total = Math.Sqrt(f.DiffFwhmPx * f.DiffFwhmPx + seeing * seeing);
            return Math.Max(MinSpriteRadiusPx, Math.Min(MaxSpriteRadiusPx, SpriteRadiusFwhm * total));
        }

        /* Recomputes the craft's blur profile when its shape (the share of seeing against the lens) has moved enough to see; the mesh and the marker texture follow it only when drawn. */
        private void EnsureProfile(OpticsFrame f, double acrossPx, double total)
        {
            double seeing = Math.Sqrt(f.SeeingFwhmPx * f.SeeingFwhmPx + Math.Pow(0.59 * Math.Max(0.0, acrossPx), 2));
            double shape = Math.Atan2(seeing, Math.Max(f.DiffFwhmPx, 1e-9));
            if (_profile != null && Math.Abs(shape - _profileShape) < ShapeStep) return;
            _profileShape = shape;
            _shape++;
            double t = Math.Max(total, 1e-9);
            _profile = OpticsModel.PsfProfile(f.DiffFwhmPx / t, seeing / t, SpriteRadiusFwhm * Math.Sqrt(2.0), ProfileSamples);
        }

        private double Profile(double r)
        {
            double x = r / (SpriteRadiusFwhm * Math.Sqrt(2.0)) * (_profile.Length - 1);
            if (x <= 0) return _profile[0];
            int i = (int)x;
            if (i >= _profile.Length - 1) return 0.0;
            return _profile[i] + (_profile[i + 1] - _profile[i]) * (x - i);
        }

        /* A unit disc of rings and spokes; vertex colours carry the profile and a fade to zero at the rim. */
        private void BuildSprite()
        {
            int nv = 1 + SpriteRings * SpriteSegments;
            if (_sprite == null)
            {
                var pos = new Vector3[nv];
                pos[0] = Vector3.zero;
                for (int r = 1; r <= SpriteRings; r++)
                {
                    float rr = r / (float)SpriteRings;
                    for (int s = 0; s < SpriteSegments; s++)
                    {
                        float a = s * 2f * Mathf.PI / SpriteSegments;
                        pos[1 + (r - 1) * SpriteSegments + s] = new Vector3(rr * Mathf.Cos(a), rr * Mathf.Sin(a), 0f);
                    }
                }
                var tris = new List<int>();
                for (int s = 0; s < SpriteSegments; s++)
                {
                    int s1 = (s + 1) % SpriteSegments;
                    tris.Add(0); tris.Add(1 + s); tris.Add(1 + s1);
                }
                for (int r = 1; r < SpriteRings; r++)
                {
                    int a0 = 1 + (r - 1) * SpriteSegments, b0 = 1 + r * SpriteSegments;
                    for (int s = 0; s < SpriteSegments; s++)
                    {
                        int s1 = (s + 1) % SpriteSegments;
                        tris.Add(a0 + s); tris.Add(b0 + s); tris.Add(b0 + s1);
                        tris.Add(a0 + s); tris.Add(b0 + s1); tris.Add(a0 + s1);
                    }
                }
                _sprite = new Mesh { name = $"Kerbcast_{_flightId}_OpticsCraft" };
                _sprite.MarkDynamic();
                _sprite.vertices = pos;
                _sprite.triangles = tris.ToArray();
                _spriteColors = new Color[nv];
            }
            /* Energy of the drawn profile over the unit disc, as the triangles interpolate it. */
            double energy = 0;
            _spriteColors[0] = Color.white * (float)Profile(0);
            double prevV = Profile(0), prevR = 0;
            for (int r = 1; r <= SpriteRings; r++)
            {
                double rr = r / (double)SpriteRings;
                double fade = r == SpriteRings ? 0.0 : 1.0;
                double v = Profile(rr * SpriteRadiusFwhm) * fade;
                for (int s = 0; s < SpriteSegments; s++) _spriteColors[1 + (r - 1) * SpriteSegments + s] = new Color((float)v, (float)v, (float)v, (float)v);
                energy += Math.PI * (rr - prevR) * (prevV * (2 * prevR + rr) + v * (prevR + 2 * rr)) / 3.0;
                prevV = v;
                prevR = rr;
            }
            _sprite.colors = _spriteColors;
            _spriteEnergy = Math.Max(energy, 1e-6);
            _spriteShape = _shape;
        }

        /* The marker's texture of the same blur: colour and alpha both the square root, since the marker's shader multiplies them. */
        private void BuildMarkerTexture()
        {
            if (_markerTex == null)
                _markerTex = new Texture2D(MarkerTexels, MarkerTexels, TextureFormat.RGBA32, false)
                {
                    name = $"Kerbcast_{_flightId}_OpticsMarker",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            double c = (MarkerTexels - 1) * 0.5, sum = 0;
            var px = new Color32[MarkerTexels * MarkerTexels];
            for (int y = 0; y < MarkerTexels; y++)
            {
                for (int x = 0; x < MarkerTexels; x++)
                {
                    double r = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (MarkerTexels * 0.5) * SpriteRadiusFwhm;
                    double p = r >= SpriteRadiusFwhm ? 0.0 : Profile(r);
                    byte q = (byte)Math.Round(255.0 * Math.Sqrt(Math.Max(0.0, p)));
                    px[y * MarkerTexels + x] = new Color32(q, q, q, q);
                    sum += (q / 255.0) * (q / 255.0);
                }
            }
            _markerTex.SetPixels32(px);
            _markerTex.Apply(false, false);
            _markerMean = Math.Max(sum / px.Length, 1e-6);
            _markerShape = _shape;
        }

        private static RenderTexture Target(int w, int h, int depth, string name)
        {
            var rt = new RenderTexture(Math.Max(1, w), Math.Max(1, h), depth, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
            };
            rt.Create();
            return rt;
        }

        private void EnsureTargets(int w, int h)
        {
            if (_scaledRt != null && _w == w && _h == h) return;
            ReleaseTargets();
            _w = w;
            _h = h;
            string n = $"Kerbcast_{_flightId}_Optics";
            _scaledRt = Target(w, h, 24, n + "Scaled");
            _work = Target(w, h, 0, n + "Work");
            for (int j = 1; j < _down.Length; j++) _down[j] = Target(w >> j, h >> j, 0, $"{n}Down{j}");
            for (int j = 1; j < _up.Length; j++) _up[j] = Target(w >> j, h >> j, 0, $"{n}Up{j}");
            BuildGrid();
        }

        private void BuildGrid()
        {
            _gridCols = Math.Max(2, (_w + GridCellPx - 1) / GridCellPx + 1);
            _gridRows = Math.Max(2, (_h + GridCellPx - 1) / GridCellPx + 1);
            int n = _gridCols * _gridRows;
            var pos = new Vector3[n];
            float cellX = _w / (float)(_gridCols - 1), cellY = _h / (float)(_gridRows - 1);
            for (int j = 0, v = 0; j < _gridRows; j++)
                for (int i = 0; i < _gridCols; i++, v++)
                    pos[v] = new Vector3(i * cellX, j * cellY, 0f);
            var tris = new int[(_gridCols - 1) * (_gridRows - 1) * 6];
            int t = 0;
            for (int j = 0; j < _gridRows - 1; j++)
            {
                for (int i = 0; i < _gridCols - 1; i++)
                {
                    int v0 = j * _gridCols + i, v1 = v0 + 1, v2 = v0 + _gridCols, v3 = v2 + 1;
                    tris[t++] = v0; tris[t++] = v2; tris[t++] = v1;
                    tris[t++] = v1; tris[t++] = v2; tris[t++] = v3;
                }
            }
            _grid = new Mesh { name = $"Kerbcast_{_flightId}_OpticsGrid" };
            _grid.MarkDynamic();
            _grid.vertices = pos;
            _grid.triangles = tris;
            _gridColors = new Color32[n];
            _rayX = new double[n];
            _rayY = new double[n];
            _rayZ = new double[n];
            _keep = new double[n];
        }

        private void ReleaseTargets()
        {
            Free(ref _scaledRt);
            Free(ref _work);
            for (int j = 0; j < _down.Length; j++) Free(ref _down[j]);
            for (int j = 0; j < _up.Length; j++) Free(ref _up[j]);
            if (_grid != null) UnityEngine.Object.Destroy(_grid);
            _grid = null;
            _w = _h = 0;
        }

        private static void Free(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            rt = null;
        }

        public void Dispose()
        {
            ReleaseTargets();
            if (_sprite != null) UnityEngine.Object.Destroy(_sprite);
            _sprite = null;
            if (_markerTex != null) UnityEngine.Object.Destroy(_markerTex);
            _markerTex = null;
        }
    }
}
