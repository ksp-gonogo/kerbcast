# Vendored: UnityOpenGLAsyncReadback

Vendored copy of `Yangrc.OpenGLAsyncReadback` from
https://github.com/yangrc1234/UnityOpenGLAsyncReadback (MIT licensed,
copyright 2018 Aurélien Labate and 2019 yangrc — see `LICENSE`).

Provides true asynchronous GPU→CPU readback on Unity's OpenGL backend,
where Unity's own `AsyncGPUReadback` API is a no-op
(`SystemInfo.supportsAsyncGPUReadback == false`). The public C# entry
point is
`Yangrc.OpenGLAsyncReadback.UniversalAsyncGPUReadbackRequest.Request(...)`,
which dispatches at runtime to:

- Unity's native `AsyncGPUReadback` when supported (D3D11 / Vulkan / Metal).
- The native `libAsyncGPUReadbackPlugin.so` otherwise. Kerbcast only ships and
  loads it for Linux OpenGL; see "Native library" below.

## Files

- `AsyncGPUReadbackPlugin.cs` — C# wrapper + DllImports.
- `AsyncReadbackUpdater.cs` — `MonoBehaviour` that pumps pending readbacks
  every frame. `[RuntimeInitializeOnLoadMethod]` is unreliable in KSP
  (Unity loads mod DLLs after that hook fires), so the kerbcast plugin
  spawns this manually on first use.
- `NativeReadbackLibrary.cs` (kerbcast addition): loads the native library by
  absolute path and binds its entry points with dlsym.
- `native/`: build, check and smoke-test scripts for the native library.
- `LICENSE` — upstream MIT license, preserved verbatim.

## Native library

`libAsyncGPUReadbackPlugin.so` ships from v1.10.1 onward at
`GameData/Kerbcast/Plugins/linux-x64/` (earlier releases never included it). CI builds it (`release.yml`, job
`build-native-readback`) with `native/build.sh` from upstream commit
`aff05c25f2c12e929d009948ebf5e84bcd1ac21d` plus `native/kerbcast-self-init.patch`,
on ubuntu-22.04 so it needs no glibc newer than 2.35 (in practice 2.32) and
no GLIBCXX newer than 3.4.25 (in practice 3.4.11). `native/check.sh` (ELF
type, exports, glibc ceiling) and `native/gl_readback_smoke.c` (a real readback
on a GL core context) run on every build, and `native/check-zip.sh` refuses a
release zip without a valid copy.

Neither Mono nor Unity searches GameData for native libraries, and CKAN only
installs there, so `[DllImport]` cannot find it. `ReadbackGate` (in
`Plugin/Kerbcast/`) instead dlopens it by absolute path at startup, and
`NativeReadbackLibrary` binds every entry point with dlsym. If Unity's
AsyncGPUReadback is unsupported and the library cannot be loaded, Kerbcast logs
one error and does not start (no sidecar, no cameras). If it loads but reports
on the first render-thread call that it cannot use OpenGL, Kerbcast logs one
error and shuts down the same way.

The patch exists because Unity only calls `UnityPluginLoad` (where upstream runs
`glewInit`) for plugins it loads itself. Without it every GL call made through
GLEW would hit a null pointer, so the patched library initialises GLEW on the
render thread on first use, and fails tasks instead of crashing if GL is
unusable.

## Local modifications (kerbcast fork)

Upstream is unmaintained, so this copy is treated as a maintained fork. MIT
permits modification; the `LICENSE` and copyright notices are preserved
verbatim. Divergences from upstream, all clearly marked `kerbcast … (not
upstream)` in-source:

- **`AsyncGPUReadbackPlugin.cs`** — added zero-copy readback accessors so the
  OpenGL path can write the native plugin buffer straight into the frame ring,
  skipping `GetRawData`'s `Allocator.Temp` NativeArray + `MemMove` (one of two
  full-frame copies per readback, plus a per-frame Temp allocation):
  - `UniversalAsyncGPUReadbackRequest.TryGetRawPtr(out void*, out int)`
  - `OpenGLAsyncReadbackRequest.GetRawDataPtr(out void*, out int)`
- **`AsyncGPUReadbackPlugin.cs`**: the `[DllImport("AsyncGPUReadbackPlugin")]`
  externs are replaced by calls through `NativeReadbackLibrary`, which also
  fixes two marshalling bugs (C++ `bool` read as a 4-byte BOOL, and `size_t`
  length written into an `int`).
- **`AsyncReadbackUpdater.cs`** — added `OnDestroy()` that nulls the static
  `instance` (`if (instance == this) instance = null;`). Upstream never cleared
  it, so after the pump GameObject was destroyed the static held a stale
  reference and consumers re-checking `instance == null` (KerbcastCore re-spawning
  the pump on the next Flight scene) saw "not null" and never respawned — async
  readbacks then wedged until a full KSP restart.

Note: the `ClearDeadRefs` dictionary-during-enumeration bug is NOT fixed in
this source — it's patched at runtime via Harmony in
`Plugin/Kerbcast/AsyncReadbackRegistryFix.cs` (kept that way so that file stays
closer to upstream).

## Updating

⚠️ The `cp` below OVERWRITES the local modifications above — re-apply them
after updating (diff against git history for the exact hunks). For the native
library, bump `UPSTREAM_SHA` in `native/build.sh` and confirm
`kerbcast-self-init.patch` still applies.

```sh
cd /tmp && git clone --depth 1 https://github.com/yangrc1234/UnityOpenGLAsyncReadback.git yangrc-update
cp /tmp/yangrc-update/UnityExampleProject/Assets/OpenglAsyncReadback/Scripts/{AsyncGPUReadbackPlugin,AsyncReadbackUpdater}.cs Plugin/Vendor/UnityOpenGLAsyncReadback/
cp /tmp/yangrc-update/LICENSE Plugin/Vendor/UnityOpenGLAsyncReadback/
# then re-apply the kerbcast changes (see "Local modifications")
```
