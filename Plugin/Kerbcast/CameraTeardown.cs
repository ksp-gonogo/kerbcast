using System;
using System.IO;

namespace Kerbcast
{
    /* Unity-free teardown decisions for KerbcastCore: whether a camera leaving
       the tracked set leaves a destroyed tombstone, and which ring-dir files
       belong to a camera rather than to the session. */
    public sealed class CameraTeardown
    {
        private bool _sceneUnloading;

        /* KSP destroys every part when a scene unloads (revert, quickload,
           leaving flight) and fires onPartDestroyed for each, interleaved with
           KerbcastCore.OnDestroy in no fixed order. Called from
           onGameSceneLoadRequested, which fires before any of that, so later
           part destructions are treated as normal teardowns. */
        public void OnSceneLoadRequested()
        {
            _sceneUnloading = true;
        }

        /// <summary>
        /// True when a camera disposed now should write a <c>lifecycle: "destroyed"</c>
        /// tombstone (a genuine in-flight loss); false once the scene is unloading.
        /// </summary>
        public bool WriteTombstone => !_sceneUnloading;

        private static readonly string[] PerCameraSuffixes = { ".ring", ".info.json", ".control.bin" };

        /// <summary>
        /// Whether <paramref name="fileName"/> is a <c>&lt;flightId&gt;.ring</c>,
        /// <c>.info.json</c> or <c>.control.bin</c> file. Session files such as
        /// <c>global.inflight</c> never match.
        /// </summary>
        public static bool IsPerCameraFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            foreach (var suffix in PerCameraSuffixes)
            {
                if (!fileName.EndsWith(suffix, StringComparison.Ordinal)) continue;
                string stem = fileName.Substring(0, fileName.Length - suffix.Length);
                return IsFlightId(stem);
            }
            return false;
        }

        private static bool IsFlightId(string stem)
        {
            if (stem.Length == 0) return false;
            foreach (char c in stem)
                if (c < '0' || c > '9') return false;
            return uint.TryParse(stem, out _);
        }

        /* Delete per-camera files left by an earlier flight scene (a crash, or
           tombstones the sidecar never acknowledged). Run at flight start
           before any camera exists, so nothing live can match. Returns the
           number of files removed; a missing directory or a locked file is
           skipped rather than thrown. */
        public static int SweepStaleCameraFiles(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            int removed = 0;
            foreach (var path in Directory.GetFiles(dir))
            {
                if (!IsPerCameraFile(Path.GetFileName(path))) continue;
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return removed;
        }
    }
}
