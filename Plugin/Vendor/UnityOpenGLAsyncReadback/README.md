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
- The bundled `AsyncGPUReadbackPlugin` (`libAsyncGPUReadbackPlugin.so` on
  Linux, `AsyncGPUReadbackPlugin.dll` on Windows) otherwise.

## Files

- `AsyncGPUReadbackPlugin.cs` — C# wrapper + DllImports.
- `AsyncReadbackUpdater.cs` — `MonoBehaviour` that pumps pending readbacks
  every frame. `[RuntimeInitializeOnLoadMethod]` is unreliable in KSP
  (Unity loads mod DLLs after that hook fires), so the kerbcam plugin
  spawns this manually on first use.
- `LICENSE` — upstream MIT license, preserved verbatim.

## Native plugin binary

`libAsyncGPUReadbackPlugin.so` ships in
`GameData/Kerbcam/Plugins/x86_64/`. Mono on Linux only finds it from
specific paths (`KSP_Data/MonoBleedingEdge/x86_64/` or `KSP_Data/Plugins/`);
the install script copies it to the right one. Sourced from the
upstream's `UnityExampleProject/Assets/OpenglAsyncReadback/Plugins/Linux/`
folder.

## Local modifications (kerbcam fork)

Upstream is unmaintained, so this copy is treated as a maintained fork. MIT
permits modification; the `LICENSE` and copyright notices are preserved
verbatim. Divergences from upstream, all clearly marked `kerbcam … (not
upstream)` in-source:

- **`AsyncGPUReadbackPlugin.cs`** — added zero-copy readback accessors so the
  OpenGL path can write the native plugin buffer straight into the frame ring,
  skipping `GetRawData`'s `Allocator.Temp` NativeArray + `MemMove` (one of two
  full-frame copies per readback, plus a per-frame Temp allocation):
  - `UniversalAsyncGPUReadbackRequest.TryGetRawPtr(out void*, out int)`
  - `OpenGLAsyncReadbackRequest.GetRawDataPtr(out void*, out int)`
  Rationale: `local_docs/perf_profiles/readback_investigation.md` change #1.
- **`AsyncReadbackUpdater.cs`** — added `OnDestroy()` that nulls the static
  `instance` (`if (instance == this) instance = null;`). Upstream never cleared
  it, so after the pump GameObject was destroyed the static held a stale
  reference and consumers re-checking `instance == null` (KerbcamCore re-spawning
  the pump on the next Flight scene) saw "not null" and never respawned — async
  readbacks then wedged until a full KSP restart.
  Rationale: `local_docs/perf_profiles/session_20260606.md` (pump-respawn bug).

Note: the `ClearDeadRefs` dictionary-during-enumeration bug is NOT fixed in
this source — it's patched at runtime via Harmony in
`Plugin/Kerbcam/AsyncReadbackRegistryFix.cs` (kept that way so that file stays
closer to upstream).

## Updating

⚠️ The `cp` below OVERWRITES the local modifications above — re-apply them
after updating (diff against git history for the exact hunks).

```sh
cd /tmp && git clone --depth 1 https://github.com/yangrc1234/UnityOpenGLAsyncReadback.git yangrc-update
cp /tmp/yangrc-update/UnityExampleProject/Assets/OpenglAsyncReadback/Scripts/{AsyncGPUReadbackPlugin,AsyncReadbackUpdater}.cs Plugin/Vendor/UnityOpenGLAsyncReadback/
cp /tmp/yangrc-update/LICENSE Plugin/Vendor/UnityOpenGLAsyncReadback/
cp /tmp/yangrc-update/UnityExampleProject/Assets/OpenglAsyncReadback/Plugins/Linux/libAsyncGPUReadbackPlugin.so <install>/GameData/Kerbcam/Plugins/x86_64/
# then re-apply the kerbcam zero-copy accessors (see "Local modifications")
```
