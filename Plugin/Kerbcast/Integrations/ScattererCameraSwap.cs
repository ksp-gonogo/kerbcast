// Per-clone Scatterer camera-reference swap. Scatterer renders against the
// Camera it has cached in its singleton; to make a kerbcast clone pick up
// Scatterer during the clone's render, this component (attached to the clone)
// overwrites the relevant singleton camera field to point at the clone in
// OnPreCull and restores the stock reference in OnPostRender. OnDisable restores
// defensively so the player's main view is never left pointing at our clone.
//
// The singleton instance is read live each frame via the Instance property
// getter, so a Scatterer that initialises after our cameras are built is still
// picked up. Reflection-only: no compile-time Scatterer reference.

using System;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class ScattererCameraSwap : MonoBehaviour
    {
        // Scatterer.Scatterer.Instance (public static property) and the camera
        // field on that instance this component swaps (nearCamera /
        // scaledSpaceCamera / farCamera). Set by ScattererIntegration after
        // AddComponent.
        public PropertyInfo InstanceProperty;
        public FieldInfo CameraField;

        // Camera to write into CameraField during the swap. Defaults to this
        // component's own camera; set to a sibling when a clone must masquerade as a
        // DIFFERENT Scatterer camera than itself (the near clone points
        // scaledSpaceCamera at the scaled clone so the sunflare's scaled-space
        // viewport math is computed in the right coordinate space).
        public Camera SwapInOverride;

        private Camera _cam;
        private object _savedInstance;
        private object _savedValue;
        private bool _swapped;

        private void Awake() => _cam = GetComponent<Camera>();

        private void OnPreCull()
        {
            if (_swapped) return; // nested/duplicate guard
            if (InstanceProperty == null || CameraField == null || _cam == null) return;
            try
            {
                var inst = InstanceProperty.GetValue(null, null);
                if (inst == null) return;
                var swapIn = SwapInOverride != null ? SwapInOverride : _cam;
                if (swapIn == null) return;
                _savedInstance = inst;
                _savedValue = CameraField.GetValue(inst);
                CameraField.SetValue(inst, swapIn);
                _swapped = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast-Scatterer] swap failed: {ex.Message}");
                _swapped = false;
            }
        }

        private void OnPostRender() => Restore();
        private void OnDisable() => Restore();

        private void Restore()
        {
            if (!_swapped) return;
            try { CameraField.SetValue(_savedInstance, _savedValue); }
            catch (Exception ex) { Debug.LogError($"[Kerbcast-Scatterer] restore failed: {ex.Message}"); }
            finally { _swapped = false; }
        }
    }
}
