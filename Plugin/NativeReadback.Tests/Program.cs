/* Headless check that NativeReadbackLibrary resolves the readback library from
   a plain GameData tree, with no copy on any Mono/Unity search path, and that
   a render-thread call with no GL context fails the task instead of crashing.

   Point KERBCAST_GAMEDATA at a GameData directory (for example an unzipped
   release). Unset, the harness SKIPs, since the library is Linux x86_64 only. */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Yangrc.OpenGLAsyncReadback;

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderEventFn(int eventId);

    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) _failures++;
    }

    private static int Main()
    {
        var gameData = Environment.GetEnvironmentVariable("KERBCAST_GAMEDATA");
        if (string.IsNullOrEmpty(gameData))
        {
            Console.WriteLine("SKIP: KERBCAST_GAMEDATA not set");
            return 0;
        }

        var path = Path.Combine(Path.GetFullPath(gameData),
            "Kerbcast", "Plugins", "linux-x64", NativeReadbackLibrary.FileName);
        Console.WriteLine("resolving " + path);

        string error;
        Check(!NativeReadbackLibrary.TryLoad(path + ".absent", out error) && error == "file not found",
            "a missing file is refused: " + error);
        Check(!NativeReadbackLibrary.IsLoaded, "nothing is bound after a refused load");

        /* A file that exists but is not a library: dlopen's own reason must
           come through, not text left over from Mono resolving dlerror. */
        var notElf = Path.Combine(Path.GetTempPath(), "kerbcast-not-a-library.so");
        File.WriteAllText(notElf, "not an ELF file");
        bool badLoaded = NativeReadbackLibrary.TryLoad(notElf, out error);
        Check(!badLoaded && error != null && error.StartsWith("dlopen failed: ") && error.Contains(notElf),
            "a non-library is refused with dlopen's reason: " + error);
        Check(!NativeReadbackLibrary.IsLoaded && NativeReadbackLibrary.TaskDone == null,
            "nothing is bound after a failed dlopen");
        File.Delete(notElf);

        bool loaded = NativeReadbackLibrary.TryLoad(path, out error);
        Check(loaded, "loads from GameData: " + (error ?? "ok"));
        if (!loaded) return 1;
        Check(NativeReadbackLibrary.LoadedPath == path, "LoadedPath is the GameData path");

        Check(NativeReadbackLibrary.GlState() == 0, "GL untried before any render-thread call");
        Check(!NativeReadbackLibrary.TaskExists(4242), "unknown task does not exist");
        Check(NativeReadbackLibrary.TaskDone(4242), "unknown task reads as done");

        int id = NativeReadbackLibrary.RequestTextureMainThread(0, 0);
        Check(id > 0, "texture request returns a handle: " + id);
        Check(NativeReadbackLibrary.TaskExists(id), "requested task exists");
        Check(!NativeReadbackLibrary.TaskDone(id), "requested task is pending");

        var kick = NativeReadbackLibrary.GetKickstartFunctionPtr();
        var update = NativeReadbackLibrary.GetUpdateRenderThreadFunctionPtr();
        Check(kick != IntPtr.Zero && update != IntPtr.Zero, "render-event function pointers resolve");

        /* No GL context here, as if Unity never initialised the plugin. The
           patched library must mark the task failed, not call a null GLEW
           pointer. */
        var kickFn = (RenderEventFn)Marshal.GetDelegateForFunctionPointer(kick, typeof(RenderEventFn));
        kickFn(id);
        Check(NativeReadbackLibrary.GlState() == -1, "GL reported unusable without a context");
        Check(NativeReadbackLibrary.TaskDone(id) && NativeReadbackLibrary.TaskError(id),
            "task fails cleanly without GL");

        IntPtr buffer = IntPtr.Zero;
        UIntPtr length = UIntPtr.Zero;
        NativeReadbackLibrary.GetData(id, ref buffer, ref length);
        Check(buffer == IntPtr.Zero, "a failed task yields no data");
        NativeReadbackLibrary.UpdateMainThread();

        Console.WriteLine(_failures == 0 ? "all passed" : _failures + " failed");
        return _failures == 0 ? 0 : 1;
    }
}
