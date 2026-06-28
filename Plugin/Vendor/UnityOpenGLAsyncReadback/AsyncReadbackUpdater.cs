using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Yangrc.OpenGLAsyncReadback {
    /// <summary>
    /// A helper class to trigger readback update every frame.
    /// </summary>
    [AddComponentMenu("")]
    public class AsyncReadbackUpdater : MonoBehaviour {
        public static AsyncReadbackUpdater instance;
        private void Awake() {
            instance = this;
        }
        // kerbcast fix (not upstream): clear the static on destroy so a consumer
        // that re-checks `instance == null` (e.g. KerbcastCore re-spawning the
        // pump on the next Flight scene) reliably sees null. Upstream never
        // nulled it, leaving a stale reference after the GameObject was
        // destroyed; the pump then failed to re-spawn and async readbacks wedged
        // until a full restart. See local_docs/perf_profiles/session_20260606.md.
        private void OnDestroy() {
            if (instance == this) instance = null;
        }
        void Update() {
            OpenGLAsyncReadbackRequest.Update();
            RenderTextureRegistery.ClearDeadRefs();
        }
    }
}