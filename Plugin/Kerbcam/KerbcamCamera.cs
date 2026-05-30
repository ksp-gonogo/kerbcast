// Per-Hullcam VDS tracking. Owns three Unity Cameras (near / scaled /
// galaxy) parented to the part's transform, an AsyncGPUReadback request in
// flight at most, and writes RGBA frames into a per-camera mmap ring on
// each completed readback.
//
// Layered camera shape matches KSP's own flight camera (and OCISLY's
// TrackingCamera): galaxy renders skybox + distant celestials, scaled
// renders planet terrain/atmosphere at scale, near renders close parts +
// atmosphere effects. All three target the same RenderTexture so one
// readback captures the composite.
//
// Render path: all three cameras are permanently disabled (enabled=false)
// so Unity's auto-render never fires them. Instead, Refresh() calls
// camera.Render() explicitly each frame in galaxy → scaled → near order.
// This prevents KSP's deferred "Composite Shadows" CommandBuffer from
// running against the wrong framebuffer when our cameras render — that
// buffer would otherwise null out sun diffuse on planet surfaces, leaving
// them black while the atmospheric limb (a separate render path) stayed
// bright. ScaledSunLightHelper strips and restores the buffer around the
// Scaled layer's Render() call. Per-layer shedding is now expressed as
// "skip camera.Render() this tick" rather than toggling enabled.
//
// Atmospheric FX: kerbcam's own pluggable plasma overlay (the stock FXCamera
// effect couldn't be faithfully reproduced offscreen — see
// atmospheric_fx_parked.md). A per-camera FxHost owns a set of IAtmoFxEffect
// layers (core sheath, bowshock, trail, embers). The core effect attaches a
// CommandBuffer to _nearCam at AfterForwardAlpha that re-draws the vessel's
// part renderers with our additive plasma material — inside the near render, so
// it composites against the near depth (correct occlusion). See Fx/.

using System;
using System.Collections.Generic;
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

        private readonly PanCapability _panCap;
        public bool SupportsPan => _panCap.SupportsPan;
        public float PanYawMin => _panCap.YawMin;
        public float PanYawMax => _panCap.YawMax;
        public float PanPitchMin => _panCap.PitchMin;
        public float PanPitchMax => _panCap.PitchMax;
        /// <summary>Current interpolated yaw (degrees). Use this for
        /// status reporting — not the target — so operators see what
        /// is actually on-screen, not the commanded position.</summary>
        public float PanYaw => _panYawCurrent;
        /// <summary>Current interpolated pitch (degrees).</summary>
        public float PanPitch => _panPitchCurrent;

        // Snapshot of identity fields captured at construction. Used by
        // WriteInfoManifest and WriteDestroyedManifest so neither method
        // needs to touch Hullcam.part (which may be null or dying by the
        // time Dispose is called from a part-destruction handler).
        private readonly string _cachedPartName;
        private readonly string _cachedPartTitle;
        private readonly string _cachedCameraName;
        private readonly string _cachedVesselName;

        private Camera _nearCam;
        private Camera _scaledCam;
        private Camera _galaxyCam;
        // Whether this camera should replicate KSP's atmospheric FX. Set from
        // settings.cfg at construction; flipped at runtime by the control file.
        private bool _enableFx;
        // Pluggable atmospheric-FX host for this camera. Owns the enabled FX
        // effects (core sheath, bowshock, …); each effect renders into the near
        // pass. Null until SetCameras builds it.
        private FxHost _fxHost;
        // Which FX layers this camera runs (subject to the _enableFx master).
        private AtmoFxLayers _fxLayers;
        private RenderTexture _captureRt;
        private RenderTexture _readbackRt; // depth=0, GL_TEXTURE_2D-clean
        private Texture2D _scratchTex;
        private readonly MmapFrameRing _ring;
        private readonly string _ringPath;
        private readonly string _infoPath;
        private readonly string _controlPath;
        private DateTime _lastControlMtime = DateTime.MinValue;
        private int _controlCheckCountdown;

        // Pan/tilt interpolation state. Target is written by PollControlFile
        // from operator control.json; Current tracks toward it each Refresh()
        // at SlewDegPerSec. Near-cam rotation and mesh transforms are driven
        // from Current, so status echoes what is actually on-screen.
        private float _panYawTarget;
        private float _panPitchTarget;
        private float _panYawCurrent;
        private float _panPitchCurrent;
        // Base rotation of the near camera at part-attach time. Pan is applied
        // as baseRot * Euler(-pitch, yaw, 0) so the camera's natural forward
        // direction is the zero point.
        private Quaternion _baseRotation;
        // Mesh transform nodes driven by pan slew. Null when the capability
        // table leaves the transform name empty (no animated joint in the mesh).
        private Transform _yawTransform;
        private Quaternion _yawRestRot;
        private Vector3 _yawRestLocalPos;
        private Transform _pitchTransform;
        private Quaternion _pitchRestRot;
        // Optional co-rotating base (yaw-only, no pitch) to prevent the
        // moving head from clipping into a symmetric fixed base.
        private Transform _yawBaseTransform;
        private Quaternion _yawBaseRestRot;

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
        private bool _disposed;
        private bool _firstRender = true;
        private bool _firstPixelCheck = true;
        private struct FaderState
        {
            public Renderer Renderer;
            public bool WasEnabled;
            public float OriginalFade;
        }
        // Pre-allocated; cleared and repopulated each scaled render to avoid
        // per-frame GC pressure.
        private readonly List<FaderState> _faderOverrides = new List<FaderState>();
        private static readonly int _fadeAltitudeId = Shader.PropertyToID("_FadeAltitude");

        private UniversalAsyncGPUReadbackRequest _pendingRequest;
        private bool _readbackInFlight;
        private double _pendingCaptureTsMs;
        private int _consecutiveErrors;

        // HullcamVDS per-part shader filter (NightVision, MovieTime,
        // CRT scanlines etc — 9 modes total enumerated by
        // HullcamVDS.CameraFilter.eCameraMode). Created from
        // hullcam.cameraMode at attach; null if the mode is Normal
        // (no filter), if creation failed, or if EnableHullcamEffects
        // is false in settings.cfg. When non-null, replaces the
        // capture-RT → readback-RT blit in Refresh() with a filter
        // pass that lands the post-processed pixels into _readbackRt
        // for the existing AsyncGPUReadback path to read.
        private CameraFilter _cameraFilter;
        // kerbcam's own NightVision material, used instead of HullcamVDS's
        // additive-shift filter when the shader bundle is available.
        private Material _nvMaterial;

        public KerbcamCamera(
            MuMechModuleHullCamera hullcam,
            uint flightId,
            string ringDir,
            int slotCount,
            int maxWidth,
            int maxHeight,
            int renderWidth,
            int renderHeight,
            CameraLayers initialLayers,
            bool enableAtmosphericFx,
            AtmoFxLayers fxLayers,
            PanCapability panCap = default)
        {
            Hullcam = hullcam;
            _panCap = panCap;
            FlightId = flightId;
            MaxWidth = maxWidth;
            MaxHeight = maxHeight;
            OperatorWidth = renderWidth;
            OperatorHeight = renderHeight;
            RenderWidth = renderWidth;
            RenderHeight = renderHeight;
            _operatorLayers = initialLayers;
            _layers = initialLayers;
            _enableFx = enableAtmosphericFx;
            _fxLayers = fxLayers;

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

            // Cache identity fields now while the Part is guaranteed live.
            // WriteDestroyedManifest (called from Dispose, which may be
            // invoked from a part-destruction event) reads these instead of
            // touching Hullcam.part, which may already be null by that point.
            _cachedPartName = hullcam.part.partInfo?.name ?? "unknown";
            _cachedPartTitle = hullcam.part.partInfo?.title ?? _cachedPartName;
            _cachedCameraName = string.IsNullOrEmpty(hullcam.cameraName) ? _cachedPartTitle : hullcam.cameraName;
            _cachedVesselName = hullcam.vessel?.GetDisplayName() ?? hullcam.vessel?.vesselName ?? "<unknown>";

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
            BuildHullcamFilter();
        }

        // Instantiate the HullcamVDS shader filter that matches this
        // part's configured cameraMode. Read once at attach; mode
        // changes from the operator's right-click Hullcam UI mid-flight
        // are NOT picked up — a vessel reload (or kerbcam re-attach)
        // would. Acceptable for v1; can add a periodic mode-poll later.
        //
        // cameraMode is a float in the Hullcam ConfigNode (0-8, mapped
        // to eCameraMode by value) — converted to enum by direct cast.
        // Mode 0 (Normal) returns a no-op filter; we leave _cameraFilter
        // null in that case so Refresh's blit takes the fast path.
        private void BuildHullcamFilter()
        {
            if (!KerbcamSettings.EnableHullcamEffects) return;
            try
            {
                int modeInt = (int)Hullcam.cameraMode;
                var mode = (CameraFilter.eCameraMode)modeInt;
                if (mode == CameraFilter.eCameraMode.Normal) return;

                // Night vision: use kerbcam's multiplicative-gain shader
                // instead of HullcamVDS's additive-shift filter. Falls back
                // to the HullcamVDS path below if the bundle is missing.
                if (mode == CameraFilter.eCameraMode.NightVision)
                {
                    _nvMaterial = KerbcamNightVisionFilter.GetMaterial();
                    if (_nvMaterial != null)
                    {
                        UnityEngine.Debug.Log($"[Kerbcam] cam={FlightId} kerbcam NightVision shader active");
                        return;
                    }
                }

                var filter = CameraFilter.CreateFilter(mode);
                if (filter == null) return;
                if (!filter.Activate())
                {
                    UnityEngine.Debug.LogWarning($"[Kerbcam] cam={FlightId} CameraFilter.Activate failed for mode={mode}");
                    return;
                }
                _cameraFilter = filter;
                UnityEngine.Debug.Log($"[Kerbcam] cam={FlightId} HullcamVDS filter active: mode={mode}");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Kerbcam] cam={FlightId} BuildHullcamFilter failed: {ex.Message}");
            }
        }

        private void BuildRenderTargets(int width, int height)
        {
            _captureRt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
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

        private static void DumpModelTransforms(Part part)
        {
            var sb = new System.Text.StringBuilder();
            var root = part.FindModelTransform("model");
            if (root == null) root = part.transform;
            WalkTransforms(root, 0, sb);
            Debug.Log($"[Kerbcam] model transforms for {part.name}:\n{sb}");
        }

        private static void WalkTransforms(Transform t, int depth, System.Text.StringBuilder sb)
        {
            var p = t.localPosition;
            sb.Append(' ', depth * 2)
              .Append(t.name)
              .AppendFormat(" pos=({0:F3},{1:F3},{2:F3})", p.x, p.y, p.z)
              .Append('\n');
            for (int i = 0; i < t.childCount; i++)
                WalkTransforms(t.GetChild(i), depth + 1, sb);
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

            // Resolve the yaw mesh transform before camera setup so we can
            // parent the near camera to it. Must happen here, at rest pose,
            // so InverseTransformPoint reads the unrotated frame correctly.
            DumpModelTransforms(Hullcam.part);
            if (!string.IsNullOrEmpty(_panCap.YawTransformName))
            {
                _yawTransform = Hullcam.part.FindModelTransform(_panCap.YawTransformName);
                if (_yawTransform != null)
                {
                    _yawRestRot = _yawTransform.localRotation;
                    _yawRestLocalPos = _yawTransform.localPosition;
                    Debug.Log($"[Kerbcam] cam={FlightId} yaw transform '{_panCap.YawTransformName}' found, restRot={_yawRestRot} restPos={_yawRestLocalPos}");
                }
                else
                    Debug.LogWarning($"[Kerbcam] cam={FlightId} yaw transform '{_panCap.YawTransformName}' not found on {Hullcam.part.name}");
            }

            // When a yaw joint exists, parent the near camera to it so it
            // physically follows the rotating head. cameraPosition and
            // cameraForward/Up are defined in partTransform space; re-express
            // them in the joint's local frame so the lens stays at the correct
            // world position after the joint rotates.
            Transform nearParent;
            Vector3 nearLocalPos;
            if (_yawTransform != null)
            {
                Vector3 worldPos = partTransform.TransformPoint(Hullcam.cameraPosition);
                nearLocalPos = _yawTransform.InverseTransformPoint(worldPos);
                Vector3 worldFwd = partTransform.TransformDirection(Hullcam.cameraForward);
                Vector3 worldUp  = partTransform.TransformDirection(Hullcam.cameraUp);
                _baseRotation = Quaternion.LookRotation(
                    _yawTransform.InverseTransformDirection(worldFwd),
                    _yawTransform.InverseTransformDirection(worldUp));
                nearParent = _yawTransform;
            }
            else
            {
                nearLocalPos = Hullcam.cameraPosition;
                _baseRotation = Quaternion.LookRotation(Hullcam.cameraForward, Hullcam.cameraUp);
                nearParent = partTransform;
            }

            if (_panCap.CameraRollDeg != 0f)
            {
                _baseRotation *= Quaternion.AngleAxis(_panCap.CameraRollDeg, Vector3.forward);
                Debug.Log($"[Kerbcam] cam={FlightId} applied camera roll {_panCap.CameraRollDeg}°, baseRot eulers={_baseRotation.eulerAngles}");
            }

            // Near layer — close-up of parts + atmospheric effects.
            var nearGo = new GameObject($"Kerbcam_{FlightId}_Near");
            _nearCam = nearGo.AddComponent<Camera>();
            var sourceNear = FindKspCamera("Camera 00");
            if (sourceNear != null) _nearCam.CopyFrom(sourceNear);
            _nearCam.name = $"Kerbcam_{FlightId}_Near";
            nearGo.transform.parent = nearParent;
            nearGo.transform.localPosition = nearLocalPos;
            nearGo.transform.localRotation = _baseRotation;
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
            // Add kerbcam's FX-only layer so ember ParticleSystems (and any
            // future GameObject-based effects on AtmoFxConstants.Layer) render
            // on kerbcam streams without leaking into the main flight view.
            _nearCam.cullingMask |= AtmoFxConstants.LayerMask;
            nearGo.AddComponent<CanvasHack>();

            // Scaled layer — planet terrain + atmosphere at scaled-space scale.
            var scaledGo = new GameObject($"Kerbcam_{FlightId}_Scaled");
            _scaledCam = scaledGo.AddComponent<Camera>();
            var sourceScaled = FindKspCamera("Camera ScaledSpace");
            if (sourceScaled != null)
            {
                _scaledCam.CopyFrom(sourceScaled);
                scaledGo.transform.parent = sourceScaled.transform;
                var scaledComps = sourceScaled.gameObject.GetComponents<MonoBehaviour>();
                Debug.Log($"[Kerbcam] Camera ScaledSpace components ({scaledComps.Length}): " +
                    string.Join(", ", System.Array.ConvertAll(scaledComps, c => c.GetType().Name)));
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
            // Deferred lighting fails silently for offscreen RTs on Mesa/OpenGL
            // (surface goes pure black while the atmosphere limb, which uses its
            // own MPB-based lighting, stays bright). Scaled space has exactly one
            // light (scaledSunLight, layer 10 only) so forward costs nothing extra.
            _scaledCam.renderingPath = RenderingPath.Forward;
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

            // TUFX (TexturesUnlimitedFX) post-processing. Reflection-only
            // — silently no-ops when TUFX isn't installed. Applied to ALL
            // THREE layers (not just near, per JustReadTheInstructions'
            // pattern, the more recent reference implementation). The
            // near cam carries the heaviest tonemap+bloom load since it
            // sees the highest-luminance content (engines, near-vessel
            // atmosphere); the scaled cam handles the wide-DR atmospheric
            // gradient that was the original "dark Kerbin / black hole
            // horizon" complaint that triggered this work; the galaxy cam
            // applies it to the skybox composite for consistency.
            if (KerbcamSettings.EnableTUFX)
            {
                TUFXIntegration.ApplyToCamera(_nearCam);
                TUFXIntegration.ApplyToCamera(_scaledCam);
                TUFXIntegration.ApplyToCamera(_galaxyCam);
            }

            // Pitch transform — resolved here rather than above because it
            // doesn't affect camera parenting in the current design.
            if (!string.IsNullOrEmpty(_panCap.PitchTransformName))
            {
                _pitchTransform = Hullcam.part.FindModelTransform(_panCap.PitchTransformName);
                if (_pitchTransform != null)
                    _pitchRestRot = _pitchTransform.localRotation;
                else
                    Debug.LogWarning($"[Kerbcam] cam={FlightId} pitch transform '{_panCap.PitchTransformName}' not found on {Hullcam.part.name}");
            }

            // Yaw-base transform: a fixed base that co-rotates in yaw so the
            // moving head doesn't clip through it. No pitch — the base is static.
            if (!string.IsNullOrEmpty(_panCap.YawBaseTransformName))
            {
                _yawBaseTransform = Hullcam.part.FindModelTransform(_panCap.YawBaseTransformName);
                if (_yawBaseTransform != null)
                {
                    _yawBaseRestRot = _yawBaseTransform.localRotation;
                    Debug.Log($"[Kerbcam] cam={FlightId} yaw-base transform '{_panCap.YawBaseTransformName}' found");
                }
                else
                    Debug.LogWarning($"[Kerbcam] cam={FlightId} yaw-base transform '{_panCap.YawBaseTransformName}' not found on {Hullcam.part.name}");
            }

            // All three cameras are permanently disabled — Unity must not
            // auto-render them. Refresh() drives explicit camera.Render()
            // calls each tick; disabled cameras still participate in
            // LayerCamRotator.OnPreRender (which fires on camera.Render())
            // so transform tracking continues to work correctly.
            _nearCam.enabled = false;
            _scaledCam.enabled = false;
            _galaxyCam.enabled = false;

            // Debug log of per-camera cullingMask + source-camera
            // cullingMask. Gated on settings.cfg DebugCameraLogging
            // (default off). Hook for the cam-stream-FX investigation
            // — we suspect KSP dynamically modifies Camera 00's mask
            // after our one-shot CopyFrom and that's why atmospheric
            // effects are missing from streams. Logging both sides
            // lets the operator catch the divergence in KSP.log.
            if (KerbcamSettings.DebugCameraLogging)
            {
                long srcNearMask = sourceNear != null ? sourceNear.cullingMask : 0;
                long srcScaledMask = sourceScaled != null ? sourceScaled.cullingMask : 0;
                long srcGalaxyMask = sourceGalaxy != null ? sourceGalaxy.cullingMask : 0;
                Debug.Log(
                    $"[Kerbcam-debug] cam={FlightId} cullingMasks " +
                    $"near=src:0x{srcNearMask:X8}/ours:0x{_nearCam.cullingMask:X8} " +
                    $"scaled=src:0x{srcScaledMask:X8}/ours:0x{_scaledCam.cullingMask:X8} " +
                    $"galaxy=src:0x{srcGalaxyMask:X8}/ours:0x{_galaxyCam.cullingMask:X8}");
            }

            // Build the pluggable atmospheric-FX host for the near camera. The
            // effective layer set folds the master toggle in (off → no effects,
            // a genuine no-op). Each effect owns its own rendering surface.
            _fxHost = new FxHost(_nearCam);
            _fxHost.SetEnabledLayers(EffectiveFxLayers());
            _fxHost.OnVesselChanged(Hullcam?.vessel);
        }

        // Master gate folded into the layer set: FX off ⇒ no layers ⇒ no effects.
        private AtmoFxLayers EffectiveFxLayers() => _enableFx ? _fxLayers : AtmoFxLayers.None;

        // Build this frame's FX inputs from the vessel's flight state. Effects
        // derive their own intensities from these.
        private FxFrameState BuildFxFrameState()
        {
            var v = Hullcam != null ? Hullcam.vessel : null;
            Vector3 vel = v != null ? (Vector3)v.srf_velocity : Vector3.zero;
            float mach = v != null ? (float)v.mach : 0f;
            float q = v != null ? (float)v.dynamicPressurekPa : 0f;

            // Prefer KSP's published aero direction (_LightDirection0) when
            // meaningful — it's the same vector FXCamera uses internally and
            // is physics-driven from the actual flight state. Falling through
            // to the vessel's srf_velocity if FXCamera isn't publishing
            // anything yet (early in flight init, or in vacuum). This makes
            // every effect (core, bowshock, trail, embers) wind-driven by the
            // game's own aero solver without each shader having to sample
            // globals independently.
            //
            // IMPORTANT SIGN: AerodynamicsFX.cs (line 411 of the decompile)
            // sets `fxCamera.effectDirection = -velocity`, so _LightDirection0
            // is the AIRFLOW direction (the direction the air is blowing,
            // i.e. opposite of the vessel's motion). Our state.VelocityWorld
            // convention is the vessel's velocity direction, so negate.
            Vector3 fxDir = Shader.GetGlobalVector(_LightDirection0Id);
            if (fxDir.sqrMagnitude > 0.01f)
                vel = -fxDir;

            // Debug override beats both: pretend the vessel is moving in this
            // world-space direction so motion-dependent shader paths can be
            // exercised on the pad. Pair with ForceAtmosphericFx.
            if (KerbcamSettings.DebugWindDirection.sqrMagnitude > 0.0001f)
                vel = KerbcamSettings.DebugWindDirection;

            return new FxFrameState(v, _nearCam, vel, mach, q, Time.deltaTime, Time.time);
        }

        // Cached shader ID for FXCamera's published wind direction global.
        private static readonly int _LightDirection0Id = Shader.PropertyToID("_LightDirection0");

        /// <summary>
        /// Turn atmospheric-FX replication on or off at runtime. Re-syncs the
        /// host's effective layer set; takes effect next frame. Called by
        /// PollControlFile on operator flips.
        /// </summary>
        public void SetEnableAtmosphericFx(bool enabled)
        {
            _enableFx = enabled;
            _fxHost?.SetEnabledLayers(EffectiveFxLayers());
        }

        /// <summary>
        /// Rebuild FX per-vessel state (the effects' draw lists) — called by
        /// KerbcamCore on part destruction / vessel modification so stale part
        /// renderers don't linger in an effect's CommandBuffer.
        /// </summary>
        public void MarkFxDirty() => _fxHost?.OnVesselChanged(Hullcam != null ? Hullcam.vessel : null);

        // Periodic cullingMask diff between our cams and their KSP
        // source cameras. Catches the case where KSP mutates the
        // source cam's mask after we CopyFrom'd — our cams would
        // miss whatever the new layer is. Gated, called once per
        // minute by KerbcamCore so the log stays readable.
        public void LogCullingMaskIfDiverged()
        {
            if (!KerbcamSettings.DebugCameraLogging) return;
            var srcNear = FindKspCamera("Camera 00");
            if (srcNear == null || _nearCam == null) return;
            if (srcNear.cullingMask != _nearCam.cullingMask)
            {
                Debug.Log(
                    $"[Kerbcam-debug] cam={FlightId} near cullingMask DIVERGED — " +
                    $"src:0x{srcNear.cullingMask:X8} ours:0x{_nearCam.cullingMask:X8} " +
                    $"missing-from-ours:0x{srcNear.cullingMask & ~_nearCam.cullingMask:X8}");
            }
        }

        private static Camera FindKspCamera(string name)
        {
            return Camera.allCameras.FirstOrDefault(c => c.name == name);
        }

        public CameraLayers Layers => _layers;
        public CameraLayers OperatorLayers => _operatorLayers;
        /// <summary>Whether atmospheric-FX replication is currently requested
        /// for this camera. Echoed in status JSON so the operator/gonogo can
        /// confirm a control-file flip was received.</summary>
        public bool EnableFx => _enableFx;

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
            // Cameras are permanently disabled (enabled=false) — Unity's
            // auto-render is never used for our offscreen cameras. The
            // layer mask (_layers) is already updated by the caller before
            // ApplyLayers() is invoked; Refresh() consumes it to decide
            // which camera.Render() calls to make this tick. Nothing
            // additional needed here.
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
                    if (_subscribed)
                    {
                        // Snap interpolation to target on peer reconnect so the
                        // peer sees the current commanded position immediately
                        // instead of a phantom pan-back from the last rest position.
                        _panYawCurrent = _panYawTarget;
                        _panPitchCurrent = _panPitchTarget;
                    }
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
                // Field-missing leaves the construction-time value untouched.
                var enableFx = ParseBoolField(raw, "enableAtmosphericFx");
                if (enableFx.HasValue && enableFx.Value != _enableFx)
                {
                    SetEnableAtmosphericFx(enableFx.Value);
                    Debug.Log($"[Kerbcam] cam={FlightId} atmospheric FX → {_enableFx}");
                }
                if (SupportsPan)
                {
                    var panYaw = ParseFloatField(raw, "panYaw");
                    var panPitch = ParseFloatField(raw, "panPitch");
                    if (panYaw.HasValue)
                        _panYawTarget = Mathf.Clamp(panYaw.Value, _panCap.YawMin, _panCap.YawMax);
                    if (panPitch.HasValue)
                        _panPitchTarget = Mathf.Clamp(panPitch.Value, _panCap.PitchMin, _panCap.PitchMax);
                }
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
        //
        // Identity fields are read from the cached snapshot (populated in
        // the constructor) rather than from Hullcam.part directly so the
        // same code path stays safe when called from Dispose during part
        // destruction (where Hullcam.part may already be null).
        private void WriteInfoManifest()
        {
            WriteManifest("active");
        }

        // Rewrites the info.json with lifecycle="destroyed". Called from
        // Dispose before any cleanup so the sidecar can observe the
        // transition before the ring is closed. Wrapped in try/catch so a
        // write failure never blocks the cleanup path.
        public void WriteDestroyedManifest()
        {
            try
            {
                WriteManifest("destroyed");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] cam={FlightId} destroyed manifest write failed: {ex.Message}");
            }
        }

        private void WriteManifest(string lifecycle)
        {
            try
            {
                var json = "{\n"
                    + $"  \"lifecycle\": \"{lifecycle}\",\n"
                    + $"  \"flight_id\": {FlightId},\n"
                    + $"  \"part_name\": \"{EscapeJson(_cachedPartName)}\",\n"
                    + $"  \"part_title\": \"{EscapeJson(_cachedPartTitle)}\",\n"
                    + $"  \"camera_name\": \"{EscapeJson(_cachedCameraName)}\",\n"
                    + $"  \"vessel_name\": \"{EscapeJson(_cachedVesselName)}\",\n"
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
                Debug.LogWarning($"[Kerbcam] cam={FlightId} info manifest write failed (lifecycle={lifecycle}): {ex.Message}");
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
            // Control-file poll ~20Hz (countdown=3 at 60fps). Fast enough
            // for interactive pan; File.GetLastWriteTime is a stat() so the
            // cost is negligible even at 20Hz. The old 1Hz cadence (60) was
            // fine for layer/fov changes but too sluggish for pan input.
            if (--_controlCheckCountdown <= 0)
            {
                _controlCheckCountdown = 3;
                PollControlFile();
            }

            // Pan slew runs every tick regardless of subscription state so
            // the physical mesh keeps animating when no peer is watching.
            // The near-cam rotation update is a no-op while cameras are
            // unsubscribed (no Render() call happens), but it keeps the
            // transform consistent for the frame when subscription resumes.
            if (SupportsPan)
            {
                float maxDelta = _panCap.SlewDegPerSec * Time.deltaTime;
                _panYawCurrent = Mathf.MoveTowards(_panYawCurrent, _panYawTarget, maxDelta);
                _panPitchCurrent = Mathf.MoveTowards(_panPitchCurrent, _panPitchTarget, maxDelta);

                // When yaw and pitch share a single joint (e.g. launchcam's
                // hc_launchcam), applying them separately would fight — the
                // second assignment overwrites the first. Apply a single compound
                // Euler so both axes land in one rotation.
                bool compoundJoint = _yawTransform != null && _pitchTransform != null
                    && ReferenceEquals(_yawTransform, _pitchTransform);

                if (_nearCam != null)
                {
                    if (compoundJoint)
                    {
                        // Joint carries both axes; near cam stays at rest relative to joint.
                        _nearCam.transform.localRotation = _baseRotation;
                    }
                    else
                    {
                        // Positive pan_yaw = camera turns right; positive pan_pitch = up.
                        // Negate pitch because Unity's X-rotation is positive-down.
                        // When parented to the yaw joint, the joint's own rotation
                        // carries the yaw — applying it here too would double it.
                        float camYaw = _yawTransform != null ? 0f : _panYawCurrent;
                        _nearCam.transform.localRotation = _baseRotation
                            * Quaternion.Euler(-_panPitchCurrent, camYaw, 0f);
                    }
                }
                if (compoundJoint)
                {
                    var rotation = _yawRestRot
                        * Quaternion.Euler(-_panPitchCurrent, _panYawCurrent, 0f);

                    if (_panCap.PitchPivotLocalY != 0f)
                    {
                        // The physical hinge sits above the transform origin.
                        // Rotating around the origin would swing the whole head
                        // from the base; instead, rotate around the hinge by
                        // adjusting localPosition so the pivot stays fixed.
                        var pivot = _yawRestLocalPos + new Vector3(0f, _panCap.PitchPivotLocalY, 0f);
                        _yawTransform.localPosition = pivot + rotation * (_yawRestLocalPos - pivot);
                    }
                    _yawTransform.localRotation = rotation;

                    // Co-rotate the static base in yaw so the moving head
                    // doesn't clip into it (pitch is not applied to the base).
                    if (_yawBaseTransform != null)
                        _yawBaseTransform.localRotation = _yawBaseRestRot
                            * Quaternion.Euler(0f, _panYawCurrent, 0f);
                }
                else
                {
                    if (_yawTransform != null)
                        _yawTransform.localRotation = _yawRestRot
                            * Quaternion.Euler(0f, _panYawCurrent, 0f);
                    if (_pitchTransform != null)
                        _pitchTransform.localRotation = _pitchRestRot
                            * Quaternion.Euler(-_panPitchCurrent, 0f, 0f);
                }
            }

            // Subscriber-aware skip: when no peer is subscribed, skip all
            // rendering work — no camera.Render() calls, no readback, no
            // ring writes, no encoder work downstream. Pending in-flight
            // readbacks (subscribe→unsubscribe race) still drain on the
            // next path.
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
                // Manual render sequence: galaxy → scaled → near.
                // Cameras are permanently disabled (enabled=false) so
                // Unity's auto-render never fires them; we drive each
                // layer explicitly here and gate on the current layer mask
                // (mirroring the old enabled-flag gating).
                //
                // The Scaled layer is bracketed by strip/restore of the
                // "Composite Shadows" CommandBuffer on scaledSunLight —
                // defensive measure for Scatterer-installed configs where
                // KSP's deferred renderer attaches such a buffer. No-op
                // on configs without that buffer attached (our case at
                // dev time, but anyone running Scatterer would need it).
                // try/finally ensures restore even if Render() throws.
                if (_galaxyCam != null && (_layers & CameraLayers.Galaxy) != 0)
                {
                    _galaxyCam.Render();
                }

                if (_scaledCam != null && (_layers & CameraLayers.Scaled) != 0)
                {
                    ScaledSunLightHelper.StripCompositeShadowsBuffer();
                    // ScaledSpaceFader disables scaled-body renderers globally
                    // based on the main camera's angular size. Our camera renders
                    // from a different position, so we manage visibility ourselves:
                    // force-enable any renderer that ScaledSpaceFader switched off,
                    // render, then restore. The finally block guarantees restore even
                    // if Render() throws.
                    // ScaledSpaceFader controls scaled-body visibility in two ways:
                    // (1) r.enabled=false when below fadeStart — renderer entirely off.
                    // (2) r.material._FadeAltitude ramping 0→1 in the transition zone
                    //     while r.enabled=true — planet fades in gradually.
                    // Our camera renders from a different position so we bypass both:
                    // force-enable and override _FadeAltitude=1 for any renderer that
                    // ScaledSpaceFader has suppressed. Original state is saved and
                    // restored in the finally block so the main camera's view is
                    // unaffected.
                    _faderOverrides.Clear();
                    foreach (var body in FlightGlobals.Bodies)
                    {
                        var r = body.scaledBody?.GetComponent<Renderer>();
                        if (r == null) continue;
                        bool wasEnabled = r.enabled;
                        // Guard the read: some scaled-body materials (e.g. the
                        // sun, mod-added bodies) lack _FadeAltitude. GetFloat on
                        // a missing property logs an error every body, every cam,
                        // every frame — thousands/sec. Default to 1 (no fade).
                        float fade = r.material.HasProperty(_fadeAltitudeId)
                            ? r.material.GetFloat(_fadeAltitudeId)
                            : 1f;
                        if (!wasEnabled || fade < 1f)
                        {
                            _faderOverrides.Add(new FaderState { Renderer = r, WasEnabled = wasEnabled, OriginalFade = fade });
                            if (!wasEnabled) r.enabled = true;
                            var mpb = new MaterialPropertyBlock();
                            r.GetPropertyBlock(mpb);
                            mpb.SetFloat(_fadeAltitudeId, 1f);
                            r.SetPropertyBlock(mpb);
                        }
                    }
                    try
                    {
                        if (_firstPixelCheck)
                        {
                            var ssl = ScaledSunLightHelper.GetScaledSunLight();
                            Debug.Log($"[Kerbcam] pre-scaled-render: " +
                                $"ambient={RenderSettings.ambientLight} ambientMode={RenderSettings.ambientMode} " +
                                $"scaledSunLight={(ssl == null ? "null" : $"enabled={ssl.enabled} intensity={ssl.intensity} color={ssl.color} cullingMask={ssl.cullingMask}")}");
                        }
                        _scaledCam.Render();
                        if (_firstRender)
                        {
                            _firstRender = false;
                            Debug.Log($"[Kerbcam] cam={FlightId} scaled actualRenderingPath={_scaledCam.actualRenderingPath} (set={_scaledCam.renderingPath})");
                        }
                        if (_firstPixelCheck)
                        {
                            _firstPixelCheck = false;
                            var prev = RenderTexture.active;
                            RenderTexture.active = _captureRt;
                            var sample = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                            sample.ReadPixels(new Rect(_captureRt.width / 2, _captureRt.height / 2, 1, 1), 0, 0);
                            sample.Apply();
                            var px = sample.GetPixel(0, 0);
                            UnityEngine.Object.Destroy(sample);
                            RenderTexture.active = prev;
                            Debug.Log($"[Kerbcam] cam={FlightId} scaled center pixel: r={px.r:F3} g={px.g:F3} b={px.b:F3} a={px.a:F3}");
                        }
                    }
                    finally
                    {
                        foreach (var state in _faderOverrides)
                        {
                            // Restore _FadeAltitude to ScaledSpaceFader's value in the
                            // MPB so the main camera sees the correct fade. Get the
                            // current MPB (which may carry _sunLightDirection etc) and
                            // only replace the one property we overrode.
                            var mpb = new MaterialPropertyBlock();
                            state.Renderer.GetPropertyBlock(mpb);
                            mpb.SetFloat(_fadeAltitudeId, state.OriginalFade);
                            state.Renderer.SetPropertyBlock(mpb);
                            state.Renderer.enabled = state.WasEnabled;
                        }
                        ScaledSunLightHelper.RestoreCompositeShadowsBuffer();
                    }
                }

                if (_nearCam != null && (_layers & CameraLayers.Near) != 0)
                {
                    // FX effects update materials and (re)attach their command
                    // buffers before the render; the near render then executes
                    // those CBs (e.g. the core sheath at AfterForwardAlpha).
                    _fxHost?.Render(BuildFxFrameState());
                    _nearCam.Render();
                }

                // Blit the depth-bundled capture RT into the clean readback RT.
                // When a HullcamVDS filter is active (NightVision etc), it
                // replaces the plain Blit with its own shader pass that
                // post-processes _captureRt → _readbackRt in one step. The
                // existing AsyncGPUReadback path then reads the
                // already-filtered pixels — no extra round-trip needed.
                if (_nvMaterial != null)
                {
                    Graphics.Blit(_captureRt, _readbackRt, _nvMaterial);
                }
                else if (_cameraFilter != null)
                {
                    _cameraFilter.RenderImageWithFilter(_captureRt, _readbackRt);
                }
                else
                {
                    Graphics.Blit(_captureRt, _readbackRt);
                }

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

        /// <summary>
        /// Standard disposal: tear down cameras and ring, delete all sidecar
        /// files (ring, info, control). Used by RebuildCameraList (vessel change)
        /// and OnDestroy (scene exit) where the part was not destroyed in-flight.
        /// </summary>
        public void Dispose()
        {
            DisposeCore(partDestroyed: false);
        }

        /// <summary>
        /// Destruction-path disposal: writes <c>lifecycle: "destroyed"</c> to
        /// the info.json tombstone BEFORE closing the ring, then deletes the
        /// ring and control files but leaves the info.json for the sidecar to
        /// read. Called by OnPartDestroyed and the LateUpdate defensive sweep.
        /// </summary>
        public void DisposeDestroyed()
        {
            DisposeCore(partDestroyed: true);
        }

        private void DisposeCore(bool partDestroyed)
        {
            // Guard against double-dispose (onPartDestroyed and the LateUpdate
            // defensive sweep can both fire; RebuildCameraList on vessel change
            // is also a caller). Second call is a no-op.
            if (_disposed) return;
            _disposed = true;

            // The Material is owned by the static KerbcamNightVisionFilter cache;
            // only null the ref, not Destroy it.
            _nvMaterial = null;

            if (partDestroyed)
            {
                // Write the destroyed tombstone BEFORE closing the ring so the
                // sidecar can observe the lifecycle transition. Failure here
                // must never block the cleanup path.
                WriteDestroyedManifest();
            }

            if (_cameraFilter != null)
            {
                try { _cameraFilter.Deactivate(); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"[Kerbcam] cam={FlightId} CameraFilter.Deactivate failed: {ex.Message}"); }
                _cameraFilter = null;
            }
            // Tear down FX effects (detach their CBs, release materials) before
            // destroying the camera they're attached to.
            _fxHost?.Dispose();
            _fxHost = null;
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
                if (partDestroyed)
                {
                    // _infoPath is intentionally NOT deleted on the destruction
                    // path. It serves as the lifecycle tombstone the sidecar
                    // reads to detect that this camera was destroyed. The sidecar
                    // is responsible for cleaning it up after it acknowledges the
                    // transition.
                }
                else
                {
                    // Normal teardown (vessel change, scene exit): delete the
                    // info file so stale entries don't outlive the session.
                    if (File.Exists(_infoPath)) File.Delete(_infoPath);
                }
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
