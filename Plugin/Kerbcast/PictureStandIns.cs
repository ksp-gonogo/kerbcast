using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Kerbcast
{
    /*
     * One camera's stand-ins for every craft the scene does not draw where
     * its light shows it: loaded craft hidden beyond the boundary, those it
     * hid that have since left the scene, and every unloaded craft the cull
     * keeps. Each is drawn at its past instant: under
     * PictureCull.PointBelowPx across as a point of light, all of the
     * camera's points in one draw, otherwise as a part model, up to
     * PictureCull.ModelCap models a camera, the largest first, the rest as
     * points. Nothing is drawn for a craft behind a body or in shadow at its
     * drawn place. Everything is switched on only around the camera's own
     * local layers.
     */
    internal sealed class CraftStandIns : IDisposable
    {
        private readonly PicturePass _pass;
        private readonly List<uint> _candidates = new List<uint>();
        private readonly List<Drawn> _drawn = new List<Drawn>();
        private readonly List<CommandCameraTargetModel> _shown = new List<CommandCameraTargetModel>();
        private PointBatch _points;
        private bool _pointsFailed;
        private bool _pointsOn;
        private CullView _view;
        private DVec3[] _bodyCentres;
        private double[] _bodyRadii;

        private struct Drawn
        {
            public uint Pid;
            public PastPicture Seen;
            public double Distance;
            public double RadiusPx;
            public double PhaseCos;
            public CommandCameraTargetModel Model;
        }

        private sealed class Larger : IComparer<Drawn>
        {
            public int Compare(Drawn a, Drawn b) => b.RadiusPx.CompareTo(a.RadiusPx);
        }

        private static readonly Larger s_larger = new Larger();

        public CraftStandIns(PicturePass pass)
        {
            _pass = pass;
        }

        /* Every craft this frame's Prepare solved, by persistentId. */
        public List<uint> Solved => _candidates;

        /*
         * Before the frame renders: solves each craft to draw and decides how,
         * and builds its models, outside the render so nothing is cloned while
         * switched off.
         * `hider` says which loaded craft this camera hides and which the
         * scene draws itself.
         */
        public void Prepare(CraftHider hider)
        {
            var hidden = hider.HiddenPids;
            var rigDraws = hider.RigDrawn;
            _drawn.Clear();
            _candidates.Clear();
            if (!LightTime.LightSpeed.HasValue) return;
            var cam = _pass.View;
            if (cam == null) return;
            long t0 = Stopwatch.GetTimestamp();
            var t = cam.transform;
            var rt = _pass.Target ?? cam.targetTexture;
            int rows = rt != null ? rt.height : cam.pixelHeight;
            double aspect = rt != null && rt.height > 0 ? (double)rt.width / rt.height : cam.aspect;
            _view = CullView.Of(ToD(t.position), ToD(t.forward), ToD(t.up), cam.fieldOfView, aspect, rows);

            for (int i = 0; i < hidden.Count; i++)
                if (!rigDraws.Draws(hidden[i])) _candidates.Add(hidden[i]);
            hider.Lingering.Collect(hider.SceneDrawn, rigDraws, _candidates);
            int fromHidden = _candidates.Count;
            int culled = _pass.Candidates(_view, rigDraws, hider, _candidates);
            long t1 = Stopwatch.GetTimestamp();

            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            int bodyCount = _candidates.Count > 0 ? _pass.DrawnBodies(ref _bodyCentres, ref _bodyRadii) : 0;
            int sunIndex = sun != null && FlightGlobals.Bodies != null ? FlightGlobals.Bodies.IndexOf(sun) : -1;
            for (int i = 0; i < _candidates.Count; i++)
            {
                uint pid = _candidates[i];
                if (!_pass.TryPast(pid, out var seen, out _))
                {
                    /* Only a solve from a real lens counts against a lingering craft. */
                    if (_pass.TryLens(out _)) hider.Lingering.Missed(pid, hider.SceneDrawn);
                    continue;
                }
                hider.Lingering.Seen(pid);
                if (rigDraws.Draws(pid, seen.Pid)) continue;
                if (seen.Pid == pid && !hider.SceneDraws(pid) && hider.Lingering.Contains(pid) && _pass.FliesOn(pid)) hider.Lingering.LeaveToCull(pid);
                double size = seen.SizeM > 0 ? seen.SizeM : DrawnSubject.MinRadius;
                var toward = seen.World - _view.Eye;
                double d = toward.Magnitude;
                double radiusPx = PictureCull.RadiusPx(size, d, _view.RadPerPxToward(toward));
                if (radiusPx < PictureCull.CutoffPx || PictureCull.OutOfView(_view, seen.World, size)) continue;
                if (!DrawnSpheres.Shown(_view.Eye, seen.World, _bodyCentres, _bodyRadii, bodyCount, sunIndex)) continue;
                double phase = 1;
                if (sun != null)
                {
                    var toSun = (ToD(sun.position) - seen.World).Normalized;
                    phase = DVec3.Dot(toSun, (toward * -1).Normalized);
                }
                _drawn.Add(new Drawn
                {
                    Pid = seen.Pid != 0 ? seen.Pid : pid, Seen = seen, Distance = d, RadiusPx = radiusPx, PhaseCos = phase,
                });
            }
            /* The largest get the models, built here so nothing is cloned while
               switched off, and at most one built a frame: the rest are points
               until their turn. */
            _drawn.Sort(s_larger);
            int models = 0;
            bool mayBuild = true;
            for (int i = 0; i < _drawn.Count && models < PictureCull.ModelCap; i++)
            {
                var c = _drawn[i];
                if (2 * c.RadiusPx < PictureCull.PointBelowPx) break;
                if (c.Seen.Parts == null) continue;
                c.Model = StandIns.For(c.Pid, c.Seen.Parts, ref mayBuild);
                if (c.Model == null) continue;
                _drawn[i] = c;
                models++;
            }
            StandInCost.Prepared(t1 - t0, Stopwatch.GetTimestamp() - t1, _candidates.Count - fromHidden, culled, _drawn.Count);
        }

        /* Switches this frame's stand-ins on. */
        public void Show()
        {
            if (_drawn.Count == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            int models = 0, points = 0;
            var cam = _pass.View;
            if (_points == null && !_pointsFailed && cam != null)
            {
                _points = PointBatch.Create(cam);
                _pointsFailed = _points == null;
            }
            _points?.Begin();
            for (int i = 0; i < _drawn.Count; i++)
            {
                var c = _drawn[i];
                if (c.Model != null && !c.Model.Gone)
                {
                    /* The history follows the centre of mass; the model hangs from its root part. */
                    var at = PictureBoundary.Proxy(_view.Eye, c.Seen.World, out double scale);
                    var com = c.Seen.Rotation * new Vector3(c.Seen.Parts.ComX, c.Seen.Parts.ComY, c.Seen.Parts.ComZ) * (float)scale;
                    c.Model.Place(true, at - new DVec3(com.x, com.y, com.z), c.Seen.Rotation, (float)scale);
                    _shown.Add(c.Model);
                    c.Model.SetVisible(true, false);
                    models++;
                    continue;
                }
                if (_points == null) continue;
                var toward = (c.Seen.World - _view.Eye) * (1.0 / c.Distance);
                double across = Math.Max(PointBatch.MinPx, 2 * c.RadiusPx);
                _points.Add(_view, toward, c.Distance, across, (float)PictureCull.PointBrightness(c.RadiusPx, c.PhaseCos, across));
                points++;
            }
            if (_points != null && points > 0)
            {
                _points.End();
                _points.SetVisible(true);
                _pointsOn = true;
            }
            StandInCost.Shown(Stopwatch.GetTimestamp() - t0, models, points);
        }

        /* Undoes Show exactly, however far it got. */
        public void Restore()
        {
            for (int i = 0; i < _shown.Count; i++)
                if (!_shown[i].Gone) _shown[i].SetVisible(false, false);
            _shown.Clear();
            if (_pointsOn) _points?.SetVisible(false);
            _pointsOn = false;
        }

        public void Dispose()
        {
            _points?.Dispose();
            _points = null;
        }

        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
        private static DVec3 ToD(Vector3 v) => new DVec3(v.x, v.y, v.z);
    }

    /*
     * One camera's points of light as one mesh of camera-facing quads, drawn
     * in one call. Each sits on its true line of sight a few km out, sized to
     * subtend its pixels, so it is inside the local layers' range with
     * ordinary float precision, as the command camera's marker does.
     */
    internal sealed class PointBatch : IDisposable
    {
        public const double MinPx = 2.0;
        private const double PreferredDistance = 5000.0;
        private const double MinUlps = 4.0;
        private static readonly Color Warm = new Color(1f, 0.94f, 0.82f, 1f);
        private static readonly int TintColorId = Shader.PropertyToID("_TintColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private readonly GameObject _go;
        private readonly MeshRenderer _renderer;
        private readonly Mesh _mesh;
        private readonly Material _material;
        private readonly Texture2D _texture;
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<Color> _colors = new List<Color>();
        private readonly List<int> _triangles = new List<int>();

        private PointBatch(int layer, Shader shader)
        {
            _texture = CommandCameraMarker.BuildDot();
            _material = new Material(shader) { mainTexture = _texture };
            if (_material.HasProperty(TintColorId)) _material.SetColor(TintColorId, new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (_material.HasProperty(ColorId)) _material.SetColor(ColorId, Color.white);
            _mesh = new Mesh { name = "Kerbcast_PointsOfLight" };
            _mesh.MarkDynamic();
            _go = new GameObject("Kerbcast_PointsOfLight") { layer = layer };
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.enabled = false;
        }

        /* Null without a layer the camera draws or a usable shader: the camera then draws no points. */
        public static PointBatch Create(Camera cam)
        {
            int layer = CommandCameraMarker.PickLayer(cam.cullingMask);
            if (layer < 0) return null;
            Shader shader = null;
            foreach (var name in CommandCameraMarker.ShaderNames)
            {
                shader = Shader.Find(name);
                if (shader != null) break;
            }
            if (shader == null)
            {
                Debug.LogWarning($"[Kerbcast][PICTURE] points of light off: none of {string.Join(", ", CommandCameraMarker.ShaderNames)} found");
                return null;
            }
            return new PointBatch(layer, shader);
        }

        public void Begin()
        {
            _vertices.Clear();
            _uv.Clear();
            _colors.Clear();
            _triangles.Clear();
        }

        /* A point `rangeM` along unit `toward` from the view's eye, `acrossPx` wide, at `brightness` 0 to 1. */
        public void Add(in CullView view, DVec3 toward, double rangeM, double acrossPx, float brightness)
        {
            double sizeAtUnit = view.RadPerPxToward(toward) * acrossPx;
            double extent = view.Eye.Magnitude + PreferredDistance;
            double d = CommandCameraMath.ImpostorDistance(rangeM, PreferredDistance, sizeAtUnit, extent, MinUlps);
            var p = view.Eye + toward * d;
            double half = 0.5 * sizeAtUnit * d;
            var right = view.Right * half;
            var up = view.Up * half;
            int b = _vertices.Count;
            _vertices.Add(F(p - right - up));
            _vertices.Add(F(p + right - up));
            _vertices.Add(F(p - right + up));
            _vertices.Add(F(p + right + up));
            _uv.Add(new Vector2(0, 0));
            _uv.Add(new Vector2(1, 0));
            _uv.Add(new Vector2(0, 1));
            _uv.Add(new Vector2(1, 1));
            /* Brightness in colour only: the additive shader multiplies by alpha too. */
            var c = new Color(Warm.r * brightness, Warm.g * brightness, Warm.b * brightness, 1f);
            for (int i = 0; i < 4; i++) _colors.Add(c);
            /* Both windings, so a quad shows whichever way culling runs. */
            _triangles.Add(b); _triangles.Add(b + 2); _triangles.Add(b + 1);
            _triangles.Add(b + 2); _triangles.Add(b + 3); _triangles.Add(b + 1);
            _triangles.Add(b); _triangles.Add(b + 1); _triangles.Add(b + 2);
            _triangles.Add(b + 2); _triangles.Add(b + 1); _triangles.Add(b + 3);
        }

        public void End()
        {
            _mesh.Clear();
            if (_vertices.Count > 65000) _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _mesh.SetVertices(_vertices);
            _mesh.SetUVs(0, _uv);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e9f);
        }

        public void SetVisible(bool on)
        {
            if (_renderer != null) _renderer.enabled = on;
        }

        public void Dispose()
        {
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
        }

        private static Vector3 F(DVec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
    }

    /*
     * Stand-in models built from the history's parts, shared by every camera:
     * one per craft id and set of parts, so cameras whose light shows a craft
     * on either side of a staging each keep their own rather than rebuilding
     * one every frame. Each camera places one and switches it on around its
     * own local layers only. A model unused for a while is reused for another
     * set of the same craft's parts, or dropped.
     */
    internal static class StandIns
    {
        private const float DropAfterS = 10f;
        private const float ReuseAfterS = 1f;
        private const float RetryAfterS = 5f;

        private sealed class Entry
        {
            public CommandCameraTargetModel Model;
            public float UsedAt;
        }

        private static readonly Dictionary<uint, List<Entry>> s_models = new Dictionary<uint, List<Entry>>();
        private static readonly Dictionary<uint, float> s_failedAt = new Dictionary<uint, float>();
        private static readonly List<uint> s_scratch = new List<uint>();
        private static readonly Camera[] NoLayers = new Camera[0];
        private static float s_prunedAt;
        /* The frame a model was last built or changed in, by any camera: one a frame across them all. */
        private static int s_builtFrame = -1;

        /*
         * The stand-in for craft `pid` showing the parts in `record`, or null.
         * One already built for these parts is returned as it is; otherwise
         * one is built, or an idle one of the same craft changed to these
         * parts, only while `mayBuild`, which it then clears, and only once
         * a frame however many cameras ask.
         */
        public static CommandCameraTargetModel For(uint pid, Composition record, ref bool mayBuild)
        {
            if (record == null) return null;
            float now = Time.realtimeSinceStartup;
            Prune(now);
            if (!s_models.TryGetValue(pid, out var list)) s_models[pid] = list = new List<Entry>();
            Entry idle = null;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var e = list[i];
                if (e.Model == null || e.Model.Gone)
                {
                    list.RemoveAt(i);
                    continue;
                }
                if (e.Model.Parts == record)
                {
                    e.UsedAt = now;
                    return e.Model;
                }
                if (now - e.UsedAt > ReuseAfterS) idle = e;
            }
            if (!mayBuild || s_builtFrame == Time.frameCount) return null;
            if (s_failedAt.TryGetValue(pid, out float failed) && now - failed < RetryAfterS) return null;
            mayBuild = false;
            s_builtFrame = Time.frameCount;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (idle != null && idle.Model.Update(record, out _, out _))
                {
                    idle.UsedAt = now;
                    return idle.Model;
                }
                var model = CommandCameraTargetModel.Build(pid, record, 0, NoLayers);
                if (model == null) s_failedAt[pid] = now;
                else list.Add(new Entry { Model = model, UsedAt = now });
                return model;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][PICTURE] stand-in for {pid} threw: {ex}");
                s_failedAt[pid] = now;
                return null;
            }
            finally
            {
                StandInCost.Built(Stopwatch.GetTimestamp() - t0);
            }
        }

        private static void Prune(float now)
        {
            if (now - s_prunedAt < 1f) return;
            s_prunedAt = now;
            s_scratch.Clear();
            foreach (var kv in s_models)
            {
                var list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (now - list[i].UsedAt <= DropAfterS) continue;
                    list[i].Model?.Dispose();
                    list.RemoveAt(i);
                }
                if (list.Count == 0) s_scratch.Add(kv.Key);
            }
            foreach (var pid in s_scratch) s_models.Remove(pid);
            s_scratch.Clear();
            foreach (var kv in s_failedAt) if (now - kv.Value > RetryAfterS) s_scratch.Add(kv.Key);
            foreach (var pid in s_scratch) s_failedAt.Remove(pid);
        }
    }

    /*
     * What the stand-ins cost over every camera, per rendered frame: the cull
     * over the ring's craft, the past-instant solves of the survivors, the
     * models and points drawn, and model builds. Logged every LogSeconds while
     * a light speed is set.
     */
    internal static class StandInCost
    {
        private const float LogSeconds = 10f;
        private static int s_frame = -1;
        private static long s_cull, s_solve, s_show, s_build;
        private static int s_walked, s_culled, s_drawn, s_models, s_points;
        private static int s_frames;
        private static double s_sumMs, s_maxMs, s_cullMs, s_solveMs, s_showMs, s_buildMs;
        private static long s_sumWalked, s_sumCulled, s_sumDrawn, s_sumModels, s_sumPoints;
        private static float s_loggedAt;

        public static void Prepared(long cull, long solve, int walked, int culled, int drawn)
        {
            Frame();
            s_cull += cull;
            s_solve += solve;
            s_walked += walked;
            s_culled += culled;
            s_drawn += drawn;
        }

        public static void Shown(long ticks, int models, int points)
        {
            Frame();
            s_show += ticks;
            s_models += models;
            s_points += points;
        }

        public static void Built(long ticks)
        {
            Frame();
            s_build += ticks;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void Frame()
        {
            int frame = Time.frameCount;
            if (frame == s_frame) return;
            if (s_frame >= 0)
            {
                double ms = Ms(s_cull + s_solve + s_show);
                s_frames++;
                s_sumMs += ms;
                if (ms > s_maxMs) s_maxMs = ms;
                s_cullMs += Ms(s_cull);
                s_solveMs += Ms(s_solve);
                s_showMs += Ms(s_show);
                s_buildMs += Ms(s_build);
                s_sumWalked += s_walked;
                s_sumCulled += s_culled;
                s_sumDrawn += s_drawn;
                s_sumModels += s_models;
                s_sumPoints += s_points;
            }
            s_frame = frame;
            s_cull = s_solve = s_show = s_build = 0;
            s_walked = s_culled = s_drawn = s_models = s_points = 0;
            float now = Time.realtimeSinceStartup;
            if (s_frames == 0 || now - s_loggedAt < LogSeconds) return;
            var inv = CultureInfo.InvariantCulture;
            string Per(double v) => (v / s_frames).ToString("F2", inv);
            Debug.Log($"[Kerbcast][PICTURE] stand-in cost over {s_frames} frames: meanMs={(s_sumMs / s_frames).ToString("F4", inv)} "
                + $"maxMs={s_maxMs.ToString("F4", inv)} cullMs/frame={(s_cullMs / s_frames).ToString("F4", inv)} "
                + $"solveMs/frame={(s_solveMs / s_frames).ToString("F4", inv)} drawMs/frame={(s_showMs / s_frames).ToString("F4", inv)} "
                + $"walked/frame={Per(s_sumWalked + s_sumCulled)} culled/frame={Per(s_sumCulled)} drawn/frame={Per(s_sumDrawn)} "
                + $"models/frame={Per(s_sumModels)} points/frame={Per(s_sumPoints)} buildMs={s_buildMs.ToString("F1", inv)}");
            s_loggedAt = now;
            s_frames = 0;
            s_sumMs = s_maxMs = s_cullMs = s_solveMs = s_showMs = s_buildMs = 0;
            s_sumWalked = s_sumCulled = s_sumDrawn = s_sumModels = s_sumPoints = 0;
        }
    }
}
