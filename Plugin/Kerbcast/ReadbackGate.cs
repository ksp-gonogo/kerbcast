using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Yangrc.OpenGLAsyncReadback;

namespace Kerbcast
{
    /// <summary>
    /// Startup check that camera capture can read frames back from the GPU at
    /// all. Where Unity's AsyncGPUReadback is unsupported (Linux OpenGL) the
    /// bundled native library is required; without it every readback fails and
    /// a feed would stream an empty buffer. Kerbcast refuses to start instead.
    /// Decided once per KSP session; the error is logged once.
    /// </summary>
    internal static class ReadbackGate
    {
        private static bool? _ready;
        private static bool _glConfirmed;

        /* Kept in lockstep with release.yml's assemble step. */
        internal static string NativeLibraryPath => Path.Combine(
            KSPUtil.ApplicationRootPath,
            "GameData", "Kerbcast", "Plugins", "linux-x64", NativeReadbackLibrary.FileName);

        internal static bool EnsureReady()
        {
            if (!_ready.HasValue)
            {
                try
                {
                    _ready = Check();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Kerbcast] cannot start: the camera readback check failed ({ex.GetType().Name}: {ex.Message}). Kerbcast is disabled for this session: no sidecar, no cameras.");
                    _ready = false;
                }
            }
            return _ready.Value;
        }

        /// <summary>
        /// True once the native library has reported that it cannot use GL on
        /// the render thread, which leaves every readback failing. Logs once and
        /// marks the session as not ready, so later flight scenes refuse to start.
        /// Cheap after GL is confirmed working: it stops asking.
        /// </summary>
        internal static bool GlUnusable()
        {
            if (_glConfirmed || !NativeReadbackLibrary.IsLoaded) return false;
            int state;
            try
            {
                state = NativeReadbackLibrary.GlState();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast] stopping: the native readback library could not report its GL state ({ex.GetType().Name}: {ex.Message}). Kerbcast is disabled for this session: no sidecar, no cameras.");
                _ready = false;
                return true;
            }
            if (state == 1)
            {
                _glConfirmed = true;
                return false;
            }
            if (state == -1)
            {
                Debug.LogError($"[Kerbcast] stopping: the native readback library loaded from {NativeReadbackLibrary.LoadedPath} could not initialise OpenGL, so no camera frame can be captured. Kerbcast is disabled for this session: no sidecar, no cameras.");
                _ready = false;
                return true;
            }
            return false;
        }

        private static bool Check()
        {
            var api = SystemInfo.graphicsDeviceType;
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                Debug.Log($"[Kerbcast] readback: using Unity AsyncGPUReadback ({api})");
                return true;
            }

            if (api == GraphicsDeviceType.OpenGLCore
                && Application.platform == RuntimePlatform.LinuxPlayer)
            {
                var path = NativeLibraryPath;
                if (NativeReadbackLibrary.TryLoad(path, out var error))
                {
                    Debug.Log($"[Kerbcast] readback: loaded native OpenGL readback library from {path}");
                    return true;
                }
                Debug.LogError($"[Kerbcast] cannot start: camera capture on OpenGL needs {path}, which could not be loaded ({error}). Reinstall Kerbcast so that file is present. Kerbcast is disabled for this session: no sidecar, no cameras.");
                return false;
            }

            Debug.LogError($"[Kerbcast] cannot start: {api} on {Application.platform} has no asynchronous GPU readback, and Kerbcast's native readback library only covers Linux OpenGL. Kerbcast is disabled for this session: no sidecar, no cameras.");
            return false;
        }
    }
}
