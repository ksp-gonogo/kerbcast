// Harmony patch fixing a dictionary-during-enumeration bug in the bundled
// UnityOpenGLAsyncReadback plugin (yangrc1234/UnityOpenGLAsyncReadback).
//
// Upstream's RenderTextureRegistery.ClearDeadRefs iterates two private static
// dicts (ptrs, cbPtrs) with `foreach` and calls Remove() on each from inside
// the loop:
//
//     foreach (var item in cbPtrs) {
//         if (item.Key == null) cbPtrs.Remove(item.Key);
//     }
//
// This throws InvalidOperationException ("Collection was modified") the
// instant any dead ref is present — and since the only way entries are
// removed IS ClearDeadRefs, dead refs accumulate indefinitely. The updater
// pumps this method every frame, so KSP.log fills with the same exception
// any time a kerbcast RenderTexture has been destroyed (camera disposal,
// scene change, part destruction).
//
// Upstream is unmaintained, so we patch in-process. Prefix replaces the
// buggy implementation with a snapshot-then-remove version and returns
// false to skip the original.
//
// Graceful degradation: if the registry type or fields are not found
// (different bundled plugin version), the patch logs and lets the
// original run.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Kerbcast
{
    [KSPAddon(KSPAddon.Startup.Instantly, once: true)]
    internal sealed class AsyncReadbackRegistryFix : MonoBehaviour
    {
        private const string HarmonyId = "kerbcast.asyncreadback.cleardeadrefs";
        private static Harmony _harmony;

        private void Awake()
        {
            // KSPAddon is once-per-game-session, but the host GameObject is
            // attached to the MainMenu scene by default — it would otherwise
            // be destroyed on the first scene transition, taking the Harmony
            // patch with it via OnDestroy below. The bug needs the patch alive
            // for the whole game session, not just MainMenu.
            DontDestroyOnLoad(gameObject);

            try
            {
                ApplyPatch();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast-ReadbackFix] failed to apply Harmony patch: {ex}");
            }
        }

        private static void ApplyPatch()
        {
            var registryType = AccessTools.TypeByName("Yangrc.OpenGLAsyncReadback.RenderTextureRegistery");
            if (registryType == null)
            {
                Debug.LogWarning("[Kerbcast-ReadbackFix] RenderTextureRegistery type not found; skipping patch");
                return;
            }

            var method = registryType.GetMethod(
                "ClearDeadRefs",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
            {
                Debug.LogWarning("[Kerbcast-ReadbackFix] ClearDeadRefs not found; skipping patch");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            var prefix = new HarmonyMethod(
                typeof(ClearDeadRefsPatch),
                nameof(ClearDeadRefsPatch.Prefix));
            _harmony.Patch(method, prefix: prefix);
            Debug.Log("[Kerbcast-ReadbackFix] patched RenderTextureRegistery.ClearDeadRefs");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;
        }
    }

    internal static class ClearDeadRefsPatch
    {
        private static FieldInfo _ptrsField;
        private static FieldInfo _cbPtrsField;
        private static bool _ready;

        public static bool Prefix()
        {
            try
            {
                EnsureReflection();
                if (!_ready) return true; // run original as fallback

                CleanDict(_ptrsField);
                CleanDict(_cbPtrsField);
                return false; // skip the buggy original
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast-ReadbackFix] prefix threw; falling back to original: {ex}");
                return true;
            }
        }

        private static void EnsureReflection()
        {
            if (_ready) return;
            var registryType = AccessTools.TypeByName("Yangrc.OpenGLAsyncReadback.RenderTextureRegistery");
            if (registryType == null) return;
            _ptrsField = registryType.GetField("ptrs", BindingFlags.NonPublic | BindingFlags.Static);
            _cbPtrsField = registryType.GetField("cbPtrs", BindingFlags.NonPublic | BindingFlags.Static);
            _ready = _ptrsField != null && _cbPtrsField != null;
        }

        // Snapshot keys, then remove dead entries in a second pass.
        // `ptrs` keys are Texture (UnityEngine.Object) so we need Unity's
        // "fake null" check; `cbPtrs` keys are ComputeBuffer (not a
        // UnityEngine.Object) so plain reference-null is correct. The
        // combined check below covers both safely.
        private static void CleanDict(FieldInfo field)
        {
            var dict = field.GetValue(null) as System.Collections.IDictionary;
            if (dict == null) return;

            List<object> deadKeys = null;
            foreach (var key in dict.Keys)
            {
                bool dead = key == null
                    || (key is UnityEngine.Object uo && uo == null);
                if (dead)
                {
                    if (deadKeys == null) deadKeys = new List<object>();
                    deadKeys.Add(key);
                }
            }

            if (deadKeys == null) return;
            foreach (var k in deadKeys) dict.Remove(k);
        }
    }
}
