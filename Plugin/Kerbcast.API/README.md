# Kerbcast.API

The public interface to [Kerbcast](https://github.com/ksp-gonogo/kerbcast), the Kerbal Space Program mod that streams camera feeds to browsers. Build your mod against the `KspGonogo.Kerbcast.API` package and Kerbcast's own `Kerbcast.API.dll`, shipped in `GameData/Kerbcast/Plugins/`, answers at runtime.

It references nothing but the base class library: no UnityEngine, no Assembly-CSharp. The package carries a net48 build, the one KSP loads, and a netstandard2.0 build so test projects on modern .NET restore it cleanly. Vectors are its own `Vec3d` and `Quatd`, bodies are `flightGlobalsIndex`, craft and parts are persistentIds.

## Referencing it

- Reference the package as `<PackageReference Include="KspGonogo.Kerbcast.API" Version="..." PrivateAssets="all" ExcludeAssets="runtime" />` (or `Private="false"` on a plain reference) and never ship a copy of `Kerbcast.API.dll` with your mod. Kerbcast ships the one KSP loads. `PrivateAssets` alone still copies the DLL into your build output
- A hard dependency declares `[assembly: KSPAssemblyDependency("KerbcastAPI", 1, 0)]`
- A soft dependency checks that `"KerbcastAPI"` is in `AssemblyLoader.loadedAssemblies` before touching any API type, and keeps API-typed calls in their own `[MethodImpl(MethodImplOptions.NoInlining)]` methods so the JIT never resolves them when Kerbcast is absent

## Using it

```csharp
using Kerbcast.Api;

var kerbcast = KerbcastApi.Instance;          // null outside flight and the Space Center, or without Kerbcast
if (kerbcast != null && KerbcastApi.Supports(ApiFeature.CraftIdentity))
{
    foreach (var cam in kerbcast.Cameras.CamerasFor(vessel.persistentId))
        if (cam.HasDockingNode == true)
            kerbcast.Cameras.SetFov(cam.FlightId, 20);
}
```

- `KerbcastApi.Instance`, `ApiMinor` and `Supports` are safe on any thread. So are `IKerbcast`'s own members
- `ICameras` and `ICommand` are main thread only; every member throws `InvalidOperationException` on another thread
- `IWorldModelPublisher` is safe on any thread
- `ICameras` controls part cameras. `View` also shows command feeds (`Kind == CameraKind.Command`), which only `ICommand` controls
- A command feed's view says whether its target is occluded (`Occluded`), since when in game time (`OccludedSinceUt`, NaN while clear) and by what (`Occluder`, a body's name). It is the raw reading with no smoothing, so ease it yourself. Part cameras always read as clear. Added in 1.2
- `ICommand.SetOptics` overrides how blurred a command feed's far planets and craft look (`OpticsSettings`: the lens's blur, the air's blur and the rest, NaN keeping Kerbcast's own value), and null returns the feed to its own. The lock applies as for `SetAim`. It takes effect only while the player's `DeepSpaceOptics` setting is on, which it is by default from Kerbcast 1.12.1, and the game can draw it. Added in 1.3, behind `ApiFeature.Optics`
- `KerbcastApi.Identity` reports merges, splits, destructions, recoveries and renumbers. A subscription lasts for the whole process, whatever the scene and whether or not `Instance` is set
- `ICameras.TryVessel` and `TryPersistentId` translate between a craft's persistentId and Vessel.id, even after KSP has dropped the craft
- `KerbcastApi.LockLost` says when the command lock left its holder without a `ReleaseLock`: the scene is ending, or the player released it in game. Take the lock again once the next scene starts; the dying scene refuses a take from the handler. Like `Identity`, a subscription lasts for the whole process
- A reserved scene whose owner makes no call within 30 s of real time lapses: the lock stays held and the feeds stay blank, never the truth, but browser clients may create and remove command cameras. The owner's next call ends the lapse; only the player's in-game release returns the scene to truth
- `TakeLock`, `SetAim` and `Publish` return `NotRunning` when Kerbcast is not running in the scene, as through an object kept from an earlier one
- Every result enum (`LockResult`, `AimResult`, `PublishResult`) has `Unknown = 0`, which Kerbcast never returns, so a result nobody set never reads as success
- A member whose `ApiFeature` is unsupported still exists and throws `NotSupportedException`. Check `Supports` when you were built against a newer minor than the Kerbcast you run with
- A member added without its own `ApiFeature` (such as `WorldModelBuilder.Acceleration` in 1.1, or `CameraView`'s occlusion in 1.2) is guarded by `KerbcastApi.ApiMinor`, not `Supports`
- Built against a newer minor, keep each call to a member that minor added in its own `[MethodImpl(MethodImplOptions.NoInlining)]` method too. The JIT resolves every member a method names when it compiles that method, so an older Kerbcast throws `MissingMethodException` even when the call sits behind a `Supports` check that returns false

## Versioning

`AssemblyVersion` stays `1.0.0.0` for the whole 1.x line, so a mod built against any 1.x binds to any other. The minor is `KerbcastApi.ApiMinor`, the `KSPAssembly` version and the file version. The package version is the API's own version, not Kerbcast's: it moves only when the API does, so its minor is the `KSPAssemblyDependency` minor to declare. Within a major, changes are additive: members and enum values are added, never removed or renumbered. Only Kerbcast implements the interfaces, so new interface members are not breaking.

## Testing without Kerbcast

The `KspGonogo.Kerbcast.API.Testing` package (`Kerbcast.Api.Testing.RecordingKerbcast`) is a headless implementation for your unit tests. It needs no Kerbcast.dll, Unity or KSP, and never ships in GameData, so reference it from test projects only:

```csharp
using (var kc = RecordingKerbcast.Install())   // now KerbcastApi.Instance
{
    kc.Cameras.Add(new CameraView { FlightId = 7, Kind = CameraKind.Part, VesselPersistentId = 42 });
    kc.Command.AddFeed(new CommandFeed { FlightId = 500, SiteKey = "KSC" });

    RunCodeUnderTest();

    Assert.Equal("my-mod", kc.Command.LockOwner);
    Assert.Single(kc.Command.Published);
}
```

It keeps the threading contract against the thread that installed it, applies the documented lock, aim and publishing rules, records every call (`Calls`), and lets a test raise identity events, change scene or turn features off (`SetSupported`).

## Licence

MIT. Kerbcast itself is licensed separately; see its repository.
