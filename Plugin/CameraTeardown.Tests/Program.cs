using System;
using System.IO;
using System.Linq;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

/* A part destroyed while the flight scene is live is a real loss and keeps
   its SIGNAL LOST tombstone. Once a scene load is requested (revert,
   quickload, leaving flight) KSP destroys every part as the scene unloads,
   and those are normal teardowns. */
var gate = new CameraTeardown();
Check(gate.WriteTombstone, "in-flight part destruction writes a tombstone");
Check(gate.LostCameraDisposal == CameraDisposal.Destroyed, "before the scene-load gate a lost camera is disposed with a tombstone");
gate.OnSceneLoadRequested();
Check(gate.LostCameraDisposal == CameraDisposal.Normal, "once the scene-load gate is set a lost camera gets a normal dispose");
Check(!gate.WriteTombstone, "part destruction during scene unload is a normal teardown");

Check(CameraTeardown.IsPerCameraFile("780776309.ring"), "ring file is per-camera");
Check(CameraTeardown.IsPerCameraFile("780776309.info.json"), "manifest is per-camera");
Check(CameraTeardown.IsPerCameraFile("780776309.control.bin"), "control block is per-camera");
Check(CameraTeardown.IsPerCameraFile("4294967295.ring"), "max uint flight id is per-camera");
Check(!CameraTeardown.IsPerCameraFile("global.inflight"), "host scene flag is not per-camera");
Check(!CameraTeardown.IsPerCameraFile("global.status.json"), "status file is not per-camera");
Check(!CameraTeardown.IsPerCameraFile("global.control.json"), "global control is not per-camera");
Check(!CameraTeardown.IsPerCameraFile("sidecar.log"), "unrelated file is not per-camera");
Check(!CameraTeardown.IsPerCameraFile("abc.ring"), "non-numeric stem is not per-camera");
Check(!CameraTeardown.IsPerCameraFile("-1.ring"), "negative stem is not per-camera");
Check(!CameraTeardown.IsPerCameraFile(".ring"), "empty stem is not per-camera");
Check(!CameraTeardown.IsPerCameraFile("12.info.json.tmp"), "temp suffix is not per-camera");

var dir = Path.Combine(Path.GetTempPath(), "kerbcast-sweep-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    string[] stale = { "1.ring", "1.info.json", "1.control.bin", "2.info.json" };
    string[] kept = { "global.inflight", "global.status.json", "global.control.json", "notes.txt" };
    foreach (var f in stale.Concat(kept)) File.WriteAllText(Path.Combine(dir, f), "x");

    int swept = CameraTeardown.SweepStaleCameraFiles(dir, out int skipped);
    Check(swept == stale.Length, $"sweep reports {stale.Length} files removed (got {swept})");
    Check(skipped == 0, "nothing skipped when no file is locked");
    Check(stale.All(f => !File.Exists(Path.Combine(dir, f))), "every stale per-camera file is gone");
    Check(kept.All(f => File.Exists(Path.Combine(dir, f))), "global and unrelated files survive");
    Check(CameraTeardown.SweepStaleCameraFiles(dir, out _) == 0, "a second sweep finds nothing");
    Check(CameraTeardown.SweepStaleCameraFiles(Path.Combine(dir, "missing"), out _) == 0,
        "a missing directory sweeps nothing");
}
finally
{
    Directory.Delete(dir, recursive: true);
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
