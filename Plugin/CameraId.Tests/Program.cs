using System;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

// Module 0 keeps the bare flightID (back-compat), name ignored.
Check(CameraId.Synthetic(100u, 0, "fwd") == 100u, "module 0 == baseId");
Check(CameraId.Synthetic(100u, 0, "aft") == 100u, "module 0 ignores cameraName");
// Modules 1+ are deterministic and differ from the base id.
Check(CameraId.Synthetic(100u, 1, "fwd") == CameraId.Synthetic(100u, 1, "fwd"), "module 1 stable");
Check(CameraId.Synthetic(100u, 1, "fwd") != 100u, "module 1 != baseId");
// Base id and camera name both perturb the hash.
Check(CameraId.Synthetic(100u, 1, "fwd") != CameraId.Synthetic(101u, 1, "fwd"), "baseId perturbs");
Check(CameraId.Synthetic(100u, 1, "fwd") != CameraId.Synthetic(100u, 1, "aft"), "cameraName perturbs");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
