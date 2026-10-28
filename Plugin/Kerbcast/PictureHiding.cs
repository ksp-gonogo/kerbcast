using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Kerbcast
{
    /*
     * One camera's hiding of loaded craft beyond the boundary (PictureBoundary).
     * Each frame it decides, from the lens, which loaded craft other than the
     * one the lens rides may reach past c·τ, and loose world objects
 * (LooseWorld) likewise; around the camera's local-layer
     * renders it switches their renderers and lights off and their stand-ins
     * on, at their past instants, and then puts everything back exactly as it
     * was, so no other camera and no later frame sees a change.
     *
     * With no light speed nothing is hidden and no hook does any work.
     *
     * Known gaps: stand-ins are prefab-only, so a procedural fairing is absent
     * and its payload shows; reflection probes render outside the hide window
     * and keep a hidden craft's reflection; LensFlare and Projector components
     * on a hidden craft are not switched off; SmokeScreen emitters detached
     * into the world are not reached.
     */
    internal sealed class CraftHider
    {
        private readonly PicturePass _pass;
        /* Craft hidden on the last decision: the hysteresis state, by persistentId. */
        private readonly HashSet<uint> _hiddenIds = new HashSet<uint>();
        private readonly List<Vessel> _hidden = new List<Vessel>();
        private readonly HashSet<uint> _loadedIds = new HashSet<uint>();
        private readonly List<uint> _scratch = new List<uint>();
        /* Loose world objects (LooseWorld) hidden on the last decision, by instance id. */
        private readonly HashSet<int> _looseIds = new HashSet<int>();
        private readonly List<GameObject> _loose = new List<GameObject>();
        private readonly HashSet<int> _looseNow = new HashSet<int>();
        private readonly List<int> _looseScratch = new List<int>();
        private readonly List<Renderer> _off = new List<Renderer>();
        private readonly List<Light> _dark = new List<Light>();
        private readonly List<uint> _hiddenPids = new List<uint>();
        private readonly Lingering _lingering = new Lingering();
        /* Every loaded craft and the one the lens rides: the scene draws these itself, live or hidden. */
        private readonly HashSet<uint> _sceneDraws = new HashSet<uint>();
        private int _frame = -1;
        private bool _stale = true;
        private RigDrawn _rigDraws;
        private int _rigDrawsFrame = -1;

        public CraftHider(PicturePass pass)
        {
            _pass = pass;
        }

        /* A rig placed its lens: the next ask decides again from it. */
        public void LensPlaced() => _stale = true;

        /* The craft this camera's rig draws a stand-in for itself this frame, or 0. */
        public RigDrawn RigDrawn => _rigDrawsFrame == Time.frameCount ? _rigDraws : default;

        /* The loaded craft hidden this frame, by persistentId. */
        public List<uint> HiddenPids => _hiddenPids;

        public bool SceneDraws(uint pid) => _sceneDraws.Contains(pid);

        /* Every loaded craft and the one the lens rides, this frame. */
        public HashSet<uint> SceneDrawn => _sceneDraws;

        /* The craft this camera hid that have since left the scene, still drawn while their light lasts. */
        public Lingering Lingering => _lingering;

        /* The rig draws its subject's stand-in itself this frame, with its own clip plan. */
        public void RigDraws(RigDrawn drawn)
        {
            _rigDraws = drawn;
            _rigDrawsFrame = Time.frameCount;
        }

        public bool Hides(Vessel v)
        {
            if (v == null || !LightTime.LightSpeed.HasValue) return false;
            Decide();
            return _hiddenIds.Contains(v.persistentId);
        }

        /* The craft hidden this frame, by persistentId. */
        public HashSet<uint> HiddenIds()
        {
            Decide();
            return _hiddenIds;
        }

        public bool Hides(uint pid)
        {
            if (pid == 0 || !LightTime.LightSpeed.HasValue) return false;
            Decide();
            return _hiddenIds.Contains(pid);
        }

        /*
         * Before the frame renders: settles what is hidden and reads each
         * hidden craft's renderers and lights. False when a craft that must be
         * hidden cannot be, and the frame must go out black.
         */
        public bool Prepare()
        {
            if (!LightTime.LightSpeed.HasValue)
            {
                _hidden.Clear();
                _hiddenPids.Clear();
                _hiddenIds.Clear();
                _lingering.Clear();
                _loose.Clear();
                _looseIds.Clear();
                return true;
            }
            Decide();
            if (_hidden.Count == 0) return true;
            long t0 = Stopwatch.GetTimestamp();
            bool ok = true;
            for (int i = 0; i < _hidden.Count; i++)
                if (_hidden[i] != null && LoadedCraft.Collect(_hidden[i]) == null) ok = false;
            HideCost.Prepared(Stopwatch.GetTimestamp() - t0);
            return ok;
        }

        /* Switches hidden craft off and their stand-ins on, for this camera's
           local layers only. A craft it cannot switch off throws, so the frame
           is dropped rather than sent with that craft live. */
        public void Hide()
        {
            if (_hidden.Count == 0 && _loose.Count == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            int renderers = _off.Count;
            /* Nothing of a loose object is drawn in its place: it has no history to draw it from. */
            for (int i = 0; i < _loose.Count; i++)
            {
                var o = _loose[i];
                if (o == null) continue;
                var l = LooseWorld.Of(o);
                for (int r = 0; r < l.Renderers.Count; r++)
                {
                    var rend = l.Renderers[r];
                    if (rend == null || rend.forceRenderingOff) continue;
                    rend.forceRenderingOff = true;
                    _off.Add(rend);
                }
                for (int k = 0; k < l.Lights.Count; k++)
                {
                    var light = l.Lights[k];
                    if (light == null || !light.enabled) continue;
                    light.enabled = false;
                    _dark.Add(light);
                }
            }
            for (int i = 0; i < _hidden.Count; i++)
            {
                /* A craft destroyed since the decision draws nothing to hide. */
                if (_hidden[i] == null) continue;
                var parts = LoadedCraft.Collect(_hidden[i]);
                if (parts == null) throw new InvalidOperationException("a loaded craft beyond the boundary could not be hidden");
                for (int r = 0; r < parts.Renderers.Count; r++)
                {
                    var rend = parts.Renderers[r];
                    if (rend == null || rend.forceRenderingOff) continue;
                    rend.forceRenderingOff = true;
                    _off.Add(rend);
                }
                for (int l = 0; l < parts.Lights.Count; l++)
                {
                    var light = parts.Lights[l];
                    if (light == null || !light.enabled) continue;
                    light.enabled = false;
                    _dark.Add(light);
                }
            }
            renderers = _off.Count - renderers;
            HideCost.Toggled(Stopwatch.GetTimestamp() - t0, _hidden.Count, renderers);
        }

        /* Undoes Hide exactly, however far it got. */
        public void Restore()
        {
            if (_off.Count == 0 && _dark.Count == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < _off.Count; i++)
                if (_off[i] != null) _off[i].forceRenderingOff = false;
            _off.Clear();
            for (int i = 0; i < _dark.Count; i++)
                if (_dark[i] != null) _dark[i].enabled = true;
            _dark.Clear();
            HideCost.Toggled(Stopwatch.GetTimestamp() - t0, 0, 0);
        }

        /*
         * Which loaded craft are hidden this frame, decided once a frame and
         * again after a rig places its lens. Distances are from the lens's
         * solve point plus the spread of its cameras, or from this camera's
         * eye when there is no lens this frame, so they bound the distance
         * from the eye to any part of the craft.
         */
        private void Decide()
        {
            int frame = Time.frameCount;
            if (frame == _frame && !_stale) return;
            _frame = frame;
            _stale = false;
            _hidden.Clear();
            _hiddenPids.Clear();
            _sceneDraws.Clear();
            _loose.Clear();
            double? c = LightTime.LightSpeed;
            if (!c.HasValue)
            {
                _hiddenIds.Clear();
                _lingering.Clear();
                _looseIds.Clear();
                return;
            }
            DVec3 from;
            double spread;
            uint rides;
            if (_pass.TryLens(out var lens))
            {
                from = lens.Point;
                spread = lens.Spread;
                rides = lens.Rides;
            }
            else
            {
                from = _pass.EyePoint();
                spread = 0;
                var craft = _pass.Craft;
                rides = craft != null ? craft.persistentId : _pass.LastRides;
            }
            _loadedIds.Clear();
            if (rides != 0) _sceneDraws.Add(rides);
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    var v = loaded[i];
                    if (v == null) continue;
                    uint pid = v.persistentId;
                    _sceneDraws.Add(pid);
                    if (pid == rides) continue;
                    _loadedIds.Add(pid);
                    double far;
                    try { far = LoadedCraft.Reach(v, from, PictureBoundary.BoundaryM(c.Value)) + spread; }
                    catch (Exception) { far = double.NaN; }
                    if (PictureBoundary.Hidden(_hiddenIds.Contains(pid), far, c.Value))
                    {
                        _hiddenIds.Add(pid);
                        _hidden.Add(v);
                        _hiddenPids.Add(pid);
                    }
                    else _hiddenIds.Remove(pid);
                }
            }
            _scratch.Clear();
            foreach (var pid in _hiddenIds) if (!_loadedIds.Contains(pid)) _scratch.Add(pid);
            for (int i = 0; i < _scratch.Count; i++) _hiddenIds.Remove(_scratch[i]);
            /* Noted on every decision, rendered or not, so a craft hidden on a frame this camera skips still lingers. */
            _lingering.Note(_hiddenPids);
            DecideLoose(from, spread, c.Value);
        }

        private void DecideLoose(DVec3 from, double spread, double c)
        {
            _loose.Clear();
            _looseNow.Clear();
            var objects = LooseWorld.Objects();
            if (objects != null)
            {
                for (int i = 0; i < objects.Count; i++)
                {
                    var o = objects[i];
                    if (o == null) continue;
                    int id = o.GetInstanceID();
                    _looseNow.Add(id);
                    double far;
                    try { far = LooseWorld.Reach(o, from, PictureBoundary.BoundaryM(c)) + spread; }
                    catch (Exception) { far = double.NaN; }
                    if (PictureBoundary.Hidden(_looseIds.Contains(id), far, c))
                    {
                        _looseIds.Add(id);
                        _loose.Add(o);
                    }
                    else _looseIds.Remove(id);
                }
            }
            _looseScratch.Clear();
            foreach (var id in _looseIds) if (!_looseNow.Contains(id)) _looseScratch.Add(id);
            for (int i = 0; i < _looseScratch.Count; i++) _looseIds.Remove(_looseScratch[i]);
        }
    }

    /*
     * What the hider reads of a loaded craft, shared by every camera in a
     * frame: a bounding sphere for the boundary test, and its renderers and
     * lights, read afresh each frame it is hidden so a part, plume or light
     * added since is never missed.
     */
    internal static class LoadedCraft
    {
        public sealed class Parts
        {
            public int SphereFrame = -1;
            public DVec3 Centre;
            public double Radius;
            public int CollectFrame = -1;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<Light> Lights = new List<Light>();
        }

        private static readonly Dictionary<Vessel, Parts> s_craft = new Dictionary<Vessel, Parts>();
        private static readonly HashSet<int> s_ids = new HashSet<int>();
        private static readonly List<Renderer> s_renderers = new List<Renderer>();
        private static readonly List<Light> s_lights = new List<Light>();
        private static readonly List<Vessel> s_dead = new List<Vessel>();
        private static readonly Dictionary<string, float> s_partRadius = new Dictionary<string, float>();
        private static int s_prunedFrame = -1;

        /*
         * How far the farthest part of `v` may be from `from`, against
         * `boundaryM`: from a sphere about the root built from part origins
         * and prefab meshes, shared by every camera in a frame, while that
         * settles the decision, and from every part as it is drawn now
         * (deployed, opened, rescaled) only when the craft is within reach
         * of the boundary.
         */
        public static double Reach(Vessel v, DVec3 from, double boundaryM)
        {
            var p = Of(v);
            if (p.SphereFrame != Time.frameCount)
            {
                p.SphereFrame = Time.frameCount;
                Sphere(v, out p.Centre, out p.Radius);
            }
            double reach = (p.Centre - from).Magnitude + p.Radius;
            if (reach < QuickBelow(boundaryM) || reach > boundaryM) return reach;
            return Math.Max(reach, LiveLightTime.FarthestPartM(new Vector3d(from.X, from.Y, from.Z), v));
        }

        /*
         * Below this reach a craft's prefab sphere decides: half the
         * boundary, and at least NearGapM short of it, since a deployed
         * chute, an inflated shield or a rescaled part can reach some tens of
         * metres beyond a prefab however small the boundary is.
         */
        internal static double QuickBelow(double boundaryM) => Math.Min(NearFraction * boundaryM, boundaryM - NearGapM);

        private const double NearFraction = 0.5;
        private const double NearGapM = 50;

        /* This frame's renderers and lights of `v`, read now if not yet; null when they cannot be read. */
        public static Parts Collect(Vessel v)
        {
            var p = Of(v);
            if (p.CollectFrame == Time.frameCount) return p;
            try
            {
                p.Renderers.Clear();
                p.Lights.Clear();
                s_ids.Clear();
                var parts = v.parts;
                if (parts == null) return null;
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (part == null || part.transform == null || Nested(part)) continue;
                    part.GetComponentsInChildren(true, s_renderers);
                    for (int r = 0; r < s_renderers.Count; r++)
                        if (s_renderers[r] != null && s_ids.Add(s_renderers[r].GetInstanceID())) p.Renderers.Add(s_renderers[r]);
                    part.GetComponentsInChildren(true, s_lights);
                    for (int l = 0; l < s_lights.Count; l++)
                        if (s_lights[l] != null && s_ids.Add(s_lights[l].GetInstanceID())) p.Lights.Add(s_lights[l]);
                }
                p.CollectFrame = Time.frameCount;
                return p;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][PICTURE] cannot read the parts of a craft to hide: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static Parts Of(Vessel v)
        {
            Prune();
            if (!s_craft.TryGetValue(v, out var p)) s_craft[v] = p = new Parts();
            return p;
        }

        private static void Prune()
        {
            int frame = Time.frameCount;
            if (frame == s_prunedFrame || frame % 300 != 0) return;
            s_prunedFrame = frame;
            s_dead.Clear();
            foreach (var kv in s_craft) if (kv.Key == null || !kv.Key.loaded) s_dead.Add(kv.Key);
            for (int i = 0; i < s_dead.Count; i++) s_craft.Remove(s_dead[i]);
        }

        /* A part nested under its parent's transform is read with its parent. */
        private static bool Nested(Part part)
        {
            var parent = part.parent;
            return parent != null && parent.transform != null && part.transform.parent == parent.transform;
        }

        /* About the root part: each part's origin plus its prefab's reach about it. */
        private static void Sphere(Vessel v, out DVec3 centre, out double radius)
        {
            var parts = v.parts;
            var root = v.rootPart;
            var c = root != null && root.transform != null ? root.transform.position : v.transform.position;
            centre = new DVec3(c.x, c.y, c.z);
            radius = 0;
            if (parts == null || parts.Count == 0)
            {
                radius = double.NaN;
                return;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null || part.transform == null) continue;
                var at = part.transform.position;
                double d = (new DVec3(at.x, at.y, at.z) - centre).Magnitude + PartRadius(part.partInfo);
                if (d > radius) radius = d;
            }
        }

        /* From the prefab's mesh bounds, once per part type. */
        private static float PartRadius(AvailablePart info)
        {
            if (info?.partPrefab == null) return 2f;
            if (s_partRadius.TryGetValue(info.name, out float cached)) return cached;
            var root = info.partPrefab.transform;
            float rootScale = Mathf.Max(root.lossyScale.x, Mathf.Max(root.lossyScale.y, root.lossyScale.z));
            double r = 0;
            foreach (var mf in info.partPrefab.GetComponentsInChildren<MeshFilter>(true))
                if (mf != null) r = Math.Max(r, MeshReach(root, rootScale, mf.transform, mf.sharedMesh));
            foreach (var sm in info.partPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (sm != null) r = Math.Max(r, MeshReach(root, rootScale, sm.transform, sm.sharedMesh));
            float radius = (float)Math.Max(r, 0.5);
            s_partRadius[info.name] = radius;
            return radius;
        }

        private static double MeshReach(Transform root, float rootScale, Transform t, Mesh mesh)
        {
            if (mesh == null) return 0;
            var s = t.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))) / Mathf.Max(rootScale, 1e-6f);
            return root.InverseTransformPoint(t.TransformPoint(mesh.bounds.center)).magnitude + mesh.bounds.extents.magnitude * scale;
        }
    }

    /*
     * World objects that show something of a craft but belong to no craft:
     * loose physics objects (jettisoned fairing panels and the like) and the
     * game's own effects left in the world (explosions, splashes, debris
     * blasts, fireworks), read from FXMonger by reflection since it keeps
     * them to itself. Shared by every camera in a frame. A camera hides each
     * beyond the boundary with nothing drawn in its place, having no history.
     */
    internal static class LooseWorld
    {
        public sealed class Parts
        {
            public int Frame = -1;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<Light> Lights = new List<Light>();
        }

        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo s_fetch = typeof(FXMonger).GetField("fetch", Fields);
        private static readonly FieldInfo s_explosions = typeof(FXMonger).GetField("explosionObjects", Fields);
        private static readonly FieldInfo s_debris = typeof(FXMonger).GetField("debrisExplosions", Fields);
        private static readonly FieldInfo s_fireworks = typeof(FXMonger).GetField("fireworks", Fields);
        private static FieldInfo s_effectObj;
        private static readonly List<GameObject> s_objects = new List<GameObject>();
        private static readonly Dictionary<GameObject, Parts> s_parts = new Dictionary<GameObject, Parts>();
        private static readonly List<GameObject> s_dead = new List<GameObject>();
        private static int s_frame = -1;
        private static bool s_warned;

        /* This frame's loose objects. */
        public static List<GameObject> Objects()
        {
            if (s_frame == Time.frameCount) return s_objects;
            s_frame = Time.frameCount;
            s_objects.Clear();
            var physical = FlightGlobals.physicalObjects;
            if (physical != null)
                for (int i = 0; i < physical.Count; i++)
                    if (physical[i] != null) s_objects.Add(physical[i].gameObject);
            try
            {
                var monger = s_fetch?.GetValue(null);
                if (monger != null)
                {
                    AddAll(s_explosions?.GetValue(monger));
                    AddAll(s_debris?.GetValue(monger));
                    AddAll(s_fireworks?.GetValue(monger));
                }
            }
            catch (Exception ex)
            {
                if (!s_warned) Debug.LogWarning($"[Kerbcast][PICTURE] cannot read the game's effects to hide (logged once): {ex.GetType().Name}: {ex.Message}");
                s_warned = true;
            }
            if (s_parts.Count > 256) Prune();
            return s_objects;
        }

        /* A list of effect objects, components or FXObjects (whose effectObj is the object). */
        private static void AddAll(object list)
        {
            if (!(list is IEnumerable items)) return;
            foreach (var item in items)
            {
                GameObject go = null;
                if (item is GameObject g) go = g;
                else if (item is Component c) go = c != null ? c.gameObject : null;
                else if (item != null)
                {
                    if (s_effectObj == null) s_effectObj = item.GetType().GetField("effectObj", Fields);
                    go = s_effectObj?.GetValue(item) as GameObject;
                }
                if (go != null) s_objects.Add(go);
            }
        }

        /* An object's renderers and lights, read once a frame. */
        public static Parts Of(GameObject o)
        {
            if (!s_parts.TryGetValue(o, out var p)) s_parts[o] = p = new Parts();
            if (p.Frame != Time.frameCount)
            {
                p.Frame = Time.frameCount;
                o.GetComponentsInChildren(true, p.Renderers);
                o.GetComponentsInChildren(true, p.Lights);
            }
            return p;
        }

        /* The farthest an object's drawn bounds may reach from `from`; its bounds are read only near `boundaryM`. */
        public static double Reach(GameObject o, DVec3 from, double boundaryM)
        {
            var at = o.transform.position;
            double reach = (new DVec3(at.x, at.y, at.z) - from).Magnitude;
            if (reach < LoadedCraft.QuickBelow(boundaryM) || reach > boundaryM) return reach;
            var list = Of(o).Renderers;
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r == null) continue;
                var b = r.bounds;
                double d = (new DVec3(b.center.x, b.center.y, b.center.z) - from).Magnitude + b.extents.magnitude;
                if (d > reach) reach = d;
            }
            return reach;
        }

        private static void Prune()
        {
            s_dead.Clear();
            foreach (var kv in s_parts) if (kv.Key == null) s_dead.Add(kv.Key);
            for (int i = 0; i < s_dead.Count; i++) s_parts.Remove(s_dead[i]);
        }
    }

    /*
     * What hiding costs over every camera, per rendered frame: reading the
     * hidden craft's parts (shared by cameras), and switching them off and
     * back for each camera. Logged every LogSeconds while anything is hidden,
     * so a Deck run with several cameras can read it.
     */
    internal static class HideCost
    {
        private const float LogSeconds = 10f;
        private static int s_frame = -1;
        private static long s_collect, s_toggle;
        private static int s_toggles, s_renderers;
        private static int s_frames;
        private static double s_sumMs, s_maxMs, s_collectMs, s_toggleMs;
        private static long s_sumToggles, s_sumRenderers;
        private static float s_loggedAt;

        public static void Prepared(long collect)
        {
            Frame();
            s_collect += collect;
        }

        public static void Toggled(long ticks, int craft, int renderers)
        {
            Frame();
            s_toggle += ticks;
            s_toggles += craft;
            s_renderers += renderers;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void Frame()
        {
            int frame = Time.frameCount;
            if (frame == s_frame) return;
            if (s_frame >= 0)
            {
                double ms = Ms(s_collect + s_toggle);
                s_frames++;
                s_sumMs += ms;
                if (ms > s_maxMs) s_maxMs = ms;
                s_collectMs += Ms(s_collect);
                s_toggleMs += Ms(s_toggle);
                s_sumToggles += s_toggles;
                s_sumRenderers += s_renderers;
            }
            s_frame = frame;
            s_collect = s_toggle = 0;
            s_toggles = s_renderers = 0;
            float now = Time.realtimeSinceStartup;
            if (s_frames == 0 || now - s_loggedAt < LogSeconds) return;
            var inv = CultureInfo.InvariantCulture;
            double perToggle = s_sumToggles > 0 ? s_toggleMs / s_sumToggles : 0;
            Debug.Log($"[Kerbcast][PICTURE] hide cost over {s_frames} frames: meanMs={(s_sumMs / s_frames).ToString("F4", inv)} "
                + $"maxMs={s_maxMs.ToString("F4", inv)} collectMs/frame={(s_collectMs / s_frames).ToString("F4", inv)} "
                + $"toggleMs/frame={(s_toggleMs / s_frames).ToString("F4", inv)} msPerCraftToggle={perToggle.ToString("F4", inv)} "
                + $"craftToggles/frame={((double)s_sumToggles / s_frames).ToString("F2", inv)} "
                + $"renderers/frame={((double)s_sumRenderers / s_frames).ToString("F0", inv)}");
            s_loggedAt = now;
            s_frames = 0;
            s_sumMs = s_maxMs = s_collectMs = s_toggleMs = 0;
            s_sumToggles = s_sumRenderers = 0;
        }
    }
}
