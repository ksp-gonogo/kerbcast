// KSPAddon entry point. Hooks into the Flight scene (and the Space Center,
// for the command camera alone), scans the active
// vessel for Hullcam VDS parts, and pumps an AsyncGPUReadback per camera
// each LateUpdate. Each KerbcastCamera owns its own MmapFrameRing keyed
// by KSP's stable Part.flightID; the Rust sidecar discovers rings by
// globbing the kerbcast/ subdirectory under XDG_RUNTIME_DIR.
//
// Lifecycle:
//   - Awake:    spawn AsyncReadbackUpdater (KSP loads mod DLLs after
//               [RuntimeInitializeOnLoadMethod] would fire, so the
//               vendored yangrc updater never auto-attaches).
//               Ensure the rings directory exists, then notify the
//               session-persistent KerbcastSidecarHost (which spawns the
//               sidecar on the FIRST flight of the session and keeps it
//               running across scene changes).
//               Subscribe GameEvents.onPartDestroyed to detect Hullcam
//               part destruction mid-flight.
//   - GameEvents.onVesselChange: rebuild the camera list (which
//               creates/destroys the matching ring files).
//   - GameEvents.onPartDestroyed: write lifecycle:"destroyed" tombstone
//               to the camera's info.json, close the ring, then leave
//               info.json for the sidecar to clean up. Once
//               onGameSceneLoadRequested has fired the scene is unloading,
//               so part destructions become normal teardowns instead.
//   - GameEvents.onVesselCrewWasModified: crew-scoped rescan (board /
//               transfer / EVA / re-seat within the same vessel). Clean-
//               removes vacated kerbal cams (Dispose, not a tombstone)
//               and adds newly-seated ones via CrewProvider only.
//   - LateUpdate: defensive null-check sweep before the main Refresh()
//               loop catches cameras whose part/vessel went null without
//               a matching onPartDestroyed event (mod interactions, etc).
//               Also refreshes each tracked camera.
//   - OnDestroy: unsubscribe onPartDestroyed, tear down cameras (each
//               disposes its own ring + deletes its files). The sidecar
//               is NOT stopped: it belongs to KerbcastSidecarHost and
//               survives scene changes (the camera-ring removals are all
//               it observes). It dies on game exit or via the heartbeat
//               orphan watch.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using Yangrc.OpenGLAsyncReadback;
using Debug = UnityEngine.Debug;

namespace Kerbcast
{
    /* Flight runs everything. The Space Center runs only the command camera,
       and only when EnableCommandCamera is on; otherwise it starts nothing. */
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, once: false)]
    public sealed class KerbcastCore : MonoBehaviour
    {
        private const int RingSlots = 4;
        private static readonly string RingDir = ResolveRingDir();

        /* Live singleton for the flight scene, set in Awake / cleared in
           OnDestroy. Kerbcast.API's implementation resolves cameras through
           this, and is KerbcastApi.Instance exactly while it is set. */
        public static KerbcastCore Instance { get; private set; }

        /* Live at the Space Center: feeds are up outside flight, which the
           sidecar host reports as in-flight so clients do not show standby. */
        internal static bool SpaceCenterFeedsLive => Instance != null && Instance._atSpaceCenter;
        private bool _atSpaceCenter;

        // Read-only view of the tracked cameras for the KerbcastControl facade.
        internal System.Collections.Generic.IReadOnlyList<ICamera> Cameras => _cameras;

        /* The command lock holder's authority over the command feeds; one per
           scene, so a scene change ends any registration. */
        internal CommandAuthority CommandAuthority { get; } = new CommandAuthority();

        /* ~0.5s at 60fps: long enough that a transient scene-change hiccup
           doesn't evict a healthy camera, short enough to stop a genuinely
           broken one from log-spamming for minutes. */
        private const int RefreshQuarantineThreshold = 30;

        private KerbcastSettings _settings;
        private readonly List<ICamera> _cameras = new List<ICamera>();
        private List<ICameraSourceProvider> _providers;
        // Typed reference into _providers for the crew-scoped rescan
        // (OnVesselCrewWasModified must not touch HullcamProvider).
        private CrewProvider _crewProvider;
        private CommandCameraProvider _commandProvider;

        // Status file (sidecar ← plugin). Rewritten ~1Hz when anything has
        // changed — KSP fps, shed level, per-camera effective layers / dims.
        // Sidecar reads, diffs, and pushes camera-state-changed +
        // adaptive-shed messages to all peers' control channels.
        private string _statusPath;
        /* Set when ReadbackGate refused startup; nothing was initialised. */
        private bool _disabled;
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
        /* Mission-time capture-clock epoch stamped into global.status.json.
           Bumped on a UT discontinuity (scene load / quickload) so a consumer
           flushes and resyncs. See CaptureEpoch. */
        private readonly CaptureEpoch _captureEpoch = new CaptureEpoch();
        // Read by CaptureCore so every ring frame carries the epoch its UT belongs to.
        internal uint CaptureEpochValue => _captureEpoch.Value;
        /* The real-time clock every ring frame is stamped with, read once per
           frame so all of a frame's captures share it. A Stopwatch in double
           ms, because Time.unscaledTime is a float that coarsens to whole
           milliseconds after a couple of hours, and the sidecar spaces RTP
           timestamps by its differences. */
        private static readonly System.Diagnostics.Stopwatch s_frameClock = System.Diagnostics.Stopwatch.StartNew();
        internal static double FrameClockMs { get; private set; }
        // Tombstone-or-normal decision for cameras whose part goes away. See CameraTeardown.
        private readonly CameraTeardown _teardown = new CameraTeardown();
        // Adaptive-shed decision state machine. The fps thresholds + hysteresis
        // and the anti-flap dwell/back-off all live in ShedController (which is
        // Unity-free and unit-tested). Each LateUpdate we hand it the rolling
        // fps average + the unscaled clock and apply the level it returns.
        // Stagger controller: the lossless temporal-degrade regulator. Holds
        // kerbcast's OWN main-thread cost within a frametime budget by capturing
        // fewer cameras per frame — full resolution + all layers, just less
        // often. Targets kerbcast's cost (not game fps), so it's KSP-independent.
        // kerbcast auto-sheds quality only when AdaptiveQuality is enabled (on by default).
        // Built in Awake from settings; null until then (guarded at call sites).
        private StaggerBudgetController _staggerController;
        // Opt-in quality ladder (AdaptiveQuality=true): demotes resolution/FX
        // layers via KerbcastCamera.ApplyAutoShed once staggering is exhausted,
        // promotes back after sustained headroom. null when the flag is off,
        // which keeps the flag-off path bit-for-bit the pre-flag behaviour
        // (no evaluation, no ApplyAutoShed calls, shedLevel always 0).
        private AdaptiveQualityController _qualityController;

        /* Background-background capture rung: the FIRST thing shed under load, before the
           stagger regulator starts starving primaries and long before the
           quality ladder demotes them. Thresholds sit ABOVE the MinKspFps
           physics floor (18 by default) precisely so this reacts first — by the
           time the game is near the floor background capture is already gone.

           A ShedController instance rather than a second anti-flap machine: the
           dwell, the asymmetric fast-attack/slow-release and the adaptive
           restore back-off are exactly the contract this needs, and that
           contract exists because a captured Deck session once flapped 638
           resolution changes. Not worth re-deriving. */
        private ShedController _backgroundShed;
        private int _backgroundShedLevel;
        /// <summary>Hum ceiling computed once per tick. Cached because reading it
        /// must not re-run the shed controller — Evaluate advances dwell state,
        /// so calling it twice a frame would corrupt the anti-flap timing.</summary>
        private float _effectiveBackgroundCeiling;
        /// <summary>Fraction of the operator's BackgroundCaptureFps still
        /// allowed at each background-shed level. Level 2 is background capture fully off.</summary>
        private static readonly float[] HumCeilingScale = { 1f, 0.5f, 0f };
        // Cameras that actually captured last frame, for the per-camera cost
        // estimate (kerbcastFrameMs / captured).
        private int _lastCapturedCount = 1;
        // Rolling estimate of kerbcast's own main-thread cost per frame (the
        // capture loop's wall-time, EMA-smoothed). Divided by frame time gives
        // the cost-share the stagger controller gates escalation on.
        private double _kerbcastFrameMs;
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

        // Staggering is budgeted over the SUBSCRIBED (streaming) cameras only —
        // idle/attached cameras cost nothing and must not consume permits.
        // _subscribedIdx[0.._streamCount) maps a rank to a full _cameras index,
        // ordered most-overdue-first so the scheduler can grant the front of it;
        // _streamEff is the rank-indexed capture rate a grant is stamped at,
        // _streamOverdue the sort key.
        private int[] _subscribedIdx = new int[0];
        private float[] _streamEff = new float[0];
        private float[] _streamOverdue = new float[0];
        private int _streamCount;
        /* The capture pass (ReadbackScheduler.GrantAsReached): which cameras are
           ranked this tick, its scratch, the tick's grant time, and its
           callbacks, cached so the pass allocates nothing. */
        private bool[] _ranked = new bool[0];
        private int[] _deferredRanks = new int[0];
        private float _grantNow;
        private Func<int, bool> _rankReady;
        private Action<int, bool> _runRank;
        private Action _runUnranked;
        /* Cameras capturing at all, due this tick or not. _streamCount is only
           the ones ELIGIBLE this tick, and now that every camera is paced (not
           just background ones) that number dips below the streaming count on
           most ticks. Anything that means "how many cameras are streaming" wants
           this instead: the per-tick concurrency budget, and the two controllers
           that treat the count as their budget ceiling and would otherwise
           ratchet themselves down to the smallest momentary dip. */
        private int _streamSubscribedCount;

        // Telemetry (Recommendation 1): GC collection-count tracker. Sampled
        // once per LateUpdate when KerbcastSettings.EnableTelemetry is true,
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
            // The kerbcast/ subdirectory namespaces our ring files so the
            // sidecar's *.ring glob doesn't pick up stray files.
            var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(xdg) && Directory.Exists(xdg))
            {
                return Path.Combine(xdg, "kerbcast");
            }
            return "/tmp/kerbcast";
        }

        private void Awake()
        {
            VesselIdentity.EnsureHooked();
            _atSpaceCenter = HighLogic.LoadedScene == GameScenes.SPACECENTER;
            if (_atSpaceCenter)
            {
                _settings = KerbcastSettings.Load();
                if (!_settings.EnableCommandCamera)
                {
                    _disabled = true;
                    enabled = false;
                    return;
                }
            }

            /* Before anything else spins up: without a working GPU readback
               every feed would stream an empty buffer, so start nothing. */
            if (!ReadbackGate.EnsureReady())
            {
                _disabled = true;
                enabled = false;
                return;
            }

            Instance = this;
            KerbcastApiImpl.Start();
            Debug.Log($"[Kerbcast] KerbcastCore.Awake: initialising (scene={HighLogic.LoadedScene})");

            if (_settings == null) _settings = KerbcastSettings.Load();
            _providers = new List<ICameraSourceProvider>();
            if (_atSpaceCenter)
            {
                _commandProvider = new CommandCameraProvider(_settings, RingDir, RingSlots, spaceCenter: true, CommandAuthority);
                _providers.Add(_commandProvider);
                Debug.Log("[Kerbcast][CMDCAM] scene start: Space Center, command camera only");
            }
            else
            {
                _crewProvider = new CrewProvider(RingDir, RingSlots, _settings.Width, _settings.Height);
                _providers.Add(new HullcamProvider(_settings, RingDir, RingSlots));
                _providers.Add(_crewProvider);
                /* Always present so cameras can be created at runtime; the
                   settings-driven one only when enabled. */
                _commandProvider = new CommandCameraProvider(_settings, RingDir, RingSlots, spaceCenter: false, CommandAuthority,
                    withDefault: _settings.EnableCommandCamera);
                _providers.Add(_commandProvider);
                if (_settings.EnableCommandCamera)
                    Debug.Log("[Kerbcast][CMDCAM] scene start: flight, command camera follows the active craft");
            }
            StartReservedLock();
            _staggerController = BuildStaggerController(_settings);
            /* Only built when the operator permits background capture at all:
               with the ceiling at 0 nothing can ever background capture, so there is nothing to
               shed and the ladder keeps exactly its old shape. Thresholds sit
               well above MinKspFps so background capture is gone before the primary
               machinery reacts. Permitting is not enabling — a camera only captures in the background
               once a connected client asks for it. */
            if (_settings.BackgroundCaptureFps > 0f)
            {
                _backgroundShed = new ShedController(
                    maxLevel: 2,
                    shedBelow: new[] { 24f, 20f },
                    restoreAbove: new[] { 28f, 24f });
                Debug.Log($"[Kerbcast] background capture permitted up to {_settings.BackgroundCaptureFps:F1}fps (clients must request it; shed first under load)");
            }
            if (_settings.AdaptiveQuality)
            {
                _qualityController = BuildQualityController(_settings);
                Debug.Log("[Kerbcast] stagger quality ladder enabled (AdaptiveQuality=true)");
            }

            try
            {
                /* yangrc's [RuntimeInitializeOnLoadMethod] hook never fires
                   for mod DLLs (KSP loads them post-init), so the updater
                   MonoBehaviour that pumps OpenGLAsyncReadbackRequest never
                   auto-attaches. Spawn it ourselves on a DontDestroyOnLoad
                   GameObject, only when ReadbackGate loaded the native library
                   (Linux OpenGL). Elsewhere the wrapper uses Unity's own
                   AsyncGPUReadback and the pump has nothing to drive. */
                if (NativeReadbackLibrary.IsLoaded
                    && AsyncReadbackUpdater.instance == null)
                {
                    _readbackUpdaterGo = new GameObject("Kerbcast_AsyncReadbackUpdater");
                    UnityEngine.Object.DontDestroyOnLoad(_readbackUpdaterGo);
                    _readbackUpdaterGo.AddComponent<AsyncReadbackUpdater>();
                    Debug.Log("[Kerbcast] spawned AsyncReadbackUpdater (readback pump)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast] failed to spawn AsyncReadbackUpdater: {ex}");
            }

            try
            {
                Directory.CreateDirectory(RingDir);
                int swept = CameraTeardown.SweepStaleCameraFiles(RingDir, out int skipped);
                if (swept > 0 || skipped > 0)
                    Debug.Log($"[Kerbcast] swept {swept} stale camera file(s) left by an earlier flight, skipped {skipped} in use");
                _statusPath = Path.Combine(RingDir, "global.status.json");
                _controlPath = Path.Combine(RingDir, "global.control.json");
                _commandsPath = Path.Combine(RingDir, CommandsFileName);
                SeedCommandRequests();
                Debug.Log($"[Kerbcast] rings directory ready at {RingDir} ({RingSlots} slots × {_settings.Width}×{_settings.Height} RGBA per camera)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast] failed to create rings directory at {RingDir}: {ex}");
                enabled = false;
                return;
            }

            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onPartDestroyed.Add(OnPartDestroyed);
            GameEvents.onVesselWasModified.Add(OnVesselWasModified);
            GameEvents.onVesselCrewWasModified.Add(OnVesselCrewWasModified);
            /* Mission-time discontinuities: a scene load or a quickload makes
               universal time jump (revert/quickload run backward). Bump the
               capture epoch so a clock consumer flushes rather than waiting on
               a UT that will never arrive. */
            GameEvents.onGameSceneLoadRequested.Add(OnGameSceneLoadRequested);
            GameEvents.onGameStatePostLoad.Add(OnGameStatePostLoad);
            GameEvents.onVesselPersistentIdChanged.Add(OnVesselPersistentIdChanged);

            /* Defensive: at Awake the scene's source cameras (Camera 00,
               ScaledSpace, GalaxyCamera, FXCamera) may already be disabled, for
               example after loading a save last left in IVA, or after an older
               build's throttle left the DontDestroyOnLoad scaled and galaxy
               cameras off. Re-enable them before the first SetCameras, as the
               earlier restore-at-rebuild did, so no clone is built from a
               camera KSP or a mod has switched off. Skipped at the Space Center,
               whose scene decides for itself which of these cameras run. */
            if (!_atSpaceCenter && AreAnySourceCamerasDisabled())
            {
                Debug.Log("[Kerbcast] source cameras already disabled at Awake: re-enabling before SetCameras");
                EnableSourceCameras();
            }

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
            KerbcastSidecarHost.NotifyFlightEntered(_settings, RingDir);
        }

        private void OnVesselChange(Vessel v)
        {
            Debug.Log($"[Kerbcast] vessel change: {(v != null ? v.vesselName : "<null>")}");
            // Going EVA fires onVesselChange (control follows the kerbal onto the
            // EVA vessel), but the ship stays loaded and in physics range — like a
            // dock/stage change (onVesselWasModified, additive). Keep the ship's
            // cameras through the EVA switch instead of the scope-and-drop a real
            // craft switch does; a genuine out-of-range unload is still caught by
            // onPartDestroyed + the LateUpdate !IsAlive sweep. See VesselChangePolicy.
            bool goingEva = v != null && v.isEVA;
            RebuildCameraList(v, VesselChangePolicy.DisposeMissingOnVesselChange(goingEva));
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
                if (cam.OwnsPart(part))
                {
                    if (affected == null) affected = cam.Vessel;
                    Debug.Log($"[Kerbcast] part destroyed — disposing cam={cam.FlightId} ({part.name}){(_teardown.LostCameraDisposal == CameraDisposal.Destroyed ? "" : " during scene unload, normal teardown")}");
                    _cameras.RemoveAt(i);
                    DisposeLost(cam);
                }
            }
            // Rebuild FX draw lists on the surviving cams of the same vessel so
            // the destroyed part's renderers don't linger in their CommandBuffers.
            if (affected != null) MarkFxDirtyForVessel(affected);
        }

        /* A camera whose part went away: a tombstone (SIGNAL LOST) for a genuine
           in-flight loss, a normal teardown once the scene is unloading. */
        private void DisposeLost(ICamera cam)
        {
            if (_teardown.LostCameraDisposal == CameraDisposal.Destroyed) cam.DisposeDestroyed();
            else cam.Dispose();
        }

        // Decoupling, staging, docking, fairing jettison etc. change a
        // vessel's part set without firing onVesselChange (no pilot switch)
        // or onPartDestroyed for our tracked parts. Pick up any newly-joined
        // parts (e.g. a docking partner's cameras) immediately rather than
        // waiting for an incidental later vessel switch — additive only, see
        // RebuildCameraList's disposeMissing doc comment for why staged-off
        // debris must not be torn down here. Also rebuild the FX draw lists
        // so they match the current parts.
        private void OnVesselWasModified(Vessel v)
        {
            if (v == null) return;
            RebuildCameraList(v, disposeMissing: false);
            MarkFxDirtyForVessel(v);
        }

        // Board / transfer / EVA / re-seat: a kerbal camera's liveness (seat
        // membership) can change WITHOUT onVesselChange or onVesselWasModified
        // firing (same vessel, no part-set change). Crew-scoped only: part
        // cameras are handled by their own events, so HullcamProvider is never
        // touched here.
        //
        // Clean-remove first: a seat vacate is a normal state change, not a
        // destruction, so Dispose() (no tombstone), not DisposeDestroyed().
        // Then add-pass: newly-seated kerbals via CrewProvider only.
        private void OnVesselCrewWasModified(Vessel v)
        {
            if (v == null) return;

            for (int i = _cameras.Count - 1; i >= 0; i--)
            {
                if (_cameras[i] is KerbalFaceCamera kfc && !kfc.IsAlive)
                {
                    Debug.Log($"[Kerbcast] crew modified, vacated cam={kfc.FlightId}");
                    _cameras.RemoveAt(i);
                    kfc.Dispose();
                }
            }

            if (_crewProvider == null) return;
            var existing = new HashSet<uint>(_cameras.Select(c => c.FlightId));
            foreach (var cam in _crewProvider.Enumerate(v, existing))
            {
                _cameras.Add(cam);
            }
        }

        /* Scene load / quickload: universal time is about to jump, so mark a
           capture-clock discontinuity. The `scene`/`node` args are unused;
           any fire of these events is a resync point. A scene load also means
           the parts are about to be destroyed wholesale, which must not leave
           destroyed tombstones behind. */
        private void OnGameSceneLoadRequested(GameScenes scene)
        {
            _captureEpoch.Bump();
            _teardown.OnSceneLoadRequested();
            EndSceneLock();
            CommandAuthority.WorldModel.Close();
            /* Restore now, before the scene tears down. Camera ScaledSpace and
               GalaxyCamera survive into the next scene, so a throttle left on
               them would keep the next scene's main view in an 8x8 corner. */
            RestoreMainScreen();
        }

        /* From now until the scene is gone every take is refused, so a LockLost
           handler that takes the lock again does so in the next scene. */
        internal bool SceneEnding { get; private set; }

        /* The lock ends with the scene, and its holder hears so once: here on a
           scene load, or at OnDestroy when no load was requested. */
        private void EndSceneLock()
        {
            if (SceneEnding) return;
            SceneEnding = true;
            string holder = CommandAuthority.Owner;
            CommandAuthority.Reset();
            if (holder != null) RaiseLockLost(holder, Api.LockLossReason.SceneEnded);
        }

        /* A game-state load inside the scene also leaves the world model's
           snapshot describing a timeline that is gone. */
        private void OnGameStatePostLoad(ConfigNode node)
        {
            _captureEpoch.Bump();
            CommandAuthority.WorldModel.InvalidateForLoad();
        }

        private void MarkFxDirtyForVessel(Vessel v)
        {
            foreach (var cam in _cameras)
                if (cam.Vessel == v) cam.MarkFxDirty();
        }

        // disposeMissing=true (onVesselChange — an actual pilot switch): scope
        // the camera list to `vessel`, tearing down anything not on it. This
        // is the documented "leaving a vessel drops its cameras" behavior.
        //
        // EXCEPTION for kerbal-face feed continuity: a KerbalFaceCamera whose
        // kerbal is still live (IsAlive) is PRESERVED (kept in _cameras, not
        // disposed+recreated), so a seat<->EVA switch — which fires
        // onVesselChange as control follows the kerbal onto/off the EVA vessel —
        // keeps the same ring/info/control + subscription instead of dropping
        // the feed. Liveness alone is the rule, matching OnVesselCrewWasModified
        // and the LateUpdate defensive sweep (both dispose kerbal cams purely on
        // !IsAlive), so an operator watching a kerbal keeps that feed even when
        // the player switches to a different vessel. A truly-gone kerbal reports
        // IsAlive==false (ResolveLocation scans all loaded vessels) and is still
        // disposed here; if a kept kerbal later vanishes without a vessel change,
        // the LateUpdate sweep disposes it on !IsAlive. Every part camera is
        // disposed exactly as before: part-camera behaviour is unchanged (a part
        // camera is never kept here — the `is KerbalFaceCamera` guard excludes
        // it — so it is disposed and rebuilt below just like today).
        //
        // disposeMissing=false (onVesselWasModified — dock/stage without a
        // pilot switch): additive only. A part leaving `vessel` via staging
        // is still physically out there on debris and should only stop
        // streaming when it's actually destroyed (physics range /
        // onPartDestroyed), not because this vessel's membership changed —
        // so nothing already tracked gets touched, and only cameras for
        // newly-appeared parts (e.g. a docking partner's) get added.
        private void RebuildCameraList(Vessel vessel, bool disposeMissing = true)
        {
            if (disposeMissing)
            {
                for (int i = _cameras.Count - 1; i >= 0; i--)
                {
                    var cam = _cameras[i];
                    /* A command camera follows whichever craft is
                       active, so a pilot switch keeps it like a live kerbal. */
                    bool keep = (cam is KerbalFaceCamera || IsCommandCamera(cam)) && cam.IsAlive;
                    if (keep) continue;
                    cam.Dispose();
                    _cameras.RemoveAt(i);
                }
            }

            /* The Space Center has no active vessel; its command camera needs none. */
            if (vessel == null && !_atSpaceCenter) return;

            /* While the main-screen throttle is active, the KSP source cameras
               carry its cull mask of 0 and 8x8 pixel viewport, and
               KerbcastCamera.SetCameras copies every setting from them with
               CopyFrom. A rebuild against throttled sources would build clones
               that cull everything into a corner of the frame.

               Temporarily restore the source cameras so SetCameras sees the
               real render stack, then re-apply the throttle once the rebuild
               is complete. The restore/apply pair captures whatever state KSP
               currently considers canonical (e.g. cullingMask updates from
               other mods, post-IVA-exit state). */
            bool wasThrottled = _mainScreenThrottled;
            if (wasThrottled)
            {
                Debug.Log("[Kerbcast] rebuild: temporarily restoring main screen so SetCameras copies unthrottled source cameras");
                RestoreMainScreen();
            }

            var existing = new HashSet<uint>(_cameras.Select(c => c.FlightId));
            foreach (var provider in _providers)
            {
                foreach (var cam in provider.Enumerate(vessel, existing))
                {
                    _cameras.Add(cam);
                    existing.Add(cam.FlightId);
                }
            }
            Debug.Log($"[Kerbcast] tracking {_cameras.Count} Hullcam VDS camera(s)");

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
                Debug.Log("[Kerbcast] rebuild: re-applying main screen throttle");
                ApplyMainScreenThrottle();
            }
        }

        private static bool IsCommandCamera(ICamera cam) => cam is KerbcastCamera kc && kc.Rig != null;

        internal CommandCameraProvider CommandProvider => _commandProvider;

        internal IReadOnlyCollection<uint> CameraFlightIds() => new HashSet<uint>(_cameras.Select(c => c.FlightId));

        /* Forgets a created command camera and disposes it if built. False for
           the default camera or an unknown id. */
        internal bool DestroyCommandCamera(uint flightId)
        {
            if (_commandProvider == null || !_commandProvider.Destroy(flightId)) return false;
            for (int i = _cameras.Count - 1; i >= 0; i--)
            {
                if (_cameras[i].FlightId != flightId) continue;
                var cam = _cameras[i];
                _cameras.RemoveAt(i);
                cam.Dispose();
            }
            return true;
        }

        /* Requests from clients, which the sidecar appends to
           global.commands.json. Read whole every CommandsPollSeconds; the
           ledger applies each request once, in id order. */
        private const string CommandsFileName = "global.commands.json";
        private const float CommandsPollSeconds = 0.25f;
        private string _commandsPath;
        private string _commandsLastJson;
        private float _commandsCooldown;
        private readonly CommandRequestLedger _commandLedger = new CommandRequestLedger();

        private void SeedCommandRequests()
        {
            try
            {
                if (!File.Exists(_commandsPath)) return;
                _commandsLastJson = File.ReadAllText(_commandsPath);
                _commandLedger.SeedFrom(CommandCameraRequest.ParseFile(_commandsLastJson, null));
                /* Anchors are state, not requests: the table on disk applies to this scene too. */
                ApplyRemoteAnchors(_commandsLastJson);
                if (_commandLedger.LastAppliedId > 0)
                    Debug.Log($"[Kerbcast][CMDCAM] client requests up to id {_commandLedger.LastAppliedId} predate this scene; skipped");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] client request file read failed: {ex.Message}");
            }
        }

        private void MaybeApplyCommandRequests()
        {
            if (_commandsPath == null) return;
            _commandsCooldown -= Time.unscaledDeltaTime;
            if (_commandsCooldown > 0f) return;
            _commandsCooldown = CommandsPollSeconds;
            try
            {
                if (!File.Exists(_commandsPath)) return;
                var json = File.ReadAllText(_commandsPath);
                if (json == _commandsLastJson) return;
                _commandsLastJson = json;
                ApplyRemoteAnchors(json);
                if (_commandProvider == null) return;
                var skipped = new List<string>();
                var requests = CommandCameraRequest.ParseFile(json, skipped);
                foreach (var why in skipped)
                    Debug.LogWarning($"[Kerbcast][CMDCAM] client request skipped: {why}");
                foreach (var r in _commandLedger.Fresh(requests))
                    ApplyCommandRequest(r);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] client request read failed: {ex.Message}");
            }
        }

        /* Every browser client's anchors, from the sidecar. */
        internal AnchorRegistry Anchors { get; } = new AnchorRegistry();

        private void ApplyRemoteAnchors(string json)
        {
            var skipped = new List<string>();
            int before = Anchors.Count;
            Anchors.ReplaceRemote(AnchorRegistry.ParseRemote(json, skipped), skipped);
            foreach (var why in skipped)
                Debug.LogWarning($"[Kerbcast][ANCHOR] client anchors skipped: {why}");
            if (Anchors.Count != before)
                Debug.Log($"[Kerbcast][ANCHOR] anchors now {Anchors.Count} (was {before})");
        }

        /* A dock or claw grab: anchors naming the absorbed craft follow it into the survivor. */
        private void OnVesselPersistentIdChanged(uint oldPid, uint newPid)
        {
            Anchors.Merged(oldPid, newPid);
        }

        /* A reservation starts the scene locked for its owner, with every craft
           unknown until it attaches. It lapses once per process if its
           assembly is not loaded, and for the scene if its owner never calls. */
        private void StartReservedLock()
        {
            LockReservationStore.CheckLapse();
            PollPlayerRelease();
            var r = LockReservationStore.Reservation;
            if (!r.Held || !LockReservationStore.AssemblyLoaded(r.Assembly)) return;
            KerbcastControl.TakeLockCore(r.Owner, Api.LockOptions.WorldModelRequired, r.SilentText);
            _reservedHeartbeatPending = true;
            _reservedWatch.Watch(r.Owner, CommandAuthority.Lock);
            Debug.Log($"[Kerbcast][CMDCAM] scene starts locked for {r.Owner} (reserved)");
        }

        private bool _reservedHeartbeatPending;
        private readonly ReservationWatch _reservedWatch = new ReservationWatch();
        private float _playerReleaseCheckAt;

        private void WatchReservedOwner()
        {
            string owner = _reservedWatch.Lapse(CommandAuthority.Lock, Time.realtimeSinceStartup);
            if (owner == null) return;
            Debug.LogWarning($"[Kerbcast][CMDCAM] {owner} reserved the command lock but made no call within {ReservationWatch.LapseSeconds:F0} s; "
                + "its feeds stay blank and browsers may now create and remove command cameras. To show the scene, release the lock in game: "
                + "Difficulty Settings > Kerbcast > Release command lock");
        }

        /* Browsers manage command cameras with no lock, or while a reserved owner has never answered: its feeds are blank either way. */
        private bool BrowserManagesCameras => CommandAuthority.Owner == null || _reservedWatch.Lapsed(CommandAuthority.Lock);

        /* The Difficulty Settings one-shot, read live, once a second: release the lock and clear the reservation, whoever holds them. */
        private void PollPlayerRelease()
        {
            if (Time.realtimeSinceStartup < _playerReleaseCheckAt) return;
            _playerReleaseCheckAt = Time.realtimeSinceStartup + 1f;
            try
            {
                var p = HighLogic.CurrentGame?.Parameters?.CustomParams<KerbcastGameParameters>();
                if (p == null || !p.ReleaseCommandLock) return;
                p.ReleaseCommandLock = false;
                var r = LockReservationStore.Reservation;
                string reserved = r.Owner;
                r.ClearForPlayer();
                string holder = CommandAuthority.Owner;
                Debug.Log($"[Kerbcast][CMDCAM] player released the command lock (held by {holder ?? "nobody"}, reserved by {reserved ?? "nobody"})");
                if (holder != null)
                {
                    KerbcastControl.ReleaseLockCore(holder, byPlayer: true);
                    RaiseLockLost(holder, Api.LockLossReason.ReleasedByPlayer);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] release command lock: {ex.Message}");
            }
        }


        private static void RaiseLockLost(string owner, Api.LockLossReason reason)
        {
            Debug.Log(CommandLock.LostLine(owner, reason));
            Api.KerbcastApi.RaiseLockLost(new Api.LockLostEvent(owner, reason), Debug.LogException);
        }

        private void ApplyCommandRequest(CommandCameraRequest r)
        {
            string holder = CommandAuthority.Owner;
            if (!BrowserManagesCameras && (r.Op == CommandRequestOp.Create || r.Op == CommandRequestOp.Destroy))
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] client request {r.Id}: {holder} holds the command lock; command cameras are not {(r.Op == CommandRequestOp.Create ? "created" : "destroyed")} by clients");
                return;
            }
            switch (r.Op)
            {
                case CommandRequestOp.Create:
                {
                    if (!_commandLedger.ClaimToken(r.Token) || _commandProvider.HasToken(r.Token))
                    {
                        Debug.Log($"[Kerbcast][CMDCAM] client request {r.Id}: token {r.Token} already used this scene; ignored");
                        return;
                    }
                    string why = r.Aim?.Invalid();
                    if (why != null)
                    {
                        Debug.LogWarning($"[Kerbcast][CMDCAM] client request {r.Id}: aim refused: {why}");
                        return;
                    }
                    var aim = r.Aim != null ? KerbcastControl.AimOf(r.Aim) : null;
                    uint id = KerbcastControl.Create(r.Vantage, r.Name, aim, r.Token, out string error);
                    if (id == 0)
                        Debug.LogWarning($"[Kerbcast][CMDCAM] client request {r.Id}: command camera refused: {error}");
                    return;
                }
                case CommandRequestOp.Destroy:
                    if (!DestroyCommandCamera(r.FlightId))
                        Debug.LogWarning($"[Kerbcast][CMDCAM] client request {r.Id}: cam={r.FlightId} is not a created command camera; nothing destroyed");
                    return;
                case CommandRequestOp.Aim:
                {
                    string why = r.Aim?.Invalid();
                    string owner = KerbcastControl.CommandAuthorityOwner;
                    if (why == null && owner != null) why = $"{owner} holds the command lock";
                    bool ok = why == null && KerbcastControl.SetCommandAim(r.FlightId,
                        r.Aim != null ? KerbcastControl.AimOf(r.Aim) : null);
                    if (!ok)
                        Debug.LogWarning($"[Kerbcast][CMDCAM] client request {r.Id}: aim for cam={r.FlightId} refused{(why != null ? ": " + why : "")}");
                    return;
                }
            }
        }

        /* Every frame, paused or not, so a command feed's state transitions and
           the authority timeout are caught close to when they happen even while
           nobody is watching it (a rendered feed solves on every frame anyway). */
        private void PollCommandRigs()
        {
            for (int i = 0; i < _cameras.Count; i++)
            {
                if (!(_cameras[i] is KerbcastCamera kc) || !(kc.Rig is CommandCameraRig gr)) continue;
                try { gr.Poll(); }
                catch (Exception ex)
                {
                    if (Time.unscaledTime < _commandPollWarnAt) continue;
                    _commandPollWarnAt = Time.unscaledTime + 10f;
                    Debug.LogWarning($"[Kerbcast][CMDCAM] cam={kc.FlightId} poll threw: {ex}");
                }
            }
        }

        private float _commandPollWarnAt = float.MinValue;

        /*
         * A command camera whose vantage craft is destroyed (crashed,
         * recovered; a dock is followed instead) has no position. A created
         * camera is removed, as if its creator destroyed it. The default
         * camera is rebuilt where settings resolve now, the home station
         * unless a craft with the configured name exists. Until then the feed
         * streams black, never a guessed position.
         */
        private void DropLostVantages()
        {
            /* A scene unload destroys every vessel; the teardown disposes the cameras. */
            if (_commandProvider == null || _teardown.LostCameraDisposal != CameraDisposal.Destroyed) return;
            List<KerbcastCamera> lost = null;
            foreach (var cam in _cameras)
                if (cam is KerbcastCamera kc && kc.Rig is CommandCameraRig gr && gr.VantageLost)
                    (lost ?? (lost = new List<KerbcastCamera>())).Add(kc);
            if (lost == null) return;
            foreach (var kc in lost)
            {
                var gr = (CommandCameraRig)kc.Rig;
                if (_commandProvider.IsCreated(kc.FlightId))
                {
                    Debug.LogWarning($"[Kerbcast][CMDCAM] cam={kc.FlightId} vantage craft persistentId={gr.Site.VantagePersistentId} is gone; command camera removed");
                    DestroyCommandCamera(kc.FlightId);
                    continue;
                }
                Debug.LogWarning($"[Kerbcast][CMDCAM] cam={kc.FlightId} vantage craft persistentId={gr.Site.VantagePersistentId} is gone; command camera moves to where settings resolve now");
                _commandProvider.ReresolveDefault(gr.Aim, gr.AimOrbit);
                _cameras.Remove(kc);
                kc.Dispose();
            }
        }

        /* On the first scene of a process the launch site's coordinates arrive
           after startup, so the command camera waits for them (additively, like
           a docking rebuild) instead of standing at (0,0). A camera created at
           runtime is due at once, so it is built on the next frame. */
        private void RetryPendingCommandCamera()
        {
            if (_commandProvider == null || !_commandProvider.RetryDue) return;
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null && !_atSpaceCenter) return;
            RebuildCameraList(vessel, disposeMissing: false);
        }

        // LateUpdate drives explicit camera.Render() calls via each
        // KerbcastCamera.Refresh(). Our offscreen cameras are permanently
        // disabled (enabled=false) so they never fire during Unity's normal
        // render pass — we own the render timing entirely.
        private void LateUpdate()
        {
            FrameClockMs = s_frameClock.Elapsed.TotalMilliseconds;
            /* The library loaded but its render-thread GL setup failed, so every
               readback errors and feeds would stay empty. Shut down exactly as
               if startup had been refused: OnDestroy disposes the cameras and
               the pump, and destroying the host stops the sidecar. */
            if (ReadbackGate.GlUnusable())
            {
                if (KerbcastSidecarHost.Instance != null)
                    Destroy(KerbcastSidecarHost.Instance.gameObject);
                /* Cleared now rather than in the deferred OnDestroy, so
                   KerbcastControl reads as inactive from this frame on. */
                if (Instance == this)
                {
                    Instance = null;
                    KerbcastApiImpl.Stop();
                }
                enabled = false;
                Destroy(this);
                return;
            }

            /* A reserved lock is taken at Awake so nothing runs unlocked, but
               its silence clock starts with the first frame: a long scene load
               must not time the owner out before it can call. */
            if (_reservedHeartbeatPending)
            {
                _reservedHeartbeatPending = false;
                CommandAuthority.Lock.RestartSilence(Time.realtimeSinceStartup);
                _reservedWatch.Start(Time.realtimeSinceStartup);
            }
            WatchReservedOwner();
            PollPlayerRelease();
            UpdateFpsAverage();
            MaybeApplyCommandRequests();
            DropLostVantages();
            RetryPendingCommandCamera();
            PollCommandRigs();

            // Telemetry: sample the GC collection counters once per frame and
            // fold this frame's wall-clock duration into the interval stats, so
            // a frametime spike that coincides with a gen-0/1/2 collection is
            // recorded as GC-caused. CollectionCount is a cheap field read; gated
            // so it's a single bool check when telemetry is off.
            if (KerbcastSettings.EnableTelemetry)
            {
                _gcTracker.Sample(
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                    Time.unscaledDeltaTime);
            }
            // Defensive: scan for cameras whose Hullcam part has gone null
            // (e.g. a destruction event we missed, or a KSP internal teardown
            // that doesn't fire onPartDestroyed). DisposeDestroyed + remove them
            // so the orphaned Unity Camera GameObjects and ring files are cleaned
            // up even if the event path failed. Iterate backwards to allow
            // removal by index. Runs before the subscribed set is built, whose
            // ranks are indices into _cameras for the rest of the tick.
            for (int i = _cameras.Count - 1; i >= 0; i--)
            {
                var cam = _cameras[i];
                if (!cam.IsAlive)
                {
                    Debug.LogWarning($"[Kerbcast] defensive sweep: cam={cam.FlightId} has null part/vessel, disposing (missed destruction event?)");
                    _cameras.RemoveAt(i);
                    DisposeLost(cam);
                }
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
                if (_streamEff.Length < n) _streamEff = new float[n];
                if (_streamOverdue.Length < n) _streamOverdue = new float[n];
                _streamCount = 0;
                _streamSubscribedCount = 0;
                /* The set is the cameras ELIGIBLE to capture this tick, not
                   simply the subscribed ones. Every camera only becomes eligible
                   once its own interval has elapsed, so each draws stagger
                   permits in proportion to its rate: that is what caps the rate,
                   and it is what stops a camera in background capture from
                   competing head-on with the feed being watched. */
                float primaryFps = _settings.MaxCaptureFps;
                _effectiveBackgroundCeiling = EffectiveHumCeiling();
                float backgroundCeiling = _effectiveBackgroundCeiling;
                float now = Time.unscaledTime;
                for (int i = 0; i < n; i++)
                {
                    var cam = _cameras[i];
                    if (!cam.Subscribed) continue;
                    float eff = cam.EffectiveCaptureFps(primaryFps, backgroundCeiling);
                    if (eff <= 0f) continue;
                    _streamSubscribedCount++;
                    /* Due is enough to be ranked. Whether its last readback has
                       landed is asked only when the capture pass reaches it (see
                       ReadbackScheduler.GrantAsReached). */
                    if (!cam.CaptureDue(now, eff)) continue;
                    /* Ordered most-overdue-first, because when more cameras come
                       due than the frame's concurrency bound allows the scheduler
                       grants the front of this list, and the camera furthest behind
                       has to be there. Insertion sort: a handful of entries, and no
                       allocation on the main thread. */
                    float overdue = cam.CaptureOverdue(now, eff);
                    int at = _streamCount;
                    while (at > 0 && _streamOverdue[at - 1] < overdue)
                    {
                        _streamOverdue[at] = _streamOverdue[at - 1];
                        _subscribedIdx[at] = _subscribedIdx[at - 1];
                        _streamEff[at] = _streamEff[at - 1];
                        at--;
                    }
                    _streamOverdue[at] = overdue;
                    _subscribedIdx[at] = i;
                    _streamEff[at] = eff;
                    _streamCount++;
                }
            }

            // Regulate the capture budget from kerbcast's own frame cost (lossless
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

            // Undo any KSP or mod write to the throttled cameras before this frame's main render.
            if (_mainScreenThrottled)
                _mainThrottle.Enforce();

            // Stagger captures so the cameras don't all render +
            // read back on the same frame. Budget = how many captures per frame
            // the whole streaming set needs to sustain MaxCaptureFps at the
            // current game fps (_fpsAvg); below MaxCaptureFps it grants all of
            // them. This bounds CONCURRENCY only. The rate cap is each camera's
            // own interval (CapturePacing), so a tick where more cameras come due
            // than the budget allows delays the extras by a frame rather than
            // speeding anyone up.
            int camCount = _cameras.Count;
            if (_ranked.Length < camCount) _ranked = new bool[camCount];
            if (_deferredRanks.Length < camCount) _deferredRanks = new int[camCount];
            if (_subscribedIdx.Length < camCount) _subscribedIdx = new int[camCount];
            if (_streamEff.Length < camCount) _streamEff = new float[camCount];
            if (_streamOverdue.Length < camCount) _streamOverdue = new float[camCount];

            // Staggering is budgeted over the SUBSCRIBED set only. Idle cameras
            // cost nothing (Refresh early-outs on !_subscribed) and must not eat
            // permits — otherwise the few cameras actually streaming would be
            // staggered as if among all attached. (Both counts were filled before
            // UpdateDegradeLevel so the controllers saw the same numbers.)
            int budget = _streamSubscribedCount; // safe default if controller not built
            if (_streamSubscribedCount > 0)
            {
                /* Over the STREAMING set, not the subset eligible this tick: the
                   eligible count is already thinned by pacing, so budgeting over it
                   would apply the rate cap a second time and pull every camera
                   below it. Which also means the bound does not move with how many
                   cameras happen to be due, so it stays reportable on a tick where
                   none are. */
                int rateBudget = ReadbackScheduler.ConcurrencyBudget(
                    _streamSubscribedCount, _settings.MaxCaptureFps, _fpsAvg);
                int staggerBudget = _staggerController != null
                    ? _staggerController.Budget
                    : _streamSubscribedCount;
                budget = Math.Min(rateBudget, staggerBudget);
            }
            _staggerBudget = budget;

            for (int i = 0; i < camCount; i++) _ranked[i] = false;
            for (int rank = 0; rank < _streamCount; rank++) _ranked[_subscribedIdx[rank]] = true;
            if (_runRank == null)
            {
                _rankReady = RankReady;
                _runRank = RunRank;
                _runUnranked = RunUnranked;
            }
            _grantNow = Time.unscaledTime;

            // Measure kerbcast's own main-thread cost this frame (the capture loop
            // wall-time), EMA-smoothed, + how many cameras actually captured —
            // together they give the per-camera cost the controller regulates.
            long capStart = System.Diagnostics.Stopwatch.GetTimestamp();
            int captured = ReadbackScheduler.GrantAsReached(
                _streamCount, budget, _rankReady, _runRank, _runUnranked, _deferredRanks);
            _lastCapturedCount = captured > 0 ? captured : 1;
            /* Quarantined after the pass rather than during it, so every rank
               still names the camera it was given for. */
            for (int i = _cameras.Count - 1; i >= 0; i--)
            {
                var cam = _cameras[i];
                if (cam.RefreshFailureStreak < RefreshQuarantineThreshold) continue;
                Debug.LogError($"[Kerbcast] cam={cam.FlightId} quarantined after {cam.RefreshFailureStreak} consecutive Refresh failures");
                _cameras.RemoveAt(i);
                try { cam.DisposeDestroyed(); }
                catch (Exception dex) { Debug.LogError($"[Kerbcast] cam={cam.FlightId} quarantine dispose threw: {dex}"); }
            }
            double capMs = (System.Diagnostics.Stopwatch.GetTimestamp() - capStart) * _msPerTick;
            _kerbcastFrameMs = _kerbcastFrameMs <= 0.0 ? capMs : _kerbcastFrameMs * 0.8 + capMs * 0.2;

            MaybeApplyGlobalControl();
            MaybeWriteStatusFile();
        }

        private bool RankReady(int rank) => _cameras[_subscribedIdx[rank]].ReadyToCapture;

        /* Stamp the grant, not the eligibility: a camera that was due but lost
           the draw keeps its deadline and stays due, so a busy tick delays it
           rather than silently skipping a whole interval. It also gets more
           overdue, which is what puts it at the front of the next tick's order. */
        private void RunRank(int rank, bool permit)
        {
            var cam = _cameras[_subscribedIdx[rank]];
            if (permit) cam.MarkCaptureGranted(_grantNow, _streamEff[rank]);
            RefreshCamera(cam, permit);
        }

        /* Idle cameras and those not due get no permit; they still refresh, to
           drain a landed readback and keep their aim moving. */
        private void RunUnranked()
        {
            for (int i = _cameras.Count - 1; i >= 0; i--)
                if (!_ranked[i]) RefreshCamera(_cameras[i], false);
        }

        private static void RefreshCamera(ICamera cam, bool permit)
        {
            try
            {
                cam.Refresh(permit);
                cam.RefreshFailureStreak = 0;
            }
            catch (Exception ex)
            {
                /* Isolate per-camera failures: one camera throwing must
                   not abort the other cameras' captures or the control
                   logic below. A sustained streak means the camera is
                   broken beyond self-recovery (it would otherwise spam
                   the log every frame forever), and the pass quarantines
                   it like a missed-destruction part. */
                cam.RefreshFailureStreak++;
                if (cam.RefreshFailureStreak == 1)
                    Debug.LogError($"[Kerbcast] cam={cam.FlightId} Refresh threw: {ex}");
            }
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
                "Main flight camera disabled by kerbcast to free GPU for camera streams. " +
                "Go to Pause > Difficulty Settings > Kerbcast to restore.";

            const float w = 560f;
            const float h = 80f;
            float x = (Screen.width - w) * 0.5f;
            float y = (Screen.height - h) * 0.5f;
            GUI.Box(new Rect(x, y, w, h), msg, _throttleWarnStyle);
        }

        // -- throttle plumbing --

        private bool _throttleDesired;
        private bool _throttleEffective;
        private bool _mainScreenThrottled;
        private readonly MainScreenThrottle _mainThrottle = new MainScreenThrottle();
        // Tracks whether we have successfully disabled FXCamera's camera
        // component as part of a throttle. ApplyMainScreenThrottle sets this
        // only when FXCamera.Instance is available at call time; LateUpdate
        // retries every frame until it succeeds, so a late-initialising
        // FXCamera doesn't escape suppression.
        private bool _fxCamSuppressed;
        private GUIStyle _throttleWarnStyle;

        // Read the operator-set Difficulty value, with a fallback to
        // the settings.cfg seed when no save is active (shouldn't
        // happen in flight scene but defensive — Awake order is
        // sometimes weird across mod combinations).
        private bool ReadThrottleDesired()
        {
            /* The throttle blanks the flight view; the Space Center view stays. */
            if (_atSpaceCenter) return false;
            try
            {
                var game = HighLogic.CurrentGame;
                if (game?.Parameters != null)
                {
                    var node = game.Parameters.CustomParams<KerbcastGameParameters>();
                    if (node != null) return node.ThrottleMainScreen;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast] CustomParams read failed: {ex.Message}");
            }
            return KerbcastSettings.SeedThrottleMainScreen;
        }

        private void TrySuppressFxCamera()
        {
            if (FXCamera.Instance == null) return;
            // Keep FXCamera alive when atmospheric FX is on — our plasma shader
            // samples its globals (_FXDepthMap, _LightDirection0, _FXColor) and
            // they only refresh while FXCamera renders. The "wind without ship"
            // artifact in the main view is moot during throttle because the
            // operator is watching kerbcast streams, not the main view.
            if (_settings.EnableAtmosphericFx) return;
            var fxCam = FXCamera.Instance.GetComponent<Camera>();
            if (fxCam == null) return;
            fxCam.enabled = false;
            _fxCamSuppressed = true;
            Debug.Log("[Kerbcast] FXCamera suppressed (late-init, throttle already active)");
        }

        /* Stops KSP's own view costing render time while leaving its cameras
           enabled. Scatterer and EVE update the sky, haze, cloud and sunlight
           state the clones render with from callbacks on those cameras
           (Scatterer's SkyNode.OnPreRender on Camera ScaledSpace, among others),
           and a disabled camera fires none of them. Scatterer also finds its
           cameras through Camera.allCameras when it initialises a scene, which
           skips disabled ones. So the cameras keep rendering, into an 8x8 pixel
           viewport, and the local ones cull everything. See MainScreenThrottle
           and MainScreenThrottlePolicy. */
        private void ApplyMainScreenThrottle()
        {
            if (_mainScreenThrottled) { _throttleEffective = true; return; }
            /* The scene is unloading and the restore already ran; the per-frame
               reconcile must not throttle the cameras again on the way out. */
            if (_teardown.SceneUnloading) return;
            try
            {
                var detail = new System.Text.StringBuilder();
                _mainThrottle.Apply(ThrottleTargets(), detail);
                // FXCamera is a separate singleton MonoBehaviour with its
                // own Camera component (NOT in FlightCamera.cameras array)
                // that renders aero/re-entry FX via a velocity-based shader.
                // Without disabling it the operator sees "wind effects + ship
                // silhouette" in the *main view* while throttled — but when
                // EnableAtmosphericFx is on, kerbcast's plasma shader samples
                // FXCamera's globals (_FXDepthMap, _LightDirection0, _FXColor)
                // and needs them refreshed, so we deliberately leave FXCamera
                // alive in that case. The main-view artifact doesn't matter
                // because the operator is watching kerbcast streams, not the
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
                _mainScreenThrottled = true;
                _throttleEffective = true;
                Debug.Log($"[Kerbcast] main flight render throttled, KSP cameras kept enabled (ThrottleMainScreen=true):{detail}");
                LogAllCameras("throttle-applied");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast] ApplyMainScreenThrottle failed: {ex}");
            }
        }

        /* FlightCamera.cameras holds Camera 00 and Camera 01 (on D3D11 KSP
           deactivates Camera 01's GameObject and Camera 00 covers the whole
           range). Name lookup is the fallback for a scene without one. */
        private static IEnumerable<KeyValuePair<Camera, ThrottledCameraRole>> ThrottleTargets()
        {
            var local = FlightCamera.fetch != null ? FlightCamera.fetch.cameras : null;
            if (local != null)
            {
                foreach (var c in local)
                    yield return new KeyValuePair<Camera, ThrottledCameraRole>(c, ThrottledCameraRole.Local);
            }
            else
            {
                foreach (var c in KerbcastCamera.FindKspCamerasByName("Camera 00"))
                    yield return new KeyValuePair<Camera, ThrottledCameraRole>(c, ThrottledCameraRole.Local);
                foreach (var c in KerbcastCamera.FindKspCamerasByName("Camera 01"))
                    yield return new KeyValuePair<Camera, ThrottledCameraRole>(c, ThrottledCameraRole.Local);
            }
            foreach (var c in KerbcastCamera.FindKspCamerasByName("Camera ScaledSpace"))
                yield return new KeyValuePair<Camera, ThrottledCameraRole>(c, ThrottledCameraRole.Scaled);
            foreach (var c in KerbcastCamera.FindKspCamerasByName("GalaxyCamera"))
                yield return new KeyValuePair<Camera, ThrottledCameraRole>(c, ThrottledCameraRole.Scaled);
        }

        private void RestoreMainScreen()
        {
            if (!_mainScreenThrottled) { _throttleEffective = false; return; }
            try
            {
                var detail = new System.Text.StringBuilder();
                int restored = _mainThrottle.Restore(detail);
                if (FXCamera.Instance != null)
                {
                    var fxCam = FXCamera.Instance.GetComponent<Camera>();
                    if (fxCam != null) fxCam.enabled = true;
                }
                _mainScreenThrottled = false;
                _throttleEffective = false;
                _fxCamSuppressed = false;
                Debug.Log($"[Kerbcast] main flight render restored, {restored} camera(s) (ThrottleMainScreen=false):{detail}");
                LogAllCameras("throttle-restored");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast] RestoreMainScreen failed: {ex}");
            }
        }

        /* Re-enables every source camera KSP or a mod left switched off. Only
           the Awake recovery uses it; the throttle never disables them. */
        private static void EnableSourceCameras()
        {
            try
            {
                if (FlightCamera.fetch != null)
                    FlightCamera.fetch.EnableCamera();
                if (!SetKspCamerasEnabled("Camera ScaledSpace", true))
                    Debug.LogWarning("[Kerbcast] re-enable: 'Camera ScaledSpace' not found, left as is");
                if (!SetKspCamerasEnabled("GalaxyCamera", true))
                    Debug.LogWarning("[Kerbcast] re-enable: 'GalaxyCamera' not found, left as is");
                if (FXCamera.Instance != null)
                {
                    var fxCam = FXCamera.Instance.GetComponent<Camera>();
                    if (fxCam != null) fxCam.enabled = true;
                }
                LogAllCameras("source-cameras-reenabled");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast] EnableSourceCameras failed: {ex}");
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
                Debug.Log($"[Kerbcast] cameras-snapshot ({tag}): {names}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast] LogAllCameras failed: {ex.Message}");
            }
        }

        // Probe whether ANY of the four main source cameras KSP uses for
        // its in-flight composite (Camera 00, Camera ScaledSpace,
        // GalaxyCamera, FXCamera) are currently disabled. Returns true
        // even if only one is. Used at Awake to re-enable them before the
        // first RebuildCameraList, needed for load-from-IVA-save / similar
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
                            Debug.Log($"[Kerbcast] AreAnySourceCamerasDisabled: '{c.name}' is disabled");
                            anyDisabled = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast] AreAnySourceCamerasDisabled probe failed: {ex.Message}");
            }
            return anyDisabled;
        }

        /* Includes disabled cameras, which Camera.allCameras skips: Camera
           ScaledSpace and GalaxyCamera live in ScaledSpace's DontDestroyOnLoad
           tree and KSP never re-enables their Camera components itself. Acts
           on every match, so a mod's same-named clone cannot stand in for the
           real camera. */
        private static bool SetKspCamerasEnabled(string name, bool enabled)
        {
            var matches = KerbcastCamera.FindKspCamerasByName(name);
            foreach (var c in matches) c.enabled = enabled;
            return matches.Count > 0;
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
                /* Background-background ceiling actually in force: the operator's
                   setting after background capture shed rung. The sidecar reads this to
                   know what rate to ask undisplayed cameras for — the plugin
                   stays the single authority on the number, the sidecar only
                   knows which cameras nobody is displaying. 0 = background capture off, which
                   is the default and makes this whole path inert. */
                sb.Append($"  \"backgroundCaptureFps\": {_effectiveBackgroundCeiling.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},\n");
                // Primary pacing rate, so the sidecar can report each camera's target rate.
                sb.Append($"  \"maxCaptureFps\": {_settings.MaxCaptureFps.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},\n");
                /* Mission-time capture clock: KSP universal time (seconds) and
                   the current time-warp multiplier at write time, plus the
                   discontinuity epoch. Round-trip ("R") + InvariantCulture so
                   the double/float never localises to a comma. */
                sb.Append($"  \"captureUt\": {Planetarium.GetUniversalTime().ToString("R", System.Globalization.CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"captureEpoch\": {_captureEpoch.Value},\n");
                sb.Append($"  \"timeWarpRate\": {TimeWarp.CurrentRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},\n");
                /* Wall clock at the UT sample, so the sidecar on this machine can age captureUt to the moment it sends. */
                sb.Append($"  \"captureWallMs\": {(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)},\n");
                // Stagger telemetry — watch the budget regulator converge + tune
                // MaxKerbcastFrameBudgetMs / MinKspFps. staggerBudget: cameras
                // permitted to capture this tick. kerbcastFrameMs: EMA of kerbcast's
                // own per-frame main-thread cost (the regulated quantity).
                sb.Append($"  \"staggerBudget\": {_staggerBudget},\n");
                sb.Append($"  \"kerbcastFrameMs\": {_kerbcastFrameMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},\n");
                sb.Append("  \"cameras\": [\n");
                // Diagnostic fields are KerbcastCamera-specific; kerbal cameras
                // contribute no diagnostics this stage. Filter first so the
                // trailing-comma logic below keys on the filtered count, not
                // the raw index — otherwise a skipped camera landing last
                // leaves a dangling comma on the entry before it.
                var streaming = _cameras.OfType<KerbcastCamera>().ToList();
                for (int i = 0; i < streaming.Count; i++)
                {
                    var kc = streaming[i];
                    sb.Append("    {\n");
                    sb.Append($"      \"flightId\": {kc.FlightId},\n");
                    sb.Append($"      \"renderWidth\": {kc.RenderWidth},\n");
                    sb.Append($"      \"renderHeight\": {kc.RenderHeight},\n");
                    sb.Append($"      \"operatorWidth\": {kc.OperatorWidth},\n");
                    sb.Append($"      \"operatorHeight\": {kc.OperatorHeight},\n");
                    sb.Append($"      \"layers\": {LayersToJson(kc.Layers)},\n");
                    sb.Append($"      \"operatorLayers\": {LayersToJson(kc.OperatorLayers)},\n");
                    sb.Append($"      \"enableAtmosphericFx\": {(kc.EnableFx ? "true" : "false")},\n");
                    sb.Append($"      \"fov\": {kc.Fov.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    sb.Append($"      \"panYaw\": {kc.PanYaw.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    sb.Append($"      \"panPitch\": {kc.PanPitch.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n");
                    // Auto-track echo UP: the plugin's CURRENT applied mode + a
                    // monotonic seq bumped ONLY on a kOS-facade set. The sidecar
                    // adopts the mode into its authoritative ControlState when the
                    // seq advances (linked track_mode: kOS proposes, sidecar
                    // converges), never re-adopting a control-block-applied change.
                    sb.Append($"      \"trackMode\": {kc.GetTrackMode()},\n");
                    if (kc.Rig is CommandCameraRig gr) AppendCommandFields(sb, gr);
                    sb.Append($"      \"trackReportSeq\": {kc.TrackReportSeq}\n");
                    sb.Append(i == streaming.Count - 1 ? "    }\n" : "    },\n");
                }
                sb.Append("  ]");

                // Telemetry section (Recommendation 1). Per-phase render cost
                // (summed across cameras = total main-thread cost per tick; max
                // across cameras' rolling-max = the spike peak), the GC interval
                // deltas + spike correlation, degrade level, and camera count.
                // Only emitted when EnableTelemetry; the array above closes with
                // no trailing comma, so we add one only when extending the object.
                if (KerbcastSettings.EnableTelemetry)
                {
                    AppendTelemetry(sb);
                }
                sb.Append("\n}\n");

                if (KerbcastSettings.EnableTelemetry)
                {
                    // Roll the interval stats over now that they've been written:
                    // clear the GC interval accumulators and each camera's
                    // rolling-max, so the next write reflects the next ~1s window.
                    _gcTracker.ResetInterval();
                    for (int i = 0; i < _cameras.Count; i++)
                        if (_cameras[i] is KerbcastCamera kc) kc.PhaseTimings.ResetMax();
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
                Debug.LogWarning($"[Kerbcast] status file write failed: {ex.Message}");
            }
        }

        /* Command camera geometry. Polled here so it stays current while nobody
           is watching the feed. */
        private static void AppendCommandFields(System.Text.StringBuilder sb, CommandCameraRig gr)
        {
            gr.Poll();
            var state = gr.StateNow();
            CommandTrackJson.AppendCameraFields(sb, state, gr.ElevationDeg, gr.SubjectName);
            AppendCommandCentre(sb, gr);
            AppendCommandTrack(sb, gr, state);
        }

        /* The wire's CommandAimKind tag. */
        internal static string AimKindTag(CommandAimKind kind)
        {
            switch (kind)
            {
                case CommandAimKind.Vessel: return "vessel";
                case CommandAimKind.Orbit: return "orbit";
                case CommandAimKind.Surface: return "surface";
                case CommandAimKind.PositionAt: return "positionAt";
                default: return "direction";
            }
        }

        /* Aim kind, subject, range, Tracking Station level and the craft the
           camera could watch. Ranges go out to 3 significant figures so the sidecar only
           re-broadcasts when one moves visibly. */
        private static void AppendCommandCentre(System.Text.StringBuilder sb, CommandCameraRig gr)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            double ut = Planetarium.GetUniversalTime();
            List<CommandTargetView> targets;
            try { targets = CommandCameraTargets.List(gr.Site, ut, gr.FovMax); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast][CMDCAM] target list failed: {ex.Message}");
                targets = new List<CommandTargetView>();
            }
            uint pid = gr.SubjectPersistentId;
            double range = double.NaN;
            foreach (var t in targets)
                if (t.PersistentId == pid) { range = t.RangeM; break; }
            if (double.IsNaN(range) && pid != 0) range = CommandCameraTargets.RangeTo(gr.Site, pid, ut);
            string Range(double m) => double.IsNaN(m) || double.IsInfinity(m)
                ? "null"
                : CommandCameraTargets.RoundRange(m).ToString("R", ci);

            sb.Append("      \"commandCentre\": {\n");
            sb.Append($"        \"ut\": {ut.ToString("R", ci)},\n");
            sb.Append($"        \"vantagePersistentId\": {gr.Site.VantagePersistentId},\n");
            if (gr.Aim != null) sb.Append($"        \"aimKind\": \"{AimKindTag(gr.Aim.Kind)}\",\n");
            sb.Append($"        \"subjectPersistentId\": {pid},\n");
            sb.Append($"        \"rangeM\": {Range(range)},\n");
            sb.Append($"        \"trackingLevel\": {CommandCameraTargets.TrackingLevel()},\n");
            sb.Append("        \"targets\": [");
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append($"          {{ \"persistentId\": {t.PersistentId}, \"vesselName\": \"{JsonEsc(t.VesselName)}\", "
                    + $"\"vesselType\": \"{JsonEsc(t.VesselType)}\", \"rangeM\": {Range(t.RangeM)}, "
                    + $"\"eligibility\": \"{JsonEsc(t.Eligibility)}\", \"eligibilityText\": \"{JsonEsc(t.EligibilityText)}\" }}");
            }
            sb.Append(targets.Count > 0 ? "\n        ]\n" : "]\n");
            sb.Append("      },\n");
        }

        /* The bracket contract for clients older than 1.12 (CommandTrackJson). Newer clients read the state from each frame. */
        private static void AppendCommandTrack(System.Text.StringBuilder sb, CommandCameraRig gr, CommandFrameState state)
        {
            sb.Append("      \"commandTrack\": ");
            CommandTrackJson.Append(sb, gr.TrackUt, state, gr.TrackEvents());
            sb.Append(",\n");
        }

        private static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                else sb.Append(c);
            }
            return sb.ToString();
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
                        var param = game.Parameters.CustomParams<KerbcastGameParameters>();
                        if (param != null) param.ThrottleMainScreen = throttle;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Kerbcast] global control: param write failed: {ex.Message}");
                }
                /* Drive the live throttle state immediately (same as the reconcile). */
                _throttleDesired = throttle;
                if (throttle) ApplyMainScreenThrottle();
                else RestoreMainScreen();
                Debug.Log($"[Kerbcast] global control: throttleMainScreen={throttle} (seq={seq})");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast] global control read failed: {ex.Message}");
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
            AppendPhase(sb, "far", RenderPhase.Far, ci, first: false);
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
            sb.Append("    },\n");

            AppendCosts(sb, ci);
            sb.Append("  }");
        }

        /*
         * The 1.12 costs, beside phasesMs and gc in the telemetry block. A time
         * or size is { "ms" | "bytes": last, "emaMs" | "emaBytes": EMA,
         * "maxMs" | "maxBytes": the largest since the previous write }, null
         * until anything was recorded; counts and rates are plain numbers.
         * Each interval max resets on the write, like the phases. A cost per
         * rendered or camera frame is null too for an interval with no such
         * frame (no camera subscribed), rather than its last value.
         *
         *   ringTickMs        the light-time history's sampling, per physics tick
         *   ringEntriesPerS   history entries written per second since the previous write
         *   ringMB            history memory, as the ring counts it
         *   ringTracks        history tracks (craft, bodies and ghosts)
         *   ringBodyTracks    of those, body tracks (bodies seen off their Kepler paths)
         *   pictureMs         past-instant sight solves, per rendered frame, summed over lenses
         *   pictureLenses     lenses with sights kept
         *   bodySolveMs       placing the bodies at their past instants, per rendered frame, summed over lenses
         *   bodyCarriesPerS   body searches that ran out of reads and carried into the next frame, summed over lenses,
         *                     per second since the previous write
         *   bodyHidesPerS     bodies left out of a frame with no instant to draw them at, summed over lenses, per second
         *                     since the previous write
         *   liveLightMs       the live light-time bound's walk over the lens craft's parts, per camera frame
         *   metaBytes         frame-metadata JSON written, per camera frame
         *   metaEncodeMs      encoding it, per camera frame
         *   worldModelFillMs  copying an accepted world-model snapshot, per publish, on the publisher's thread
         *   worldModelRecords records in the current snapshot
         *   saveMs, loadMs    the history's save into and load from the save file; read the last value
         *                     (they happen at saves and loads, not every interval)
         */
        private static void AppendCosts(System.Text.StringBuilder sb, System.Globalization.CultureInfo ci)
        {
            var ring = LightHistoryHost.History;
            int bodyTracks = 0;
            foreach (var t in ring.Tracks) if (t.IsBody) bodyTracks++;
            double entriesPerS = Telemetry.RingEntries.TakeInterval(ring.EntriesWritten, Time.realtimeSinceStartup);
            var store = KerbcastCore.Instance?.CommandAuthority.WorldModel;
            sb.Append("    \"costs\": {\n");
            AppendCost(sb, "ringTickMs", "Ms", Telemetry.RingTick.TakeInterval(), ci);
            sb.Append($"      \"ringEntriesPerS\": {entriesPerS.ToString("F1", ci)},\n");
            sb.Append($"      \"ringMB\": {(ring.Bytes / (1024.0 * 1024.0)).ToString("F3", ci)},\n");
            sb.Append($"      \"ringTracks\": {ring.TrackCount},\n");
            sb.Append($"      \"ringBodyTracks\": {bodyTracks},\n");
            AppendCost(sb, "pictureMs", "Ms", Telemetry.Picture.TakeInterval(), ci, perFrame: true);
            sb.Append($"      \"pictureLenses\": {PicturePass.LensCount},\n");
            AppendCost(sb, "bodySolveMs", "Ms", Telemetry.BodySolve.TakeInterval(), ci, perFrame: true);
            double carriesPerS = Telemetry.BodyCarries.TakeInterval(Telemetry.BodyCarriesTotal, Time.realtimeSinceStartup);
            double hidesPerS = Telemetry.BodyHides.TakeInterval(Telemetry.BodyHidesTotal, Time.realtimeSinceStartup);
            sb.Append($"      \"bodyCarriesPerS\": {carriesPerS.ToString("F1", ci)},\n");
            sb.Append($"      \"bodyHidesPerS\": {hidesPerS.ToString("F1", ci)},\n");
            AppendCost(sb, "liveLightMs", "Ms", Telemetry.LiveLight.TakeInterval(), ci, perFrame: true);
            AppendCost(sb, "metaBytes", "Bytes", Telemetry.MetaBytes.TakeInterval(), ci, perFrame: true);
            AppendCost(sb, "metaEncodeMs", "Ms", Telemetry.MetaEncode.TakeInterval(), ci, perFrame: true);
            AppendCost(sb, "worldModelFillMs", "Ms", Telemetry.WorldModelFill.TakeInterval(), ci);
            sb.Append($"      \"worldModelRecords\": {store?.CurrentRecords ?? 0},\n");
            AppendCost(sb, "saveMs", "Ms", Telemetry.Save.TakeInterval(), ci);
            AppendCost(sb, "loadMs", "Ms", Telemetry.Load.TakeInterval(), ci, lastField: true);
            sb.Append("    }\n");
        }

        private static void AppendCost(System.Text.StringBuilder sb, string name, string unit, CostReading r,
            System.Globalization.CultureInfo ci, bool lastField = false, bool perFrame = false)
        {
            string end = lastField ? "\n" : ",\n";
            string lower = unit.ToLowerInvariant();
            if (!r.Seen || (perFrame && r.Count == 0))
            {
                sb.Append($"      \"{name}\": null{end}");
                return;
            }
            sb.Append($"      \"{name}\": {{ \"{lower}\": {r.Last.ToString("F3", ci)}, \"ema{unit}\": {r.Ema.ToString("F3", ci)}, "
                + $"\"max{unit}\": {r.Max.ToString("F3", ci)}, \"count\": {r.Count} }}{end}");
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
                // Diagnostic-only; kerbal cameras contribute no phase timings this stage.
                if (!(_cameras[i] is KerbcastCamera kc)) continue;
                var pt = kc.PhaseTimings;
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
            if ((layers & CameraLayers.Far) != 0) parts.Add("\"FAR\"");
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

        // Per-tick: regulate the capture budget to hold kerbcast's own per-frame
        // main-thread cost within MaxKerbcastFrameBudgetMs (lossless temporal
        // degrade — fewer cameras per frame, full quality each), tightening
        // further if game fps falls below the MinKspFps physics floor. Quality
        // is untouched unless AdaptiveQuality is enabled (on by default), in which case the
        // quality ladder below runs AFTER the stagger decision, off the same
        // signals: demote only once staggering is exhausted, promote only
        // after sustained headroom.
        // Skips until the fps window has filled — early post-load frames noisy.
        /// <summary>
        /// The background-capture ceiling actually in force this tick: the
        /// operator's setting reduced by background capture shed rung. Zero once the rung
        /// is fully shed, which makes every background camera ineligible, shrinks the
        /// eligible set back to the primaries, and so relieves the pressure
        /// BEFORE the stagger regulator or the quality ladder touch a feed
        /// anyone is watching.
        /// </summary>
        private float EffectiveHumCeiling()
        {
            if (_backgroundShed == null) return 0f; // background capture disabled: nothing to allow
            /* Skip until the fps window has filled, same guard the other
               controllers use — early post-load frames are too noisy to act on,
               and acting on them would shed a background capture that was never in trouble. */
            if (_fpsCount < FpsSamples) return _settings.BackgroundCaptureFps;
            _backgroundShedLevel = _backgroundShed.Evaluate(_fpsAvg, Time.unscaledTime);
            int idx = _backgroundShedLevel;
            if (idx < 0) idx = 0;
            if (idx >= HumCeilingScale.Length) idx = HumCeilingScale.Length - 1;
            return _settings.BackgroundCaptureFps * HumCeilingScale[idx];
        }

        private void UpdateDegradeLevel()
        {
            if (_fpsCount < FpsSamples) return;
            if (_staggerController == null) return;
            // Per-camera cost = this frame's measured kerbcast cost ÷ the cameras
            // that actually captured (budget-independent ≈ constant). Unscaled
            // time so dwell tracks wall-clock, not in-game time (timewarp).
            double msPerCam = _kerbcastFrameMs / (_lastCapturedCount > 0 ? _lastCapturedCount : 1);
            int before = _staggerController.Budget;
            /* Budgeted over the STREAMING set, not all attached and not the
               subset eligible this tick: the count is the controller's budget
               ceiling, so feeding it a pacing-thinned number would clamp the
               budget to the smallest momentary dip and then make it climb back
               one step per release dwell. _fpsAvg feeds the one-way
               physics-floor safety (MinKspFps). */
            int budget = _staggerController.Evaluate(
                _kerbcastFrameMs, msPerCam, _streamSubscribedCount, _fpsAvg, Time.unscaledTime);
            if (budget != before)
                Debug.Log($"[Kerbcast] stagger budget={budget}/{_streamSubscribedCount} streaming "
                    + $"(kerbcast {_kerbcastFrameMs:F1}ms, {msPerCam:F1}ms/cam, KSP {_fpsAvg:F0}fps, "
                    + $"max {_settings.MaxKerbcastFrameBudgetMs:F0}ms, floor {_settings.MinKspFps:F0}fps) "
                    + $"[{_staggerController.LastChangeReason}]");

            // Opt-in quality ladder, fed the stagger controller's own signals.
            // The budget passed is the stagger budget BEFORE the MaxCaptureFps
            // rate cap: the cap is user config, not load, and must not block a
            // promote. null when AdaptiveQuality=false (nothing evaluated).
            if (_qualityController != null)
            {
                int qBefore = _qualityController.Level;
                int qLevel = _qualityController.Evaluate(
                    _kerbcastFrameMs, budget, _streamSubscribedCount, _fpsAvg, Time.unscaledTime);
                if (qLevel != qBefore)
                {
                    Debug.Log($"[Kerbcast] stagger quality level={qLevel}/{KerbcastCamera.MaxShedLevel} "
                        + $"({_qualityController.LastChangeReason}; kerbcast {_kerbcastFrameMs:F1}ms, "
                        + $"KSP {_fpsAvg:F0}fps, budget {budget}/{_streamSubscribedCount}, "
                        + $"max {_settings.MaxKerbcastFrameBudgetMs:F0}ms, floor {_settings.MinKspFps:F0}fps)");
                    for (int i = 0; i < _cameras.Count; i++)
                        _cameras[i].ApplyAutoShed(qLevel);
                }
            }
        }

        // Build the stagger budget regulator from settings. MaxKerbcastFrameBudgetMs
        // <= 0 means "no ms cap" — a huge budget so cost never triggers a cut
        // (capture then bounded only by MaxCaptureFps + the MinKspFps floor).
        private static StaggerBudgetController BuildStaggerController(KerbcastSettings s)
        {
            double budgetMs = s.MaxKerbcastFrameBudgetMs > 0f ? s.MaxKerbcastFrameBudgetMs : 1e6;
            return new StaggerBudgetController(
                budgetMs,
                initialBudget: 1,
                minKspFps: s.MinKspFps);
        }

        // Build the opt-in quality ladder from the same targets as the stagger
        // regulator (the two share the ms budget and the fps floor, so demote
        // engages exactly where staggering runs out of room). Level 0 is the
        // configured Width/Height/layers; the ladder never goes above it.
        private static AdaptiveQualityController BuildQualityController(KerbcastSettings s)
        {
            double budgetMs = s.MaxKerbcastFrameBudgetMs > 0f ? s.MaxKerbcastFrameBudgetMs : 1e6;
            return new AdaptiveQualityController(
                enabled: true,
                maxLevel: KerbcastCamera.MaxShedLevel,
                budgetMs: budgetMs,
                minKspFps: s.MinKspFps);
        }

        private void OnDestroy()
        {
            if (_disabled) return;
            if (Instance == this)
            {
                Instance = null;
                KerbcastApiImpl.Stop();
            }
            EndSceneLock();
            CommandAuthority.WorldModel.Close();

            // ALWAYS restore the main flight cameras on scene exit so a
            // revert / quit-to-KSC / quit-to-main-menu doesn't leave KSP
            // with a dead viewport. This is unconditional even if the
            // operator never enabled throttle this session — a no-op in
            // that case.
            RestoreMainScreen();

            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onPartDestroyed.Remove(OnPartDestroyed);
            GameEvents.onVesselWasModified.Remove(OnVesselWasModified);
            GameEvents.onVesselCrewWasModified.Remove(OnVesselCrewWasModified);
            GameEvents.onGameSceneLoadRequested.Remove(OnGameSceneLoadRequested);
            GameEvents.onGameStatePostLoad.Remove(OnGameStatePostLoad);
            GameEvents.onVesselPersistentIdChanged.Remove(OnVesselPersistentIdChanged);
            foreach (var cam in _cameras) cam.Dispose();
            _cameras.Clear();
            /* Deliberately NOT stopping the sidecar: KerbcastSidecarHost owns
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
                Debug.Log("[Kerbcast] tore down AsyncReadbackUpdater (readback pump) on scene exit");
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
                Debug.LogWarning($"[Kerbcast] status file delete failed: {ex.Message}");
            }
        }
    }
}
