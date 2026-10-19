using System;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

const int Px = MainScreenThrottlePolicy.CheapViewportPixels;

// Only the local cameras lose their cull mask; the scaled and galaxy masks
// belong to MapView and Scatterer while throttled.
Check(MainScreenThrottlePolicy.ManagesCullingMask(ThrottledCameraRole.Local),
    "local cameras have their cull mask zeroed");
Check(!MainScreenThrottlePolicy.ManagesCullingMask(ThrottledCameraRole.Scaled),
    "scaled and galaxy cameras keep their own cull mask");

MainScreenThrottlePolicy.CheapViewport(1280, 800, out float w, out float h);
Check(Math.Abs(w * 1280 - Px) < 1e-3f && Math.Abs(h * 800 - Px) < 1e-3f,
    $"1280x800 viewport covers {Px}x{Px} pixels");

MainScreenThrottlePolicy.CheapViewport(3840, 2160, out w, out h);
Check(Math.Abs(w * 3840 - Px) < 1e-3f && Math.Abs(h * 2160 - Px) < 1e-3f,
    $"3840x2160 viewport covers {Px}x{Px} pixels");

// A zero-area viewport would stop Unity firing the callbacks the throttle
// exists to keep alive, so degenerate screen sizes clamp to the full screen.
MainScreenThrottlePolicy.CheapViewport(0, 0, out w, out h);
Check(w == 1f && h == 1f, "zero screen size never yields a zero-area viewport");
MainScreenThrottlePolicy.CheapViewport(4, 4, out w, out h);
Check(w == 1f && h == 1f, "screen smaller than the cheap viewport uses the whole screen");

MainScreenThrottlePolicy.CheapViewport(1280, 800, out w, out h);
Check(!MainScreenThrottlePolicy.NeedsReapply(true, 0, 0f, 0f, w, h, w, h),
    "untouched local camera needs nothing");
Check(MainScreenThrottlePolicy.NeedsReapply(true, 1 << 15, 0f, 0f, w, h, w, h),
    "a mask written onto a local camera is re-zeroed");
Check(!MainScreenThrottlePolicy.NeedsReapply(false, unchecked((int)0x80000400), 0f, 0f, w, h, w, h),
    "MapView's layer 31 toggle on the scaled camera is left alone");
Check(MainScreenThrottlePolicy.NeedsReapply(false, 0x400, 0f, 0f, 1f, 1f, w, h),
    "PlanetariumCamera.Deactivate's full-screen rect is shrunk again");
Check(MainScreenThrottlePolicy.NeedsReapply(true, 0, 0.5f, 0f, w, h, w, h),
    "a moved viewport is put back in the corner");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
