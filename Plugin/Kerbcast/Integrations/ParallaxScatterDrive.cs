// Per-clone ParallaxContinued scatter draw. Parallax evaluates its GPU-instanced
// scatters once per frame into buffers shared across all cameras (culled for the
// player's view) and submits the draws to the stock flight cameras. To make those
// scatters appear on a kerbcast clone, this component (attached to the near and far
// clones) re-submits Parallax's existing draws to the clone in OnPreCull. It does
// NOT re-evaluate: the instance transforms are world-space and correct from any
// viewpoint, and re-evaluating would corrupt the shared buffers the player's own
// view draws from and multiply the per-frame GPU cost. The tradeoff is that
// scatters use the player's culling/LOD set, so some may be missing near a feed's
// edges when the player looks elsewhere. All members are reflected in by
// ParallaxIntegration; this component holds no compile-time Parallax reference.
//
// RenderInCameras only queues an indirect draw; with no scatters loaded the active
// renderer set is empty and this is a no-op.

using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class ParallaxScatterDrive : MonoBehaviour
    {
        public FieldInfo ManagerInstance;        // static ScatterManager Instance (field)
        public FieldInfo ActiveRenderersField;   // List<ScatterRenderer> (instance)
        public MethodInfo RenderInCamerasMethod; // ScatterRenderer.RenderInCameras(params Camera[])

        private Camera _cam;

        private void Awake() => _cam = GetComponent<Camera>();

        private void OnPreCull()
        {
            if (!Ready()) return;
            try
            {
                object manager = ManagerInstance.GetValue(null);
                if (manager == null) return;
                var renderers = ActiveRenderersField.GetValue(manager) as IEnumerable;
                if (renderers == null) return;

                var camArg = new object[] { new Camera[] { _cam } };
                foreach (var r in renderers) RenderInCamerasMethod.Invoke(r, camArg);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast-Parallax] draw failed: {ex.Message}");
            }
        }

        private bool Ready()
        {
            return _cam != null && ManagerInstance != null
                && ActiveRenderersField != null && RenderInCamerasMethod != null;
        }
    }
}
