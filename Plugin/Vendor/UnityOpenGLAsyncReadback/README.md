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

## Updating

```sh
cd /tmp && git clone --depth 1 https://github.com/yangrc1234/UnityOpenGLAsyncReadback.git yangrc-update
cp /tmp/yangrc-update/UnityExampleProject/Assets/OpenglAsyncReadback/Scripts/{AsyncGPUReadbackPlugin,AsyncReadbackUpdater}.cs Plugin/Vendor/UnityOpenGLAsyncReadback/
cp /tmp/yangrc-update/LICENSE Plugin/Vendor/UnityOpenGLAsyncReadback/
cp /tmp/yangrc-update/UnityExampleProject/Assets/OpenglAsyncReadback/Plugins/Linux/libAsyncGPUReadbackPlugin.so <install>/GameData/Kerbcam/Plugins/x86_64/
```
