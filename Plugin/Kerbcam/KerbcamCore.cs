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
//   - GameEvents.onVesselChange: rebuild the camera list (which
//               creates/destroys the matching ring files).
//   - LateUpdate: refresh each tracked camera.
//   - OnDestroy: tear down cameras (each disposes its own ring + deletes
//               its ring file) and stop the sidecar.

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
                        _cameras.Add(new KerbcamCamera(
                            hullcam,
                            flightId,
                            RingDir,
                            RingSlots,
                            _settings.Width,
                            _settings.Height,
                            renderW,
                            renderH,
                            initialLayers));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Kerbcam] failed to attach to {part.name}: {ex}");
                    }
                    moduleIdx++;
                }
            }
            Debug.Log($"[Kerbcam] tracking {_cameras.Count} Hullcam VDS camera(s)");
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

        // LateUpdate so the Unity render cameras have finished compositing
        // the scene into our RenderTextures before we kick the readback.
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

            for (int i = 0; i < _cameras.Count; i++)
            {
                _cameras[i].Refresh();
            }

            MaybeWriteStatusFile();
        }

        // Warning overlay shown to the operator while the main flight
        // render is disabled. Top-centred so it doesn't compete with
        // the navball / staging / right-click panels. Tells them where
        // to look to undo, including the active hotkey if one is bound.
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
            const float h = 60f;
            float x = (Screen.width - w) * 0.5f;
            float y = 8f;
            GUI.Box(new Rect(x, y, w, h), msg, _throttleWarnStyle);
        }

        // -- throttle plumbing --

        private bool _throttleDesired;
        private bool _throttleEffective;
        private bool _mainCamerasDisabled;
        private GUIStyle _throttleWarnStyle;

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
                _mainCamerasDisabled = true;
                _throttleEffective = true;
                Debug.Log("[Kerbcam] main flight render disabled (ThrottleMainScreen=true)");
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
                _mainCamerasDisabled = false;
                _throttleEffective = false;
                Debug.Log("[Kerbcam] main flight render restored (ThrottleMainScreen=false)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcam] RestoreMainScreen failed: {ex}");
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
