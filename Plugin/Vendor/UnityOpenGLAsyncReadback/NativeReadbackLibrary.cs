/* kerbcast addition (not upstream). Loads libAsyncGPUReadbackPlugin.so from an
   absolute path inside GameData and binds every entry point through dlsym, so
   nothing depends on Mono's or Unity's native-library search paths (neither
   looks in GameData, and CKAN installs only there). Unity-free on purpose: the
   headless loader test compiles this file on its own. */

using System;
using System.Runtime.InteropServices;

namespace Yangrc.OpenGLAsyncReadback {
    internal static class NativeReadbackLibrary {
        public const string FileName = "libAsyncGPUReadbackPlugin.so";

        private const int RTLD_NOW = 2;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int RequestTextureMainThreadFn(int texture, int miplevel);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int RequestComputeBufferMainThreadFn(int bufferId, int bufferSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr GetFunctionPtrFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void UpdateMainThreadFn();
        /* size_t length, not int: the upstream DllImport passed `ref int` and
           the native side wrote 8 bytes into it. */
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void GetDataFn(int eventId, ref IntPtr buffer, ref UIntPtr length);
        /* C++ bool is one byte; the default marshalling reads four. */
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal delegate bool TaskQueryFn(int eventId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int GlStateFn();

        internal static RequestTextureMainThreadFn RequestTextureMainThread;
        internal static RequestComputeBufferMainThreadFn RequestComputeBufferMainThread;
        internal static GetFunctionPtrFn GetKickstartFunctionPtr;
        internal static GetFunctionPtrFn GetUpdateRenderThreadFunctionPtr;
        internal static UpdateMainThreadFn UpdateMainThread;
        internal static GetDataFn GetData;
        internal static TaskQueryFn TaskError;
        internal static TaskQueryFn TaskExists;
        internal static TaskQueryFn TaskDone;
        /* 0 = render thread has not run yet, 1 = GL ready, -1 = GL entry points unresolved. */
        internal static GlStateFn GlState;

        public static bool IsLoaded { get; private set; }
        public static string LoadedPath { get; private set; }

        /// <summary>
        /// Load the library from <paramref name="path"/> and bind every entry
        /// point. Idempotent once it has succeeded. On failure nothing is
        /// bound and <paramref name="error"/> says why (including dlerror()).
        /// </summary>
        public static bool TryLoad(string path, out string error) {
            error = null;
            if (IsLoaded) return true;
            if (!System.IO.File.Exists(path)) {
                error = "file not found";
                return false;
            }

            IntPtr handle;
            try {
                handle = Dl.Open(path, RTLD_NOW);
            } catch (Exception ex) {
                error = "cannot call dlopen: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            if (handle == IntPtr.Zero) {
                error = "dlopen failed: " + Dl.LastError();
                return false;
            }

            try {
                RequestTextureMainThread = Bind<RequestTextureMainThreadFn>(handle, "RequestTextureMainThread");
                RequestComputeBufferMainThread = Bind<RequestComputeBufferMainThreadFn>(handle, "RequestComputeBufferMainThread");
                GetKickstartFunctionPtr = Bind<GetFunctionPtrFn>(handle, "GetKickstartFunctionPtr");
                GetUpdateRenderThreadFunctionPtr = Bind<GetFunctionPtrFn>(handle, "GetUpdateRenderThreadFunctionPtr");
                UpdateMainThread = Bind<UpdateMainThreadFn>(handle, "UpdateMainThread");
                GetData = Bind<GetDataFn>(handle, "GetData");
                TaskError = Bind<TaskQueryFn>(handle, "TaskError");
                TaskExists = Bind<TaskQueryFn>(handle, "TaskExists");
                TaskDone = Bind<TaskQueryFn>(handle, "TaskDone");
                GlState = Bind<GlStateFn>(handle, "KerbcastGlState");
            } catch (Exception ex) {
                Unbind();
                error = ex is EntryPointNotFoundException ? ex.Message : "cannot bind: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            LoadedPath = path;
            IsLoaded = true;
            return true;
        }

        private static void Unbind() {
            RequestTextureMainThread = null;
            RequestComputeBufferMainThread = null;
            GetKickstartFunctionPtr = null;
            GetUpdateRenderThreadFunctionPtr = null;
            UpdateMainThread = null;
            GetData = null;
            TaskError = null;
            TaskExists = null;
            TaskDone = null;
            GlState = null;
        }

        private static T Bind<T>(IntPtr handle, string name) where T : class {
            var fn = Dl.Sym(handle, name);
            if (fn == IntPtr.Zero)
                throw new EntryPointNotFoundException("missing export " + name + ": " + Dl.LastError());
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        /* dlopen lives in libdl.so.2 on every glibc (a stub forwarding to libc
           from 2.34); libc.so.6 is the fallback for runtimes without it. */
        private static class Dl {
            private static bool _useLibc;

            /* dlerror() is called once first so Mono resolves that binding now:
               resolving it lazily after a failure runs Mono's own probes, which
               overwrite the error we want to report. It also clears stale state. */
            public static IntPtr Open(string path, int flags) {
                if (!_useLibc) {
                    try {
                        LibDl.dlerror();
                        return LibDl.dlopen(path, flags);
                    }
                    catch (DllNotFoundException) { _useLibc = true; }
                    catch (EntryPointNotFoundException) { _useLibc = true; }
                }
                LibC.dlerror();
                return LibC.dlopen(path, flags);
            }

            public static IntPtr Sym(IntPtr handle, string name) {
                return _useLibc ? LibC.dlsym(handle, name) : LibDl.dlsym(handle, name);
            }

            public static string LastError() {
                var p = _useLibc ? LibC.dlerror() : LibDl.dlerror();
                return p == IntPtr.Zero ? "(no dlerror)" : Marshal.PtrToStringAnsi(p);
            }
        }

        private static class LibDl {
            [DllImport("libdl.so.2")] public static extern IntPtr dlopen(string file, int mode);
            [DllImport("libdl.so.2")] public static extern IntPtr dlsym(IntPtr handle, string name);
            [DllImport("libdl.so.2")] public static extern IntPtr dlerror();
        }

        private static class LibC {
            [DllImport("libc.so.6")] public static extern IntPtr dlopen(string file, int mode);
            [DllImport("libc.so.6")] public static extern IntPtr dlsym(IntPtr handle, string name);
            [DllImport("libc.so.6")] public static extern IntPtr dlerror();
        }
    }
}
