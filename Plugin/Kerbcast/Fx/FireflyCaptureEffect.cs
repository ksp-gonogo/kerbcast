// Captures the Firefly mod's reentry plasma onto kerbcast's near clone. Firefly
// holds its effect as live (CameraEvent, CommandBuffer) pairs in a singleton and
// applies them to the stock near camera; this effect reads those pairs by
// reflection and mirrors them onto the kerbcast near clone each frame, adding new
// buffers and removing stale ones by object identity. It owns no rendering surface
// of its own: the buffers belong to Firefly and are only borrowed.
//
// Selected mutually-exclusively with kerbcast's own reentry FX (see
// KerbcastCamera.EffectiveFxLayers). Reflection-only; ships nothing of Firefly.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kerbcast
{
    internal sealed class FireflyCaptureEffect : IAtmoFxEffect
    {
        private const string LogTag = "[Kerbcast-Firefly]";

        // Firefly.CameraManager type + Instance accessor, probed once per process.
        private static bool _typeProbed;
        private static Type _cmType;
        private static PropertyInfo _instanceProp;

        private FieldInfo _buffersField; // CameraManager.cameraBuffers (nonpublic instance)
        private Camera _nearCam;
        // Buffers we have attached to _nearCam, with the event we attached them at.
        private readonly Dictionary<CommandBuffer, CameraEvent> _attached =
            new Dictionary<CommandBuffer, CameraEvent>();

        public AtmoFxLayers Layer => AtmoFxLayers.Firefly;

        // Cheap availability probe used by EffectiveFxLayers to decide whether to
        // substitute Firefly for kerbcast's own reentry FX. Firefly installed and
        // its singleton live.
        public static bool IsFireflyAvailable()
        {
            ProbeType();
            if (_instanceProp == null) return false;
            try { return _instanceProp.GetValue(null, null) != null; }
            catch { return false; }
        }

        private static void ProbeType()
        {
            if (_typeProbed) return;
            _typeProbed = true;
            try
            {
                var asm = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name == "Firefly")?.assembly;
                _cmType = asm?.GetType("Firefly.CameraManager");
                _instanceProp = _cmType?.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogTag} type probe failed: {ex.Message}");
            }
        }

        public bool TryInitialize(Camera nearCam)
        {
            _nearCam = nearCam;
            ProbeType();
            if (_cmType == null || _instanceProp == null)
            {
                Debug.Log($"{LogTag} Firefly not installed; capture effect dropped");
                return false;
            }
            const BindingFlags NonPubInst = BindingFlags.NonPublic | BindingFlags.Instance;
            _buffersField = _cmType.GetField("cameraBuffers", NonPubInst)
                ?? _cmType.GetField("commandBuffers", NonPubInst)
                ?? _cmType.GetField("buffers", NonPubInst);
            if (_buffersField == null)
            {
                Debug.LogWarning($"{LogTag} CameraManager buffer field not found; unsupported Firefly version");
                return false;
            }
            Debug.Log($"{LogTag} capture effect enabled");
            return true;
        }

        public void OnVesselChanged(Vessel vessel) { /* buffers come from Firefly, no per-vessel rebuild */ }

        public void Render(in FxFrameState state)
        {
            // FxHost synthesises Vessel=null when packed/unloaded; drop the buffers.
            if (state.Vessel == null) { RemoveAll(); return; }
            object inst;
            try { inst = _instanceProp.GetValue(null, null); }
            catch { RemoveAll(); return; }
            if (inst == null) { RemoveAll(); return; }

            var list = _buffersField.GetValue(inst) as IList;
            if (list == null || list.Count == 0) { RemoveAll(); return; }

            /* Build this frame's set of Firefly buffers and reconcile against what
               we have attached: add new, remove stale (Firefly rebuilds its buffer
               object on ReloadCommandBuffer, so track by object identity). */
            // Keyed by buffer instance; assumes Firefly holds each CommandBuffer at
            // one CameraEvent (its source registers one buffer per event).
            var current = new Dictionary<CommandBuffer, CameraEvent>();
            foreach (var item in list)
            {
                var pair = (KeyValuePair<CameraEvent, CommandBuffer>)item;
                if (pair.Value != null) current[pair.Value] = pair.Key;
            }

            // Remove ours that Firefly no longer has. Guard each call: Firefly
            // disposes its buffer objects when it rebuilds them, so a stale entry
            // can reference a released buffer that RemoveCommandBuffer rejects with
            // ArgumentNullException. Swallow per-buffer so one bad buffer cannot
            // throw out of Render and get the whole effect disabled by FxHost.
            var stale = _attached.Where(kv => !current.ContainsKey(kv.Key)).ToList();
            foreach (var kv in stale)
            {
                try { _nearCam.RemoveCommandBuffer(kv.Value, kv.Key); } catch { }
                _attached.Remove(kv.Key);
            }
            // Add Firefly's that we have not attached yet.
            foreach (var kv in current)
            {
                if (kv.Key == null || _attached.ContainsKey(kv.Key)) continue;
                try { _nearCam.AddCommandBuffer(kv.Value, kv.Key); _attached[kv.Key] = kv.Value; } catch { }
            }
        }

        private void RemoveAll()
        {
            if (_attached.Count == 0 || _nearCam == null) { _attached.Clear(); return; }
            foreach (var kv in _attached)
            {
                // Firefly may have already released the buffer; ignore per-buffer.
                try { _nearCam.RemoveCommandBuffer(kv.Value, kv.Key); } catch { }
            }
            _attached.Clear();
        }

        public void Dispose() => RemoveAll();
    }
}
