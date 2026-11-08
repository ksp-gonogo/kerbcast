using System;
using System.Linq;
using Kerbcast.Api;
using Kerbcast.Api.Testing;
using ProbeMod;

if (DockingZoom.Apply(42, 20) != 0) return Fail("without Kerbcast nothing is narrowed");

using (var kc = RecordingKerbcast.Install())
{
    kc.Cameras.Add(new CameraView { FlightId = 7, Kind = CameraKind.Part, VesselPersistentId = 42, HasDockingNode = true, SupportsZoom = true, FovMin = 5, FovMax = 60 });
    kc.Cameras.Add(new CameraView { FlightId = 8, Kind = CameraKind.Part, VesselPersistentId = 42, HasDockingNode = false });
    if (DockingZoom.Apply(42, 20) != 1) return Fail("one docking camera is narrowed");
    if (!kc.Calls.Contains("SetFov 7 20")) return Fail("the recorder saw SetFov 7 20");
}

Console.WriteLine("ALL PASS");
return 0;

static int Fail(string what)
{
    Console.WriteLine("FAIL " + what);
    return 1;
}
