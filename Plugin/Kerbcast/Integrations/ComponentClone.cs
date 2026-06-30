// Shared helper for capture integrations that clone a third-party mod's
// per-camera MonoBehaviour onto a kerbcast camera. Copies only the fields the
// component's own type hierarchy declares (the mod's configured state: material,
// light, body references) up to but not including UnityEngine.MonoBehaviour, so
// no Unity base state is touched. Each field copies in its own try/catch so one
// unsettable member cannot abort the rest.

using System;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal static class ComponentClone
    {
        private const string LogTag = "[Kerbcast-integrations]";

        // Copy src's declared instance fields (public + nonpublic) to dst.
        // src and dst must be the same runtime component type.
        public static void CopyDeclaredFields(Component src, Component dst)
        {
            if (src == null || dst == null) return;
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = src.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                foreach (var f in t.GetFields(Flags))
                {
                    try { f.SetValue(dst, f.GetValue(src)); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{LogTag} field copy skipped {t.Name}.{f.Name}: {ex.Message}");
                    }
                }
            }
        }
    }
}
