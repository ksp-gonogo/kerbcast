using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /* A physics-free stand-in for an unloaded craft: each saved part's prefab
       model, renderers only, posed from the save and placed in local space at
       the craft's propagated position. Local space so the rig's existing clip
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
        private bool _show;

        /* True while the craft is under a pixel: only the sub-pixel camera
           draws it, the frame's own layers do not. */
        internal bool SubpixelMode { get; set; }

        public uint PersistentId { get; }
        /* Bounding radius about the root from renderer bounds, for framing. */
        public double Radius { get; }

        private CommandCameraTargetModel(ProtoVessel pv, uint flightId, out int parts, out int skipped)
        {
            PersistentId = pv.persistentId;
            _root = new GameObject($"Kerbcast_{flightId}_TargetModel");
            /* Built inactive so nothing on the clones wakes before it is stripped. */
            _root.SetActive(false);

            parts = 0;
            skipped = 0;
            foreach (var pps in pv.protoPartSnapshots)
            {
                var prefab = pps?.partInfo?.partPrefab;
                var model = prefab != null ? prefab.transform.Find("model") : null;
                if (model == null) { skipped++; continue; }

                var node = new GameObject(pps.partName ?? "part").transform;
                node.SetParent(_root.transform, false);
                node.localPosition = new Vector3((float)pps.position.x, (float)pps.position.y, (float)pps.position.z);
                node.localRotation = pps.rotation;
                var clone = UnityEngine.Object.Instantiate(model.gameObject, node, false);
                clone.transform.localPosition = model.localPosition;
                clone.transform.localRotation = model.localRotation;
                clone.transform.localScale = model.localScale;
                Strip(clone);
                parts++;
            }

            _root.GetComponentsInChildren(true, _renderers);
            for (int i = 0; i < _renderers.Count; i++) _renderers[i].enabled = false;
            _root.SetActive(true);

            Radius = MeasureRadius();
        }

        /* Null when the craft has no buildable parts. */
        public static CommandCameraTargetModel Build(ProtoVessel pv, uint flightId, Camera[] layers)
        {
            if (pv == null || pv.protoPartSnapshots == null || pv.protoPartSnapshots.Count == 0) return null;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var m = new CommandCameraTargetModel(pv, flightId, out int parts, out int skipped);
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
            Debug.Log($"{Tag} cam={flightId} target model built: vessel='{pv.vesselName}' parts={parts} skipped={skipped} "
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
        private double MeasureRadius()
        {
            var centre = _root.transform.position;
            double r = 0;
            foreach (var mf in _root.GetComponentsInChildren<MeshFilter>(true))
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
