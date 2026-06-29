// Defensive helpers for stripping/restoring KSP's "Composite Shadows"
// CommandBuffer on scaledSunLight around offscreen Scaled-space camera
// renders. In Scatterer-installed configs KSP's deferred renderer attaches
// a CommandBuffer named "Composite Shadows" at LightEvent.AfterScreenspaceMask;
// if our extra offscreen Scaled camera renders while that buffer is live, the
// buffer runs against the wrong framebuffer and nulls out the sun's diffuse
// contribution to planet surfaces. Bracketing our camera.Render() call with
// strip/restore prevents that. Configs without Scatterer (or any other mod
// attaching such a buffer) get a no-op: the strip iterates zero buffers and
// the restore is a no-op too. Doesn't fix the broader Mesa-OpenGL black-
// planet-surface issue documented in local_docs/kerbcast/known_issues.md in
// the gonogo repo — that issue affects every offscreen-Scaled-camera mod
// on this rendering stack.

using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kerbcast
{
    internal static class ScaledSunLightHelper
    {
        // Cached after the first successful reflection lookup. Stays valid for
        // the lifetime of the KSP session (Sun.Instance doesn't change between
        // scene loads in normal play). Null means either the field doesn't exist
        // (KSP version mismatch) or Sun.Instance isn't available yet — both are
        // handled gracefully by the strip/restore no-op path.
        private static Light _cachedScaledSunLight;
        private static bool _sunLightResolved;

        // Reused across calls to avoid per-frame Dictionary allocation. This
        // helper is only called from the Unity main thread (inside LateUpdate →
        // Refresh) so there's no need for thread safety.
        private static readonly Dictionary<Light, CommandBuffer> _strippedBuffers =
            new Dictionary<Light, CommandBuffer>();

        private const string BufferName = "Composite Shadows";

        /// <summary>
        /// Returns the <c>scaledSunLight</c> field from <c>Sun.Instance</c>,
        /// resolving it via reflection on first call and caching for the session.
        /// Returns <c>null</c> if <c>Sun.Instance</c> is not yet available or
        /// the field is not present in this KSP version.
        /// </summary>
        public static Light GetScaledSunLight()
        {
            if (_sunLightResolved) return _cachedScaledSunLight;

            _sunLightResolved = true; // only attempt once even on failure
            if (Sun.Instance == null) return null;

            var field = typeof(Sun).GetField(
                "scaledSunLight",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _cachedScaledSunLight = field?.GetValue(Sun.Instance) as Light;
            return _cachedScaledSunLight;
        }

        /// <summary>
        /// Removes the "Composite Shadows" CommandBuffer from
        /// <c>scaledSunLight.AfterScreenspaceMask</c>, saving it for
        /// <see cref="RestoreCompositeShadowsBuffer"/>. Safe to call even if
        /// the light or buffer is absent — degrades to a no-op.
        /// </summary>
        public static void StripCompositeShadowsBuffer()
        {
            _strippedBuffers.Clear();
            var sunLight = GetScaledSunLight();
            if (sunLight == null) return;

            foreach (var buf in sunLight.GetCommandBuffers(LightEvent.AfterScreenspaceMask))
            {
                if (buf.name != BufferName) continue;
                _strippedBuffers[sunLight] = buf;
                sunLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, buf);
            }
        }

        /// <summary>
        /// Re-attaches any CommandBuffers previously removed by
        /// <see cref="StripCompositeShadowsBuffer"/>. Must be called after
        /// the Scaled camera's <c>Render()</c> completes (or throws) to ensure
        /// the main flight render continues to receive correct shadow compositing.
        /// </summary>
        public static void RestoreCompositeShadowsBuffer()
        {
            foreach (var kvp in _strippedBuffers)
                kvp.Key.AddCommandBuffer(LightEvent.AfterScreenspaceMask, kvp.Value);
            _strippedBuffers.Clear();
        }

        /// <summary>
        /// Resets the cached sun-light reference so it will be re-resolved on
        /// the next call to <see cref="GetScaledSunLight"/>. Call this if
        /// Sun.Instance may have changed (e.g. on scene reload). Not required
        /// in normal operation — the cache is valid for a session lifetime.
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedScaledSunLight = null;
            _sunLightResolved = false;
        }
    }
}
