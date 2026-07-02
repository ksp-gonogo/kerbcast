/* Per-Hullcam VDS tracking. Owns Unity Cameras (near / scaled / galaxy,
   plus a spike far-local layer) parented to the part's transform, an
   AsyncGPUReadback request in flight at most, and writes RGBA frames
   into a per-camera mmap ring on each completed readback.

   Layered camera shape matches KSP's flight camera: galaxy renders skybox
   + distant celestials, scaled renders planet terrain/atmosphere at scale,
   far (spike) renders the mid-range terrain band between the near clip and
   the scaled-space handoff, near renders close parts + atmosphere effects.
   All layers target the same RenderTexture so one readback captures the
   composite.

   Render path: all cameras are permanently disabled (enabled=false) so
   Unity's auto-render never fires them. Instead, Refresh() calls
   camera.Render() explicitly each frame in galaxy -> scaled -> far -> near
   order. */
// This prevents KSP's deferred "Composite Shadows" CommandBuffer from
// running against the wrong framebuffer when our cameras render — that
// buffer would otherwise null out sun diffuse on planet surfaces, leaving
// them black while the atmospheric limb (a separate render path) stayed
// bright. ScaledSunLightHelper strips and restores the buffer around the
// Scaled layer's Render() call. Per-layer shedding is now expressed as
// "skip camera.Render() this tick" rather than toggling enabled.
//
// Atmospheric FX: kerbcast's own pluggable plasma overlay (the stock FXCamera
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
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Yangrc.OpenGLAsyncReadback;

namespace Kerbcast
{
    internal sealed class KerbcastCamera
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

        /* Consecutive Refresh() exceptions, owned by KerbcastCore's capture
           loop: reset on a clean pass, camera quarantined at the threshold
           so one persistently-broken camera can't break the rest. */
        public int RefreshFailureStreak;

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
        /* Spike: far-local camera cloned from KSP's "Camera 01". Renders the
           mid-range terrain band between Camera 01's near clip and the
           scaled-space handoff. Null when "Camera 01" is not present. */
        private Camera _farCam;
        // Whether this camera should replicate KSP's atmospheric FX. Set from
        // settings.cfg at construction; flipped at runtime by the control file.
        private bool _enableFx;
        // Visual-mod integration host (TUFX, and Scatterer/EVE/... as they land).
        // Null until SetCameras constructs it. The only mod-aware surface in this class.
        private IntegrationHost _integrationHost;
        // Pluggable atmospheric-FX host for this camera. Owns the enabled FX
        // effects (core sheath, bowshock, …); each effect renders into the near
        // pass. Null until SetCameras builds it.
        private FxHost _fxHost;
        // Which FX layers this camera runs (subject to the _enableFx master).
        private AtmoFxLayers _fxLayers;
        // The *current* render-target pair (also held in _rtPool). Refresh()
        // renders + blits + issues new readbacks against these.
        private RenderTexture _captureRt;
        private RenderTexture _readbackRt; // depth=0, GL_TEXTURE_2D-clean
        // RenderTexture pool keyed by render size (RenderSizeKey.Pack). Adaptive
        // shedding switches between pooled sets instead of destroying and
        // reallocating: destroying a RenderTexture while an AsyncGPUReadback is
        // still in flight against it orphans the native readback task (the
        // bundled OpenGL plugin exposes no cancel/free API), and that orphan is
        // then walked every frame by the DontDestroyOnLoad updater for the rest
        // of the process. Pooling never destroys mid-flight (so nothing is
        // orphaned) and removes the per-change realloc hitch. The shed cascade
        // yields ≤6 distinct sizes, so the pool stays small and bounded.
        private struct RenderTargetSet
        {
            public RenderTexture Capture;
            public RenderTexture Readback;
        }
        private readonly Dictionary<long, RenderTargetSet> _rtPool =
            new Dictionary<long, RenderTargetSet>();
        // The in-flight readback is described independently of the *current*
        // target so a resolution change mid-readback still drains the pending
        // frame at its original dimensions before switching. The decoupling
        // logic is unit-tested in ReadbackTargetTracker.Tests.
        private readonly ReadbackTargetTracker _targets = new ReadbackTargetTracker();
        private readonly MmapFrameRing _ring;
        private readonly string _ringPath;
        private readonly string _infoPath;
        private readonly string _controlPath;
        // Shared-memory control block written by the sidecar (replaces the
        // <flight_id>.control.json poll). Opened lazily once the file appears.
        // Its monotonic seqlock counter is the change detector — collision-proof,
        // unlike the mtime/content compare it replaces, so a rate=0 stop right
        // after a drag can never be silently dropped.
        private ControlBlock _controlBlock;
        private int _controlCheckCountdown;

        // Pan/tilt interpolation state. Target is written by PollControlFile
        // from operator control.json; Current tracks toward it each Refresh()
        // at SlewDegPerSec. Near-cam rotation and mesh transforms are driven
        // from Current, so status echoes what is actually on-screen.
        private float _panYawTarget;
        private float _panPitchTarget;
        private float _panYawCurrent;
        private float _panPitchCurrent;
        // Persistent velocities from set-pan-rate / set-zoom-rate, normalised
        // -1..1. Each holds its last value until a new rate supersedes it
        // (a missing JSON field leaves the rate unchanged; only an explicit 0
        // stops). Integrated every Refresh() into the pan/fov targets.
        //   +panYawRate   = pan right    (matches absolute panYaw sign)
        //   +panPitchRate = pan up       (matches absolute panPitch sign)
        //   +zoomRate     = zoom IN      (FoV DECREASES — subtracted from target)
        private float _panYawRate;
        private float _panPitchRate;
        private float _zoomRate;
        // Smoothed FoV target. SetFov and the zoom-rate integrator write this;
        // Fov slews toward it each Refresh() at _zoomCap.FovSlewDegPerSec so both
        // discrete set-fov and hold-to-zoom animate rather than snap. Initialised
        // to the camera's starting FoV in the constructor (snapped, not slewed).
        private float _fovTarget;
        // Last-seen absolute-command sequence numbers (panSeq / fovSeq in
        // control.json). control.json is a full-state snapshot the sidecar
        // re-serialises on *every* command, so the stale absolute panYaw /
        // panPitch / fov rides along on unrelated writes — and on the
        // rate-stop flush itself. While a rate has integrated the target away
        // from that stale absolute, re-applying it would snap the camera back
        // (e.g. every release slews back to the last preset). The sidecar
        // bumps these seqs ONLY on an absolute set-pan / set-fov (never on a
        // rate command or the disconnect deadman), so we apply the absolute
        // only when its seq changes — a re-serialised stale value is then
        // idempotent, while a genuine new absolute (even re-issuing the same
        // value, e.g. re-clicking a preset after drift) still lands. Init to
        // -1 (no real seq) so the first snapshot carrying an absolute applies.
        private long _lastPanSeq = -1;
        private long _lastFovSeq = -1;
        // Zoom rate caps. No per-part table — every camera carries the default
        // rates; they only matter when SupportsZoom. Must NOT be left as
        // default(ZoomCapability): all-zero would freeze the FoV slew.
        private readonly ZoomCapability _zoomCap = ZoomCapability.Default;
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
        /// <summary>Whether a peer is currently streaming this camera. Staggering
        /// is budgeted over the subscribed set only — idle cameras cost nothing
        /// and must not consume capture permits.</summary>
        public bool Subscribed => _subscribed;
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
        // per-frame GC pressure. FaderState is a struct, so the list stores them
        // inline (no per-entry GC).
        private readonly List<FaderState> _faderOverrides = new List<FaderState>();
        // Reused across bodies and frames. Previously the fader loop did
        // `new MaterialPropertyBlock()` twice per overridden body, per camera,
        // per frame — a steady stream of Mono garbage (stop-the-world GC ⇒
        // frametime spikes). GetPropertyBlock overwrites it each call, so a
        // single shared scratch instance is correct.
        private readonly MaterialPropertyBlock _faderMpb = new MaterialPropertyBlock();
        // Scaled-body renderers, cached on first use. Celestial bodies don't
        // change during a flight and the camera is recreated on vessel change,
        // so this avoids a GetComponent<Renderer> over every body on every
        // scaled render. Entries can still go null defensively (handled below).
        private List<Renderer> _scaledBodyRenderers;
        private static readonly int _fadeAltitudeId = Shader.PropertyToID("_FadeAltitude");

        private UniversalAsyncGPUReadbackRequest _pendingRequest;
        private bool _readbackInFlight;
        private double _pendingCaptureTsMs;
        private int _consecutiveErrors;

        // Per-phase render-timing accumulator (last / EMA / rolling-max per
        // phase). Populated only when KerbcastSettings.EnableTelemetry is true;
        // the status writer reads it and aggregates across cameras. Always
        // allocated (cheap, one small object per camera) but never written to
        // when telemetry is off, so the OFF path stays allocation-free.
        private readonly PhaseTimings _phaseTimings = new PhaseTimings();
        /// <summary>Per-phase render timings for this camera. Read by the 1Hz
        /// status writer; only meaningful when telemetry is enabled.</summary>
        public PhaseTimings PhaseTimings => _phaseTimings;
        // Cached for this Refresh tick (read once at the top). When false, every
        // timing bracket is skipped so the OFF path makes zero GetTimestamp
        // calls. When true, drain (ProcessReadback) timing accumulates here so
        // it can be combined with the Request-issue timing into one Readback
        // sample at the end of the tick.
        private bool _telemetry;
        private double _readbackDrainMs;
        // Milliseconds per Stopwatch tick — computed once. Stopwatch.GetTimestamp
        // returns raw ticks (no allocation, no shared mutable Stopwatch state);
        // multiply the tick delta by this to get milliseconds.
        private static readonly double _msPerTick =
            1000.0 / System.Diagnostics.Stopwatch.Frequency;

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
        /* dockingdisplay.png, passed to RenderTitlePage before each blit so
           the _Title/_TitleTex uniforms are set deterministically per camera
           rather than inherited from stale shared state. LoadTextureFile
           decodes a fresh Texture2D from disk (it is NOT the GameDatabase
           instance); the static field keeps the single copy alive for the
           plugin's lifetime, shared by every kerbcast camera. */
        private static Texture2D _hullcamTitleTex;
        /* Per-camera private-material blit mechanism (the c00fe16 fix):
           owns this camera's private clone of HullcamVDS's MovieTime
           material and the mtShader static redirect around each filter
           pass. Mechanism lives in HullcamBlit/HullcamFilterBlit.cs (a
           KSP-free file the ci/kerbcast-shaders determinism test compiles
           verbatim); this class only supplies the CameraFilter type and
           the render pass below. */
        private readonly HullcamFilterBlit _filterBlit =
            new HullcamFilterBlit(typeof(CameraFilter));
        // Bound once in the constructor so the per-frame Run call
        // allocates no closure.
        private readonly Action<RenderTexture, RenderTexture> _filterRenderPass;
        // kerbcast's own NightVision material, used instead of HullcamVDS's
        // additive-shift filter when the shader bundle is available.
        private Material _nvMaterial;

        public KerbcastCamera(
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
            if (zoomable != null)
            {
                FovMin = zoomable.cameraFoVMin;
                FovMax = zoomable.cameraFoVMax;
                // Clamp a fisheye-wide authored max (TurretCam maxes at 100).
                // Applied here so the initial Fov clamp below and SetFov both
                // respect the capped maximum.
                if (_panCap.FovMaxCap.HasValue)
                    FovMax = Mathf.Min(FovMax, _panCap.FovMaxCap.Value);
            }
            else
            {
                FovMin = hullcam.cameraFoV;
                FovMax = hullcam.cameraFoV;
            }
            // Zoom needs a real FoV range, not just the subclass type. Several
            // MuMechModuleHullCameraZoom parts pin cameraFoVMin == cameraFoVMax
            // (DC.munCam 25/25, DC.aerocam2 45/45, RoverCam, base mumech.hullcam
            // 30/30). With a zero-width range SetFov's Clamp pegs every request
            // to the single value, so zoom "works once" off the default FoV then
            // never again. Treat that as non-zoomable so the browser hides the
            // control and set-fov / set-zoom-rate are honest no-ops.
            SupportsZoom = zoomable != null && FovMax > FovMin + 0.01f;
            // Snap both the displayed FoV and the slew target so a fresh camera
            // starts settled (SetFov now only moves _fovTarget; the constructor
            // is the one place that snaps Fov directly).
            Fov = _fovTarget = Mathf.Clamp(hullcam.cameraFoV, FovMin, FovMax);

            // Cache identity fields now while the Part is guaranteed live.
            // WriteDestroyedManifest (called from Dispose, which may be
            // invoked from a part-destruction event) reads these instead of
            // touching Hullcam.part, which may already be null by that point.
            _cachedPartName = hullcam.part.partInfo?.name ?? "unknown";
            _cachedPartTitle = hullcam.part.partInfo?.title ?? _cachedPartName;
            _cachedCameraName = string.IsNullOrEmpty(hullcam.cameraName) ? _cachedPartTitle : hullcam.cameraName;
            _cachedVesselName = hullcam.vessel?.GetDisplayName() ?? hullcam.vessel?.vesselName ?? "<unknown>";

            _filterRenderPass = RenderFilterPass;

            _ringPath = Path.Combine(ringDir, $"{FlightId}.ring");
            _infoPath = Path.Combine(ringDir, $"{FlightId}.info.json");
            _controlPath = Path.Combine(ringDir, $"{FlightId}.control.bin");
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
        // are NOT picked up — a vessel reload (or kerbcast re-attach)
        // would. Acceptable for v1; can add a periodic mode-poll later.
        //
        // cameraMode is a float in the Hullcam ConfigNode (0-8, mapped
        // to eCameraMode by value) — converted to enum by direct cast.
        // Mode 0 (Normal) returns a no-op filter; we leave _cameraFilter
        // null in that case so Refresh's blit takes the fast path.
        private void BuildHullcamFilter()
        {
            if (!KerbcastSettings.EnableHullcamEffects) return;
            try
            {
                int modeInt = (int)Hullcam.cameraMode;
                var mode = (CameraFilter.eCameraMode)modeInt;
                if (mode == CameraFilter.eCameraMode.Normal) return;

                // Night vision: use kerbcast's multiplicative-gain shader
                // instead of HullcamVDS's additive-shift filter. Falls back
                // to the HullcamVDS path below if the bundle is missing.
                if (mode == CameraFilter.eCameraMode.NightVision)
                {
                    _nvMaterial = KerbcastNightVisionFilter.GetMaterial();
                    if (_nvMaterial != null)
                    {
                        UnityEngine.Debug.Log($"[Kerbcast] cam={FlightId} kerbcast NightVision shader active");
                        return;
                    }
                }

                var filter = CameraFilter.CreateFilter(mode);
                if (filter == null) return;
                if (!filter.Activate())
                {
                    UnityEngine.Debug.LogWarning($"[Kerbcast] cam={FlightId} CameraFilter.Activate failed for mode={mode}");
                    return;
                }
                _cameraFilter = filter;
                if (_hullcamTitleTex == null)
                {
                    _hullcamTitleTex = CameraFilter.LoadTextureFile("dockingdisplay.png");
                    // Hullcam clamps its own dockingDisplay instance in
                    // InitializeAssets; LoadTextureFile leaves the default
                    // Repeat. Match it so the reticle never tiles if a
                    // V-hold roll offsets the title UV outside [0,1].
                    if (_hullcamTitleTex != null)
                        _hullcamTitleTex.wrapMode = TextureWrapMode.Clamp;
                }
                UnityEngine.Debug.Log($"[Kerbcast] cam={FlightId} HullcamVDS filter active: mode={mode}");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Kerbcast] cam={FlightId} BuildHullcamFilter failed: {ex.Message}");
            }
        }

        /* The filter pass HullcamFilterBlit.Run redirects: Hullcam's
           CameraFilter writes its uniforms and runs its Graphics.Blit via
           the mtShader static, which Run points at this camera's private
           material for exactly these two calls. Parameterised by source
           and destination so Run's one-time orientation probe can push its
           test frame through the same pass. */
        private void RenderFilterPass(RenderTexture src, RenderTexture dst)
        {
            _cameraFilter.RenderTitlePage(true, _hullcamTitleTex);
            _cameraFilter.RenderImageWithFilter(src, dst);
        }

        // Select (or lazily create) the pooled render-target set for this size
        // and make it current. Never destroys — see the _rtPool field comment.
        private void BuildRenderTargets(int width, int height)
        {
            long key = RenderSizeKey.Pack(width, height);
            if (!_rtPool.TryGetValue(key, out var set))
            {
                var capture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    antiAliasing = 1,
                };
                capture.Create();

                // depth=0 so GetNativeTexturePtr returns a vanilla GL_TEXTURE_2D
                // handle on Mesa OpenGL. With depth=24 (the capture RT) the
                // yangrc plugin's glGetTexLevelParameteriv reads back zero
                // dimensions and silently does nothing.
                var readback = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                readback.Create();

                set = new RenderTargetSet { Capture = capture, Readback = readback };
                _rtPool[key] = set;
            }
            _captureRt = set.Capture;
            _readbackRt = set.Readback;
            _targets.SetCurrent(width, height);
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

            // Switch to the pooled set for the new size. The old set stays in
            // the pool (never destroyed), so a readback still in flight against
            // it drains safely at its captured dimensions (_pendingW/_pendingH/
            // _pendingScratch) — nothing is orphaned, and there's no realloc.
            BuildRenderTargets(width, height);
            RenderWidth = width;
            RenderHeight = height;

            if (_nearCam != null) _nearCam.targetTexture = _captureRt;
            if (_scaledCam != null) _scaledCam.targetTexture = _captureRt;
            if (_galaxyCam != null) _galaxyCam.targetTexture = _captureRt;
            if (_farCam != null) _farCam.targetTexture = _captureRt;

            Debug.Log($"[Kerbcast] cam={FlightId} render size → {width}×{height}");
        }

        /// <summary>
        /// Update the operator-set ceiling for render size. Adaptive
        /// downscale and the viewer quality clamp can render below this;
        /// never above. Currently only settable at construction; runtime
        /// API for resolution is a future addition.
        /// </summary>
        public void SetOperatorRenderSize(int width, int height)
        {
            OperatorWidth = width;
            OperatorHeight = height;
            // Recompute through the one quality path so any in-effect shed
            // level / viewer clamp scales from the new ceiling.
            ApplyEffectiveQuality();
        }

        private static void DumpModelTransforms(Part part)
        {
            var sb = new System.Text.StringBuilder();
            var root = part.FindModelTransform("model");
            if (root == null) root = part.transform;
            WalkTransforms(root, 0, sb);
            Debug.Log($"[Kerbcast] model transforms for {part.name}:\n{sb}");
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

        /* Layered camera stack. Each layer copies the corresponding KSP
           flight camera so depth-ordering, clearFlags, cullingMask, and
           per-layer rendering tricks all inherit correctly. All layers
           target `_captureRt`; Unity composites by camera.depth (galaxy
           back, scaled next, far-spike next, near front), so a single
           readback against the RT captures the composite frame. */
        private void SetCameras()
        {
            var partTransform = string.IsNullOrEmpty(Hullcam.cameraTransformName)
                ? Hullcam.part.transform
                : Hullcam.part.FindModelTransform(Hullcam.cameraTransformName);
            if (partTransform == null)
            {
                Debug.LogWarning($"[Kerbcast] cam={FlightId} cameraTransformName '{Hullcam.cameraTransformName}' not found on part {Hullcam.part.name}");
                return;
            }

            // Visual-mod integrations (TUFX, and Scatterer/EVE/... as they land). The
            // host is the only mod-aware surface; the render code below treats all
            // integrations uniformly. Constructed here so the MSAA format lever is known
            // before the cloned cameras are configured.
            _integrationHost = new IntegrationHost();
            bool integrationsForceNoMsaa = _integrationHost.ForceNoMsaa;
            bool allowMsaa = !integrationsForceNoMsaa;

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
                    Debug.Log($"[Kerbcast] cam={FlightId} yaw transform '{_panCap.YawTransformName}' found, restRot={_yawRestRot} restPos={_yawRestLocalPos}");
                }
                else
                    Debug.LogWarning($"[Kerbcast] cam={FlightId} yaw transform '{_panCap.YawTransformName}' not found on {Hullcam.part.name}");
            }

            // Camera parenting strategy:
            //
            // Any joint (compound yaw+pitch like hc.launchcam, OR yaw-only like
            // DC.TurretCam / TopJoint): parent the near camera RIGIDLY to the
            // joint. The joint carries the pan rotation and the camera inherits
            // it as world-space rotation, so the lens travels WITH the visible
            // head — the head therefore stays behind the lens and never enters
            // frame. Refresh() keeps the camera's localRotation at _baseRotation
            // (no additional camera-level pan) since the joint provides it all.
            //
            // The mount position is re-expressed in the joint's local frame so
            // the lens sits at the correct world position throughout the joint's
            // travel. For a yaw-only joint whose authored cameraPosition is well
            // off the yaw axis (TurretCam's is ~0.5 lateral), parenting at that
            // offset would orbit the lens through a wide arc — the old "rotates
            // about the wrong point" symptom. CameraMountLocal lets such a part
            // pin the lens onto the yaw axis (in joint-local coords) so it
            // rotates in place while still travelling with the head.
            //
            // No joint: parent to partTransform, use cameraPosition/Forward/Up
            // as-is in part space, and Refresh() drives the camera's own
            // localRotation for pan.
            Transform nearParent;
            Vector3 nearLocalPos;
            if (_yawTransform != null)
            {
                // Joint present: camera follows the joint; re-express in joint frame.
                if (_panCap.CameraMountLocal.HasValue)
                {
                    // Authored mount is already given in the joint's local frame.
                    nearLocalPos = _panCap.CameraMountLocal.Value;
                }
                else
                {
                    Vector3 worldPos = partTransform.TransformPoint(Hullcam.cameraPosition);
                    nearLocalPos = _yawTransform.InverseTransformPoint(worldPos);
                }
                Vector3 worldFwd = partTransform.TransformDirection(Hullcam.cameraForward);
                Vector3 worldUp  = partTransform.TransformDirection(Hullcam.cameraUp);
                _baseRotation = Quaternion.LookRotation(
                    _yawTransform.InverseTransformDirection(worldFwd),
                    _yawTransform.InverseTransformDirection(worldUp));
                nearParent = _yawTransform;
            }
            else
            {
                // No joint: camera stays on partTransform and rotates in place.
                nearLocalPos = Hullcam.cameraPosition;
                _baseRotation = Quaternion.LookRotation(Hullcam.cameraForward, Hullcam.cameraUp);
                nearParent = partTransform;
            }

            // Near layer — close-up of parts + atmospheric effects.
            var nearGo = new GameObject($"Kerbcast_{FlightId}_Near");
            _nearCam = nearGo.AddComponent<Camera>();
            var sourceNear = FindKspCamera("Camera 00");
            if (sourceNear != null) _nearCam.CopyFrom(sourceNear);
            _nearCam.name = $"Kerbcast_{FlightId}_Near";
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
            _nearCam.allowMSAA = allowMsaa;
            // Offscreen RT cameras shouldn't run Unity's occlusion logic —
            // it's computed against the main viewport's frustum and either
            // wastes cycles or incorrectly culls objects in our cameras'
            // frusta. Disabled on every layer.
            _nearCam.useOcclusionCulling = false;
            // Add kerbcast's FX-only layer so ember ParticleSystems (and any
            // future GameObject-based effects on AtmoFxConstants.Layer) render
            // on kerbcast streams without leaking into the main flight view.
            _nearCam.cullingMask |= AtmoFxConstants.LayerMask;
            nearGo.AddComponent<CanvasHack>();

            // Scaled layer — planet terrain + atmosphere at scaled-space scale.
            var scaledGo = new GameObject($"Kerbcast_{FlightId}_Scaled");
            _scaledCam = scaledGo.AddComponent<Camera>();
            var sourceScaled = FindKspCamera("Camera ScaledSpace");
            if (sourceScaled != null)
            {
                _scaledCam.CopyFrom(sourceScaled);
                scaledGo.transform.parent = sourceScaled.transform;
                var scaledComps = sourceScaled.gameObject.GetComponents<MonoBehaviour>();
                Debug.Log($"[Kerbcast] Camera ScaledSpace components ({scaledComps.Length}): " +
                    string.Join(", ", System.Array.ConvertAll(scaledComps, c => c.GetType().Name)));
            }
            _scaledCam.name = $"Kerbcast_{FlightId}_Scaled";
            scaledGo.transform.localRotation = Quaternion.identity;
            scaledGo.transform.localPosition = Vector3.zero;
            scaledGo.transform.localScale = Vector3.one;
            _scaledCam.fieldOfView = Hullcam.cameraFoV;
            _scaledCam.targetTexture = _captureRt;
            _scaledCam.allowHDR = true;
            _scaledCam.allowMSAA = allowMsaa;
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
            var galaxyGo = new GameObject($"Kerbcast_{FlightId}_Galaxy");
            _galaxyCam = galaxyGo.AddComponent<Camera>();
            // Pre-CopyFrom fallback: if the source GalaxyCamera lookup
            // fails (vessel-load race, scene weirdness), at least we
            // render a predictable solid-black backdrop instead of
            // whatever Unity defaults Camera to. CopyFrom overwrites
            // these when it succeeds.
            _galaxyCam.clearFlags = CameraClearFlags.SolidColor;
            _galaxyCam.backgroundColor = Color.black;
            var sourceGalaxy = FindKspCamera("GalaxyCamera");
            if (sourceGalaxy != null)
            {
                _galaxyCam.CopyFrom(sourceGalaxy);
                galaxyGo.transform.parent = sourceGalaxy.transform;
            }
            _galaxyCam.name = $"Kerbcast_{FlightId}_Galaxy";
            galaxyGo.transform.position = Vector3.zero;
            galaxyGo.transform.localRotation = Quaternion.identity;
            galaxyGo.transform.localScale = Vector3.one;
            _galaxyCam.fieldOfView = Hullcam.cameraFoV;
            _galaxyCam.targetTexture = _captureRt;
            _galaxyCam.allowHDR = true;
            _galaxyCam.allowMSAA = allowMsaa;
            _galaxyCam.useOcclusionCulling = false;
            // Force Forward, same as the scaled clone. With the Deferred mod the
            // game runs deferred and this clone inherits DeferredShading via
            // CopyFrom, but deferred offscreen RTs render pure black on Mesa/
            // OpenGL (the Deck): the galaxy cube is a forward-authored skybox and
            // vanished entirely. DeferredIntegration only force-forwards near/far,
            // so the galaxy clone was the one layer left deferred. The galaxy has
            // no scene lights, so Forward costs nothing.
            _galaxyCam.renderingPath = RenderingPath.Forward;
            var galaxyRot = galaxyGo.AddComponent<LayerCamRotator>();
            galaxyRot.NearCamera = _nearCam;
            galaxyRot.UseScaledSpace = false;
            galaxyGo.AddComponent<CanvasHack>();

            /* Spike: far-local layer, cloned from KSP's "Camera 01". Renders
               the terrain band between the near camera's far clip and the
               scaled-space transition. Parented to the part transform like
               the near camera so it tracks vessel movement. Depth is set
               between the scaled and near values so Unity's compositing
               order is galaxy -> scaled -> far -> near. */
            var sourceFar = FindKspCamera("Camera 01");
            if (sourceFar != null)
            {
                var farGo = new GameObject($"Kerbcast_{FlightId}_Far");
                _farCam = farGo.AddComponent<Camera>();
                _farCam.CopyFrom(sourceFar);
                _farCam.name = $"Kerbcast_{FlightId}_Far";
                farGo.transform.parent = nearParent;
                farGo.transform.localPosition = nearLocalPos;
                farGo.transform.localRotation = _baseRotation;
                _farCam.fieldOfView = Hullcam.cameraFoV;
                _farCam.targetTexture = _captureRt;
                _farCam.allowHDR = true;
                _farCam.allowMSAA = allowMsaa;
                _farCam.useOcclusionCulling = false;
                /* Depth: Camera 01 inherits its depth from CopyFrom. Force it
                   between the scaled and near values so the composite is
                   galaxy -> scaled -> far -> near. KSP's typical depths are
                   GalaxyCamera=-1, ScaledSpace=0, Camera 01=1, Camera 00=2.
                   Using sourceFar.depth - 0.5 puts us at ~0.5, between scaled
                   (0) and near (2), which is correct for all typical KSP
                   depth assignments. */
                _farCam.depth = sourceFar.depth - 0.5f;
                _farCam.enabled = false;
                farGo.AddComponent<CanvasHack>();
                Debug.Log($"[Kerbcast] cam={FlightId} far-local camera created: " +
                    $"near={sourceFar.nearClipPlane} far={sourceFar.farClipPlane} " +
                    $"clearFlags={sourceFar.clearFlags} depth={_farCam.depth}");
            }
            else
            {
                Debug.LogWarning($"[Kerbcast] cam={FlightId} 'Camera 01' not found: far layer skipped");
            }

            // Attach every available integration to each cloned layer. The host skips
            // integrations that do not opt into a layer and no-ops the unavailable ones.
            // TUFX's own EnableTUFX setting is honoured inside its IsAvailable probe path.
            _integrationHost.ApplyToLayer(_nearCam, CameraLayers.Near);
            _integrationHost.ApplyToLayer(_scaledCam, CameraLayers.Scaled);
            _integrationHost.ApplyToLayer(_galaxyCam, CameraLayers.Galaxy);
            if (_farCam != null)
                _integrationHost.ApplyToLayer(_farCam, CameraLayers.Far);

            // Pitch transform — resolved here rather than above because it
            // doesn't affect camera parenting in the current design.
            if (!string.IsNullOrEmpty(_panCap.PitchTransformName))
            {
                _pitchTransform = Hullcam.part.FindModelTransform(_panCap.PitchTransformName);
                if (_pitchTransform != null)
                    _pitchRestRot = _pitchTransform.localRotation;
                else
                    Debug.LogWarning($"[Kerbcast] cam={FlightId} pitch transform '{_panCap.PitchTransformName}' not found on {Hullcam.part.name}");
            }

            // Yaw-base transform: a fixed base that co-rotates in yaw so the
            // moving head doesn't clip through it. No pitch — the base is static.
            if (!string.IsNullOrEmpty(_panCap.YawBaseTransformName))
            {
                _yawBaseTransform = Hullcam.part.FindModelTransform(_panCap.YawBaseTransformName);
                if (_yawBaseTransform != null)
                {
                    _yawBaseRestRot = _yawBaseTransform.localRotation;
                    Debug.Log($"[Kerbcast] cam={FlightId} yaw-base transform '{_panCap.YawBaseTransformName}' found");
                }
                else
                    Debug.LogWarning($"[Kerbcast] cam={FlightId} yaw-base transform '{_panCap.YawBaseTransformName}' not found on {Hullcam.part.name}");
            }

            /* All cameras are permanently disabled; Unity must not
               auto-render them. Refresh() drives explicit camera.Render()
               calls each tick; disabled cameras still participate in
               LayerCamRotator.OnPreRender (which fires on camera.Render())
               so transform tracking continues to work correctly. The far
               camera's enabled flag is set inside its own construction
               block above. */
            _nearCam.enabled = false;
            _scaledCam.enabled = false;
            _galaxyCam.enabled = false;

            // Build the pluggable atmospheric-FX host for the near camera. The
            // effective layer set folds the master toggle in (off → no effects,
            // a genuine no-op). Each effect owns its own rendering surface.
            _fxHost = new FxHost(_nearCam);
            _fxHost.SetEnabledLayers(EffectiveFxLayers());
            _fxHost.OnVesselChanged(Hullcam?.vessel);
        }

        /* Master gate folded into the layer set: FX off => no layers => no effects.
           Provider selection: when Firefly is installed and enabled, capture its
           reentry plasma INSTEAD of kerbcast's own (running both double-plasmas), by
           clearing the kerbcast-plasma bits and setting the Firefly bit. */
        private AtmoFxLayers EffectiveFxLayers()
        {
            if (!_enableFx) return AtmoFxLayers.None;
            if (KerbcastSettings.EnableFirefly && FireflyCaptureEffect.IsFireflyAvailable())
                return (_fxLayers & ~AtmoFxLayers.All) | AtmoFxLayers.Firefly;
            return _fxLayers;
        }

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
            if (KerbcastSettings.DebugWindDirection.sqrMagnitude > 0.0001f)
                vel = KerbcastSettings.DebugWindDirection;

            return new FxFrameState(v, _nearCam, vel, mach, q, Time.deltaTime, Time.time);
        }

        // Per-frame inputs for visual-mod integrations that need live flight state.
        // Mirrors BuildFxFrameState's quantities so FX and integrations agree.
        private IntegrationFrameState BuildIntegrationFrameState(CameraLayers layer)
        {
            var v = Hullcam != null ? Hullcam.vessel : null;
            float mach = v != null ? (float)v.mach : 0f;
            float q = v != null ? (float)v.dynamicPressurekPa : 0f;
            double alt = v != null ? v.altitude : 0d;
            return new IntegrationFrameState(v, layer, Time.deltaTime, mach, q, alt);
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
        /// KerbcastCore on part destruction / vessel modification so stale part
        /// renderers don't linger in an effect's CommandBuffer.
        /// </summary>
        public void MarkFxDirty()
        {
            /* Re-reconcile the layer set first: Firefly may have loaded since the
               last setup, flipping the provider selection. SetEnabledLayers no-ops
               when the set is unchanged. */
            _fxHost?.SetEnabledLayers(EffectiveFxLayers());
            _fxHost?.OnVesselChanged(Hullcam != null ? Hullcam.vessel : null);
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
        /// Apply an operator-driven (discrete) FoV change. Sets the slew
        /// *target* rather than snapping `Fov` directly — Refresh() animates
        /// the displayed FoV (and the Hullcam module + Unity cameras) toward
        /// the target at `_zoomCap.FovSlewDegPerSec`, so a discrete `set-fov`
        /// now reads as a smooth zoom instead of a hard step. Composes with
        /// the zoom rate: an absolute set-fov jumps the target, then any active
        /// rate continues integrating from there. Silently no-ops for parts
        /// where `SupportsZoom == false`.
        /// </summary>
        public void SetFov(float fov)
        {
            if (!SupportsZoom) return;
            float clamped = Mathf.Clamp(fov, FovMin, FovMax);
            // Compare against the target, not Fov: Fov lags as it slews, so a
            // Fov-based early-out would misfire mid-animation.
            if (Mathf.Abs(clamped - _fovTarget) < 0.01f) return;
            _fovTarget = clamped;
        }

        /// <summary>
        /// Cascade table: (resolution multiplier, layers to drop). Lower
        /// levels are gentler on perception. Resolution reduction wins over
        /// layer dropping because it preserves scene completeness: a blurrier
        /// full image is more useful than a sharp scene with a missing layer.
        /// All layers are kept until the emergency level; only resolution drops
        /// before then. Galaxy in particular is cheap (one skybox cube, no PQS
        /// or atmosphere) and is the operator's star-field / orientation
        /// reference, so shedding it early (as an earlier table did at level 3)
        /// blacked out the background under load for almost no headroom.
        /// </summary>
        private static readonly (float ResScale, CameraLayers Drop)[] ShedTable =
        {
            (1.00f, CameraLayers.None),                                 // 0: full
            (0.75f, CameraLayers.None),                                 // 1: gentle res drop
            (0.50f, CameraLayers.None),                                 // 2: half res
            (0.35f, CameraLayers.None),                                 // 3: deeper res drop, keep all layers
            (0.25f, CameraLayers.None),                                 // 4: quarter res, keep all layers
            /* Emergency last resort only: dropping Far reintroduces the black
               band between the scaled and near handoff, and dropping Galaxy
               blacks out the star-field. Tier placement provisional pending the
               Deck perf baseline (section 8.0). */
            (0.25f, CameraLayers.Galaxy | CameraLayers.Scaled | CameraLayers.Far),  // 5: emergency (last resort; reintroduces far black band)
        };

        public static int MaxShedLevel => ShedTable.Length - 1;

        // The two quality inputs that compose into the effective render
        // size. _shedLevel is owned by the adaptive machinery (KerbcastCore's
        // AdaptiveQualityController via ApplyAutoShed); _viewerLevel by the
        // viewer's quality preset (control block via PollControlFile). They
        // never touch each other; ApplyEffectiveQuality min()s their scales.
        private int _shedLevel;
        private int _viewerLevel;

        /// <summary>Currently-applied viewer quality level (index into
        /// QualityClamp.ViewerScales; 0 = no viewer clamp).</summary>
        public int ViewerLevel => _viewerLevel;

        /// <summary>
        /// Apply an adaptive-controller quality level. Stores the level and
        /// recomputes the effective size/layers; the viewer clamp composes
        /// via min() inside ApplyEffectiveQuality, so a demote always wins
        /// over a viewer target and a promote hands control back to it.
        /// </summary>
        public void ApplyAutoShed(int level)
        {
            if (level < 0) level = 0;
            if (level > MaxShedLevel) level = MaxShedLevel;
            _shedLevel = level;
            ApplyEffectiveQuality();
        }

        /// <summary>
        /// Apply a viewer-requested quality clamp (sidecar control block's
        /// viewer_level; 0 = full / no clamp). Only ever lowers resolution
        /// below the operator ceiling; layers are untouched (those belong
        /// to the operator mask and the shed cascade) and the adaptive
        /// controller's state is never read or written here.
        /// </summary>
        public void SetViewerQualityLevel(int level)
        {
            int clamped = QualityClamp.ClampViewerLevel(level);
            if (_viewerLevel == clamped) return;
            _viewerLevel = clamped;
            Debug.Log($"[Kerbcast] cam={FlightId} viewer quality level → {clamped} "
                + $"(scale {QualityClamp.ViewerScales[clamped]:F2})");
            ApplyEffectiveQuality();
        }

        // THE single resolution-change path. Every quality input (operator
        // ceiling changes, adaptive shed moves, viewer clamp moves) funnels
        // here, so the pooled-RT SetRenderSize mechanism is the only way
        // render dims ever change. effective = min(ceiling, shed, viewer).
        private void ApplyEffectiveQuality()
        {
            var (resScale, drop) = ShedTable[_shedLevel];
            float scale = QualityClamp.EffectiveScale(resScale, _viewerLevel);

            int targetW = QualityClamp.ScaleDimension(OperatorWidth, scale);
            int targetH = QualityClamp.ScaleDimension(OperatorHeight, scale);
            if (targetW != RenderWidth || targetH != RenderHeight)
            {
                SetRenderSize(targetW, targetH);
            }

            // Layer dropping stays purely shed-driven: viewers can lower
            // resolution but never which layers render.
            var targetLayers = _operatorLayers & ~drop;
            if (_layers != targetLayers)
            {
                _layers = targetLayers;
                ApplyLayers();
            }
        }

        // Cached list of scaled-body renderers (one per celestial body that has
        // one). Built once on first scaled render; bodies are fixed for the
        // flight and the camera is recreated on vessel change, so we never
        // refresh it. Replaces a per-frame, per-camera GetComponent sweep over
        // FlightGlobals.Bodies.
        private List<Renderer> ScaledBodyRenderers()
        {
            if (_scaledBodyRenderers != null) return _scaledBodyRenderers;
            _scaledBodyRenderers = new List<Renderer>();
            foreach (var body in FlightGlobals.Bodies)
            {
                var r = body.scaledBody?.GetComponent<Renderer>();
                if (r != null) _scaledBodyRenderers.Add(r);
            }
            return _scaledBodyRenderers;
        }

        private void ApplyLayers()
        {
            // Cameras are permanently disabled (enabled=false) — Unity's
            // auto-render is never used for our offscreen cameras. The
            // layer mask (_layers) is already updated by the caller before
            // ApplyLayers() is invoked; Refresh() consumes it to decide
            // which camera.Render() calls to make this tick. Nothing
            // additional needed here.
        }

        // Sidecar→plugin control channel. The sidecar's data-channel handlers
        // (SetLayers / SetRenderSize / SetFov / SetPan / *-rate) write the full
        // operator-requested state into the <FlightId>.control.bin shared-memory
        // block under a seqlock. The plugin reads it here each frame; the block's
        // monotonic seq is the change detector, so a skip is cheap and a rate=0
        // stop can never be missed.
        private void PollControlFile()
        {
            try
            {
                if (_controlBlock == null)
                {
                    _controlBlock = ControlBlock.Open(_controlPath, out var openRes);
                    if (openRes == ControlBlock.OpenResult.VersionMismatch)
                    {
                        Debug.LogError(
                            $"[Kerbcast] cam={FlightId} control-block layout version mismatch — "
                            + "sidecar and plugin are out of sync; control is disabled until they match");
                    }
                    if (_controlBlock == null) return; // file not ready yet
                }

                // Returns false when nothing has changed since the last applied
                // write / the writer is mid-write / nothing has been written.
                if (!_controlBlock.TryReadChanged(out var snap)) return;

                // Subscriber flag drives the per-layer Camera.enabled state via
                // ApplyLayers. Always present in the block (sidecar always writes
                // it); defaults safe-asleep on a brand-new block.
                if (snap.Subscribed != _subscribed)
                {
                    _subscribed = snap.Subscribed;
                    if (_subscribed)
                    {
                        // Snap interpolation to target on peer reconnect so the
                        // peer sees the current commanded position immediately
                        // instead of a phantom pan-back from the last rest position.
                        _panYawCurrent = _panYawTarget;
                        _panPitchCurrent = _panPitchTarget;
                        // Mirror the snap for FoV: jump the displayed FoV to its
                        // commanded target (and apply to the cameras + Hullcam GUI)
                        // so a resubscribing peer sees the commanded zoom at once
                        // rather than slewing from a stale value.
                        if (SupportsZoom)
                        {
                            Fov = _fovTarget;
                            Hullcam.cameraFoV = Fov;
                            if (_nearCam != null) _nearCam.fieldOfView = Fov;
                            if (_scaledCam != null) _scaledCam.fieldOfView = Fov;
                            if (_galaxyCam != null) _galaxyCam.fieldOfView = Fov;
                            if (_farCam != null) _farCam.fieldOfView = Fov;
                        }
                    }
                    Debug.Log($"[Kerbcast] cam={FlightId} subscribed → {_subscribed}");
                    ApplyLayers();
                }

                if (snap.HasLayers)
                {
                    // layers_mask uses the same bit values as CameraLayers
                    // (Near=1, Scaled=2, Galaxy=4).
                    SetOperatorLayers((CameraLayers)snap.LayersMask);
                    Debug.Log($"[Kerbcast] cam={FlightId} operator layers → {_operatorLayers}");
                }

                // Viewer quality clamp. Absent means auto (level 0, no
                // clamp); SetViewerQualityLevel no-ops when unchanged, so
                // re-published snapshots are free. Applies even when the
                // AdaptiveQuality flag is off: the viewer clamp rides the
                // same ApplyEffectiveQuality path with _shedLevel pinned 0.
                SetViewerQualityLevel(
                    snap.ViewerLevel.HasValue ? (int)snap.ViewerLevel.Value : 0);

                // Absolute FoV is applied only when its seq changes (see
                // _lastFovSeq) so the stale fov re-published on unrelated writes /
                // the zoom-rate-stop write doesn't snap a drifting _fovTarget back.
                long fovSeq = snap.FovSeq;
                if (snap.Fov.HasValue && SupportsZoom && fovSeq != _lastFovSeq)
                {
                    SetFov(snap.Fov.Value);
                }
                _lastFovSeq = fovSeq;
                // Zoom rate. Absent leaves the current rate unchanged; an explicit
                // 0 stops zooming. +rate = zoom IN (FoV decreases).
                if (SupportsZoom && snap.ZoomRate.HasValue)
                {
                    _zoomRate = Mathf.Clamp(snap.ZoomRate.Value, -1f, 1f);
                }

                if (SupportsPan)
                {
                    // Absolute pan is applied only when panSeq changes (covers
                    // both yaw and pitch) for the same reason as fov above.
                    long panSeq = snap.PanSeq;
                    if (panSeq != _lastPanSeq)
                    {
                        if (snap.PanYaw.HasValue)
                            _panYawTarget = Mathf.Clamp(snap.PanYaw.Value, _panCap.YawMin, _panCap.YawMax);
                        if (snap.PanPitch.HasValue)
                            _panPitchTarget = Mathf.Clamp(snap.PanPitch.Value, _panCap.PitchMin, _panCap.PitchMax);
                    }
                    _lastPanSeq = panSeq;
                    // Pan rates. Absent leaves the current rate unchanged; an
                    // explicit 0 stops that axis. +yawRate = right, +pitchRate = up.
                    if (snap.PanYawRate.HasValue)
                        _panYawRate = Mathf.Clamp(snap.PanYawRate.Value, -1f, 1f);
                    if (snap.PanPitchRate.HasValue)
                        _panPitchRate = Mathf.Clamp(snap.PanPitchRate.Value, -1f, 1f);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast] cam={FlightId} control block read failed: {ex.Message}");
            }
        }

        // (The control file's hand-rolled JSON parsers were removed when the
        // control channel moved to the binary ControlBlock — see ControlBlock.cs.)

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
                Debug.LogWarning($"[Kerbcast] cam={FlightId} destroyed manifest write failed: {ex.Message}");
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
                Debug.LogWarning($"[Kerbcast] cam={FlightId} info manifest write failed (lifecycle={lifecycle}): {ex.Message}");
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

        public void Refresh(bool mayIssueReadback)
        {
            // Read the telemetry gate once per tick so the OFF path makes zero
            // GetTimestamp calls (zero-cost when disabled). Reset the per-tick
            // drain accumulator; ProcessReadback adds to it when it drains a
            // completed readback this frame.
            _telemetry = KerbcastSettings.EnableTelemetry;
            _readbackDrainMs = 0.0;

            // Control-file poll every frame (~60Hz at 60fps). PollControlFile
            // reads the (tiny, tmpfs-backed) file and compares its contents, so
            // the cost is one small read + a string compare per frame — the work
            // of applying state only runs when the contents actually change. At
            // the previous 20Hz cadence (countdown=3), a released pan-rate command
            // could go unacknowledged for up to 50ms (~1.25° overshoot at 25°/s);
            // at 60Hz pickup latency drops to ~16ms and overshoot to ~0.42°.
            if (--_controlCheckCountdown <= 0)
            {
                _controlCheckCountdown = 1;
                PollControlFile();
            }

            // Pan slew runs every tick regardless of subscription state so
            // the physical mesh keeps animating when no peer is watching.
            // The near-cam rotation update is a no-op while cameras are
            // unsubscribed (no Render() call happens), but it keeps the
            // transform consistent for the frame when subscription resumes.
            if (SupportsPan)
            {
                // Velocity integration: advance the pan TARGET by the persistent
                // rate before the slew runs, so the existing MoveTowards (the
                // fast final smoothing filter) tracks the advancing target.
                // Bounds-clamped so a stuck rate parks at the travel limit rather
                // than running away. +yawRate = pan right, +pitchRate = pan up.
                if (_panYawRate != 0f || _panPitchRate != 0f)
                {
                    _panYawTarget = Mathf.Clamp(
                        _panYawTarget + _panYawRate * _panCap.PanRateDegPerSec * Time.deltaTime,
                        _panCap.YawMin, _panCap.YawMax);
                    _panPitchTarget = Mathf.Clamp(
                        _panPitchTarget + _panPitchRate * _panCap.PanRateDegPerSec * Time.deltaTime,
                        _panCap.PitchMin, _panCap.PitchMax);
                }

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
                    if (_yawTransform != null)
                    {
                        // A joint (compound or yaw-only) is present and the near
                        // camera is parented to it, so the joint's rotation IS the
                        // camera's pan. The camera stays at rest relative to the
                        // joint; applying pan here too would double it.
                        _nearCam.transform.localRotation = _baseRotation;
                    }
                    else
                    {
                        // No joint: the camera is parented to partTransform and
                        // carries the full pan itself. Positive pan_yaw = camera
                        // turns right; positive pan_pitch = up. Negate pitch
                        // because Unity's X-rotation is positive-down.
                        _nearCam.transform.localRotation = _baseRotation
                            * Quaternion.Euler(-_panPitchCurrent, _panYawCurrent, 0f);
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
                    {
                        // The near camera is parented rigidly to this joint, so a
                        // single rotation drives BOTH the visible head and the
                        // lens together (the lens stays at _baseRotation relative
                        // to the joint, set in the _nearCam branch above) — no
                        // head/lens sign disagreement to reconcile. YawInvert
                        // negates the angle for a joint whose local frame would
                        // otherwise turn the rigid head+lens opposite to the
                        // operator's +panYaw = pan-right convention (TopJoint).
                        float jointYaw = _panCap.YawInvert ? -_panYawCurrent : _panYawCurrent;
                        _yawTransform.localRotation = _yawRestRot
                            * Quaternion.Euler(0f, jointYaw, 0f);
                    }
                    if (_pitchTransform != null)
                        _pitchTransform.localRotation = _pitchRestRot
                            * Quaternion.Euler(-_panPitchCurrent, 0f, 0f);
                }
            }

            // Zoom: integrate the persistent zoom rate into the FoV target, then
            // slew the displayed FoV toward it. Runs every tick (BEFORE the
            // subscription gate) for the same reason as the pan slew — so a
            // resubscribing peer finds Fov already tracking its commanded target
            // rather than mid-step. +rate = zoom IN, so SUBTRACT from FoV.
            // Bounds-clamped to [FovMin, FovMax] so a stuck rate parks at the
            // limit. This makes BOTH discrete set-fov and set-zoom-rate smooth.
            if (SupportsZoom)
            {
                if (_zoomRate != 0f)
                    _fovTarget = Mathf.Clamp(
                        _fovTarget - _zoomRate * _zoomCap.ZoomRateDegPerSec * Time.deltaTime,
                        FovMin, FovMax);
                if (Mathf.Abs(Fov - _fovTarget) > 0.001f)
                {
                    Fov = Mathf.MoveTowards(Fov, _fovTarget, _zoomCap.FovSlewDegPerSec * Time.deltaTime);
                    // Keep the Hullcam module's FoV (right-click GUI) in sync with
                    // the actually-displayed FoV — this assignment used to live in
                    // SetFov but now tracks the slewing value, including rate-driven
                    // zoom, so the GUI matches the stream throughout the animation.
                    Hullcam.cameraFoV = Fov;
                    if (_nearCam != null) _nearCam.fieldOfView = Fov;
                    if (_scaledCam != null) _scaledCam.fieldOfView = Fov;
                    if (_galaxyCam != null) _galaxyCam.fieldOfView = Fov;
                    if (_farCam != null) _farCam.fieldOfView = Fov;
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

            // Capture staggering: KerbcastCore grants only a round-robin subset of
            // cameras a new capture each frame, so they don't all render + read
            // back on the same frame (bounding simultaneous in-flight readbacks).
            // The control poll, pan slew and readback drain above still run every
            // frame; only the render + readback issue below is paced.
            if (!mayIssueReadback) return;

            // Zero this frame's per-phase Last values before recording, so a
            // phase shed this tick reads 0 rather than its last-rendered figure
            // (otherwise the across-camera Last sum overstates cost under
            // adaptive layer shedding). EMA / rolling-max are preserved.
            if (_telemetry) _phaseTimings.BeginFrame();

            try
            {
                // Manual render sequence: galaxy → scaled → far → near.
                // Cameras are permanently disabled (enabled=false) so
                // Unity's auto-render never fires them; we drive each
                // layer explicitly here and gate on the current layer mask
                // (mirroring the old enabled-flag gating).
                //
                // Strip Scatterer's screen-space shadow CommandBuffers off the
                // sun light(s) for the WHOLE composite, restoring after the near
                // render (and in the outer catch, as a safety net). Those buffers
                // are attached to the sun light for the session and would
                // otherwise fire during each clone layer render without their
                // per-camera fade compensator, painting fixed-position, depth-
                // occluded dark bands at planet depth. No-op when Scatterer (or
                // another buffer-attaching mod) is absent.
                ScaledSunLightHelper.StripCompositeShadowsBuffer();

                if (_galaxyCam != null && (_layers & CameraLayers.Galaxy) != 0)
                {
                    long t0 = _telemetry ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    _integrationHost?.PerFrame(_galaxyCam, CameraLayers.Galaxy, BuildIntegrationFrameState(CameraLayers.Galaxy));
                    _galaxyCam.Render();
                    if (_telemetry)
                        _phaseTimings.Record(RenderPhase.Galaxy,
                            (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * _msPerTick);
                }

                // Scaled layer: bracket the whole block (fader override
                // bookkeeping + Render + restore) so the recorded cost matches
                // the main-thread work this layer actually adds per tick.
                long scaledStart = _telemetry ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (_scaledCam != null && (_layers & CameraLayers.Scaled) != 0)
                {
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
                    var bodyRenderers = ScaledBodyRenderers();
                    for (int bi = 0; bi < bodyRenderers.Count; bi++)
                    {
                        var r = bodyRenderers[bi];
                        if (r == null) continue; // body renderer torn down since cache
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
                            r.GetPropertyBlock(_faderMpb);
                            _faderMpb.SetFloat(_fadeAltitudeId, 1f);
                            r.SetPropertyBlock(_faderMpb);
                        }
                    }
                    try
                    {
                        if (_firstPixelCheck)
                        {
                            var ssl = ScaledSunLightHelper.GetScaledSunLight();
                            Debug.Log($"[Kerbcast] pre-scaled-render: " +
                                $"ambient={RenderSettings.ambientLight} ambientMode={RenderSettings.ambientMode} " +
                                $"scaledSunLight={(ssl == null ? "null" : $"enabled={ssl.enabled} intensity={ssl.intensity} color={ssl.color} cullingMask={ssl.cullingMask}")}");
                        }
                        _integrationHost?.PerFrame(_scaledCam, CameraLayers.Scaled, BuildIntegrationFrameState(CameraLayers.Scaled));
                        _scaledCam.Render();
                        if (_firstRender)
                        {
                            _firstRender = false;
                            Debug.Log($"[Kerbcast] cam={FlightId} scaled actualRenderingPath={_scaledCam.actualRenderingPath} (set={_scaledCam.renderingPath})");
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
                            Debug.Log($"[Kerbcast] cam={FlightId} scaled center pixel: r={px.r:F3} g={px.g:F3} b={px.b:F3} a={px.a:F3}");
                        }
                    }
                    finally
                    {
                        foreach (var state in _faderOverrides)
                        {
                            // Restore _FadeAltitude to ScaledSpaceFader's value in the
                            // MPB so the main camera sees the correct fade. Get the
                            // current MPB (which may carry _sunLightDirection etc) and
                            // only replace the one property we overrode. Reuses the
                            // shared scratch block — no per-frame allocation.
                            if (state.Renderer == null) continue;
                            state.Renderer.GetPropertyBlock(_faderMpb);
                            _faderMpb.SetFloat(_fadeAltitudeId, state.OriginalFade);
                            state.Renderer.SetPropertyBlock(_faderMpb);
                            state.Renderer.enabled = state.WasEnabled;
                        }
                    }
                }
                if (_telemetry && _scaledCam != null && (_layers & CameraLayers.Scaled) != 0)
                    _phaseTimings.Record(RenderPhase.Scaled,
                        (System.Diagnostics.Stopwatch.GetTimestamp() - scaledStart) * _msPerTick);

                if (_farCam != null && (_layers & CameraLayers.Far) != 0)
                {
                    long farStart = _telemetry ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    _integrationHost?.PerFrame(_farCam, CameraLayers.Far, BuildIntegrationFrameState(CameraLayers.Far));
                    _farCam.Render();
                    if (_telemetry)
                        _phaseTimings.Record(RenderPhase.Far,
                            (System.Diagnostics.Stopwatch.GetTimestamp() - farStart) * _msPerTick);
                }

                if (_nearCam != null && (_layers & CameraLayers.Near) != 0)
                {
                    // FX effects update materials and (re)attach their command
                    // buffers before the render; the near render then executes
                    // those CBs (e.g. the core sheath at AfterForwardAlpha).
                    // Time the FX build + render + near render together (the
                    // task's "near Render() (+ FX)" phase).
                    long t0 = _telemetry ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    _integrationHost?.PerFrame(_nearCam, CameraLayers.Near, BuildIntegrationFrameState(CameraLayers.Near));
                    _fxHost?.Render(BuildFxFrameState());
                    _nearCam.Render();
                    if (_telemetry)
                        _phaseTimings.Record(RenderPhase.Near,
                            (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * _msPerTick);
                }

                // Composite done: re-attach Scatterer's sun-light shadow buffers
                // so the main flight render this frame gets correct shadows. The
                // outer catch also restores, so a throw mid-composite cannot leave
                // them stripped.
                ScaledSunLightHelper.RestoreCompositeShadowsBuffer();

                // Blit the depth-bundled capture RT into the clean readback RT.
                // When a HullcamVDS filter is active (NightVision etc), it
                // replaces the plain Blit with its own shader pass that
                // post-processes _captureRt → _readbackRt in one step. The
                // existing AsyncGPUReadback path then reads the
                // already-filtered pixels — no extra round-trip needed.
                // Blit phase: the filter/nv/plain capture→readback blit AND the
                // vertical-flip correction below, all the main-thread Blit
                // dispatch this tick.
                long blitStart = _telemetry ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (_nvMaterial != null)
                {
                    Graphics.Blit(_captureRt, _readbackRt, _nvMaterial);
                }
                else if (_cameraFilter != null)
                {
                    /* Run the Hullcam filter pass through THIS camera's
                       private MovieTime material, never the shared static;
                       the mtShader redirect mechanism lives in
                       HullcamFilterBlit (shared source with the headless
                       determinism test in ci/kerbcast-shaders, which proves
                       the output is byte-identical under hostile writes to
                       the shared static).

                       Reticle policy is unchanged from the original fix:
                       title=true with dockingdisplay.png, mirroring
                       MovieTimeFilter's hardcoded in-game state, and the
                       per-class show/hide decision stays with the filter:
                       BWLoResTV, BWHiResTV and NightVision overwrite
                       _TitleTex=noneTX inside their own blit (no reticle);
                       DockingCam, BWFilm, ColorFilm, ColorLoResTV and
                       ColorHiResTV leave the title alone (reticle shown),
                       matching Hullcam's in-game view of each mode.

                       On top-left-UV-origin APIs (D3D11, Metal) Run also
                       measures the pass's vertical orientation once and
                       appends a compensating flip when the pass inverts;
                       see the HullcamFilterBlit header. Gate is
                       SystemInfo.graphicsUVStartsAtTop, so the GL/Deck
                       path is untouched. */
                    _filterBlit.Run(_filterRenderPass, _captureRt, _readbackRt);
                }
                else
                {
                    Graphics.Blit(_captureRt, _readbackRt);
                }

                // Vertical-flip correction. On bottom-left-origin graphics APIs
                // (OpenGL, the Deck) AsyncGPUReadback returns the frame
                // vertically inverted relative to KSP's top-down screen
                // pipeline, so every camera reads back upside down. Compensate
                // with one final blit that mirrors V. Top-left-origin APIs
                // (D3D11, Metal) read upright and need no correction here; the
                // HullcamFilterBlit handles its own top-left flip case. Done in
                // place via a temp RT so all three capture paths above benefit.
                if (!SystemInfo.graphicsUVStartsAtTop)
                {
                    var flipTmp = RenderTexture.GetTemporary(_readbackRt.descriptor);
                    Graphics.Blit(_readbackRt, flipTmp, new Vector2(1f, -1f), new Vector2(0f, 1f));
                    Graphics.Blit(flipTmp, _readbackRt);
                    RenderTexture.ReleaseTemporary(flipTmp);
                }
                if (_telemetry)
                    _phaseTimings.Record(RenderPhase.Blit,
                        (System.Diagnostics.Stopwatch.GetTimestamp() - blitStart) * _msPerTick);

                // INVARIANT: at most one readback is in flight per camera. The
                // drain guards above (both the !_subscribed branch and the main
                // path) early-return until _pendingRequest.done and clear the
                // flag before we reach here, so this Request only runs when none
                // is pending. The pending-dims snapshot below relies on this —
                // if a second request were ever issued before the first drained,
                // _pendingScratch/_pendingW/_pendingH would be clobbered and
                // ProcessReadback would corrupt the in-flight frame.
                _readbackInFlight = true;
                // Snapshot the target this readback belongs to, so a resolution
                // change before it completes doesn't make ProcessReadback use
                // the new (mismatched) dimensions / scratch texture.
                _targets.CapturePending();
                _pendingCaptureTsMs = Time.unscaledTime * 1000.0;
                long reqStart = _telemetry ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                _pendingRequest = UniversalAsyncGPUReadbackRequest.Request(_readbackRt, 0);
                if (_telemetry)
                {
                    // Readback phase = this tick's drain of the PREVIOUS frame's
                    // capture (the ring memcpy in ProcessReadback, accumulated
                    // into _readbackDrainMs at the top of Refresh) + the cheap
                    // Request() issue here. Recording them combined keeps the
                    // single "readback" column meaningful even though the work
                    // straddles two frames — both halves are main-thread time
                    // spent on this tick.
                    double issueMs = (System.Diagnostics.Stopwatch.GetTimestamp() - reqStart) * _msPerTick;
                    _phaseTimings.Record(RenderPhase.Readback, _readbackDrainMs + issueMs);
                    _phaseTimings.FrameComplete();
                }
            }
            catch (Exception ex)
            {
                _readbackInFlight = false;
                // Safety net: if a layer Render() threw mid-composite, the normal
                // restore above was skipped. Re-attach here so the main flight
                // render never loses Scatterer's sun-light shadow buffers. No-op
                // if the normal path already restored (the saved list is cleared).
                ScaledSunLightHelper.RestoreCompositeShadowsBuffer();
                LogRateLimited($"capture pipeline threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private unsafe void ProcessReadback(UniversalAsyncGPUReadbackRequest request)
        {
            // Time the drain (the full-frame ring memcpy in Produce, the main-
            // thread cost commit 458016a was about) and accumulate into the
            // per-tick drain total. Called from both the !_subscribed branch and
            // the main drain path — accumulating covers both. The Readback-phase
            // sample is recorded once at the Request site, combining this with
            // the issue cost.
            long drainStart = _telemetry ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                if (request.hasError)
                {
                    LogRateLimited("AsyncGPUReadback returned hasError");
                    return;
                }

                // Write the readback bytes straight into the ring — no
                // intermediate Texture2D, no pointless GPU Apply(). The readback
                // target is RGBA32, so the bytes are already the exact pixel
                // layout the ring expects; this is byte-identical to the old
                // LoadRawTextureData→GetRawTextureData path (proven in
                // MmapFrameRing.Tests). Dimensions come from the snapshot taken
                // when the readback was issued, since the current render size may
                // have changed in the meantime (pooled-set switch).
                //
                // On the OpenGL plugin path (the Deck), read the native plugin
                // buffer pointer directly — skipping GetData's Allocator.Temp
                // NativeArray + MemMove (one of two full-frame copies per
                // readback, plus a per-frame Temp alloc; readback_investigation.md
                // change #1). Pointer is valid only until the next readback
                // Update, so Produce (which copies into the ring) runs now. On the
                // Unity-native path GetData is already a zero-copy view — use it.
                byte* src;
                int length;
                if (request.TryGetRawPtr(out var rawPtr, out length))
                {
                    src = (byte*)rawPtr;
                }
                else
                {
                    var data = request.GetData<byte>();
                    src = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(data);
                    length = data.Length;
                }
                _ring.Produce(_targets.PendingWidth, _targets.PendingHeight, _pendingCaptureTsMs, src, length);
                _consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                LogRateLimited($"readback callback threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (_telemetry)
                    _readbackDrainMs +=
                        (System.Diagnostics.Stopwatch.GetTimestamp() - drainStart) * _msPerTick;
            }
        }

        private void LogRateLimited(string message)
        {
            // 1-in-300 frames at 30fps = log at most once per 10s per camera.
            if (_consecutiveErrors == 0 || _consecutiveErrors % 300 == 0)
            {
                Debug.Log($"[Kerbcast] cam={FlightId} {message}");
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

            // The Material is owned by the static KerbcastNightVisionFilter cache;
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
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"[Kerbcast] cam={FlightId} CameraFilter.Deactivate failed: {ex.Message}"); }
                _cameraFilter = null;
            }
            _filterBlit.DestroyMaterial();
            // Tear down FX effects (detach their CBs, release materials) before
            // destroying the camera they're attached to.
            _fxHost?.Dispose();
            _fxHost = null;
            // Detach visual-mod integrations before destroying the cameras: this
            // restores any third-party global/singleton state and removes the
            // components/buffers each integration added, mirroring the per-layer
            // ApplyToLayer calls in SetCameras. Destroying the GameObjects also
            // fires each swap component's OnDisable, but calling RemoveFromLayer
            // makes the apply/remove contract explicit rather than implicit.
            if (_integrationHost != null)
            {
                if (_nearCam != null) _integrationHost.RemoveFromLayer(_nearCam, CameraLayers.Near);
                if (_scaledCam != null) _integrationHost.RemoveFromLayer(_scaledCam, CameraLayers.Scaled);
                if (_galaxyCam != null) _integrationHost.RemoveFromLayer(_galaxyCam, CameraLayers.Galaxy);
                if (_farCam != null) _integrationHost.RemoveFromLayer(_farCam, CameraLayers.Far);
                _integrationHost = null;
            }
            if (_nearCam != null) UnityEngine.Object.Destroy(_nearCam.gameObject);
            if (_scaledCam != null) UnityEngine.Object.Destroy(_scaledCam.gameObject);
            if (_galaxyCam != null) UnityEngine.Object.Destroy(_galaxyCam.gameObject);
            if (_farCam != null) UnityEngine.Object.Destroy(_farCam.gameObject);
            // Destroy every pooled render-target set (the current _captureRt /
            // _readbackRt are members of one of these, so this covers them).
            // Safe here because the camera is being torn down — no further
            // readbacks will be issued.
            foreach (var set in _rtPool.Values)
            {
                if (set.Capture != null) { set.Capture.Release(); UnityEngine.Object.Destroy(set.Capture); }
                if (set.Readback != null) { set.Readback.Release(); UnityEngine.Object.Destroy(set.Readback); }
            }
            _rtPool.Clear();

            _ring?.Dispose();
            _controlBlock?.Dispose();
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
                Debug.LogWarning($"[Kerbcast] cam={FlightId} ring file delete failed: {ex.Message}");
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
