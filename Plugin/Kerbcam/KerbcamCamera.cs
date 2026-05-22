// Per-Hullcam VDS tracking. Owns three Unity Cameras (near / scaled /
// galaxy) parented to the part's transform, an AsyncGPUReadback request in
// flight at most, and writes RGBA frames into a per-camera mmap ring on
// each completed readback.
//
// Layered camera shape matches KSP's own flight camera (and OCISLY's
// TrackingCamera): galaxy renders skybox + distant celestials, scaled
// renders planet terrain/atmosphere at scale, near renders close parts +
// atmosphere effects. All three target the same RenderTexture so one
// readback captures the composite. Per-layer enable/disable lets the
// operator (or future adaptive-shedding logic) drop the most expensive
// layers under load — galaxy first, then scaled, never near.

using System;
using System.IO;
using System.Linq;
using HullcamVDS;
using UnityEngine;
using Yangrc.OpenGLAsyncReadback;

namespace Kerbcam
{
    [Flags]
    internal enum CameraLayers
    {
        None = 0,
        Near = 1,
        Scaled = 2,
        Galaxy = 4,
        All = Near | Scaled | Galaxy,
    }

    internal sealed class KerbcamCamera
    {
        public uint FlightId { get; }
        public MuMechModuleHullCamera Hullcam { get; }
        public int MaxWidth { get; }
        public int MaxHeight { get; }
        public int RenderWidth { get; private set; }
        public int RenderHeight { get; private set; }
        /// <summary>Operator-requested render size — ceiling for adaptive
        /// downscale (level 3 halves below this).</summary>
        public int OperatorWidth { get; private set; }
        public int OperatorHeight { get; private set; }

        /// <summary>True iff the part is a `MuMechModuleHullCameraZoom`
        /// — the zoom-capable Hullcam VDS subclass. 19 of 21 stock
        /// Hullcam parts are zoom-capable; the base
        /// `MuMechModuleHullCamera` (Basic Hull Camera Deluxe) is the
        /// only fixed-FoV exception.</summary>
        public bool SupportsZoom { get; }
        public float FovMin { get; }
        public float FovMax { get; }
        public float Fov { get; private set; }

        /// <summary>Pan/tilt capability is reserved for the planned
        /// kerbcam-side mod extension that adds steerable mounts to
        /// specific Hullcam parts. False on every shipping part today;
        /// the protocol carries the fields so clients are ready for
        /// when this flips true per part.</summary>
        public bool SupportsPan => false;
        public float PanYawMin => 0f;
        public float PanYawMax => 0f;
        public float PanPitchMin => 0f;
        public float PanPitchMax => 0f;
        public float PanYaw { get; private set; }
        public float PanPitch { get; private set; }

        private Camera _nearCam;
        private Camera _scaledCam;
        private Camera _galaxyCam;
        private RenderTexture _captureRt;
        private RenderTexture _readbackRt; // depth=0, GL_TEXTURE_2D-clean
        private Texture2D _scratchTex;
        private readonly MmapFrameRing _ring;
        private readonly string _ringPath;
        private readonly string _infoPath;
        private readonly string _controlPath;
        private DateTime _lastControlMtime = DateTime.MinValue;
        private int _controlCheckCountdown;

        private CameraLayers _layers = CameraLayers.All;
        /// <summary>
        /// Ceiling for the effective layer mask. Adaptive shedding can
        /// reduce <c>_layers</c> below this, but never expand past it.
        /// Set on construction (from settings.cfg) and on operator
        /// control-file updates.
        /// </summary>
        private CameraLayers _operatorLayers = CameraLayers.All;
        /// <summary>
        /// Subscriber-aware capture gate. False (the default on attach
        /// until the sidecar writes a control.json with subscribed=true)
        /// means the per-layer Unity Camera components are disabled and
        /// Refresh() short-circuits — no GPU readback, no ring write,
        /// no encoder work downstream. Flipped by the sidecar when a
        /// peer-track is added / the last one drops, surfaced via the
        /// same control.json poll that carries layer / fov / render-size
        /// changes.
        /// </summary>
        private bool _subscribed;

        private UniversalAsyncGPUReadbackRequest _pendingRequest;
        private bool _readbackInFlight;
        private double _pendingCaptureTsMs;
        private int _consecutiveErrors;

        public KerbcamCamera(
            MuMechModuleHullCamera hullcam,
            uint flightId,
            string ringDir,
            int slotCount,
            int maxWidth,
            int maxHeight,
            int renderWidth,
            int renderHeight,
            CameraLayers initialLayers)
        {
            Hullcam = hullcam;
            FlightId = flightId;
            MaxWidth = maxWidth;
            MaxHeight = maxHeight;
            OperatorWidth = renderWidth;
            OperatorHeight = renderHeight;
            RenderWidth = renderWidth;
            RenderHeight = renderHeight;
            _operatorLayers = initialLayers;
            _layers = initialLayers;

            // Zoom capability: the zoom subclass `MuMechModuleHullCameraZoom`
            // carries cameraFoVMin / cameraFoVMax fields the base module
            // doesn't. `is` check + cast lets us reflect zoom support
            // without taking a hard reference to the subclass type for
            // parts where it's not present.
            var zoomable = hullcam as MuMechModuleHullCameraZoom;
            SupportsZoom = zoomable != null;
            if (zoomable != null)
            {
                FovMin = zoomable.cameraFoVMin;
                FovMax = zoomable.cameraFoVMax;
            }
            else
            {
                FovMin = hullcam.cameraFoV;
                FovMax = hullcam.cameraFoV;
            }
            Fov = hullcam.cameraFoV;

            _ringPath = Path.Combine(ringDir, $"{FlightId}.ring");
            _infoPath = Path.Combine(ringDir, $"{FlightId}.info.json");
            _controlPath = Path.Combine(ringDir, $"{FlightId}.control.json");
            // Ring is allocated at the global max — adaptive resolution
            // shrinks the rendered frame but the ring's slot capacity
            // stays the same. Each slot's header carries the actual
            // content size so the sidecar encodes at the current dim.
            _ring = MmapFrameRing.Create(_ringPath, slotCount, maxWidth, maxHeight);
            WriteInfoManifest();

            BuildRenderTargets(renderWidth, renderHeight);
            SetCameras();
            ApplyLayers();
        }

        private void BuildRenderTargets(int width, int height)
        {
            _captureRt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
            };
            _captureRt.Create();

            // depth=0 so GetNativeTexturePtr returns a vanilla GL_TEXTURE_2D
            // handle on Mesa OpenGL. With depth=24 (the capture RT) the
            // yangrc plugin's glGetTexLevelParameteriv reads back zero
            // dimensions and silently does nothing.
            _readbackRt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _readbackRt.Create();

            // RGBA32 (not ARGB32). DX11/Mesa async readback only supports a
            // narrow set of GraphicsFormats as readback destinations; RGBA32
            // (R8G8B8A8_UNorm) is in the list, ARGB32 (B8G8R8A8_SRGB) isn't.
            _scratchTex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
        }

        /// <summary>
        /// Rebuild the RenderTexture chain at a new size and rebind every
        /// Unity Camera to it. Caller is responsible for staying within
        /// MaxWidth × MaxHeight (the ring slot capacity). Even dimensions
        /// only — H.264 chroma sampling requires that.
        /// </summary>
        public void SetRenderSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (width % 2 != 0 || height % 2 != 0) return;
            if (width > MaxWidth || height > MaxHeight) return;
            if (width == RenderWidth && height == RenderHeight) return;

            // Drop any in-flight readback before destroying its source RT.
            _readbackInFlight = false;

            if (_captureRt != null) { _captureRt.Release(); UnityEngine.Object.Destroy(_captureRt); }
            if (_readbackRt != null) { _readbackRt.Release(); UnityEngine.Object.Destroy(_readbackRt); }
            if (_scratchTex != null) UnityEngine.Object.Destroy(_scratchTex);

            BuildRenderTargets(width, height);
            RenderWidth = width;
            RenderHeight = height;

            if (_nearCam != null) _nearCam.targetTexture = _captureRt;
            if (_scaledCam != null) _scaledCam.targetTexture = _captureRt;
            if (_galaxyCam != null) _galaxyCam.targetTexture = _captureRt;

            Debug.Log($"[Kerbcam] cam={FlightId} render size → {width}×{height}");
        }

        /// <summary>
        /// Update the operator-set ceiling for render size. Adaptive
        /// downscale can render below this; never above. Currently only
        /// settable at construction; runtime API for resolution is a
        /// future addition.
        /// </summary>
        public void SetOperatorRenderSize(int width, int height)
        {
            OperatorWidth = width;
            OperatorHeight = height;
            SetRenderSize(width, height);
        }

        // Layered camera triple. Each layer copies the corresponding KSP
        // main flight camera so depth-ordering, clearFlags, cullingMask,
        // and per-layer rendering tricks all inherit correctly. All three
        // target `_captureRt`; Unity composites by camera.depth (galaxy
        // back, scaled middle, near front), so a single readback against
        // the RT captures the composite frame.
        private void SetCameras()
        {
            var partTransform = string.IsNullOrEmpty(Hullcam.cameraTransformName)
                ? Hullcam.part.transform
                : Hullcam.part.FindModelTransform(Hullcam.cameraTransformName);
            if (partTransform == null)
            {
                Debug.LogWarning($"[Kerbcam] cam={FlightId} cameraTransformName '{Hullcam.cameraTransformName}' not found on part {Hullcam.part.name}");
                return;
            }

            // Near layer — close-up of parts + atmospheric effects.
            var nearGo = new GameObject($"Kerbcam_{FlightId}_Near");
            _nearCam = nearGo.AddComponent<Camera>();
            var sourceNear = FindKspCamera("Camera 00");
            if (sourceNear != null) _nearCam.CopyFrom(sourceNear);
            _nearCam.name = $"Kerbcam_{FlightId}_Near";
            nearGo.transform.parent = partTransform;
            nearGo.transform.localPosition = Hullcam.cameraPosition;
            nearGo.transform.localRotation = Quaternion.LookRotation(Hullcam.cameraForward, Hullcam.cameraUp);
            _nearCam.fieldOfView = Hullcam.cameraFoV;
            _nearCam.nearClipPlane = Hullcam.cameraClip;
            _nearCam.targetTexture = _captureRt;
            // HDR + MSAA explicit on every layer. CopyFrom only inherits
            // whatever the source KSP camera was last left with, which in
            // practice ships HDR off for the layered triple — atmospheric
            // scattering on Kerbin's horizon has very wide dynamic range
            // and clips ugly-dark without HDR (showed up as a "black hole
            // horizon" in the first multi-camera streaming test). OCISLY's
            // TrackingCamera enables these explicitly for the same reason.
            _nearCam.allowHDR = true;
            _nearCam.allowMSAA = true;
            // Offscreen RT cameras shouldn't run Unity's occlusion logic —
            // it's computed against the main viewport's frustum and either
            // wastes cycles or incorrectly culls objects in our cameras'
            // frusta. JustReadTheInstructions does this on every layer.
            _nearCam.useOcclusionCulling = false;
            nearGo.AddComponent<CanvasHack>();

            // Scaled layer — planet terrain + atmosphere at scaled-space scale.
            var scaledGo = new GameObject($"Kerbcam_{FlightId}_Scaled");
            _scaledCam = scaledGo.AddComponent<Camera>();
            var sourceScaled = FindKspCamera("Camera ScaledSpace");
            if (sourceScaled != null)
            {
                _scaledCam.CopyFrom(sourceScaled);
                scaledGo.transform.parent = sourceScaled.transform;
            }
            _scaledCam.name = $"Kerbcam_{FlightId}_Scaled";
            scaledGo.transform.localRotation = Quaternion.identity;
            scaledGo.transform.localPosition = Vector3.zero;
            scaledGo.transform.localScale = Vector3.one;
            _scaledCam.fieldOfView = Hullcam.cameraFoV;
            _scaledCam.targetTexture = _captureRt;
            _scaledCam.allowHDR = true;
            _scaledCam.allowMSAA = true;
            _scaledCam.useOcclusionCulling = false;
            var scaledRot = scaledGo.AddComponent<LayerCamRotator>();
            scaledRot.NearCamera = _nearCam;
            scaledRot.UseScaledSpace = true;
            scaledGo.AddComponent<CanvasHack>();

            // Galaxy layer — skybox + distant celestials.
            var galaxyGo = new GameObject($"Kerbcam_{FlightId}_Galaxy");
            _galaxyCam = galaxyGo.AddComponent<Camera>();
            // Pre-CopyFrom fallback: if the source GalaxyCamera lookup
            // fails (vessel-load race, scene weirdness), at least we
            // render a predictable solid-black backdrop instead of
            // whatever Unity defaults Camera to. CopyFrom overwrites
            // these when it succeeds. JTI does the same.
            _galaxyCam.clearFlags = CameraClearFlags.SolidColor;
            _galaxyCam.backgroundColor = Color.black;
            var sourceGalaxy = FindKspCamera("GalaxyCamera");
            if (sourceGalaxy != null)
            {
                _galaxyCam.CopyFrom(sourceGalaxy);
                galaxyGo.transform.parent = sourceGalaxy.transform;
            }
            _galaxyCam.name = $"Kerbcam_{FlightId}_Galaxy";
            galaxyGo.transform.position = Vector3.zero;
            galaxyGo.transform.localRotation = Quaternion.identity;
            galaxyGo.transform.localScale = Vector3.one;
            _galaxyCam.fieldOfView = Hullcam.cameraFoV;
            _galaxyCam.targetTexture = _captureRt;
            _galaxyCam.allowHDR = true;
            _galaxyCam.allowMSAA = true;
            _galaxyCam.useOcclusionCulling = false;
            var galaxyRot = galaxyGo.AddComponent<LayerCamRotator>();
            galaxyRot.NearCamera = _nearCam;
            galaxyRot.UseScaledSpace = false;
            galaxyGo.AddComponent<CanvasHack>();
        }

        private static Camera FindKspCamera(string name)
        {
            return Camera.allCameras.FirstOrDefault(c => c.name == name);
        }

        public CameraLayers Layers => _layers;
        public CameraLayers OperatorLayers => _operatorLayers;

        /// <summary>
        /// Apply an operator-driven layer change. Updates the ceiling AND
        /// the effective mask, and clears any in-effect adaptive shedding
        /// (it'll re-apply on the next tick if fps is still below the
        /// shed threshold).
        /// </summary>
        public void SetOperatorLayers(CameraLayers ops)
        {
            if (_operatorLayers == ops && _layers == ops) return;
            _operatorLayers = ops;
            _layers = ops;
            ApplyLayers();
        }

        /// <summary>
        /// Apply an operator-driven FoV change. Updates the Hullcam
        /// module (so its right-click GUI stays in sync) and every
        /// active Unity Camera in our layered triple. Silently no-ops
        /// for parts where `SupportsZoom == false`.
        /// </summary>
        public void SetFov(float fov)
        {
            if (!SupportsZoom) return;
            float clamped = Mathf.Clamp(fov, FovMin, FovMax);
            if (Mathf.Abs(clamped - Fov) < 0.01f) return;
            Fov = clamped;
            Hullcam.cameraFoV = clamped;
            if (_nearCam != null) _nearCam.fieldOfView = clamped;
            if (_scaledCam != null) _scaledCam.fieldOfView = clamped;
            if (_galaxyCam != null) _galaxyCam.fieldOfView = clamped;
        }

        /// <summary>
        /// Cascade table: (resolution multiplier, layers to drop). Lower
        /// levels are gentler on perception. Resolution reduction wins
        /// over layer dropping because it preserves scene completeness
        /// — a blurrier full image is more useful than a sharp scene
        /// with missing planet terrain. Scaled goes last because it's
        /// the operator's situational-awareness layer.
        /// </summary>
        private static readonly (float ResScale, CameraLayers Drop)[] ShedTable =
        {
            (1.00f, CameraLayers.None),                                 // 0: full
            (0.75f, CameraLayers.None),                                 // 1: gentle res drop
            (0.50f, CameraLayers.None),                                 // 2: half res
            (0.50f, CameraLayers.Galaxy),                               // 3: + drop galaxy
            (0.25f, CameraLayers.Galaxy),                               // 4: quarter res
            (0.25f, CameraLayers.Galaxy | CameraLayers.Scaled),         // 5: emergency
        };

        public static int MaxShedLevel => ShedTable.Length - 1;

        public void ApplyAutoShed(int level)
        {
            if (level < 0) level = 0;
            if (level > MaxShedLevel) level = MaxShedLevel;
            var (resScale, drop) = ShedTable[level];

            int targetW = MakeEven((int)(OperatorWidth * resScale));
            int targetH = MakeEven((int)(OperatorHeight * resScale));
            if (targetW < 2) targetW = 2;
            if (targetH < 2) targetH = 2;
            if (targetW != RenderWidth || targetH != RenderHeight)
            {
                SetRenderSize(targetW, targetH);
            }

            var targetLayers = _operatorLayers & ~drop;
            if (_layers != targetLayers)
            {
                _layers = targetLayers;
                ApplyLayers();
            }
        }

        private static int MakeEven(int v) => v - (v & 1);

        private void ApplyLayers()
        {
            // _subscribed gates the per-layer Camera.enabled state so an
            // unsubscribed camera doesn't render at all — that's where
            // the dominant per-frame cost lives (6 cams × 3 layers = 18
            // scene renders per Unity frame at full attach).
            bool active = _subscribed;
            if (_nearCam != null) _nearCam.enabled = active && (_layers & CameraLayers.Near) != 0;
            if (_scaledCam != null) _scaledCam.enabled = active && (_layers & CameraLayers.Scaled) != 0;
            if (_galaxyCam != null) _galaxyCam.enabled = active && (_layers & CameraLayers.Galaxy) != 0;
        }

        // Sidecar→plugin control channel. The sidecar's data-channel
        // handlers (SetLayers / SetRenderSize / SetFov / SetPan) write
        // the full operator-requested state into <FlightId>.control.json
        // via atomic rename. The plugin only re-parses when the file's
        // mtime moves — cheap stat-based check.
        private void PollControlFile()
        {
            try
            {
                if (!File.Exists(_controlPath)) return;
                var mtime = File.GetLastWriteTimeUtc(_controlPath);
                if (mtime == _lastControlMtime) return;
                _lastControlMtime = mtime;

                var raw = File.ReadAllText(_controlPath);
                // Subscriber flag drives the per-layer Camera.enabled
                // state via ApplyLayers. Default (field missing) is
                // false — safer to leave a cam asleep than to render
                // for nothing because the sidecar shipped an older
                // ControlState shape.
                var subscribed = ParseBoolField(raw, "subscribed") ?? false;
                if (subscribed != _subscribed)
                {
                    _subscribed = subscribed;
                    Debug.Log($"[Kerbcam] cam={FlightId} subscribed → {_subscribed}");
                    ApplyLayers();
                }
                var layers = ParseLayersJson(raw);
                if (layers.HasValue)
                {
                    SetOperatorLayers(layers.Value);
                    Debug.Log($"[Kerbcam] cam={FlightId} operator layers → {_operatorLayers}");
                }
                var fov = ParseFloatField(raw, "fov");
                if (fov.HasValue && SupportsZoom)
                {
                    SetFov(fov.Value);
                }
                // Pan parsing is wired but ignored on shipping parts
                // (`SupportsPan == false` short-circuits in setters).
                // The plumbing exists so the future extended mod can
                // flip the capability flag without protocol churn.
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] cam={FlightId} control file read failed: {ex.Message}");
            }
        }

        // Minimal JSON parsers tailored to the control file's shape:
        //   { "layers": [...], "width": N, "height": N, "fov": F, ... }
        // Return null on parse failure (caller leaves field unchanged).
        // Hand-rolled because pulling Newtonsoft.Json in for one tiny
        // file with a fixed schema isn't worth the dependency.
        private static CameraLayers? ParseLayersJson(string body)
        {
            int keyIdx = body.IndexOf("\"layers\"", StringComparison.Ordinal);
            if (keyIdx < 0) return null;
            int openBracket = body.IndexOf('[', keyIdx);
            if (openBracket < 0) return null;
            int closeBracket = body.IndexOf(']', openBracket);
            if (closeBracket < 0) return null;

            var inside = body.Substring(openBracket + 1, closeBracket - openBracket - 1);
            var tokens = inside.Split(',');
            var mask = CameraLayers.None;
            foreach (var tok in tokens)
            {
                var trimmed = tok.Trim().Trim('"').Trim();
                if (trimmed.Equals("NEAR", StringComparison.OrdinalIgnoreCase)) mask |= CameraLayers.Near;
                else if (trimmed.Equals("SCALED", StringComparison.OrdinalIgnoreCase)) mask |= CameraLayers.Scaled;
                else if (trimmed.Equals("GALAXY", StringComparison.OrdinalIgnoreCase)) mask |= CameraLayers.Galaxy;
            }
            return mask;
        }

        // Tiny bool reader for the control file. Returns null when the
        // field is missing or unparseable; caller decides the default.
        private static bool? ParseBoolField(string body, string key)
        {
            int keyIdx = body.IndexOf($"\"{key}\"", StringComparison.Ordinal);
            if (keyIdx < 0) return null;
            int colon = body.IndexOf(':', keyIdx);
            if (colon < 0) return null;
            int end = colon + 1;
            while (end < body.Length && (body[end] == ' ' || body[end] == '\t' || body[end] == '\n' || body[end] == '\r')) end++;
            // serde_json emits lowercase true / false.
            if (end + 4 <= body.Length && body.Substring(end, 4) == "true") return true;
            if (end + 5 <= body.Length && body.Substring(end, 5) == "false") return false;
            return null;
        }

        private static float? ParseFloatField(string body, string key)
        {
            int keyIdx = body.IndexOf($"\"{key}\"", StringComparison.Ordinal);
            if (keyIdx < 0) return null;
            int colon = body.IndexOf(':', keyIdx);
            if (colon < 0) return null;
            int end = colon + 1;
            while (end < body.Length && (body[end] == ' ' || body[end] == '\t' || body[end] == '\n' || body[end] == '\r')) end++;
            int start = end;
            while (end < body.Length && (char.IsDigit(body[end]) || body[end] == '.' || body[end] == '-' || body[end] == 'e' || body[end] == 'E' || body[end] == '+')) end++;
            if (end == start) return null;
            var raw = body.Substring(start, end - start);
            if (float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
            {
                return v;
            }
            return null;
        }

        // Static-per-life-of-camera metadata the sidecar serves via
        // GET /cameras. The plugin owns this file; the sidecar reads it
        // when it discovers the matching ring. Vessel name is technically
        // mutable (rename in flight), but for the v0.3 milestone the
        // capture-time snapshot is sufficient — vessel renames are rare.
        private void WriteInfoManifest()
        {
            try
            {
                var partName = Hullcam.part.partInfo?.name ?? "unknown";
                var partTitle = Hullcam.part.partInfo?.title ?? partName;
                var vesselName = Hullcam.vessel?.GetDisplayName() ?? Hullcam.vessel?.vesselName ?? "<unknown>";
                var cameraName = string.IsNullOrEmpty(Hullcam.cameraName) ? partTitle : Hullcam.cameraName;

                var json = "{\n"
                    + $"  \"flight_id\": {FlightId},\n"
                    + $"  \"part_name\": \"{EscapeJson(partName)}\",\n"
                    + $"  \"part_title\": \"{EscapeJson(partTitle)}\",\n"
                    + $"  \"camera_name\": \"{EscapeJson(cameraName)}\",\n"
                    + $"  \"vessel_name\": \"{EscapeJson(vesselName)}\",\n"
                    + $"  \"supports_zoom\": {(SupportsZoom ? "true" : "false")},\n"
                    + $"  \"fov\": {Fov.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n"
                    + $"  \"fov_min\": {FovMin.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n"
                    + $"  \"fov_max\": {FovMax.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n"
                    + $"  \"supports_pan\": {(SupportsPan ? "true" : "false")},\n"
                    + $"  \"pan_yaw_min\": {PanYawMin.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n"
                    + $"  \"pan_yaw_max\": {PanYawMax.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n"
                    + $"  \"pan_pitch_min\": {PanPitchMin.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n"
                    + $"  \"pan_pitch_max\": {PanPitchMax.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n"
                    + "}\n";
                File.WriteAllText(_infoPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] cam={FlightId} info manifest write failed: {ex.Message}");
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public void Refresh()
        {
            // Cheap control-file poll once per second (LateUpdate fires
            // at KSP's frame rate, so ~30-60 ticks/sec). File.GetLastWriteTime
            // is a stat() — fine at 1Hz, would be wasteful per-frame.
            if (--_controlCheckCountdown <= 0)
            {
                _controlCheckCountdown = 60;
                PollControlFile();
            }

            // Subscriber-aware skip: when no peer is subscribed, the
            // sidecar has flushed `subscribed=false` to control.json and
            // ApplyLayers has disabled the Unity Camera components. Bail
            // before issuing a readback so we don't pump GPU→CPU bytes
            // that nothing will consume. Pending in-flight readbacks
            // (subscribe→unsubscribe race) still drain on the next path.
            if (!_subscribed)
            {
                if (_readbackInFlight && _pendingRequest.done)
                {
                    ProcessReadback(_pendingRequest);
                    _readbackInFlight = false;
                }
                return;
            }

            // Poll: drain a completed readback before issuing a new one.
            if (_readbackInFlight)
            {
                if (!_pendingRequest.done) return;
                ProcessReadback(_pendingRequest);
                _readbackInFlight = false;
            }

            try
            {
                // Blit the depth-bundled capture RT into the clean readback RT.
                Graphics.Blit(_captureRt, _readbackRt);

                _readbackInFlight = true;
                _pendingCaptureTsMs = Time.unscaledTime * 1000.0;
                _pendingRequest = UniversalAsyncGPUReadbackRequest.Request(_readbackRt, 0);
            }
            catch (Exception ex)
            {
                _readbackInFlight = false;
                LogRateLimited($"capture pipeline threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ProcessReadback(UniversalAsyncGPUReadbackRequest request)
        {
            try
            {
                if (request.hasError)
                {
                    LogRateLimited("AsyncGPUReadback returned hasError");
                    return;
                }

                var data = request.GetData<byte>();
                _scratchTex.LoadRawTextureData(data);
                _scratchTex.Apply();
                var rgba = _scratchTex.GetRawTextureData();

                _ring.Produce(RenderWidth, RenderHeight, _pendingCaptureTsMs, rgba, 0, rgba.Length);
                _consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                LogRateLimited($"readback callback threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void LogRateLimited(string message)
        {
            // 1-in-300 frames at 30fps = log at most once per 10s per camera.
            if (_consecutiveErrors == 0 || _consecutiveErrors % 300 == 0)
            {
                Debug.Log($"[Kerbcam] cam={FlightId} {message}");
            }
            _consecutiveErrors++;
        }

        public void Dispose()
        {
            if (_nearCam != null) UnityEngine.Object.Destroy(_nearCam.gameObject);
            if (_scaledCam != null) UnityEngine.Object.Destroy(_scaledCam.gameObject);
            if (_galaxyCam != null) UnityEngine.Object.Destroy(_galaxyCam.gameObject);
            if (_captureRt != null) _captureRt.Release();
            if (_readbackRt != null) _readbackRt.Release();
            UnityEngine.Object.Destroy(_scratchTex);

            _ring?.Dispose();
            try
            {
                if (File.Exists(_ringPath)) File.Delete(_ringPath);
                if (File.Exists(_infoPath)) File.Delete(_infoPath);
                if (File.Exists(_controlPath)) File.Delete(_controlPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] cam={FlightId} ring file delete failed: {ex.Message}");
            }
        }
    }

    // OnPreRender hook that aligns the scaled / galaxy layer's transform
    // with the near camera. Ported from OCISLY's TgpCamRotator. Scaled
    // uses ScaledSpace.LocalToScaledSpace so the planet-terrain layer
    // tracks the near camera's world position correctly across the
    // near-space ↔ scaled-space boundary; galaxy doesn't translate
    // (skybox is positionally invariant) — it just inherits rotation.
    internal sealed class LayerCamRotator : MonoBehaviour
    {
        public Camera NearCamera { get; set; }
        public bool UseScaledSpace { get; set; }

        private void OnPreRender()
        {
            if (NearCamera == null || NearCamera.transform == null) return;
            if (UseScaledSpace)
            {
                transform.position = ScaledSpace.LocalToScaledSpace(NearCamera.transform.position);
            }
            transform.rotation = NearCamera.transform.rotation;
        }
    }

    // Defeats Unity's "willRenderCanvases" delegate during the extra
    // cameras' render passes — without this, UI canvases get re-rendered
    // for each of our offscreen cameras, multiplying their cost and
    // sometimes causing visible UI duplication. Ported from OCISLY's
    // CanvasHack; the reflection lookup is once-per-class-load.
    internal sealed class CanvasHack : MonoBehaviour
    {
        private static readonly System.Reflection.FieldInfo CanvasHackField =
            typeof(Canvas).GetField("willRenderCanvases",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        private object _saved;

        private void OnPreRender()
        {
            if (CanvasHackField == null) return;
            _saved = CanvasHackField.GetValue(null);
            CanvasHackField.SetValue(null, null);
        }

        private void OnPostRender()
        {
            if (CanvasHackField == null) return;
            CanvasHackField.SetValue(null, _saved);
        }
    }
}
