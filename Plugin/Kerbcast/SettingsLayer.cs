// Unity-free key-application helpers behind KerbcastSettings' layered
// settings load (shipped defaults file first, then the user override file
// on top). The layering contract every helper implements:
//
//   - absent or empty key: keep the current value (no setter call), so
//     applying a second file over the first only changes the keys the
//     second file actually names;
//   - unparsable value: warn and keep the current value;
//   - values are trimmed before parsing; floats parse with
//     InvariantCulture ('.' decimal separator), the KSP config
//     convention regardless of player locale.
//
// Takes a value getter (ConfigNode.GetValue in the plugin, a dictionary
// lookup in the console tests) and a warn sink (Debug.LogWarning in the
// plugin) so this class has no UnityEngine or KSP dependency and stays
// runnable from the SettingsLayer.Tests harness.

using System;
using System.Globalization;

namespace Kerbcast
{
    internal static class SettingsLayer
    {
        public static void ApplyString(Func<string, string> get, string key, Action<string> set)
        {
            var raw = get(key);
            if (!string.IsNullOrEmpty(raw)) set(raw.Trim());
        }

        public static void ApplyInt(Func<string, string> get, string key, Action<int> set, Action<string> warn)
        {
            var raw = get(key);
            if (string.IsNullOrEmpty(raw)) return;
            if (int.TryParse(raw.Trim(), out int v)) set(v);
            else warn($"[Kerbcast] settings.cfg: {key}='{raw}' is not an integer; keeping current value");
        }

        public static void ApplyBool(Func<string, string> get, string key, Action<bool> set, Action<string> warn)
        {
            var raw = get(key);
            if (string.IsNullOrEmpty(raw)) return;
            if (bool.TryParse(raw.Trim(), out bool v)) set(v);
            else warn($"[Kerbcast] settings.cfg: {key}='{raw}' is not a bool; keeping current value");
        }

        public static void ApplyFloat(Func<string, string> get, string key, Action<float> set, Action<string> warn)
        {
            var raw = get(key);
            if (string.IsNullOrEmpty(raw)) return;
            if (float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                set(v);
            else warn($"[Kerbcast] settings.cfg: {key}='{raw}' is not a number; keeping current value");
        }

        // Parses three comma-separated floats: `x, y, z`. The plugin wraps
        // the setter to build a UnityEngine.Vector3; this class stays
        // Unity-free by handing the components over individually.
        public static void ApplyFloat3(Func<string, string> get, string key, Action<float, float, float> set, Action<string> warn)
        {
            var raw = get(key);
            if (string.IsNullOrEmpty(raw)) return;
            var parts = raw.Split(',');
            if (parts.Length != 3)
            {
                warn($"[Kerbcast] settings.cfg: {key}='{raw}' must be three comma-separated floats; keeping current value");
                return;
            }
            if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                set(x, y, z);
            else
                warn($"[Kerbcast] settings.cfg: {key}='{raw}' contains a non-float component; keeping current value");
        }

        // Nullable int parse for fields that need presence detection (the
        // per-camera Width/Height overrides). null = absent or unparsable;
        // only the unparsable case warns.
        public static int? TryParseInt(Func<string, string> get, string key, Action<string> warn)
        {
            var raw = get(key);
            if (string.IsNullOrEmpty(raw)) return null;
            if (int.TryParse(raw.Trim(), out int v)) return v;
            warn($"[Kerbcast] settings.cfg: {key}='{raw}' is not an integer; ignoring");
            return null;
        }

        /* Parses a comma-separated Layers value from settings.cfg into a
           CameraLayers mask. Tokens are case-insensitive; unknown tokens warn
           and are skipped. An empty or all-invalid list returns All rather
           than None, almost certainly not what the operator intended. */
        public static CameraLayers ParseCameraLayers(string raw, Action<string> warn)
        {
            var mask = CameraLayers.None;
            foreach (var tok in raw.Split(','))
            {
                var t = tok.Trim();
                if (t.Equals("NEAR", StringComparison.OrdinalIgnoreCase))   mask |= CameraLayers.Near;
                else if (t.Equals("FAR", StringComparison.OrdinalIgnoreCase))    mask |= CameraLayers.Far;
                else if (t.Equals("SCALED", StringComparison.OrdinalIgnoreCase)) mask |= CameraLayers.Scaled;
                else if (t.Equals("GALAXY", StringComparison.OrdinalIgnoreCase)) mask |= CameraLayers.Galaxy;
                else if (t.Equals("ALL", StringComparison.OrdinalIgnoreCase))    mask |= CameraLayers.All;
                else if (!string.IsNullOrEmpty(t))
                    warn($"[Kerbcast] settings.cfg: unknown layer '{t}', skipping");
            }
            return mask == CameraLayers.None ? CameraLayers.All : mask;
        }
    }
}
