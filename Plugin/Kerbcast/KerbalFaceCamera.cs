/* Reporting-only kerbal face camera (Stage 3). Implements ICamera so
   KerbcastCore's capture loop tracks it uniformly, allocates a per-camera
   mmap ring and writes an info.json manifest, but captures no frames yet:
   Refresh/MarkFxDirty/ApplyAutoShed are no-ops. Frame capture lands in a
   later stage. Liveness is resolved from the owning part + persistentID
   each call; the ProtoCrewMember/Part refs are never assumed live. */

using System;
using System.IO;

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
        private bool _disposed;

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
            // Ring allocated at the global max even though no frames flow yet,
            // so a later capture stage can write without reallocating.
            _ring = MmapFrameRing.Create(_ringPath, ringSlots, width, height);
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

        // No subscription-driven capture this stage.
        public bool Subscribed => false;

        public int RefreshFailureStreak { get; set; }

        public bool OwnsPart(Part part) => part == _occupiedPart;

        public void MarkFxDirty() { /* no FX on a kerbal camera */ }

        public void Refresh(bool mayIssueReadback) { /* no frames this stage */ }

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
                UnityEngine.Debug.LogWarning($"[Kerbcast] kerbal cam={FlightId} info manifest write failed (lifecycle={lifecycle}): {ex.Message}");
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
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"[Kerbcast] kerbal cam={FlightId} destroyed manifest write failed: {ex.Message}"); }
            }

            _ring?.Dispose();
            try
            {
                if (File.Exists(_ringPath)) File.Delete(_ringPath);
                // On the destruction path the info.json is intentionally kept as
                // the tombstone the sidecar reads; the sidecar cleans it up.
                if (!destroyed && File.Exists(_infoPath)) File.Delete(_infoPath);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Kerbcast] kerbal cam={FlightId} ring file delete failed: {ex.Message}");
            }
        }
    }
}
