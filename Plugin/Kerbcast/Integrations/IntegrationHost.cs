// Owns the visual-mod integrations for one KerbcastCamera and is the only
// surface SetCameras and the capture loop talk to. The registration list in
// the constructor is the single place a new integration is added: implement
// ICameraModIntegration and add one line here. Every integration that is not
// available is a silent no-op, so the host is safe to drive unconditionally.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class IntegrationHost
    {
        private const string LogTag = "[Kerbcast-integrations]";

        private readonly List<ICameraModIntegration> _integrations;

        public IntegrationHost()
        {
            // Registration list: the ONE place new integrations are added.
            // Order matters where one integration sets a whole-camera mode
            // another reads: Deferred must be first (it establishes the
            // rendering path that EVE and Scatterer key off).
            _integrations = new List<ICameraModIntegration>
            {
                new DeferredIntegration(),
                new TUFXIntegration(),
                new EVEIntegration(),
                new ScattererIntegration(),
                new ParallaxIntegration(),
            };
        }

        // Process-global MSAA lever. Read by SetCameras / BuildRenderTargets
        // before any camera or RT is built, so it must not touch a camera.
        public bool ForceNoMsaa
        {
            get
            {
                var facts = new List<IntegrationFact>(_integrations.Count);
                foreach (var i in _integrations)
                {
                    bool available = SafeAvailable(i);
                    facts.Add(new IntegrationFact(i.Name, available,
                        available && i.ForcesNoMsaa));
                }
                return IntegrationPolicy.ForceNoMsaa(facts);
            }
        }

        public void ApplyToLayer(Camera cam, CameraLayers layer)
        {
            if (cam == null) return;
            foreach (var i in _integrations)
            {
                if (!SafeAvailable(i)) continue;
                if ((i.AppliesToLayers & layer) == 0) continue;
                try { i.ApplyToLayer(cam, layer); }
                catch (Exception ex)
                {
                    Debug.LogError($"{LogTag} {i.Name} ApplyToLayer({layer}) failed: {ex.Message}");
                }
            }
        }

        public void RemoveFromLayer(Camera cam, CameraLayers layer)
        {
            if (cam == null) return;
            foreach (var i in _integrations)
            {
                if (!SafeAvailable(i)) continue;
                if ((i.AppliesToLayers & layer) == 0) continue;
                try { i.RemoveFromLayer(cam, layer); }
                catch (Exception ex)
                {
                    Debug.LogError($"{LogTag} {i.Name} RemoveFromLayer({layer}) failed: {ex.Message}");
                }
            }
        }

        public void PerFrame(Camera cam, CameraLayers layer, in IntegrationFrameState state)
        {
            if (cam == null) return;
            foreach (var i in _integrations)
            {
                if (!i.NeedsPerFrame) continue;
                if (!SafeAvailable(i)) continue;
                if ((i.AppliesToLayers & layer) == 0) continue;
                try { i.PerFrame(cam, layer, state); }
                catch (Exception ex)
                {
                    Debug.LogError($"{LogTag} {i.Name} PerFrame({layer}) failed: {ex.Message}");
                }
            }
        }

        private static bool SafeAvailable(ICameraModIntegration i)
        {
            try { return i.IsAvailable; }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast-integrations] {i.Name} availability probe threw: {ex.Message}");
                return false;
            }
        }
    }
}
