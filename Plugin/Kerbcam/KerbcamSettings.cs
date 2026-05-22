// settings.cfg loader, OCISLY-style. Mirrors the shape used by
// OfCourseIStillLoveYou's own settings.cfg — a single top-level
// `Settings { ... }` node parsed via KSP's ConfigNode API. All fields
// are optional; missing ones fall back to the defaults below.
//
// Top-level fields:
//   BindAddress       — host the sidecar's HTTP signalling endpoint
//                       binds to. 127.0.0.1 = localhost only (safe).
//                       0.0.0.0 = any interface (needed for LAN
//                       streaming to gonogo / a browser on another
//                       machine).
//   Port              — sidecar HTTP signalling port (default 8088).
//   Width / Height    — capture dimensions per Hullcam (default 768).
//                       Larger = more pixels to push through openh264
//                       on the CPU.
//   AutoSpawnSidecar  — whether the plugin should `Process.Start` the
//                       bundled sidecar binary on Awake. Set to false
//                       during sidecar development so `cargo run`
//                       owns the process.
//   EnableAdaptiveShed — whether the plugin steps the per-camera
//                       resolution / layer-mask cascade down when KSP
//                       fps drops below the ShedBelow thresholds.
//                       Default true; set false for perf-comparison
//                       runs where you want the raw camera cost
//                       without the cascade masking it.
//
// Per-camera override nodes (zero or more `Camera { ... }` blocks):
//   PartName          — internal KSP part name (e.g. "navCam1"). Match
//                       case-sensitive.
//   Layers            — comma-separated subset of NEAR, SCALED, GALAXY.
//                       Sets the initial layer mask for the camera on
//                       attach; operator can still override at runtime
//                       via POST /cameras/{id}/layers.
//
// Per-camera Width / Height overrides aren't supported yet — the
// sidecar still opens all rings at the global max dims. Plumbing
// variable dims through MmapRingConfig is a follow-up.

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Kerbcam
{
    internal sealed class KerbcamSettings
    {
        public string BindAddress { get; private set; } = "0.0.0.0";
        public int Port { get; private set; } = 8088;
        public int Width { get; private set; } = 768;
        public int Height { get; private set; } = 768;
        public bool AutoSpawnSidecar { get; private set; } = true;
        public bool EnableAdaptiveShed { get; private set; } = true;

        // Debug: when true, log additional per-camera diagnostics
        // useful for investigating render-mask / cullingMask issues
        // (atmospheric FX missing from streams, layer mismatches
        // after vessel changes, etc). Off by default — log spam in
        // KSP.log otherwise. Static so KerbcamCamera can read it
        // without needing a back-ref to the settings instance.
        public static bool DebugCameraLogging { get; private set; } = false;

        // Default for the per-save ThrottleMainScreen Difficulty Setting
        // when a save is loaded for the first time. After that the
        // value stored in the save file wins; settings.cfg changes
        // don't retroactively override existing saves. Read by
        // KerbcamGameParameters's constructor.
        public static bool SeedThrottleMainScreen { get; private set; } = false;

        // KeyCode the operator can press at runtime to toggle the
        // throttle live without opening the Difficulty Settings menu.
        // Unbound (KeyCode.None) by default; set via settings.cfg
        // ThrottleMainScreenKey. Examples: F11, M (collision with
        // map view! avoid), Numlock, ScrollLock, Quote.
        public static KeyCode ThrottleMainScreenKey { get; private set; } = KeyCode.None;

        public string HttpBind => $"{BindAddress}:{Port}";

        // Per-PartName initial layer mask (e.g. "navCam1" → NEAR only).
        // Cameras whose PartName isn't here default to CameraLayers.All.
        private readonly Dictionary<string, CameraLayers> _initialLayers =
            new Dictionary<string, CameraLayers>();
        // Per-PartName render-size overrides. Each entry is (width, height);
        // cameras without an override use the global Width × Height.
        private readonly Dictionary<string, (int, int)> _renderSize =
            new Dictionary<string, (int, int)>();

        /// <summary>
        /// Initial layer mask for a part. Falls back to All if no override
        /// applies. Operator can still change layers at runtime via the
        /// sidecar's /layers endpoint — this only sets the value on attach.
        /// </summary>
        public CameraLayers GetInitialLayers(string partName)
        {
            if (!string.IsNullOrEmpty(partName) && _initialLayers.TryGetValue(partName, out var layers))
            {
                return layers;
            }
            return CameraLayers.All;
        }

        /// <summary>
        /// Per-PartName render-size override, if any. Returns the global
        /// Width × Height when no override is configured.
        /// </summary>
        public (int width, int height) GetRenderSize(string partName)
        {
            if (!string.IsNullOrEmpty(partName) && _renderSize.TryGetValue(partName, out var dims))
            {
                return dims;
            }
            return (Width, Height);
        }

        public static KerbcamSettings Load()
        {
            var settings = new KerbcamSettings();
            var path = Path.Combine(
                KSPUtil.ApplicationRootPath,
                "GameData", "Kerbcam", "settings.cfg");
            if (!File.Exists(path))
            {
                Debug.Log($"[Kerbcam] no settings.cfg at {path}; using defaults ({settings.HttpBind}, {settings.Width}×{settings.Height})");
                return settings;
            }

            var root = ConfigNode.Load(path);
            var node = root?.GetNode("Settings");
            if (node == null)
            {
                Debug.LogWarning($"[Kerbcam] settings.cfg at {path} is missing a 'Settings' node; using defaults");
                return settings;
            }

            ApplyString(node, "BindAddress", v => settings.BindAddress = v);
            ApplyInt(node, "Port", v => settings.Port = v);
            ApplyInt(node, "Width", v => settings.Width = v);
            ApplyInt(node, "Height", v => settings.Height = v);
            ApplyBool(node, "AutoSpawnSidecar", v => settings.AutoSpawnSidecar = v);
            ApplyBool(node, "EnableAdaptiveShed", v => settings.EnableAdaptiveShed = v);
            ApplyBool(node, "DebugCameraLogging", v => DebugCameraLogging = v);
            // Static slots so KerbcamGameParameters (constructed by
            // KSP before our plugin instance loads) can pick up the
            // seed values. Settings.cfg is the source of truth for
            // first-time-on-this-save defaults.
            ApplyBool(node, "ThrottleMainScreen", v => SeedThrottleMainScreen = v);
            ApplyString(node, "ThrottleMainScreenKey", v =>
            {
                if (System.Enum.TryParse<KeyCode>(v.Trim(), ignoreCase: true, out var k))
                {
                    ThrottleMainScreenKey = k;
                }
                else
                {
                    Debug.LogWarning($"[Kerbcam] settings.cfg: ThrottleMainScreenKey='{v}' is not a Unity KeyCode; ignoring");
                }
            });

            foreach (var camNode in node.GetNodes("Camera"))
            {
                var partName = camNode.GetValue("PartName")?.Trim();
                if (string.IsNullOrEmpty(partName))
                {
                    Debug.LogWarning("[Kerbcam] settings.cfg: Camera node missing PartName, skipping");
                    continue;
                }
                var layersRaw = camNode.GetValue("Layers");
                if (!string.IsNullOrEmpty(layersRaw))
                {
                    settings._initialLayers[partName] = ParseLayers(layersRaw);
                }
                // Per-camera Width/Height overrides must be even (H.264
                // chroma sampling) and <= the global Width/Height (the
                // ring's allocated capacity). Either field defaults to the
                // global value if omitted.
                int? w = TryParseIntField(camNode, "Width");
                int? h = TryParseIntField(camNode, "Height");
                if (w.HasValue || h.HasValue)
                {
                    int width = w ?? settings.Width;
                    int height = h ?? settings.Height;
                    if (width % 2 != 0) width = width - (width & 1);
                    if (height % 2 != 0) height = height - (height & 1);
                    if (width > settings.Width || height > settings.Height)
                    {
                        Debug.LogWarning($"[Kerbcam] settings.cfg: Camera '{partName}' size {width}x{height} exceeds global max {settings.Width}x{settings.Height}; capping");
                        if (width > settings.Width) width = settings.Width;
                        if (height > settings.Height) height = settings.Height;
                    }
                    settings._renderSize[partName] = (width, height);
                }
            }

            var camCount = settings._initialLayers.Count;
            Debug.Log($"[Kerbcam] settings loaded: bind={settings.HttpBind} dims={settings.Width}x{settings.Height} autoSpawn={settings.AutoSpawnSidecar} cameraOverrides={camCount}");
            return settings;
        }

        private static CameraLayers ParseLayers(string raw)
        {
            var mask = CameraLayers.None;
            foreach (var tok in raw.Split(','))
            {
                var t = tok.Trim();
                if (t.Equals("NEAR", System.StringComparison.OrdinalIgnoreCase)) mask |= CameraLayers.Near;
                else if (t.Equals("SCALED", System.StringComparison.OrdinalIgnoreCase)) mask |= CameraLayers.Scaled;
                else if (t.Equals("GALAXY", System.StringComparison.OrdinalIgnoreCase)) mask |= CameraLayers.Galaxy;
                else if (t.Equals("ALL", System.StringComparison.OrdinalIgnoreCase)) mask |= CameraLayers.All;
                else if (!string.IsNullOrEmpty(t))
                {
                    Debug.LogWarning($"[Kerbcam] settings.cfg: unknown layer '{t}', skipping");
                }
            }
            // An empty / all-invalid Layers list would be CameraLayers.None
            // and mean "render nothing for this camera" — almost certainly
            // not what the operator meant. Fall back to All.
            return mask == CameraLayers.None ? CameraLayers.All : mask;
        }

        private static int? TryParseIntField(ConfigNode node, string key)
        {
            var raw = node.GetValue(key);
            if (string.IsNullOrEmpty(raw)) return null;
            if (int.TryParse(raw.Trim(), out int v)) return v;
            Debug.LogWarning($"[Kerbcam] settings.cfg: {key}='{raw}' is not an integer; ignoring");
            return null;
        }

        private static void ApplyString(ConfigNode node, string key, System.Action<string> set)
        {
            var raw = node.GetValue(key);
            if (!string.IsNullOrEmpty(raw)) set(raw.Trim());
        }

        private static void ApplyInt(ConfigNode node, string key, System.Action<int> set)
        {
            var raw = node.GetValue(key);
            if (string.IsNullOrEmpty(raw)) return;
            if (int.TryParse(raw.Trim(), out int v)) set(v);
            else Debug.LogWarning($"[Kerbcam] settings.cfg: {key}='{raw}' is not an integer; using default");
        }

        private static void ApplyBool(ConfigNode node, string key, System.Action<bool> set)
        {
            var raw = node.GetValue(key);
            if (string.IsNullOrEmpty(raw)) return;
            if (bool.TryParse(raw.Trim(), out bool v)) set(v);
            else Debug.LogWarning($"[Kerbcam] settings.cfg: {key}='{raw}' is not a bool; using default");
        }
    }
}
