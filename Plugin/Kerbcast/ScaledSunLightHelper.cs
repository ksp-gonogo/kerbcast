// Strips and restores Scatterer's screen-space shadow CommandBuffers from KSP's
// sun light(s) around kerbcast's offscreen clone renders.
//
// Scatterer attaches persistent, screen-space, depth-tested shadow-mask buffers
// to the sun light for the whole session (ScattererScreenspaceShadowMaskCopy /
// ScattererScreenspaceShadowMaskmodulate at LightEvent.AfterScreenspaceMask, and
// a shadow-map retrieve at LightEvent.AfterShadowMap), and installs its
// per-camera fade compensator only on the real flight camera. Our clones
// CopyFrom the KSP cameras, but Unity does not copy per-camera CommandBuffers, so
// a clone runs the sun light's shadow pass WITHOUT that compensator. The result
// is thin, fixed-screen-position, depth-occluded dark bands painted at planet
// depth on the stream.
//
// Bracketing the whole clone composite (galaxy -> scaled -> far -> near) with
// strip/restore keeps those passes off our cameras while leaving them intact for
// the main flight render: the buffers are restored every tick before Unity
// renders the real cameras. Also covers KSP's legacy "Composite Shadows" buffer
// name. With no Scatterer (or other buffer-attaching mod) installed there are no
// matching buffers, so both calls are a no-op.

using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kerbcast
{
    internal static class ScaledSunLightHelper
    {
        // Cached after the first successful resolve (Sun.Instance's lights don't
        // change between scene loads in normal play). Null entries are tolerated:
        // a missing light just means fewer buffers to strip.
        private static Light _cachedScaledSunLight;
        private static Light _cachedNearSunLight;
        private static bool _sunLightsResolved;

        // Saved for exact restore: one entry per stripped (light, event, buffer).
        private struct StrippedBuffer
        {
            public Light Light;
            public LightEvent Event;
            public CommandBuffer Buffer;
        }

        // Reused across calls to avoid per-frame allocation. Only touched on the
        // Unity main thread inside a single strip/restore pair, so no locking.
        private static readonly List<StrippedBuffer> _stripped = new List<StrippedBuffer>();

        // The light events Scatterer attaches its screen-space shadow passes to.
        private static readonly LightEvent[] ShadowEvents =
        {
            LightEvent.AfterShadowMap,
            LightEvent.AfterScreenspaceMask,
        };

        // KSP's legacy deferred buffer name, plus every Scatterer-attached buffer
        // (all of Scatterer's buffer names start with "Scatterer").
        private static bool ShouldStrip(string name)
            => name == "Composite Shadows"
               || (name != null && name.StartsWith("Scatterer"));

        /// <summary>
        /// The scaled-space sun light from <c>Sun.Instance</c>, resolved via
        /// reflection on first use and cached. Null if Sun.Instance is not yet
        /// available or the field is absent in this KSP version.
        /// </summary>
        public static Light GetScaledSunLight()
        {
            ResolveSunLights();
            return _cachedScaledSunLight;
        }

        // Resolve both sun lights once Sun.Instance exists. scaledSunLight is a
        // protected field; sunLight is a public property returning the near-scene
        // local light. Retries each call until Sun.Instance is available, then
        // caches (even a null result) so reflection runs at most once.
        private static void ResolveSunLights()
        {
            if (_sunLightsResolved) return;
            if (Sun.Instance == null) return;
            _sunLightsResolved = true;
            _cachedScaledSunLight = ResolveLight("scaledSunLight");
            _cachedNearSunLight = ResolveLight("sunLight");
        }

        private static Light ResolveLight(string memberName)
        {
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var field = typeof(Sun).GetField(memberName, flags);
            if (field != null) return field.GetValue(Sun.Instance) as Light;
            var prop = typeof(Sun).GetProperty(memberName, flags);
            return prop?.GetValue(Sun.Instance, null) as Light;
        }

        /// <summary>
        /// Removes every Scatterer (and legacy "Composite Shadows") shadow
        /// CommandBuffer from both sun lights across both shadow light-events,
        /// saving each for <see cref="RestoreCompositeShadowsBuffer"/>. Safe to
        /// call when a light or buffer is absent: it degrades to a no-op.
        /// </summary>
        public static void StripCompositeShadowsBuffer()
        {
            _stripped.Clear();
            ResolveSunLights();
            StripFrom(_cachedScaledSunLight);
            StripFrom(_cachedNearSunLight);
        }

        private static void StripFrom(Light light)
        {
            if (light == null) return;
            foreach (var ev in ShadowEvents)
            {
                // GetCommandBuffers returns a fresh array, so removing while
                // iterating it is safe.
                foreach (var buf in light.GetCommandBuffers(ev))
                {
                    if (!ShouldStrip(buf.name)) continue;
                    _stripped.Add(new StrippedBuffer { Light = light, Event = ev, Buffer = buf });
                    light.RemoveCommandBuffer(ev, buf);
                }
            }
        }

        /// <summary>
        /// Re-attaches every CommandBuffer removed by
        /// <see cref="StripCompositeShadowsBuffer"/>. Idempotent: a second call
        /// with nothing saved is a no-op, so it is safe to call both on the
        /// normal path and from a catch handler as a safety net.
        /// </summary>
        public static void RestoreCompositeShadowsBuffer()
        {
            for (int i = 0; i < _stripped.Count; i++)
            {
                var s = _stripped[i];
                if (s.Light != null) s.Light.AddCommandBuffer(s.Event, s.Buffer);
            }
            _stripped.Clear();
        }

        /// <summary>
        /// Drops the cached sun-light references so they re-resolve on next use.
        /// Not required in normal operation (the cache is valid for a session).
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedScaledSunLight = null;
            _cachedNearSunLight = null;
            _sunLightsResolved = false;
        }
    }
}
