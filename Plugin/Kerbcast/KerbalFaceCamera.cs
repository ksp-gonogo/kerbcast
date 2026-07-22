/* Kerbal face camera. Implements ICamera so KerbcastCore's capture loop
   tracks it uniformly, allocates a per-camera mmap ring and writes an
   info.json manifest. When a peer subscribes (via the shared-memory control
   block) it renders KSP's IVA portrait camera into a square ring frame
   (min(512, tier width, tier height) per side, so sub-512 tiers still fit)
   each tick through the shared CaptureCore tail. Liveness is resolved from
   the owning part + persistentID each call; the ProtoCrewMember/Part refs are
   never assumed live, and the IVA avatar (KerbalRef) is re-resolved every
   tick rather than cached. */

using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class KerbalFaceCamera : ICamera
    {
        public uint FlightId { get; }

        private readonly ProtoCrewMember _pcm;
        private readonly Part _occupiedPart;
        // Identity snapshot at construction, mirroring KerbcastCamera: the
        // manifest writers stay safe even when the part is dying by teardown.
        private readonly uint _persistentId;
        private readonly string _cachedCameraName;
        private readonly string _cachedVesselName;

        private readonly MmapFrameRing _ring;
        private readonly string _ringPath;
        private readonly string _infoPath;
        private readonly string _controlPath;
        private bool _disposed;

        // Square capture side: min(512, tier width, tier height), so the ring
        // and the capture RT can never drift apart. Below-512 tiers (e.g. low
        // at 640x360) previously produced 512x512 frames into a ring sized for
        // the tier, throwing on every frame; this clamps the capture itself
        // down to whatever the tier actually allows.
        private readonly int _captureDim;

        // Shared-memory control block written by the sidecar. Opened lazily
        // once the file appears (mirrors KerbcastCamera). Kerbal cameras only
        // read the subscription flag: no pan/zoom/layers.
        private ControlBlock _controlBlock;
        private bool _subscribed;

        // Reusable capture tail: pooled capture/readback RT pair + in-flight
        // readback bookkeeping + ring write. Built lazily on first subscribe at
        // 512x512, reused across ticks, disposed at teardown. Shared impl with
        // KerbcastCamera so the streaming path stays single-source.
        private CaptureCore _capture;
        private readonly PhaseTimings _phaseTimings = new PhaseTimings();
        private int _consecutiveErrors;

        // KSP nulls Canvas.willRenderCanvases (static private) around a manual
        // portrait Camera.Render so a re-entrant canvas render can't fire
        // mid-render; mirror that here. FieldInfo cached once for the process.
        private static readonly FieldInfo _willRenderCanvasesField =
            typeof(Canvas).GetField("willRenderCanvases", BindingFlags.Static | BindingFlags.NonPublic);

        public KerbalFaceCamera(
            ProtoCrewMember pcm,
            Part occupiedPart,
            string ringDir,
            int ringSlots,
            int width,
            int height)
        {
            _pcm = pcm;
            _occupiedPart = occupiedPart;
            FlightId = CameraId.KerbalWireId(pcm.persistentID);
            _persistentId = pcm.persistentID;
            _cachedCameraName = pcm.displayName;
            var vessel = occupiedPart != null ? occupiedPart.vessel : null;
            _cachedVesselName = vessel != null
                ? (vessel.GetDisplayName() ?? vessel.vesselName ?? "<unknown>")
                : "<unknown>";

            _ringPath = Path.Combine(ringDir, $"{FlightId}.ring");
            _infoPath = Path.Combine(ringDir, $"{FlightId}.info.json");
            _controlPath = Path.Combine(ringDir, $"{FlightId}.control.bin");
            _captureDim = Math.Min(512, Math.Min(width, height));
            // Ring sized to match the capture dim exactly: the capture stage
            // (EnsureCapture) renders at the same _captureDim, so ring and
            // frame can never disagree regardless of quality tier.
            _ring = MmapFrameRing.Create(_ringPath, ringSlots, _captureDim, _captureDim);
            WriteInfoManifest();
        }

        // Re-read live off the owning part; guard null so a torn-down part
        // reports no vessel rather than throwing.
        public Vessel Vessel => _occupiedPart != null ? _occupiedPart.vessel : null;

        // Alive == the kerbal is still seated in the owning part.
        public bool IsAlive =>
            _occupiedPart != null
            && _occupiedPart.vessel != null
            && _occupiedPart.protoModuleCrew.Contains(_pcm);

        // Peer-driven capture gate, backed by the control block's subscription
        // flag. Idle (unsubscribed) kerbal cameras do no render/readback work.
        public bool Subscribed => _subscribed;

        public int RefreshFailureStreak { get; set; }

        public bool OwnsPart(Part part) => part == _occupiedPart;

        public void MarkFxDirty() { /* no FX on a kerbal camera */ }

        // Poll the subscription flag off the control block. Opens the block
        // lazily (the sidecar creates the file); subscription-only, ignoring the
        // pan/zoom/layer fields a kerbal camera doesn't have.
        private void PollSubscription()
        {
            try
            {
                if (_controlBlock == null)
                {
                    _controlBlock = ControlBlock.Open(_controlPath, out var openRes);
                    if (openRes == ControlBlock.OpenResult.VersionMismatch)
                    {
                        Debug.LogError(
                            $"[Kerbcast] kerbal cam={FlightId} control-block layout version mismatch: "
                            + "sidecar and plugin are out of sync; capture disabled until they match");
                    }
                    if (_controlBlock == null) return; // file not ready yet
                }

                if (!_controlBlock.TryReadChanged(out var snap)) return;
                if (snap.Subscribed != _subscribed)
                {
                    _subscribed = snap.Subscribed;
                    Debug.Log($"[Kerbcast] kerbal cam={FlightId} subscribed → {_subscribed}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast] kerbal cam={FlightId} control block read failed: {ex.Message}");
            }
        }

        // Lazily build the capture tail on first subscribe. Filterless: a plain
        // Blit passed to Publish, minimal failure/reset callbacks (kerbal
        // cameras have no telemetry columns of their own). _captureDim matches
        // the ring exactly (both derived in the ctor), so capture never
        // exceeds the ring's slot capacity on any tier.
        private CaptureCore EnsureCapture()
        {
            if (_capture == null)
            {
                // Build into a local and only assign the field once BuildTargets
                // succeeds: a throw there (e.g. RenderTexture.Create() failing)
                // must leave _capture null so the next tick retries construction,
                // rather than latching a half-initialized capture tail.
                var capture = new CaptureCore(_ring, _phaseTimings, LogRateLimited, () => _consecutiveErrors = 0);
                capture.BuildTargets(_captureDim, _captureDim);
                _capture = capture;
            }
            return _capture;
        }

        private void LogRateLimited(string message)
        {
            // 1-in-300 frames at 30fps = at most one log per 10s per camera.
            if (_consecutiveErrors == 0 || _consecutiveErrors % 300 == 0)
                Debug.Log($"[Kerbcast] kerbal cam={FlightId} {message}");
            _consecutiveErrors++;
        }

        public void Refresh(bool mayIssueReadback)
        {
            // Always drain a completed readback first, even while unsubscribed,
            // so a subscribe→unsubscribe race still lands its final frame.
            _capture?.Drain();

            PollSubscription();
            if (!_subscribed) return;

            // CaptureCore.Publish sets its in-flight flag BEFORE issuing the
            // AsyncGPUReadback request, which can throw (Deck/Mesa GPU-readback
            // quirks). Wrap the whole capture attempt so a throw anywhere in
            // here (including EnsureCapture's BuildTargets) aborts the in-flight
            // flag instead of leaving it stuck true, which would otherwise make
            // every later Refresh return early at the ReadbackInFlight guard
            // forever, silently freezing this camera. Mirrors KerbcastCamera.
            try
            {
                var capture = EnsureCapture();
                if (!mayIssueReadback) return;

                // One-in-flight invariant: CaptureCore.Publish assumes at most one
                // readback outstanding. If the previous one hasn't completed, skip
                // issuing another this tick (it drains via the Drain above).
                if (capture.ReadbackInFlight) return;

                // Re-resolve the live IVA avatar every tick; never cache it. A null
                // KerbalRef means the avatar isn't spawned (portrait not visible):
                // no frame this tick, but NOT death (IsAlive stays keyed on the seat).
                var k = _pcm.KerbalRef;
                if (k == null) return;

                // Prefer the seat's portrait camera (the IVA portrait KSP renders in
                // the crew tray); fall back to the kerbal's own cam.
                bool usedSeatCam = _pcm.seat != null && _pcm.seat.portraitCamera != null;
                var cam = usedSeatCam ? _pcm.seat.portraitCamera : k.kerbalCam;
                if (cam == null) return;

                var prevTarget = cam.targetTexture;
                cam.targetTexture = capture.CaptureRt;

                // Null Canvas.willRenderCanvases around the manual render (KSP's own
                // portrait path), restoring it (and the camera target) in finally.
                object canvasCb = null;
                bool nulledCanvas = false;
                if (_willRenderCanvasesField != null)
                {
                    canvasCb = _willRenderCanvasesField.GetValue(null);
                    _willRenderCanvasesField.SetValue(null, null);
                    nulledCanvas = true;
                }
                try
                {
                    // Seat portrait uses RenderDontRestore (KSP's kerbalSeatCamUpdate
                    // path); the fallback kerbal cam uses a plain Render.
                    if (usedSeatCam) cam.RenderDontRestore();
                    else cam.Render();
                }
                finally
                {
                    if (nulledCanvas) _willRenderCanvasesField.SetValue(null, canvasCb);
                    cam.targetTexture = prevTarget;
                }

                // Plain blit (no Hullcam filter); CaptureCore applies the flip and
                // issues the readback under the one-in-flight invariant.
                capture.Publish(Time.unscaledTime * 1000.0, (c, r) => Graphics.Blit(c, r));
            }
            catch (Exception ex)
            {
                _capture?.AbortInFlight();
                LogRateLimited($"capture pipeline threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void ApplyAutoShed(int level) { /* no adaptive quality this stage */ }

        // Same hand-rolled JSON shape as KerbcastCamera's active manifest, with
        // kerbal-specific fields. part_name/part_title empty, all fov/pan
        // numerics 0, supports_* false; adds kind, kerbal_persistent_id and
        // crew_location so the sidecar can distinguish a face camera.
        public void WriteInfoManifest()
        {
            WriteManifest("active");
        }

        private void WriteManifest(string lifecycle)
        {
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var json = "{\n"
                    + $"  \"lifecycle\": \"{lifecycle}\",\n"
                    + "  \"kind\": \"kerbal\",\n"
                    + $"  \"flight_id\": {FlightId.ToString(inv)},\n"
                    + "  \"part_name\": \"\",\n"
                    + "  \"part_title\": \"\",\n"
                    + $"  \"camera_name\": \"{EscapeJson(_cachedCameraName)}\",\n"
                    + $"  \"vessel_name\": \"{EscapeJson(_cachedVesselName)}\",\n"
                    + "  \"supports_zoom\": false,\n"
                    + "  \"fov\": 0,\n"
                    + "  \"fov_min\": 0,\n"
                    + "  \"fov_max\": 0,\n"
                    + "  \"supports_pan\": false,\n"
                    + "  \"pan_yaw_min\": 0,\n"
                    + "  \"pan_yaw_max\": 0,\n"
                    + "  \"pan_pitch_min\": 0,\n"
                    + "  \"pan_pitch_max\": 0,\n"
                    + $"  \"kerbal_persistent_id\": {_persistentId.ToString(inv)},\n"
                    + "  \"crew_location\": \"seat\"\n"
                    + "}\n";
                File.WriteAllText(_infoPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast] kerbal cam={FlightId} info manifest write failed (lifecycle={lifecycle}): {ex.Message}");
            }
        }

        // Duplicated from KerbcastCamera (its copy is private); tiny + no shared
        // helper file yet.
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

        // Normal teardown (vessel change, scene exit, crew left seat): close the
        // ring and delete both ring + info files.
        public void Dispose()
        {
            DisposeCore(destroyed: false);
        }

        // Destruction-path teardown: write a lifecycle="destroyed" tombstone
        // BEFORE closing the ring so the sidecar can observe the transition,
        // then delete the ring but leave the info.json for the sidecar to read.
        public void DisposeDestroyed()
        {
            DisposeCore(destroyed: true);
        }

        private void DisposeCore(bool destroyed)
        {
            if (_disposed) return;
            _disposed = true;

            if (destroyed)
            {
                // Failure here must never block the cleanup path.
                try { WriteManifest("destroyed"); }
                catch (Exception ex) { Debug.LogWarning($"[Kerbcast] kerbal cam={FlightId} destroyed manifest write failed: {ex.Message}"); }
            }

            // Release the capture tail's pooled RTs before the ring: the camera
            // is torn down so no further readbacks issue.
            _capture?.Dispose();
            _ring?.Dispose();
            _controlBlock?.Dispose();
            try
            {
                if (File.Exists(_ringPath)) File.Delete(_ringPath);
                // On the destruction path the info.json is intentionally kept as
                // the tombstone the sidecar reads; the sidecar cleans it up.
                if (!destroyed && File.Exists(_infoPath)) File.Delete(_infoPath);
                if (File.Exists(_controlPath)) File.Delete(_controlPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcast] kerbal cam={FlightId} ring file delete failed: {ex.Message}");
            }
        }
    }
}
