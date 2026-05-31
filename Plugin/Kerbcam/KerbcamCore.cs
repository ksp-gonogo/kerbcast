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
//               Ensure the rings directory exists, then spawn sidecar
//               pointed at that directory.
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
//               disposes its own ring + deletes its files) and stop the
//               sidecar.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
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

        private KerbcamSettings _settings;
        private readonly List<KerbcamCamera> _cameras = new List<KerbcamCamera>();
        private Process _sidecar;

        // Status file (sidecar ← plugin). Rewritten ~1Hz when anything has
        // changed — KSP fps, shed level, per-camera effective layers / dims.
        // Sidecar reads, diffs, and pushes camera-state-changed +
        // adaptive-shed messages to all peers' control channels.
        private string _statusPath;
        private float _statusCooldown;
        private const float StatusWriteIntervalSeconds = 1.0f;

        // Rolling 60-sample FPS window for adaptive layer shedding.
        // ~1s at 60fps, ~2s at 30fps — long enough to ignore single-frame
        // hitches, short enough to react to sustained slow-downs.
        private const int FpsSamples = 60;
        private readonly float[] _fpsWindow = new float[FpsSamples];
        private int _fpsIdx;
        private int _fpsCount; // up to FpsSamples; growing-average until full
        private float _fpsAvg;
        private int _shedLevel;
        // Shed level transitions. Each pair = (escalate-below, restore-above)
        // for the transition from level N to level N+1. Hysteresis (~5 fps)
        // prevents flapping. See KerbcamCamera.ShedTable for what each level
        // actually applies — scaled is in level 5 so the trigger is severe.
        //
        // Levels:  0 → 1 → 2 → 3 → 4 → 5
        //
        // Transition: 0→1     1→2     2→3     3→4     4→5
        private static readonly float[] ShedBelow    = { 25f, 18f, 12f,  7f, 3f };
        private static readonly float[] RestoreAbove = { 30f, 23f, 17f, 12f, 7f };

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

            try
            {
                // yangrc's [RuntimeInitializeOnLoadMethod] hook never
                // fires for mod DLLs (KSP loads them post-init), so the
                // updater MonoBehaviour that pumps OpenGLAsyncReadbackRequest
                // never auto-attaches. Spawn it ourselves on a dedicated
                // DontDestroyOnLoad GameObject.
                if (AsyncReadbackUpdater.instance == null)
                {
                    var updaterGo = new GameObject("Kerbcam_AsyncReadbackUpdater");
                    UnityEngine.Object.DontDestroyOnLoad(updaterGo);
                    updaterGo.AddComponent<AsyncReadbackUpdater>();
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
            RebuildCameraList(FlightGlobals.ActiveVessel);

            // Throttle state seeded from the per-save Difficulty Setting
            // (which itself seeds from settings.cfg on a fresh save). We
            // poll it every LateUpdate rather than wire to a settings-
            // changed GameEvent because KSP doesn't ship one for
            // CustomParameterNode value changes.
            _throttleDesired = ReadThrottleDesired();
            _throttleEffective = _throttleDesired;
            if (_throttleEffective) ApplyMainScreenThrottle();

            TryStartSidecar();
        }

        // Spawn the bundled sidecar binary if one is shipped alongside the
        // plugin. Missing or non-executable binary is logged at warn but
        // doesn't fail Awake — the operator can still launch the sidecar
        // manually from another shell, which is how kerbcam was originally
        // built and remains the dev workflow.
        private void TryStartSidecar()
        {
            try
            {
                if (_settings != null && !_settings.AutoSpawnSidecar)
                {
                    Debug.Log("[Kerbcam] AutoSpawnSidecar=false; sidecar must be launched manually");
                    return;
                }

                if (_sidecar != null && !_sidecar.HasExited)
                {
                    // Flight scene re-entered while a previous sidecar is
                    // still running — leave it alone.
                    return;
                }

                var binPath = ResolveSidecarBinary();
                if (binPath == null)
                {
                    Debug.LogWarning("[Kerbcam] no bundled sidecar binary found; launch ~/personal/kerbcam/sidecar manually if you need streaming");
                    return;
                }

                // CLI flags forwarded from settings.cfg. The sidecar
                // accepts every value we care about as a long-form arg,
                // so there's no config file to keep in sync on the
                // sidecar side.
                var args =
                    $"--shm-dir \"{RingDir}\" " +
                    $"--http-bind {_settings.HttpBind} " +
                    $"--max-width {_settings.Width} " +
                    $"--max-height {_settings.Height}";

                var psi = new ProcessStartInfo
                {
                    FileName = binPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                // Self-contained sidecar deployment: CI bundles the ffmpeg
                // shared libs (libavutil/libavcodec/etc) the binary links
                // against into a sibling lib/ directory, because SteamOS
                // doesn't ship those .so files. Prepend lib/ to
                // LD_LIBRARY_PATH so the dynamic linker finds the bundled
                // copies before falling back to the system path (for libva
                // + its GPU-driver shims, which we intentionally do NOT
                // bundle — those have to match the host's Mesa stack).
                //
                // Gated on Directory.Exists so the dev workflow (manual
                // sidecar launch, no bundled lib dir) and macOS dev (which
                // never hits this code path because binPath doesn't resolve
                // there either) stay harmless.
                //
                // EnvironmentVariables is the cross-platform setter on
                // .NET48/Mono despite the MSDN page tagging it Windows-only.
                var libDir = Path.Combine(Path.GetDirectoryName(binPath) ?? string.Empty, "lib");
                if (Directory.Exists(libDir))
                {
                    var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
                    psi.EnvironmentVariables["LD_LIBRARY_PATH"] =
                        string.IsNullOrEmpty(existing) ? libDir : libDir + ":" + existing;
                    Debug.Log($"[Kerbcam] LD_LIBRARY_PATH prepend: {libDir}");
                }

                _sidecar = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _sidecar.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) Debug.Log($"[Kerbcam.sidecar] {e.Data}");
                };
                _sidecar.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) Debug.Log($"[Kerbcam.sidecar] {e.Data}");
                };
                _sidecar.Exited += (sender, e) =>
                {
                    Debug.LogWarning($"[Kerbcam] sidecar exited (code {_sidecar.ExitCode})");
                };

                _sidecar.Start();
                _sidecar.BeginOutputReadLine();
                _sidecar.BeginErrorReadLine();
                Debug.Log($"[Kerbcam] sidecar started pid={_sidecar.Id} from {binPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcam] failed to start sidecar: {ex}");
                _sidecar = null;
            }
        }

        private static string ResolveSidecarBinary()
        {
            // Bundled location: GameData/Kerbcam/sidecar/kerbcam-sidecar
            // KSPUtil.ApplicationRootPath is the KSP install root.
            var bundled = Path.Combine(
                KSPUtil.ApplicationRootPath,
                "GameData", "Kerbcam", "sidecar", "kerbcam-sidecar");
            if (File.Exists(bundled)) return bundled;
            return null;
        }

        private void StopSidecar()
        {
            if (_sidecar == null) return;
            try
            {
                if (!_sidecar.HasExited)
                {
                    _sidecar.Kill();
                    _sidecar.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] sidecar stop threw: {ex.Message}");
            }
            finally
            {
                _sidecar.Dispose();
                _sidecar = null;
            }
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

            // Re-apply the throttle we restored above. ReadThrottleDesired
            // is the source of truth — honour the operator's current
            // setting in case they toggled it during the rebuild window.
            if (wasThrottled && ReadThrottleDesired())
            {
                Debug.Log("[Kerbcam] rebuild: re-applying main screen throttle");
                ApplyMainScreenThrottle();
            }
        }

        // Hotkey poll. Update runs once per frame; cheap to scan a key
        // here, and Input.GetKeyDown only fires the tick a key first
        // goes down so there's no autorepeat risk.
        private void Update()
        {
            if (KerbcamSettings.ThrottleMainScreenKey != KeyCode.None &&
                Input.GetKeyDown(KerbcamSettings.ThrottleMainScreenKey))
            {
                ToggleThrottleViaHotkey();
            }
        }

        // LateUpdate drives explicit camera.Render() calls via each
        // KerbcamCamera.Refresh(). Our offscreen cameras are permanently
        // disabled (enabled=false) so they never fire during Unity's normal
        // render pass — we own the render timing entirely.
        private void LateUpdate()
        {
            UpdateFpsAverage();
            // Shedding can be disabled via settings.cfg's EnableAdaptiveShed
            // for perf-comparison runs where we want raw per-camera cost
            // without the cascade kicking in.
            if (_settings.EnableAdaptiveShed) ApplyAdaptiveShedding();

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

            for (int i = 0; i < _cameras.Count; i++)
            {
                _cameras[i].Refresh();
            }

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

            string keyHint = KerbcamSettings.ThrottleMainScreenKey != KeyCode.None
                ? $" or press [{KerbcamSettings.ThrottleMainScreenKey}]"
                : " (no hotkey bound; set ThrottleMainScreenKey in settings.cfg)";
            string msg =
                "Main flight camera disabled by kerbcam to free GPU for camera streams. " +
                $"Go to Pause → Difficulty Settings → Kerbcam{keyHint} to restore.";

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
        private void ToggleThrottleViaHotkey()
        {
            bool next = !_throttleEffective;
            try
            {
                var game = HighLogic.CurrentGame;
                if (game?.Parameters != null)
                {
                    var node = game.Parameters.CustomParams<KerbcamGameParameters>();
                    if (node != null) node.ThrottleMainScreen = next;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam] hotkey CustomParams write failed: {ex.Message}");
            }
            _throttleDesired = next;
            if (next) ApplyMainScreenThrottle();
            else RestoreMainScreen();
            Debug.Log($"[Kerbcam] ThrottleMainScreen toggled via hotkey → {next}");
        }

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
                sb.Append($"  \"shedLevel\": {_shedLevel},\n");
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
                sb.Append("  ]\n");
                sb.Append("}\n");

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

        // Per-tick: decide whether to escalate / de-escalate the shed
        // level given the rolling fps average. Hysteresis between shed
        // and restore thresholds prevents flapping. Skips entirely until
        // the window has filled — early frames after a scene load are
        // noisy and would trigger spurious sheds.
        private void ApplyAdaptiveShedding()
        {
            if (_fpsCount < FpsSamples) return;

            int desired = _shedLevel;
            int maxLevel = KerbcamCamera.MaxShedLevel;
            // Escalate one level if we're below this transition's shed
            // threshold; de-escalate one level if we're above the
            // previous transition's restore threshold. One step per tick
            // so the system doesn't lurch through multiple resolution
            // changes in a single LateUpdate.
            if (_shedLevel < maxLevel && _fpsAvg < ShedBelow[_shedLevel])
            {
                desired = _shedLevel + 1;
            }
            else if (_shedLevel > 0 && _fpsAvg > RestoreAbove[_shedLevel - 1])
            {
                desired = _shedLevel - 1;
            }

            if (desired == _shedLevel) return;

            _shedLevel = desired;
            Debug.Log($"[Kerbcam] adaptive shed level={_shedLevel} (avg fps={_fpsAvg:F1})");
            foreach (var cam in _cameras) cam.ApplyAutoShed(_shedLevel);
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
            StopSidecar();

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
