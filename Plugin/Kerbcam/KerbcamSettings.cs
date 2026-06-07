// settings.cfg loader, OCISLY-style. Mirrors the shape used by
// OfCourseIStillLoveYou's own settings.cfg: a single top-level
// `Settings { ... }` node parsed via KSP's ConfigNode API. All fields
// are optional; missing ones fall back to the defaults below.
//
// Top-level fields:
//   BindAddress       host the sidecar's HTTP signalling endpoint binds
//                     to. 127.0.0.1 = localhost only (the default). A LAN
//                     IP or 0.0.0.0 exposes the feeds to other devices,
//                     with no authentication, so only on a trusted network.
//   Port              sidecar HTTP signalling port (default 8088).
//   Width / Height    capture dimensions per Hullcam (default 1024x576,
//                     16:9). Larger = more pixels to encode.
//
// Per-camera override nodes (zero or more `Camera { ... }` blocks):
//   PartName          internal KSP part name (e.g. "navCam1"), case-sensitive.
//   Layers            comma-separated subset of NEAR, SCALED, GALAXY (or ALL).
//                     Sets the initial layer mask for the camera on attach;
//                     operator can still override at runtime via POST
//                     /cameras/{id}/layers.
//   EnableAtmosphericFx  per-camera override (true/false) for atmospheric FX
//                     replication. Overrides the top-level default either
//                     direction; operator can flip it at runtime via the
//                     control block's enableAtmosphericFx.
//
// Per-camera Width / Height overrides aren't supported yet: the sidecar
// still opens all rings at the global max dims. Plumbing variable dims
// through MmapRingConfig is a follow-up.
//
//   EnableHullcamLinuxShaderSwap  (Linux only, default true) when true,
//                     kerbcam Harmony-patches CameraFilter.LoadBundle to
//                     load our rebuilt shaders.linux bundle from
//                     GameData/Kerbcam/HullcamShaders/ instead of
//                     HullcamVDS's broken upstream bundle. Set to false
//                     to disable the swap and test against upstream's
//                     bundle directly.
//
//   AutoSpawnSidecar  whether the plugin Process.Starts the bundled sidecar
//                     binary on Awake (default true). Undocumented in the
//                     shipped settings.cfg; it exists as a dev escape hatch
//                     so `cargo run` can own the sidecar process during
//                     sidecar development. Set false in your install's
//                     settings.cfg in that case.

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Kerbcam
{
    internal sealed class KerbcamSettings
    {
        public string BindAddress { get; private set; } = "127.0.0.1";
        public int Port { get; private set; } = 8088;
        public int Width { get; private set; } = 1024;
        public int Height { get; private set; } = 576;
        public bool AutoSpawnSidecar { get; private set; } = true;
        // Opt-in QUALITY shedding (resolution + FX-layer cascade). Default
        // false: by default kerbcam degrades *temporally* instead (capture
        // staggering scales cuts up as fps drops — see ReadbackScheduler), which
        // keeps full image quality. Set true to also drop resolution/FX layers
        // under load.
        // Per-camera capture-rate CEILING (stream target). Cameras are
        // round-robined so they don't all render + read back on the same frame,
        // and each captures at MOST this rate rather than the full game fps. Set
        // >= your game fps cap to disable the rate cap (the frame budget below
        // still applies).
        public float MaxCaptureFps { get; private set; } = 30f;

        // Stagger budget CEILING: the most main-thread time (ms/frame) kerbcam
        // will spend on its own render + readback. When it would exceed this,
        // kerbcam captures FEWER cameras per frame (each updates less often, but
        // at full resolution + all layers — a LOSSLESS temporal degrade). Targets
        // kerbcam's OWN cost, so it's independent of how slow the game is for
        // other reasons (a heavy vessel won't make it starve the feeds). Default
        // 24 ms (≈6–7 cameras at the Deck's ~3.5 ms/camera, landing ~25 fps with
        // 8 streaming — comfortably above the MinKspFps floor while using the
        // headroom a lower budget would leave idle). Set 0 to remove the ms cap
        // entirely, making MinKspFps the control target ("capture everything down
        // to that fps floor"). kerbcam NEVER drops quality to hit this — that's a
        // manual/API-only choice.
        public float MaxKerbcamFrameBudgetMs { get; private set; } = 24f;

        // Physics-floor safety (one-way). If game fps drops below this, kerbcam
        // staggers HARDER than the budget above to keep KSP above its time-
        // dilation threshold — below which game-time slows (physics can't keep
        // real-time) AND the stream itself goes slow-motion + low-cadence. Only
        // ever tightens / gates restore, so it can't set up a headroom-chasing
        // oscillation. Default 18 fps (just above typical time dilation). Set 0
        // to disable the floor (kerbcam then bounds only by MaxKerbcamFrameBudgetMs).
        public float MinKspFps { get; private set; } = 18f;

        // Master toggle for kerbcam's own atmospheric FX (a pluggable overlay
        // — see atmospheric_fx_parked.md for why the stock-FX replication was
        // abandoned). Default ON since v0.7.0; set false (or per-camera) to
        // disable.
        public bool EnableAtmosphericFx { get; private set; } = true;

        // Which FX layers are active when the master is on — individually
        // toggleable. settings.cfg token list: CORE, BOWSHOCK, TRAIL, EMBERS
        // (or ALL). Defaults to all four — bowshock now uses an oblate dome
        // (replaces the polygonal cone), embers are a geom-shader extrusion
        // off windward surfaces (replaces ParticleSystem), trail and
        // bowshock both adapt position+size from a per-frame windward
        // profile so they correctly track vessel orientation.
        public AtmoFxLayers AtmosphericFxLayers { get; private set; } = AtmoFxLayers.All;

        // Apply Hullcam VDS's per-part shader filters (NightVision green
        // grain, MovieTime film effect, CRT/TV scanlines, etc — 9 modes
        // total). Each cam reads its hullcam.cameraMode field at attach
        // and instantiates the matching HullcamVDS.CameraFilter; the
        // existing capture-RT → readback-RT blit becomes a
        // RenderImageWithFilter call. Default true so kerbcam streams
        // show the same visual effects as Hullcam's own in-game UI.
        // Set false to skip the filter pass (pure unfiltered composite).
        public static bool EnableHullcamEffects { get; private set; } = true;

        // Run TUFX post-processing on each layered kerbcam camera when TUFX
        // is installed. Without it, atmospheric scattering on Kerbin's horizon
        // has too wide a dynamic range to display correctly even with
        // allowHDR=true: the bright sky clips and the rest crushes dark
        // ("dark Kerbin / black hole horizon"). Default true; silently no-ops
        // when TUFX is not installed. Static so KerbcamCamera can read it.
        public static bool EnableTUFX { get; private set; } = true;

        // Name of the installed TUFX profile to attach to kerbcam's per-camera
        // volumes. Default empty: volumes inherit TUFX's own scene selection
        // (whatever the operator picked in TUFX's UI for Flight). No effect
        // without TUFX installed.
        public static string TUFXProfile { get; private set; } = "";

        // When true (default), kerbcam Harmony-patches HullcamVDS's
        // CameraFilter.LoadBundle to substitute our rebuilt shaders.linux.
        // Linux-only; set false to test against upstream's broken bundle.
        // Static so HullcamShaderBundleSwap (Instantly-scoped, no settings
        // instance) can read it without a back-ref.
        public static bool EnableHullcamLinuxShaderSwap { get; private set; } = true;

        // Debug: when true, log additional per-camera diagnostics
        // useful for investigating render-mask / cullingMask issues
        // (atmospheric FX missing from streams, layer mismatches
        // after vessel changes, etc). Off by default — log spam in
        // KSP.log otherwise. Static so KerbcamCamera can read it
        // without needing a back-ref to the settings instance.
        public static bool DebugCameraLogging { get; private set; } = false;

        // Low-overhead in-plugin performance telemetry (Recommendation 1 from
        // the profiling study). When true, KerbcamCamera.Refresh times each
        // render phase (galaxy / scaled / near / blit / readback) with
        // Stopwatch.GetTimestamp (allocation-free) and KerbcamCore samples the
        // GC collection counters every frame; both are surfaced into a
        // "telemetry" section of global.status.json each ~1Hz write. Lets us
        // measure on the Deck — with no profiler attach — whether the residual
        // ~100ms frametime spikes are Mono GC (a deltaTime spike that coincides
        // with a gen-0/1/2 collection is the proof) and read the per-layer
        // render-cost ratio. Default false so the feature is genuinely zero-cost
        // when off (Refresh reads the flag once and skips every GetTimestamp
        // call). Static so KerbcamCamera reads it without a settings back-ref.
        public static bool EnableTelemetry { get; private set; } = false;

        // Debug: force atmospheric-FX intensity to full regardless of mach /
        // dynamic pressure, so the effect renders even on the pad. Used to
        // verify the FX *renders* independent of the flight-state gating. Off
        // by default — leave off for normal play.
        public static bool ForceAtmosphericFx { get; private set; } = false;

        // Debug: override the vessel's surface velocity (world space) used to
        // drive FX direction — wind-aligned streaks, bowshock placement, trail
        // orientation, ember drift. Zero (default) → real srf_velocity. Pair
        // with ForceAtmosphericFx to test motion-dependent shader behaviour on
        // the pad without flying. Magnitude should be > 1 so velocity-gates in
        // effects don't trip. settings.cfg syntax: `DebugWindDirection = 100, 0, 0`.
        public static Vector3 DebugWindDirection { get; private set; } = Vector3.zero;

        // Default for the per-save ThrottleMainScreen Difficulty Setting
        // when a save is loaded for the first time. After that the
        // value stored in the save file wins; settings.cfg changes
        // don't retroactively override existing saves. Read by
        // KerbcamGameParameters's constructor.
        public static bool SeedThrottleMainScreen { get; private set; } = false;

        public string HttpBind => $"{BindAddress}:{Port}";

        // True if the bind address only accepts connections from this machine,
        // so exposing the feeds to the LAN warrants a warning when it isn't.
        private static bool IsLoopback(string addr)
        {
            if (string.IsNullOrEmpty(addr)) return true;
            addr = addr.Trim();
            return addr == "127.0.0.1" || addr == "localhost" || addr == "::1";
        }

        // Per-PartName initial layer mask (e.g. "navCam1" → NEAR only).
        // Cameras whose PartName isn't here default to CameraLayers.All.
        private readonly Dictionary<string, CameraLayers> _initialLayers =
            new Dictionary<string, CameraLayers>();
        // Per-PartName render-size overrides. Each entry is (width, height);
        // cameras without an override use the global Width × Height.
        private readonly Dictionary<string, (int, int)> _renderSize =
            new Dictionary<string, (int, int)>();
        // Per-PartName atmospheric-FX override. Present only when a Camera
        // node sets EnableAtmosphericFx; absent cameras use the global default.
        private readonly Dictionary<string, bool> _atmosphericFx =
            new Dictionary<string, bool>();
        // Per-PartName FX-layer override. Present only when a Camera node sets
        // AtmosphericFxLayers; absent cameras use the global default.
        private readonly Dictionary<string, AtmoFxLayers> _atmosphericFxLayers =
            new Dictionary<string, AtmoFxLayers>();

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

        /// <summary>
        /// Whether a part's near camera should replicate atmospheric FX.
        /// A per-PartName override (either direction) wins over the global
        /// EnableAtmosphericFx default. Operator can still flip it at runtime
        /// via the control file's enableAtmosphericFx field.
        /// </summary>
        public bool GetEnableAtmosphericFx(string partName)
        {
            if (!string.IsNullOrEmpty(partName) && _atmosphericFx.TryGetValue(partName, out var v))
            {
                return v;
            }
            return EnableAtmosphericFx;
        }

        /// <summary>
        /// Which FX layers a part's camera should run. Per-PartName override
        /// wins over the global AtmosphericFxLayers default.
        /// </summary>
        public AtmoFxLayers GetAtmosphericFxLayers(string partName)
        {
            if (!string.IsNullOrEmpty(partName) && _atmosphericFxLayers.TryGetValue(partName, out var v))
            {
                return v;
            }
            return AtmosphericFxLayers;
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
            ApplyFloat(node, "MaxCaptureFps", v => settings.MaxCaptureFps = v);
            ApplyFloat(node, "MaxKerbcamFrameBudgetMs", v => settings.MaxKerbcamFrameBudgetMs = v);
            ApplyFloat(node, "MinKspFps", v => settings.MinKspFps = v);
            ApplyBool(node, "EnableAtmosphericFx", v => settings.EnableAtmosphericFx = v);
            ApplyString(node, "AtmosphericFxLayers", v => settings.AtmosphericFxLayers = ParseAtmoFxLayers(v));
            ApplyBool(node, "EnableHullcamEffects", v => EnableHullcamEffects = v);
            ApplyBool(node, "EnableTUFX", v => EnableTUFX = v);
            ApplyString(node, "TUFXProfile", v => TUFXProfile = v);
            ApplyBool(node, "EnableHullcamLinuxShaderSwap", v => EnableHullcamLinuxShaderSwap = v);
            ApplyBool(node, "DebugCameraLogging", v => DebugCameraLogging = v);
            ApplyBool(node, "EnableTelemetry", v => EnableTelemetry = v);
            ApplyBool(node, "ForceAtmosphericFx", v => ForceAtmosphericFx = v);
            ApplyVector3(node, "DebugWindDirection", v => DebugWindDirection = v);
            // Static slots so KerbcamGameParameters (constructed by
            // KSP before our plugin instance loads) can pick up the
            // seed values. Settings.cfg is the source of truth for
            // first-time-on-this-save defaults.
            ApplyBool(node, "ThrottleMainScreen", v => SeedThrottleMainScreen = v);

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
                var fxRaw = camNode.GetValue("EnableAtmosphericFx");
                if (!string.IsNullOrEmpty(fxRaw))
                {
                    if (bool.TryParse(fxRaw.Trim(), out bool fxVal))
                        settings._atmosphericFx[partName] = fxVal;
                    else
                        Debug.LogWarning($"[Kerbcam] settings.cfg: Camera '{partName}' EnableAtmosphericFx='{fxRaw}' is not a bool; ignoring");
                }
                var fxLayersRaw = camNode.GetValue("AtmosphericFxLayers");
                if (!string.IsNullOrEmpty(fxLayersRaw))
                {
                    settings._atmosphericFxLayers[partName] = ParseAtmoFxLayers(fxLayersRaw);
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
            if (!IsLoopback(settings.BindAddress))
            {
                Debug.LogWarning($"[Kerbcam] BindAddress={settings.BindAddress} is not loopback. The camera feeds and the signalling endpoint have no authentication and are reachable by anyone on the network. Only do this on a network you trust.");
            }
            return settings;
        }

        // Parse the AtmosphericFxLayers token list (CORE, BOWSHOCK, TRAIL,
        // EMBERS, ALL). An empty/all-invalid list means "no FX layers" — which
        // is a legitimate operator choice (master on but every layer off), so
        // unlike ParseLayers we return None rather than falling back to All.
        private static AtmoFxLayers ParseAtmoFxLayers(string raw)
        {
            var mask = AtmoFxLayers.None;
            foreach (var tok in raw.Split(','))
            {
                var t = tok.Trim();
                if (t.Equals("CORE", System.StringComparison.OrdinalIgnoreCase)) mask |= AtmoFxLayers.Core;
                else if (t.Equals("BOWSHOCK", System.StringComparison.OrdinalIgnoreCase)) mask |= AtmoFxLayers.Bowshock;
                else if (t.Equals("TRAIL", System.StringComparison.OrdinalIgnoreCase)) mask |= AtmoFxLayers.Trail;
                else if (t.Equals("EMBERS", System.StringComparison.OrdinalIgnoreCase)) mask |= AtmoFxLayers.Embers;
                else if (t.Equals("ALL", System.StringComparison.OrdinalIgnoreCase)) mask |= AtmoFxLayers.All;
                else if (!string.IsNullOrEmpty(t))
                    Debug.LogWarning($"[Kerbcam] settings.cfg: unknown FX layer '{t}', skipping");
            }
            return mask;
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

        private static void ApplyFloat(ConfigNode node, string key, System.Action<float> set)
        {
            var raw = node.GetValue(key);
            if (string.IsNullOrEmpty(raw)) return;
            // Invariant culture: KSP config floats always use '.' as the decimal
            // separator regardless of the player's locale.
            if (float.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v))
                set(v);
            else Debug.LogWarning($"[Kerbcam] settings.cfg: {key}='{raw}' is not a number; using default");
        }

        // Parses three comma-separated floats: `x, y, z`.
        private static void ApplyVector3(ConfigNode node, string key, System.Action<Vector3> set)
        {
            var raw = node.GetValue(key);
            if (string.IsNullOrEmpty(raw)) return;
            var parts = raw.Split(',');
            if (parts.Length != 3)
            {
                Debug.LogWarning($"[Kerbcam] settings.cfg: {key}='{raw}' must be three comma-separated floats; using default");
                return;
            }
            if (float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                set(new Vector3(x, y, z));
            else
                Debug.LogWarning($"[Kerbcam] settings.cfg: {key}='{raw}' contains a non-float component; using default");
        }
    }
}
