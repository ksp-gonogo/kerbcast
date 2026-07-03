// Per-camera owner of the atmospheric-FX effects. Holds the near camera, the
// master gate (effective enabled layers), and sequences each IAtmoFxEffect per
// frame. Owns no rendering surface itself — effects own theirs.
//
// Master gate: KerbcastCamera passes the *effective* enabled set
// (EnableAtmosphericFx ? configured layers : None). When None, the host holds no
// effects, so Render() is a genuine no-op and non-FX flight costs nothing.

using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class FxHost
    {
        private readonly Camera _nearCam;
        private readonly List<IAtmoFxEffect> _effects = new List<IAtmoFxEffect>();
        private AtmoFxLayers _enabled = AtmoFxLayers.None;
        private Vessel _vessel;

        public FxHost(Camera nearCam)
        {
            _nearCam = nearCam;
        }

        // Reconcile the live effect set to match the effective enabled layers.
        // Adds+initializes newly-enabled effects, disposes newly-disabled ones.
        public void SetEnabledLayers(AtmoFxLayers layers)
        {
            if (layers == _enabled) return;
            _enabled = layers;

            // Drop effects whose bit is no longer set.
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                if ((_enabled & _effects[i].Layer) == 0)
                {
                    _effects[i].Dispose();
                    _effects.RemoveAt(i);
                }
            }

            // Add effects whose bit is newly set and not already present.
            AddIfEnabled(AtmoFxLayers.Core);
            AddIfEnabled(AtmoFxLayers.Bowshock);
            AddIfEnabled(AtmoFxLayers.Trail);
            AddIfEnabled(AtmoFxLayers.Embers);
            AddIfEnabled(AtmoFxLayers.Firefly);
        }

        private void AddIfEnabled(AtmoFxLayers layer)
        {
            if ((_enabled & layer) == 0) return;
            if (HasEffect(layer)) return;

            var effect = CreateEffect(layer);
            if (effect == null) return; // layer not implemented yet
            if (!effect.TryInitialize(_nearCam))
            {
                // Unavailable (missing shader bundle etc.) — drop silently.
                effect.Dispose();
                return;
            }
            if (_vessel != null) effect.OnVesselChanged(_vessel);
            _effects.Add(effect);
        }

        // Factory. Only implemented layers return an instance; others return
        // null and are skipped until their effect lands.
        private static IAtmoFxEffect CreateEffect(AtmoFxLayers layer)
        {
            switch (layer)
            {
                case AtmoFxLayers.Core: return new CoreSheathEffect();
                case AtmoFxLayers.Bowshock: return new BowshockEffect();
                case AtmoFxLayers.Trail: return new TrailEffect();
                case AtmoFxLayers.Embers: return new EmbersEffect();
                case AtmoFxLayers.Firefly: return new FireflyCaptureEffect();
                default: return null;
            }
        }

        private bool HasEffect(AtmoFxLayers layer)
        {
            for (int i = 0; i < _effects.Count; i++)
                if (_effects[i].Layer == layer) return true;
            return false;
        }

        public void OnVesselChanged(Vessel vessel)
        {
            _vessel = vessel;
            for (int i = 0; i < _effects.Count; i++) _effects[i].OnVesselChanged(vessel);
        }

        public void Render(in FxFrameState state)
        {
            // Gate when the vessel is physics-unloaded (KSP "packs" vessels
            // outside the 2.5km physics range and switches them onto rails).
            // Their part renderers' state is stale, so the CB's DrawRenderer
            // calls — and the geometry-shader extrusion on top — emit
            // garbage triangles into the frame.
            //
            // Don't early-return: each effect owns its own attached
            // CommandBuffer / GameObject. If we skip Render() entirely the CB
            // stays attached to the camera and keeps drawing the stale parts
            // every frame. Instead synthesise a Vessel=null state and dispatch
            // it — every effect's existing "vessel-null / zero-intensity"
            // branch detaches its CB and gracefully stops rendering.
            var v = state.Vessel;
            bool gated = v == null || !v.loaded || v.packed;
            var dispatched = gated
                ? new FxFrameState(null, state.NearCam, Vector3.zero, 0f, 0f, state.Dt, state.Time)
                : state;
            /* FX are cosmetic: an effect that throws is dropped rather than
               allowed to abort the camera's whole capture frame. Backwards so
               removal doesn't skip the next effect. The layer bit stays in
               _enabled, so a later SetEnabledLayers toggle re-creates the
               effect — a deliberate retry path, not a leak. */
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                var fx = _effects[i];
                try
                {
                    fx.Render(dispatched);
                }
                catch (System.Exception ex)
                {
                    _effects.RemoveAt(i);
                    Debug.LogError($"[Kerbcast] FX {fx.GetType().Name} threw during Render — disabling: {ex}");
                    try { fx.Dispose(); }
                    catch (System.Exception dex) { Debug.LogError($"[Kerbcast] FX {fx.GetType().Name} dispose threw: {dex}"); }
                }
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _effects.Count; i++) _effects[i].Dispose();
            _effects.Clear();
            _enabled = AtmoFxLayers.None;
        }
    }
}
