// KSPAddon entry point. Hooks into the Flight scene, scans the active
// vessel for Hullcam VDS parts, and pumps an AsyncGPUReadback per camera
// each LateUpdate. Each KerbcamCamera owns its own MmapFrameRing keyed
// by KSP's stable Part.flightID; the Rust sidecar discovers rings by
// globbing the kerbcam/ subdirectory under XDG_RUNTIME_DIR.
//
// Lifecycle:
//   - Awake:    spawn AsyncReadbackUpdater (KSP loads mod DLLs after
//               [RuntimeInitializeOnLoadMethod] would fire, so the
//               vendored yangrc updater never auto-attaches).
//               Ensure the rings directory exists, then notify the
//               session-persistent KerbcamSidecarHost (which spawns the
//               sidecar on the FIRST flight of the session and keeps it
//               running across scene changes).
//               Subscribe GameEvents.onPartDestroyed to detect Hullcam
//               part destruction mid-flight.
//   - GameEvents.onVesselChange: rebuild the camera list (which
//               creates/destroys the matching ring files).
//   - GameEvents.onPartDestroyed: write lifecycle:"destroyed" tombstone
//               to the camera's info.json, close the ring, then leave
//               info.json for the sidecar to clean up.
//   - LateUpdate: defensive null-check sweep before the main Refresh()
//               loop catches cameras whose part/vessel went null without
//               a matching onPartDestroyed event (mod interactions, etc).
//               Also refreshes each tracked camera.
//   - OnDestroy: unsubscribe onPartDestroyed, tear down cameras (each
//               disposes its own ring + deletes its files). The sidecar
//               is NOT stopped: it belongs to KerbcamSidecarHost and
//               survives scene changes (the camera-ring removals are all
//               it observes). It dies on game exit or via the heartbeat
//               orphan watch.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using HullcamVDS;
using UnityEngine;
using Yangrc.OpenGLAsyncReadback;
using Debug = UnityEngine.Debug;

namespace Kerbcam
{
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public sealed class KerbcamCore : MonoBehaviour
    {
        private const int RingSlots = 4;
        private static readonly string RingDir = ResolveRingDir();

        /* ~0.5s at 60fps: long enough that a transient scene-change hiccup
           doesn't evict a healthy camera, short enough to stop a genuinely
           broken one from log-spamming for minutes. */
        private const int RefreshQuarantineThreshold = 30;

        private KerbcamSettings _settings;
        private readonly List<KerbcamCamera> _cameras = new List<KerbcamCamera>();

        // Status file (sidecar ← plugin). Rewritten ~1Hz when anything has
        // changed — KSP fps, shed level, per-camera effective layers / dims.
        // Sidecar reads, diffs, and pushes camera-state-changed +
        // adaptive-shed messages to all peers' control channels.
        private string _statusPath;
        private float _statusCooldown;
        private const float StatusWriteIntervalSeconds = 1.0f;

        /* Control file (sidecar -> plugin). Polled ~1Hz with its own cooldown. */
        private string _controlPath;
        private float _controlCooldown;
        private ulong _lastAppliedControlSeq;

        // Rolling 60-sample FPS window for adaptive layer shedding.
        // ~1s at 60fps, ~2s at 30fps — long enough to ignore single-frame
        // hitches, short enough to react to sustained slow-downs.
        private const int FpsSamples = 60;
        private readonly float[] _fpsWindow = new float[FpsSamples];
        private int _fpsIdx;
        private int _fpsCount; // up to FpsSamples; growing-average until full
        private float _fpsAvg;
        // Adaptive-shed decision state machine. The fps thresholds + hysteresis
        // and the anti-flap dwell/back-off all live in ShedController (which is
        // Unity-free and unit-tested). Each LateUpdate we hand it the rolling
        // fps average + the unscaled clock and apply the level it returns.
        // Stagger controller: the lossless temporal-degrade regulator. Holds
        // kerbcam's OWN main-thread cost within a frametime budget by capturing
        // fewer cameras per frame — full resolution + all layers, just less
        // often. Targets kerbcam's cost (not game fps), so it's KSP-independent.
        // kerbcam auto-sheds quality only when AdaptiveQuality is enabled (on by default).
        // Built in Awake from settings; null until then (guarded at call sites).
        private StaggerBudgetController _staggerController;
        // Opt-in quality ladder (AdaptiveQuality=true): demotes resolution/FX
        // layers via KerbcamCamera.ApplyAutoShed once staggering is exhausted,
        // promotes back after sustained headroom. null when the flag is off,
        // which keeps the flag-off path bit-for-bit the pre-flag behaviour
        // (no evaluation, no ApplyAutoShed calls, shedLevel always 0).
        private AdaptiveQualityController _qualityController;
        // Cameras that actually captured last frame, for the per-camera cost
        // estimate (kerbcamFrameMs / captured).
        private int _lastCapturedCount = 1;
        // Rolling estimate of kerbcam's own main-thread cost per frame (the
        // capture loop's wall-time, EMA-smoothed). Divided by frame time gives
        // the cost-share the stagger controller gates escalation on.
        private double _kerbcamFrameMs;
        private int _staggerBudget;   // last applied capture budget (telemetry)
        private static readonly double _msPerTick =
            1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // The DontDestroyOnLoad GameObject hosting AsyncReadbackUpdater, whose
        // Update() pumps the OpenGL readback plugin (a per-frame render-thread
        // GL.IssuePluginEvent) every frame. Tracked so OnDestroy can tear it
        // down on flight exit — otherwise it keeps pumping in every later scene
        // (space centre, main menu) for the rest of the process, stalling the
        // render thread on any leftover readback tasks until KSP is restarted.
        private GameObject _readbackUpdaterGo;

        // Round-robins which cameras capture each frame (see ReadbackScheduler),
        // so they don't all issue a GPU render + readback on the same frame.
        private readonly ReadbackScheduler _readbackScheduler = new ReadbackScheduler();
        private bool[] _capturePermit = new bool[0];
        // Staggering is budgeted over the SUBSCRIBED (streaming) cameras only —
        // idle/attached cameras cost nothing and must not consume permits.
        // _subscribedIdx[0.._streamCount) maps a round-robin rank to a full
        // _cameras index; _streamPermit is the rank-indexed permit set.
        private int[] _subscribedIdx = new int[0];
        private bool[] _streamPermit = new bool[0];
        private int _streamCount;

        // Telemetry (Recommendation 1): GC collection-count tracker. Sampled
        // once per LateUpdate when KerbcamSettings.EnableTelemetry is true,
        // surfaced into the status JSON's "telemetry" section and reset each
        // write. Lets us see, with no profiler, whether the ~100ms frametime
        // spikes coincide with a Mono GC. Struct field — no allocation.
        private GcTracker _gcTracker;

        private static string ResolveRingDir()
        {
            // XDG_RUNTIME_DIR is the right home on Steam Deck / Linux —
            // it's a per-user tmpfs that survives the process and is
            // cleaned up at logout. Fall back to /tmp on macOS / when
            // XDG_RUNTIME_DIR isn't set (Mono returns "" not null there).
            // The kerbcam/ subdirectory namespaces our ring files so the
            // sidecar's *.ring glob doesn't pick up stray files.
            var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(xdg) && Directory.Exists(xdg))
            {
                return Path.Combine(xdg, "kerbcam");
            }
            return "/tmp/kerbcam";
        }

        private void Awake()
        {
            Debug.Log("[Kerbcam] KerbcamCore.Awake — initialising");

            _settings = KerbcamSettings.Load();
            _staggerController = BuildStaggerController(_settings);
            if (_settings.AdaptiveQuality)
            {
                _qualityController = BuildQualityController(_settings);
                Debug.Log("[Kerbcam] stagger quality ladder enabled (AdaptiveQuality=true)");
            }

            try
            {
                // yangrc's [RuntimeInitializeOnLoadMethod] hook never
                // fires for mod DLLs (KSP loads them post-init), so the
                // updater MonoBehaviour that pumps OpenGLAsyncReadbackRequest
                // never auto-attaches. Spawn it ourselves on a dedicated
                // DontDestroyOnLoad GameObject. OpenGLCore only, mirroring the
                // vendor's own auto-spawn gate: on D3D11/Metal the wrapper
                // uses Unity's native AsyncGPUReadback and the pump's Update
                // P/Invokes a native lib we only ship for Linux, throwing
                // DllNotFoundException every frame.
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore
                    && AsyncReadbackUpdater.instance == null)
                {
                    _readbackUpdaterGo = new GameObject("Kerbcam_AsyncReadbackUpdater");
                    UnityEngine.Object.DontDestroyOnLoad(_readbackUpdaterGo);
                    _readbackUpdaterGo.AddComponent<AsyncReadbackUpdater>();
                    Debug.Log("[Kerbcam] spawned AsyncReadbackUpdater (readback pump)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcam] failed to spawn AsyncReadbackUpdater: {ex}");
            }

            try
            {
                Directory.CreateDirectory(RingDir);
                _statusPath = Path.Combine(RingDir, "global.status.json");
                _controlPath = Path.Combine(RingDir, "global.control.json");
                Debug.Log($"[Kerbcam] rings directory ready at {RingDir} ({RingSlots} slots × {_settings.Width}×{_settings.Height} RGBA per camera)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcam] failed to create rings directory at {RingDir}: {ex}");
                enabled = false;
                return;
            }

            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onPartDestroyed.Add(OnPartDestroyed);
            GameEvents.onVesselWasModified.Add(OnVesselWasModified);

            // Defensive: at Awake time the scene's source cameras (Camera 00,
            // ScaledSpace, GalaxyCamera, FXCamera) may already be disabled —
            // e.g. loading a save that was last in IVA, or a save where the
            // previous session's throttle state was preserved by KSP. Sync
            // our _mainCamerasDisabled flag from reality so the first
            // RebuildCameraList correctly does the restore-rebuild-reapply
            // dance and SetCameras sees enabled sources. Without this, the
            // first per-camera SetCameras call would CopyFrom(null) and
            // inherit Unity's default cullingMask (~0 — including the IVA
            // layer 16), producing the "pod interior leak" symptom on the
            // streams.
            _mainCamerasDisabled = AreAnySourceCamerasDisabled();
            if (_mainCamerasDisabled)
                Debug.Log("[Kerbcam] source cameras already disabled at Awake — RebuildCameraList will restore before SetCameras");

            RebuildCameraList(FlightGlobals.ActiveVessel);

            // Throttle state seeded from the per-save Difficulty Setting
            // (which itself seeds from settings.cfg on a fresh save). We
            // poll it every LateUpdate rather than wire to a settings-
            // changed GameEvent because KSP doesn't ship one for
            // CustomParameterNode value changes.
            _throttleDesired = ReadThrottleDesired();
            _throttleEffective = _throttleDesired;
            if (_throttleEffective) ApplyMainScreenThrottle();

            /* The host owns the sidecar process for the whole KSP session;
               this only spawns it on the first flight entry. Scene changes
               are camera churn the running sidecar already understands. */
            KerbcamSidecarHost.NotifyFlightEntered(_settings, RingDir);
        }

        private void OnVesselChange(Vessel v)
        {
            Debug.Log($"[Kerbcam] vessel change: {(v != null ? v.vesselName : "<null>")}");
            RebuildCameraList(v);
        }

        // GameEvents.onPartDestroyed fires when a Part's GameObject is
        // destroyed (vessel crash, decoupling + physics-range expire, etc).
        // Walk _cameras and dispose any whose Hullcam belongs to this part.
        // DisposeDestroyed writes lifecycle="destroyed" to the info.json
        // tombstone before closing the ring so the sidecar observes the
        // transition, and leaves the info.json on disk for the sidecar.
        private void OnPartDestroyed(Part part)
        {
            if (part == null) return;
            Vessel affected = null;
            // Iterate backwards so we can remove by index without
            // skipping entries.
            for (int i = _cameras.Count - 1; i >= 0; i--)
            {
                var cam = _cameras[i];
                if (cam.Hullcam != null && cam.Hullcam.part == part)
                {
                    if (affected == null) affected = cam.Hullcam.vessel;
                    Debug.Log($"[Kerbcam] part destroyed — disposing cam={cam.FlightId} ({part.name})");
                    _cameras.RemoveAt(i);
                    cam.DisposeDestroyed();
                }
            }
            // Rebuild FX draw lists on the surviving cams of the same vessel so
            // the destroyed part's renderers don't linger in their CommandBuffers.
            if (affected != null) MarkFxDirtyForVessel(affected);
        }

        // Decoupling, staging, fairing jettison etc. change a vessel's part set
        // without firing onPartDestroyed for our tracked part. Rebuild the FX
        // draw lists so they match the current parts.
        private void OnVesselWasModified(Vessel v)
        {
            if (v != null) MarkFxDirtyForVessel(v);
        }

        private void MarkFxDirtyForVessel(Vessel v)
        {
            foreach (var cam in _cameras)
                if (cam.Hullcam != null && cam.Hullcam.vessel == v) cam.MarkFxDirty();
        }

        // Derive a unique FlightId per Hullcam module on a part. Module 0
        // (and parts with a single module — the common case) keep the bare
        // part.flightID for wire compatibility. Additional modules get a
        // hash of (baseId, cameraName) — deterministic across loads, and
        // a 32-bit space with at most a handful of cameras per vessel
        // makes collisions vanishingly unlikely. We use the Knuth golden-
        // ratio multiplier (2654435761) so consecutive modules don't
        // produce neighbouring hashes that could clash with another
        // part's flightID (which KSP assigns sequentially).
        private static uint SyntheticFlightId(uint baseId, int moduleIdx, string cameraName)
        {
            if (moduleIdx == 0) return baseId;
            unchecked
            {
                uint h = baseId;
                h = h * 2654435761u + (uint)moduleIdx;
                if (!string.IsNullOrEmpty(cameraName))
                {
                    foreach (var ch in cameraName) h = h * 2654435761u + ch;
                }
                return h;
            }
        }

        private void RebuildCameraList(Vessel vessel)
        {
            foreach (var cam in _cameras) cam.Dispose();
            _cameras.Clear();

            if (vessel == null) return;

            // If main-screen throttling is currently active, the KSP source
            // cameras (Camera 00, Camera ScaledSpace, GalaxyCamera) are
            // disabled. KerbcamCamera.SetCameras() locates them via
            // Camera.allCameras which only enumerates ENABLED cameras —
            // so SetCameras() would skip CopyFrom and our per-camera
            // Unity Camera components would inherit Unity's defaults
            // (cullingMask = ~0, no source-transform parenting). That
            // produces the "command pod interior + black sky" symptom
            // operators have seen on flight-scene reload / quickload,
            // when onVesselChange fires AFTER Awake's initial
            // ApplyMainScreenThrottle and re-runs SetCameras against the
            // now-disabled sources.
            //
            // Temporarily restore the source cameras so SetCameras sees
            // a fully-initialised render stack, then re-apply the
            // throttle once the rebuild is complete. The restore/apply
            // pair captures whatever state KSP currently considers
            // canonical (e.g. cullingMask updates from other mods,
            // post-IVA-exit state), not whatever stale snapshot we last
            // disabled from.
            bool wasThrottled = _mainCamerasDisabled;
            if (wasThrottled)
            {
                Debug.Log("[Kerbcam] rebuild: temporarily restoring main screen so SetCameras sees enabled source cameras");
                RestoreMainScreen();
            }

            foreach (var part in vessel.parts)
            {
                // A part can carry multiple Hullcam modules — the booster
                // segment ships with both Fwd and Aft camera modules on a
                // single part. FindModuleImplementing returns only the
                // first match, so iterate every module of the type.
                int moduleIdx = 0;
                foreach (var hullcam in part.Modules.OfType<MuMechModuleHullCamera>())
                {
                    try
                    {
                        var partName = part.partInfo?.name ?? string.Empty;
                        var initialLayers = _settings.GetInitialLayers(partName);
                        var enableFx = _settings.GetEnableAtmosphericFx(partName);
                        var fxLayers = _settings.GetAtmosphericFxLayers(partName);
                        var (renderW, renderH) = _settings.GetRenderSize(partName);
                        // Camera identity is per-module, not per-part — but
                        // the ring + info + control file names are keyed on
                        // FlightId, so two modules on the same part used to
                        // collide and silently drop the second camera.
                        // Module 0 keeps part.flightID for wire compat with
                        // single-cam parts (the common case). Modules 1+
                        // get a deterministic hash of (flightID, cameraName)
                        // so they're stable across loads.
                        uint flightId = SyntheticFlightId(part.flightID, moduleIdx, hullcam.cameraName);
                        var panCap = PartCapabilities.ForPart(hullcam.part.partInfo?.name ?? "");
                        _cameras.Add(new KerbcamCamera(
                            hullcam,
                            flightId,
                            RingDir,
                            RingSlots,
                            _settings.Width,
                            _settings.Height,
                            renderW,
                            renderH,
                            initialLayers,
                            enableFx,
                            fxLayers,
                            panCap));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Kerbcam] failed to attach to {part.name}: {ex}");
                    }
                    moduleIdx++;
                }
            }
            Debug.Log($"[Kerbcam] tracking {_cameras.Count} Hullcam VDS camera(s)");

            // Freshly built cameras start at the operator-configured quality;
            // if the opt-in ladder is currently demoted, bring them in line so
            // a vessel switch can't silently restore a load we just shed.
            if (_qualityController != null && _qualityController.Level > 0)
            {
                for (int i = 0; i < _cameras.Count; i++)
                    _cameras[i].ApplyAutoShed(_qualityController.Level);
            }

            // Re-apply the throttle we restored above. ReadThrottleDesired
            // is the source of truth — honour the operator's current
            // setting in case they toggled it during the rebuild window.
            if (wasThrottled && ReadThrottleDesired())
            {
                Debug.Log("[Kerbcam] rebuild: re-applying main screen throttle");
                ApplyMainScreenThrottle();
            }
        }

        // LateUpdate drives explicit camera.Render() calls via each
        // KerbcamCamera.Refresh(). Our offscreen cameras are permanently
        // disabled (enabled=false) so they never fire during Unity's normal
        // render pass — we own the render timing entirely.
        private void LateUpdate()
        {
            UpdateFpsAverage();

            // Telemetry: sample the GC collection counters once per frame and
            // fold this frame's wall-clock duration into the interval stats, so
            // a frametime spike that coincides with a gen-0/1/2 collection is
            // recorded as GC-caused. CollectionCount is a cheap field read; gated
            // so it's a single bool check when telemetry is off.
            if (KerbcamSettings.EnableTelemetry)
            {
                _gcTracker.Sample(
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                    Time.unscaledDeltaTime);
            }
            // Build the subscribed (streaming) set first — staggering is
            // budgeted over it, not over all attached cameras. Filled here so
            // both UpdateDegradeLevel (the controller) and the capture loop below
            // use the same count this frame. Subscription state is from the
            // previous frame's control poll (set inside Refresh); one-frame lag,
            // consistent with the cost EMA.
            {
                int n = _cameras.Count;
                if (_subscribedIdx.Length < n) _subscribedIdx = new int[n];
                _streamCount = 0;
                for (int i = 0; i < n; i++)
                    if (_cameras[i].Subscribed) _subscribedIdx[_streamCount++] = i;
            }

            // Regulate the capture budget from kerbcam's own frame cost (lossless
            // temporal degrade — fewer streaming cameras captured per frame).
            // Quality stays untouched unless AdaptiveQuality is enabled (on by default).
            UpdateDegradeLevel();

            // Reconcile the per-save Difficulty Setting against our
            // effective state. KSP has no settings-changed event for
            // CustomParameterNode, so we poll. The read is cheap (one
            // dictionary lookup) and only does work when the value
            // actually moved.
            bool desired = ReadThrottleDesired();
            if (desired != _throttleEffective)
            {
                _throttleDesired = desired;
                if (desired) ApplyMainScreenThrottle();
                else RestoreMainScreen();
            }

            // If the main screen is throttled but FXCamera arrived after we
            // applied the throttle (its singleton initialises during flight-scene
            // setup, so the first ApplyMainScreenThrottle call may have seen a
            // null Instance), suppress it now.
            if (_throttleEffective && !_fxCamSuppressed)
                TrySuppressFxCamera();

            // Debug: periodic cullingMask divergence check. ~once per
            // minute (3600 LateUpdates at 60fps; degrades gracefully
            // at lower fps). Gated inside the cam itself so this is a
            // single nullary call per tick when off.
            if (--_debugMaskCheckCountdown <= 0)
            {
                _debugMaskCheckCountdown = 3600;
                for (int i = 0; i < _cameras.Count; i++)
                    _cameras[i].LogCullingMaskIfDiverged();
            }

            // Defensive: scan for cameras whose Hullcam part has gone null
            // (e.g. a destruction event we missed, or a KSP internal teardown
            // that doesn't fire onPartDestroyed). DisposeDestroyed + remove them
            // so the orphaned Unity Camera GameObjects and ring files are cleaned
            // up even if the event path failed. Iterate backwards to allow
            // removal by index.
            for (int i = _cameras.Count - 1; i >= 0; i--)
            {
                var cam = _cameras[i];
                if (cam.Hullcam == null || cam.Hullcam.part == null || cam.Hullcam.vessel == null)
                {
                    Debug.LogWarning($"[Kerbcam] defensive sweep: cam={cam.FlightId} has null Hullcam/part/vessel — disposing (missed destruction event?)");
                    _cameras.RemoveAt(i);
                    cam.DisposeDestroyed();
                }
            }

            // Stagger captures round-robin so the cameras don't all render +
            // read back on the same frame. Budget = how many may capture this
            // frame to sustain MaxCaptureFps at the current game fps (_fpsAvg);
            // below MaxCaptureFps it grants all of them.
            int camCount = _cameras.Count;
            if (_capturePermit.Length < camCount) _capturePermit = new bool[camCount];
            if (_subscribedIdx.Length < camCount) _subscribedIdx = new int[camCount];
            if (_streamPermit.Length < camCount) _streamPermit = new bool[camCount];

            // Staggering is budgeted over the SUBSCRIBED set only. Idle cameras
            // cost nothing (Refresh early-outs on !_subscribed) and must not eat
            // permits — otherwise the few cameras actually streaming would be
            // staggered as if among all attached. (_streamCount was filled before
            // UpdateDegradeLevel so the controller saw the same count.)
            int budget = _streamCount; // safe default if controller not built
            if (_streamCount > 0)
            {
                int rateBudget = ReadbackScheduler.Budget(_streamCount, _settings.MaxCaptureFps, _fpsAvg);
                int staggerBudget = _staggerController != null ? _staggerController.Budget : _streamCount;
                budget = Math.Min(rateBudget, staggerBudget);
                _readbackScheduler.NextTick(_streamCount, budget, _streamPermit);
            }
            _staggerBudget = budget;

            // Map the rank-indexed stream permits back onto the full camera list;
            // idle cameras get no permit (they skip render anyway, but this keeps
            // _lastCapturedCount honest).
            for (int i = 0; i < camCount; i++) _capturePermit[i] = false;
            for (int rank = 0; rank < _streamCount; rank++)
                if (_streamPermit[rank]) _capturePermit[_subscribedIdx[rank]] = true;

            // Measure kerbcam's own main-thread cost this frame (the capture loop
            // wall-time), EMA-smoothed, + how many cameras actually captured —
            // together they give the per-camera cost the controller regulates.
            int captured = 0;
            for (int i = 0; i < camCount; i++) if (_capturePermit[i]) captured++;
            _lastCapturedCount = captured > 0 ? captured : 1;
            long capStart = System.Diagnostics.Stopwatch.GetTimestamp();
            /* Backwards so a quarantine removal doesn't shift the
               permit-to-camera index alignment of cameras not yet visited. */
            for (int i = camCount - 1; i >= 0; i--)
            {
                var cam = _cameras[i];
                try
                {
                    cam.Refresh(_capturePermit[i]);
                    cam.RefreshFailureStreak = 0;
                }
                catch (Exception ex)
                {
                    /* Isolate per-camera failures: one camera throwing must
                       not abort the other cameras' captures or the control
                       logic below. A sustained streak means the camera is
                       broken beyond self-recovery (it would otherwise spam
                       the log every frame forever) — dispose it like a
                       missed-destruction part. */
                    cam.RefreshFailureStreak++;
                    if (cam.RefreshFailureStreak == 1)
                        Debug.LogError($"[Kerbcam] cam={cam.FlightId} Refresh threw: {ex}");
                    if (cam.RefreshFailureStreak >= RefreshQuarantineThreshold)
                    {
                        Debug.LogError($"[Kerbcam] cam={cam.FlightId} quarantined after {cam.RefreshFailureStreak} consecutive Refresh failures");
                        _cameras.RemoveAt(i);
                        try { cam.DisposeDestroyed(); }
                        catch (Exception dex) { Debug.LogError($"[Kerbcam] cam={cam.FlightId} quarantine dispose threw: {dex}"); }
                    }
                }
            }
            double capMs = (System.Diagnostics.Stopwatch.GetTimestamp() - capStart) * _msPerTick;
            _kerbcamFrameMs = _kerbcamFrameMs <= 0.0 ? capMs : _kerbcamFrameMs * 0.8 + capMs * 0.2;

            MaybeApplyGlobalControl();
            MaybeWriteStatusFile();
        }

        // Warning overlay shown to the operator while the main flight
        // render is disabled. Centred on the (blanked) viewport so it
        // doesn't compete with the altimeter / navball / staging /
        // right-click panels at the top + sides. Tells them where to
        // look to undo, including the active hotkey if one is bound.
        private void OnGUI()
        {
            if (!_throttleEffective) return;

            // Lazy style so we pay the GUIStyle alloc once, not per
            // OnGUI frame.
            if (_throttleWarnStyle == null)
            {
                _throttleWarnStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 14,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    padding = new RectOffset(12, 12, 10, 10),
                    normal = { textColor = new Color(1f, 0.85f, 0.3f) },
                };
            }

            string msg =
                "Main flight camera disabled by kerbcam to free GPU for camera streams. " +
                "Go to Pause > Difficulty Settings > Kerbcam to restore.";

            const float w = 560f;
            const float h = 80f;
            float x = (Screen.width - w) * 0.5f;
            float y = (Screen.height - h) * 0.5f;
            GUI.Box(new Rect(x, y, w, h), msg, _throttleWarnStyle);
        }

        // -- throttle plumbing --

        private bool _throttleDesired;
        private bool _throttleEffective;
        private bool _mainCamerasDisabled;
        // Tracks whether we have successfully disabled FXCamera's camera
        // component as part of a throttle. ApplyMainScreenThrottle sets this
        // only when FXCamera.Instance is available at call time; LateUpdate
        // retries every frame until it succeeds, so a late-initialising
        // FXCamera doesn't escape suppression.
        private bool _fxCamSuppressed;
        private GUIStyle _throttleWarnStyle;
        private int _debugMaskCheckCountdown = 60;

        // Read the operator-set Difficulty value, with a fallback to
        // the settings.cfg seed when no save is active (shouldn't
        // happen in flight scene but defensive — Awake order is
        // sometimes weird across mod combinations).
        private bool ReadThrottleDesired()
        {
            try
            {
                var game = HighLogic.CurrentGame;
                if (game?.Parameters != null)
                {
                    var node = game.Parameters.CustomParams<KerbcamGameParameters>();
                    if (node != null) return node.ThrottleMainScreen;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] CustomParams read failed: {ex.Message}");
            }
            return KerbcamSettings.SeedThrottleMainScreen;
        }

        // Hotkey toggles BOTH the live state AND the per-save value
        // (so the change persists if the operator saves now). Lets the
        // hotkey behave intuitively — "press it, it stays toggled
        // until I press it again or change the Difficulty Setting".
        // KSP ships FlightCamera.EnableCamera() / DisableCamera() as
        // its first-class on/off for the layered main view; calling
        // DisableCamera here is the same code path KSP itself uses on
        // scene transitions, so we're not fighting the engine. We
        // additionally disable Camera ScaledSpace + GalaxyCamera so
        // no layer of the main composite renders. The ScaledCamera
        // and GalaxyCamera MonoBehaviours keep running their
        // transform-tracking LateUpdate logic independently of the
        // Camera component's enabled flag, so our per-Hullcam cameras
        // (which are parented to those transforms) still get correct
        // positions every frame.
        private void TrySuppressFxCamera()
        {
            if (FXCamera.Instance == null) return;
            // Keep FXCamera alive when atmospheric FX is on — our plasma shader
            // samples its globals (_FXDepthMap, _LightDirection0, _FXColor) and
            // they only refresh while FXCamera renders. The "wind without ship"
            // artifact in the main view is moot during throttle because the
            // operator is watching kerbcam streams, not the main view.
            if (_settings.EnableAtmosphericFx) return;
            var fxCam = FXCamera.Instance.GetComponent<Camera>();
            if (fxCam == null) return;
            fxCam.enabled = false;
            _fxCamSuppressed = true;
            Debug.Log("[Kerbcam] FXCamera suppressed (late-init, throttle already active)");
        }

        private void ApplyMainScreenThrottle()
        {
            if (_mainCamerasDisabled) { _throttleEffective = true; return; }
            try
            {
                if (FlightCamera.fetch != null)
                {
                    FlightCamera.fetch.DisableCamera(disableAudioListener: false);
                }
                var scaled = FindKspCameraByName("Camera ScaledSpace");
                if (scaled != null) scaled.enabled = false;
                var galaxy = FindKspCameraByName("GalaxyCamera");
                if (galaxy != null) galaxy.enabled = false;
                // FXCamera is a separate singleton MonoBehaviour with its
                // own Camera component (NOT in FlightCamera.cameras array)
                // that renders aero/re-entry FX via a velocity-based shader.
                // Without disabling it the operator sees "wind effects + ship
                // silhouette" in the *main view* while throttled — but when
                // EnableAtmosphericFx is on, kerbcam's plasma shader samples
                // FXCamera's globals (_FXDepthMap, _LightDirection0, _FXColor)
                // and needs them refreshed, so we deliberately leave FXCamera
                // alive in that case. The main-view artifact doesn't matter
                // because the operator is watching kerbcam streams, not the
                // main view, while throttled.
                if (FXCamera.Instance != null && !_settings.EnableAtmosphericFx)
                {
                    var fxCam = FXCamera.Instance.GetComponent<Camera>();
                    if (fxCam != null)
                    {
                        fxCam.enabled = false;
                        _fxCamSuppressed = true;
                    }
                }
                // If FXCamera.Instance was null here, _fxCamSuppressed stays
                // false. LateUpdate will retry every frame via
                // TrySuppressFxCamera() until it succeeds.
                _mainCamerasDisabled = true;
                _throttleEffective = true;
                Debug.Log("[Kerbcam] main flight render disabled (ThrottleMainScreen=true)");
                LogAllCameras("throttle-applied");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcam] ApplyMainScreenThrottle failed: {ex}");
            }
        }

        private void RestoreMainScreen()
        {
            if (!_mainCamerasDisabled) { _throttleEffective = false; return; }
            try
            {
                if (FlightCamera.fetch != null)
                {
                    FlightCamera.fetch.EnableCamera();
                }
                var scaled = FindKspCameraByName("Camera ScaledSpace");
                if (scaled != null) scaled.enabled = true;
                var galaxy = FindKspCameraByName("GalaxyCamera");
                if (galaxy != null) galaxy.enabled = true;
                if (FXCamera.Instance != null)
                {
                    var fxCam = FXCamera.Instance.GetComponent<Camera>();
                    if (fxCam != null) fxCam.enabled = true;
                }
                _mainCamerasDisabled = false;
                _throttleEffective = false;
                _fxCamSuppressed = false;
                Debug.Log("[Kerbcam] main flight render restored (ThrottleMainScreen=false)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcam] RestoreMainScreen failed: {ex}");
            }
        }

        // Diagnostic: log every active Camera in the scene with its
        // current enabled state. Lets us spot additional render
        // surfaces we should consider disabling on throttle — KSP +
        // mods can add cameras we don't know about ahead of time
        // (Scatterer, EVE, TUFX, FX setups, etc).
        private static void LogAllCameras(string tag)
        {
            try
            {
                var names = new System.Text.StringBuilder();
                foreach (var c in Camera.allCameras)
                {
                    names.Append(c.enabled ? "+" : "-");
                    names.Append(c.name);
                    names.Append(' ');
                }
                Debug.Log($"[Kerbcam] cameras-snapshot ({tag}): {names}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] LogAllCameras failed: {ex.Message}");
            }
        }

        // Probe whether ANY of the four main source cameras KSP uses for
        // its in-flight composite (Camera 00, Camera ScaledSpace,
        // GalaxyCamera, FXCamera) are currently disabled. Returns true
        // even if only one is. Used at Awake to sync our
        // _mainCamerasDisabled flag from reality before the first
        // RebuildCameraList — needed for load-from-IVA-save / similar
        // paths where KSP brings the scene up with some cams off.
        //
        // Uses Object.FindObjectsOfType<Camera> rather than
        // Camera.allCameras because the latter only enumerates ENABLED
        // cameras — useless for detecting disabled ones.
        private static bool AreAnySourceCamerasDisabled()
        {
            bool anyDisabled = false;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Camera>();
                foreach (var c in all)
                {
                    if (c == null) continue;
                    if (c.name == "Camera 00"
                        || c.name == "Camera ScaledSpace"
                        || c.name == "GalaxyCamera"
                        || c.name == "FXCamera")
                    {
                        if (!c.enabled)
                        {
                            Debug.Log($"[Kerbcam] AreAnySourceCamerasDisabled: '{c.name}' is disabled");
                            anyDisabled = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] AreAnySourceCamerasDisabled probe failed: {ex.Message}");
            }
            return anyDisabled;
        }

        // Camera.allCameras snapshots every active Camera, so this is
        // cheap. KerbcamCamera.SetCameras() uses the same pattern via
        // FindKspCamera; duplicated here because we run earlier than
        // the per-camera setup and don't want the cross-class call.
        private static Camera FindKspCameraByName(string name)
        {
            foreach (var c in Camera.allCameras)
            {
                if (c.name == name) return c;
            }
            return null;
        }

        // ~1 Hz status write. Each write carries the full per-camera
        // snapshot + global KSP fps + shed level. The sidecar reads,
        // diffs against a cached copy, and pushes camera-state-changed
        // and adaptive-shed messages over each peer's data channel.
        private void MaybeWriteStatusFile()
        {
            if (_statusPath == null) return;
            _statusCooldown -= Time.unscaledDeltaTime;
            if (_statusCooldown > 0f) return;
            _statusCooldown = StatusWriteIntervalSeconds;

            try
            {
                var sb = new System.Text.StringBuilder(256 + _cameras.Count * 256);
                sb.Append("{\n");
                sb.Append($"  \"kspFps\": {_fpsAvg:F2},\n");
                // shedLevel: 0 unless the opt-in AdaptiveQuality ladder is
                // active (the controller is null when the flag is off, so the
                // flag-off output stays byte-identical to the pre-flag plugin).
                sb.Append($"  \"shedLevel\": {(_qualityController != null ? _qualityController.Level : 0)},\n");
                sb.Append($"  \"throttleMainScreen\": {(_throttleEffective ? "true" : "false")},\n");
                // Stagger telemetry — watch the budget regulator converge + tune
                // MaxKerbcamFrameBudgetMs / MinKspFps. staggerBudget: cameras
                // permitted to capture this tick. kerbcamFrameMs: EMA of kerbcam's
                // own per-frame main-thread cost (the regulated quantity).
                sb.Append($"  \"staggerBudget\": {_staggerBudget},\n");
                sb.Append($"  \"kerbcamFrameMs\": {_kerbcamFrameMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},\n");
                sb.Append("  \"cameras\": [\n");
                for (int i = 0; i < _cameras.Count; i++)
                {
                    var cam = _cameras[i];
                    sb.Append("    {\n");
                    sb.Append($"      \"flightId\": {cam.FlightId},\n");
                    sb.Append($"      \"renderWidth\": {cam.RenderWidth},\n");
                    sb.Append($"      \"renderHeight\": {cam.RenderHeight},\n");
                    sb.Append($"      \"operatorWidth\": {cam.OperatorWidth},\n");
                    sb.Append($"      \"operatorHeight\": {cam.OperatorHeight},\n");
                    sb.Append($"      \"layers\": {LayersToJson(cam.Layers)},\n");
                    sb.Append($"      \"operatorLayers\": {LayersToJson(cam.OperatorLayers)},\n");
                    sb.Append($"      \"enableAtmosphericFx\": {(cam.EnableFx ? "true" : "false")},\n");
                    sb.Append($"      \"fov\": {cam.Fov.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    sb.Append($"      \"panYaw\": {cam.PanYaw.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    sb.Append($"      \"panPitch\": {cam.PanPitch.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
                    sb.Append(i == _cameras.Count - 1 ? "    }\n" : "    },\n");
                }
                sb.Append("  ]");

                // Telemetry section (Recommendation 1). Per-phase render cost
                // (summed across cameras = total main-thread cost per tick; max
                // across cameras' rolling-max = the spike peak), the GC interval
                // deltas + spike correlation, degrade level, and camera count.
                // Only emitted when EnableTelemetry; the array above closes with
                // no trailing comma, so we add one only when extending the object.
                if (KerbcamSettings.EnableTelemetry)
                {
                    AppendTelemetry(sb);
                }
                sb.Append("\n}\n");

                if (KerbcamSettings.EnableTelemetry)
                {
                    // Roll the interval stats over now that they've been written:
                    // clear the GC interval accumulators and each camera's
                    // rolling-max, so the next write reflects the next ~1s window.
                    _gcTracker.ResetInterval();
                    for (int i = 0; i < _cameras.Count; i++)
                        _cameras[i].PhaseTimings.ResetMax();
                }

                // Atomic write: drop into .tmp + rename so the sidecar
                // never reads a half-written file.
                var tmp = _statusPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(_statusPath)) File.Delete(_statusPath);
                File.Move(tmp, _statusPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] status file write failed: {ex.Message}");
            }
        }

        /*
         * Poll global.control.json written by the sidecar. On a new seq,
         * apply the requested throttle state: write the per-save difficulty
         * param so it persists, then drive the existing apply/restore methods.
         * Uses its own _controlCooldown counter (~1Hz, independent of status writes).
         */
        private void MaybeApplyGlobalControl()
        {
            if (_controlPath == null) return;
            _controlCooldown -= Time.unscaledDeltaTime;
            if (_controlCooldown > 0f) return;
            _controlCooldown = StatusWriteIntervalSeconds;
            try
            {
                if (!File.Exists(_controlPath)) return;
                var json = File.ReadAllText(_controlPath);
                if (!TryParseGlobalControl(json, out ulong seq, out bool throttle)) return;
                if (seq <= _lastAppliedControlSeq) return;
                _lastAppliedControlSeq = seq;
                /* Set the per-save difficulty param so it persists on save. */
                try
                {
                    var game = HighLogic.CurrentGame;
                    if (game?.Parameters != null)
                    {
                        var param = game.Parameters.CustomParams<KerbcamGameParameters>();
                        if (param != null) param.ThrottleMainScreen = throttle;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Kerbcam] global control: param write failed: {ex.Message}");
                }
                /* Drive the live throttle state immediately (same as the reconcile). */
                _throttleDesired = throttle;
                if (throttle) ApplyMainScreenThrottle();
                else RestoreMainScreen();
                Debug.Log($"[Kerbcam] global control: throttleMainScreen={throttle} (seq={seq})");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] global control read failed: {ex.Message}");
            }
        }

        /*
         * Parse {"throttleMainScreen": bool, "seq": number} written by the
         * sidecar. Uses Regex to avoid a JSON library dependency. The sidecar
         * controls the format exactly so the pattern is stable.
         */
        private static bool TryParseGlobalControl(string json, out ulong seq, out bool throttle)
        {
            seq = 0;
            throttle = false;
            if (string.IsNullOrEmpty(json)) return false;
            var seqMatch = System.Text.RegularExpressions.Regex.Match(json, "\"seq\"\\s*:\\s*(\\d+)");
            if (!seqMatch.Success) return false;
            if (!ulong.TryParse(seqMatch.Groups[1].Value, out seq)) return false;
            var throttleMatch = System.Text.RegularExpressions.Regex.Match(json, "\"throttleMainScreen\"\\s*:\\s*(true|false)");
            if (!throttleMatch.Success) return false;
            throttle = throttleMatch.Groups[1].Value == "true";
            return true;
        }

        // Builds the "telemetry" object appended to the status JSON when
        // EnableTelemetry is set. Phase costs are aggregated across cameras:
        // `ms` is the SUM of each camera's last per-phase value (the total
        // main-thread cost of that phase this tick across all cameras — the
        // galaxy:scaled:near ratio that answers "which layer dominates"), and
        // `maxMs` is the MAX of each camera's rolling-max for the interval (the
        // worst single-camera spike, reset each write). GC + degrade + camera
        // count are global. Allocation here is fine — 1Hz, on the blessed status
        // write path. Caller has just closed the cameras array; we open with a
        // comma so the telemetry object extends the same JSON object.
        private void AppendTelemetry(System.Text.StringBuilder sb)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append(",\n  \"telemetry\": {\n");
            sb.Append($"    \"cameraCount\": {_cameras.Count},\n");
            sb.Append($"    \"staggerBudget\": {_staggerBudget},\n");

            sb.Append("    \"phasesMs\": {\n");
            AppendPhase(sb, "galaxy", RenderPhase.Galaxy, ci, first: true);
            AppendPhase(sb, "scaled", RenderPhase.Scaled, ci, first: false);
            AppendPhase(sb, "near", RenderPhase.Near, ci, first: false);
            AppendPhase(sb, "blit", RenderPhase.Blit, ci, first: false);
            AppendPhase(sb, "readback", RenderPhase.Readback, ci, first: false);
            sb.Append("\n    },\n");

            sb.Append("    \"gc\": {\n");
            sb.Append($"      \"gen0\": {_gcTracker.IntervalGen0},\n");
            sb.Append($"      \"gen1\": {_gcTracker.IntervalGen1},\n");
            sb.Append($"      \"gen2\": {_gcTracker.IntervalGen2},\n");
            sb.Append($"      \"worstFrameMs\": {_gcTracker.WorstFrameMs.ToString("F2", ci)},\n");
            sb.Append($"      \"worstGcFrameMs\": {_gcTracker.WorstGcFrameMs.ToString("F2", ci)},\n");
            sb.Append($"      \"worstFrameWasGc\": {(_gcTracker.WorstFrameWasGc ? "true" : "false")},\n");
            // Best-effort Mono heap gauge — may read 0 in a non-development
            // player. Informational only; lead with the collection counts above,
            // which are reliable.
            sb.Append($"      \"monoHeapBytes\": {UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong()}\n");
            sb.Append("    }\n");

            sb.Append("  }");
        }

        // One phase row: { "ms": <sum-of-last>, "emaMs": <sum-of-ema>, "maxMs":
        // <max-of-rolling-max> }, all aggregated across cameras. `ms` is the
        // latest tick's total main-thread cost, `emaMs` the smoothed central
        // tendency, `maxMs` the interval's worst single-camera spike. `first`
        // controls the leading comma so the JSON object stays well-formed.
        private void AppendPhase(System.Text.StringBuilder sb, string name,
            RenderPhase phase, System.Globalization.CultureInfo ci, bool first)
        {
            double sumLast = 0.0;
            double sumEma = 0.0;
            double maxMax = 0.0;
            for (int i = 0; i < _cameras.Count; i++)
            {
                var pt = _cameras[i].PhaseTimings;
                sumLast += pt.Last(phase);
                sumEma += pt.Ema(phase);
                double m = pt.Max(phase);
                if (m > maxMax) maxMax = m;
            }
            if (!first) sb.Append(",\n");
            sb.Append($"      \"{name}\": {{ \"ms\": {sumLast.ToString("F3", ci)}, \"emaMs\": {sumEma.ToString("F3", ci)}, \"maxMs\": {maxMax.ToString("F3", ci)} }}");
        }

        private static string LayersToJson(CameraLayers layers)
        {
            var parts = new List<string>(3);
            if ((layers & CameraLayers.Near) != 0) parts.Add("\"NEAR\"");
            if ((layers & CameraLayers.Scaled) != 0) parts.Add("\"SCALED\"");
            if ((layers & CameraLayers.Galaxy) != 0) parts.Add("\"GALAXY\"");
            return "[" + string.Join(", ", parts.ToArray()) + "]";
        }

        private void UpdateFpsAverage()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt < 0.0001f) return;
            _fpsWindow[_fpsIdx] = 1f / dt;
            _fpsIdx = (_fpsIdx + 1) % FpsSamples;
            if (_fpsCount < FpsSamples) _fpsCount++;

            float sum = 0f;
            for (int i = 0; i < _fpsCount; i++) sum += _fpsWindow[i];
            _fpsAvg = sum / _fpsCount;
        }

        // Per-tick: regulate the capture budget to hold kerbcam's own per-frame
        // main-thread cost within MaxKerbcamFrameBudgetMs (lossless temporal
        // degrade — fewer cameras per frame, full quality each), tightening
        // further if game fps falls below the MinKspFps physics floor. Quality
        // is untouched unless AdaptiveQuality is enabled (on by default), in which case the
        // quality ladder below runs AFTER the stagger decision, off the same
        // signals: demote only once staggering is exhausted, promote only
        // after sustained headroom.
        // Skips until the fps window has filled — early post-load frames noisy.
        private void UpdateDegradeLevel()
        {
            if (_fpsCount < FpsSamples) return;
            if (_staggerController == null) return;
            // Per-camera cost = this frame's measured kerbcam cost ÷ the cameras
            // that actually captured (budget-independent ≈ constant). Unscaled
            // time so dwell tracks wall-clock, not in-game time (timewarp).
            double msPerCam = _kerbcamFrameMs / (_lastCapturedCount > 0 ? _lastCapturedCount : 1);
            int before = _staggerController.Budget;
            // Budgeted over the SUBSCRIBED set (_streamCount), not all attached.
            // _fpsAvg feeds the one-way physics-floor safety (MinKspFps).
            int budget = _staggerController.Evaluate(
                _kerbcamFrameMs, msPerCam, _streamCount, _fpsAvg, Time.unscaledTime);
            if (budget != before)
                Debug.Log($"[Kerbcam] stagger budget={budget}/{_streamCount} streaming "
                    + $"(kerbcam {_kerbcamFrameMs:F1}ms, {msPerCam:F1}ms/cam, KSP {_fpsAvg:F0}fps, "
                    + $"max {_settings.MaxKerbcamFrameBudgetMs:F0}ms, floor {_settings.MinKspFps:F0}fps) "
                    + $"[{_staggerController.LastChangeReason}]");

            // Opt-in quality ladder, fed the stagger controller's own signals.
            // The budget passed is the stagger budget BEFORE the MaxCaptureFps
            // rate cap: the cap is user config, not load, and must not block a
            // promote. null when AdaptiveQuality=false (nothing evaluated).
            if (_qualityController != null)
            {
                int qBefore = _qualityController.Level;
                int qLevel = _qualityController.Evaluate(
                    _kerbcamFrameMs, budget, _streamCount, _fpsAvg, Time.unscaledTime);
                if (qLevel != qBefore)
                {
                    Debug.Log($"[Kerbcam] stagger quality level={qLevel}/{KerbcamCamera.MaxShedLevel} "
                        + $"({_qualityController.LastChangeReason}; kerbcam {_kerbcamFrameMs:F1}ms, "
                        + $"KSP {_fpsAvg:F0}fps, budget {budget}/{_streamCount}, "
                        + $"max {_settings.MaxKerbcamFrameBudgetMs:F0}ms, floor {_settings.MinKspFps:F0}fps)");
                    for (int i = 0; i < _cameras.Count; i++)
                        _cameras[i].ApplyAutoShed(qLevel);
                }
            }
        }

        // Build the stagger budget regulator from settings. MaxKerbcamFrameBudgetMs
        // <= 0 means "no ms cap" — a huge budget so cost never triggers a cut
        // (capture then bounded only by MaxCaptureFps + the MinKspFps floor).
        private static StaggerBudgetController BuildStaggerController(KerbcamSettings s)
        {
            double budgetMs = s.MaxKerbcamFrameBudgetMs > 0f ? s.MaxKerbcamFrameBudgetMs : 1e6;
            return new StaggerBudgetController(
                budgetMs,
                initialBudget: 1,
                minKspFps: s.MinKspFps);
        }

        // Build the opt-in quality ladder from the same targets as the stagger
        // regulator (the two share the ms budget and the fps floor, so demote
        // engages exactly where staggering runs out of room). Level 0 is the
        // configured Width/Height/layers; the ladder never goes above it.
        private static AdaptiveQualityController BuildQualityController(KerbcamSettings s)
        {
            double budgetMs = s.MaxKerbcamFrameBudgetMs > 0f ? s.MaxKerbcamFrameBudgetMs : 1e6;
            return new AdaptiveQualityController(
                enabled: true,
                maxLevel: KerbcamCamera.MaxShedLevel,
                budgetMs: budgetMs,
                minKspFps: s.MinKspFps);
        }

        private void OnDestroy()
        {
            // ALWAYS restore the main flight cameras on scene exit so a
            // revert / quit-to-KSC / quit-to-main-menu doesn't leave KSP
            // with a dead viewport. This is unconditional even if the
            // operator never enabled throttle this session — a no-op in
            // that case.
            RestoreMainScreen();

            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onPartDestroyed.Remove(OnPartDestroyed);
            GameEvents.onVesselWasModified.Remove(OnVesselWasModified);
            foreach (var cam in _cameras) cam.Dispose();
            _cameras.Clear();
            /* Deliberately NOT stopping the sidecar: KerbcamSidecarHost owns
               the process for the whole KSP session. The ring-file deletions
               above are all the sidecar needs to drain its camera list. */

            // Tear down the readback pump now that the cameras (and their
            // readback requests) are gone. Leaving it alive would keep firing a
            // per-frame render-thread plugin event in every later scene — the
            // root of the "spikes persist on the main menu / only a KSP restart
            // recovers" symptom.
            //
            // We MUST null AsyncReadbackUpdater.instance ourselves. The previous
            // code relied on Unity's destroyed-object fake-null making
            // `instance == null` true on the next Flight Awake — but empirically
            // it did NOT respawn after an exit-to-KSC round trip (the static held
            // a stale reference, the Awake guard saw "not null", the pump never
            // came back, and async readbacks wedged until a full KSP restart).
            // Destroy() is deferred, so the static stays set across the scene
            // transition; clear it synchronously here so the next Awake's
            // `instance == null` guard reliably re-spawns a fresh pump. (The
            // vendored AsyncReadbackUpdater now also nulls it in its own
            // OnDestroy, as defence-in-depth.)
            if (_readbackUpdaterGo != null)
            {
                UnityEngine.Object.Destroy(_readbackUpdaterGo);
                _readbackUpdaterGo = null;
                AsyncReadbackUpdater.instance = null;
                Debug.Log("[Kerbcam] tore down AsyncReadbackUpdater (readback pump) on scene exit");
            }

            // Clean up status file so a stale snapshot doesn't survive
            // into the next launch.
            try
            {
                if (_statusPath != null && File.Exists(_statusPath))
                    File.Delete(_statusPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] status file delete failed: {ex.Message}");
            }
        }
    }
}
