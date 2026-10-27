using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /* A physics-free stand-in for an unloaded craft: each part's prefab model,
       renderers only, posed from the save, or from the light-time history's
       parts at the craft's past instant, and placed in local space at the
       craft's propagated position. Local space so the rig's existing clip
       planning, the scene sunlight and every local-layer effect (haze, clouds)
       treat it exactly like a loaded craft at range. Its renderers are switched
       on only around the command camera's local-layer renders.

       Known gaps: procedural meshes built at runtime (fairings) are absent,
       part variants and texture switches show the prefab default. */
    internal sealed class CommandCameraTargetModel : IDisposable
    {
        private const string Tag = "[Kerbcast][CMDCAM]";

        private readonly GameObject _root;
        private readonly List<Renderer> _renderers = new List<Renderer>();
        private readonly List<ModelRenderHook> _hooks = new List<ModelRenderHook>();
        /* A history-built model's part nodes by part id, kept across changes of its parts. */
        private readonly Dictionary<uint, Transform> _nodes = new Dictionary<uint, Transform>();
        private bool _show;

        /* True while the craft is under a pixel: only the sub-pixel camera
           draws it, the frame's own layers do not. */
        internal bool SubpixelMode { get; set; }

        public uint PersistentId { get; }
        /* The history's parts it shows; null when built from the save. */
        public Composition Parts { get; private set; }
        /* Bounding radius from renderer bounds, for framing: about the root
           for a model from the save, about the centre of mass for one from the
           history, which is the point it is placed by. */
        public double Radius { get; private set; }

        private CommandCameraTargetModel(ProtoVessel pv, uint flightId, out int parts, out int skipped)
        {
            PersistentId = pv.persistentId;
            _root = Begin(flightId);
            parts = 0;
            skipped = 0;
            foreach (var pps in pv.protoPartSnapshots)
            {
                if (pps != null && AddPart(pps.partInfo, pps.partName, new Vector3((float)pps.position.x, (float)pps.position.y, (float)pps.position.z),
                        pps.rotation) != null) parts++;
                else skipped++;
            }
            Finish();
            Radius = MeasureRadius(Vector3.zero, true);
        }

        private CommandCameraTargetModel(uint pid, Composition composition, uint flightId, out int parts, out int skipped)
        {
            PersistentId = pid;
            Parts = composition;
            _root = Begin(flightId);
            parts = 0;
            skipped = 0;
            foreach (var p in composition.Parts)
            {
                if (AddRecord(p)) parts++;
                else skipped++;
            }
            Finish();
            Radius = MeasureRadius(ComOf(composition), false);
        }

        private bool AddRecord(in PartRecord p)
        {
            var info = p.Name != null ? PartLoader.getPartInfoByName(p.Name) : null;
            var node = AddPart(info, p.Name, new Vector3(p.Px, p.Py, p.Pz), new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw));
            if (node == null) return false;
            _nodes[p.Pid] = node;
            return true;
        }

        private static Vector3 ComOf(Composition c) => new Vector3(c.ComX, c.ComY, c.ComZ);

        /*
         * A history-built model of the same craft shown with another set of
         * parts: only the parts that came are built, those that went are
         * switched off, and every kept part is posed afresh, so a staging or a
         * crash does not rebuild the whole craft. False for a model from the
         * save, which is rebuilt instead.
         */
        public bool Update(Composition composition, out int added, out int removed)
        {
            added = removed = 0;
            if (Parts == null || composition?.Parts == null || _root == null) return false;
            _keep.Clear();
            bool woke = _root.activeSelf;
            _root.SetActive(false);
            foreach (var p in composition.Parts)
            {
                _keep.Add(p.Pid);
                if (_nodes.TryGetValue(p.Pid, out var node))
                {
                    if (!node.gameObject.activeSelf) node.gameObject.SetActive(true);
                    node.localPosition = new Vector3(p.Px, p.Py, p.Pz);
                    node.localRotation = new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw);
                }
                else if (AddRecord(p)) added++;
            }
            foreach (var kv in _nodes)
            {
                if (_keep.Contains(kv.Key) || !kv.Value.gameObject.activeSelf) continue;
                kv.Value.gameObject.SetActive(false);
                removed++;
            }
            Parts = composition;
            Finish();
            if (!woke) _root.SetActive(false);
            Radius = MeasureRadius(ComOf(composition), false);
            return true;
        }

        private readonly HashSet<uint> _keep = new HashSet<uint>();

        /* Built inactive so nothing on the clones wakes before it is stripped. */
        private static GameObject Begin(uint flightId)
        {
            var root = new GameObject($"Kerbcast_{flightId}_TargetModel");
            root.SetActive(false);
            return root;
        }

        /* One part's prefab model at its pose about the root part; null when it has none. Called with the root inactive. */
        private Transform AddPart(AvailablePart info, string name, Vector3 position, Quaternion rotation)
        {
            var prefab = info?.partPrefab;
            var model = prefab != null ? prefab.transform.Find("model") : null;
            if (model == null) return null;
            var node = new GameObject(name ?? "part").transform;
            node.SetParent(_root.transform, false);
            node.localPosition = position;
            node.localRotation = rotation;
            var clone = UnityEngine.Object.Instantiate(model.gameObject, node, false);
            clone.transform.localPosition = model.localPosition;
            clone.transform.localRotation = model.localRotation;
            clone.transform.localScale = model.localScale;
            Strip(clone);
            return node;
        }

        private void Finish()
        {
            _root.GetComponentsInChildren(true, _renderers);
            for (int i = 0; i < _renderers.Count; i++) _renderers[i].enabled = false;
            _root.SetActive(true);
        }

        /* Null when the craft has no buildable parts. */
        public static CommandCameraTargetModel Build(ProtoVessel pv, uint flightId, Camera[] layers)
        {
            if (pv == null || pv.protoPartSnapshots == null || pv.protoPartSnapshots.Count == 0) return null;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var m = new CommandCameraTargetModel(pv, flightId, out int parts, out int skipped);
            return Hook(m, layers, flightId, parts, skipped, t0, $"vessel='{pv.vesselName}'");
        }

        /* The craft `pid` as the history holds its parts at one instant: a
           craft KSP no longer has, or one whose parts have changed since. Null
           when none of its parts can be built. */
        public static CommandCameraTargetModel Build(uint pid, Composition composition, uint flightId, Camera[] layers)
        {
            if (composition?.Parts == null || composition.Parts.Length == 0) return null;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var m = new CommandCameraTargetModel(pid, composition, flightId, out int parts, out int skipped);
            return Hook(m, layers, flightId, parts, skipped, t0, $"history pid={pid} partsUt={composition.Ut:F1}");
        }

        private static CommandCameraTargetModel Hook(CommandCameraTargetModel m, Camera[] layers, uint flightId, int parts, int skipped,
            long t0, string what)
        {
            if (m._renderers.Count == 0)
            {
                m.Dispose();
                Debug.LogWarning($"{Tag} cam={flightId} target model not built: no renderers ({parts} parts, {skipped} without a model)");
                return null;
            }
            foreach (var cam in layers)
            {
                if (cam == null) continue;
                var hook = cam.gameObject.AddComponent<ModelRenderHook>();
                hook.Model = m;
                m._hooks.Add(hook);
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Debug.Log($"{Tag} cam={flightId} target model built: {what} parts={parts} skipped={skipped} "
                + $"renderers={m._renderers.Count} radius={m.Radius:F1}m buildMs={ms:F1} hooks={m._hooks.Count} (fairings and variants not reproduced)");
            return m;
        }

        /* `scale` below 1 shrinks the model about its root for a proxy stand-in
           closer than the craft really is. */
        public void Place(bool show, DVec3 world, Quaternion rotation, float scale)
        {
            _show = show;
            if (!show || _root == null) return;
            _root.transform.position = new Vector3((float)world.X, (float)world.Y, (float)world.Z);
            _root.transform.rotation = rotation;
            _root.transform.localScale = new Vector3(scale, scale, scale);
        }

        /* Hooks a camera that draws the model only in sub-pixel mode. */
        internal void AttachSubpixel(Camera cam)
        {
            if (cam == null) return;
            var hook = cam.gameObject.AddComponent<ModelRenderHook>();
            hook.Model = this;
            hook.Subpixel = true;
            _hooks.Add(hook);
        }

        internal void SetVisible(bool on, bool subpixelPass)
        {
            bool v = on && _show && subpixelPass == SubpixelMode;
            for (int i = 0; i < _renderers.Count; i++)
                if (_renderers[i] != null) _renderers[i].enabled = v;
        }

        public void Dispose()
        {
            foreach (var h in _hooks) if (h != null) UnityEngine.Object.Destroy(h);
            _hooks.Clear();
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }

        /* Renderers and transforms only. Behaviours go first, then renderers
           that depend on other components, then the rest. */
        private static void Strip(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true)) Kill(c);
            foreach (var c in go.GetComponentsInChildren<Renderer>(true))
                if (!(c is MeshRenderer) && !(c is SkinnedMeshRenderer)) Kill(c);
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer) continue;
                Kill(c);
            }
        }

        private static void Kill(Component c)
        {
            try { UnityEngine.Object.DestroyImmediate(c); }
            catch (Exception) { /* a component another depends on; harmless once renderers are off */ }
        }

        /* From mesh bounds rather than Renderer.bounds, which a disabled renderer
           does not keep current. */
        private double MeasureRadius(Vector3 localCentre, bool includeInactive)
        {
            /* At build the root is unscaled, so the save's model keeps the world-space measure it always had. */
            if (Parts == null) return MeasureWorld(includeInactive);
            /* A history-built model is re-measured while placed, perhaps shrunk as a proxy: in the root's own units. */
            var root = _root.transform;
            double r = 0;
            foreach (var mf in _root.GetComponentsInChildren<MeshFilter>(includeInactive))
            {
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var t = mf.transform;
                var local = root.InverseTransformPoint(t.TransformPoint(mesh.bounds.center));
                double d = (local - localCentre).magnitude + MaxAxisReach(t, root, mesh.bounds.extents);
                if (d > r) r = d;
            }
            return Math.Max(r, 1.0);
        }

        /* A mesh's extents in the root's units: its scale relative to the root's. */
        private static double MaxAxisReach(Transform t, Transform root, Vector3 extents)
        {
            var s = t.lossyScale;
            var rs = root.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            float rootScale = Mathf.Max(Mathf.Abs(rs.x), Mathf.Max(Mathf.Abs(rs.y), Mathf.Abs(rs.z)));
            return rootScale > 0 ? extents.magnitude * scale / rootScale : extents.magnitude * scale;
        }

        private double MeasureWorld(bool includeInactive)
        {
            var centre = _root.transform.position;
            double r = 0;
            foreach (var mf in _root.GetComponentsInChildren<MeshFilter>(includeInactive))
            {
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var t = mf.transform;
                var s = t.lossyScale;
                float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                double d = (t.TransformPoint(mesh.bounds.center) - centre).magnitude + mesh.bounds.extents.magnitude * scale;
                if (d > r) r = d;
            }
            return Math.Max(r, 1.0);
        }
    }

    /* On each local layer of the command camera; Unity calls back around each
       explicit Render(). */
    internal sealed class ModelRenderHook : MonoBehaviour
    {
        internal CommandCameraTargetModel Model;
        internal bool Subpixel;

        private void OnPreCull() => Model?.SetVisible(true, Subpixel);
        private void OnPostRender() => Model?.SetVisible(false, Subpixel);
    }
}
