// Unit test for RenderSizeKey: pack/unpack round-trips, and — the property the
// RenderTexture pool's correctness depends on — distinct render sizes never
// collide onto the same key. A collision would alias two sizes onto one pooled
// RenderTexture and corrupt frames.
//
// Exit code 0 = pass, 1 = fail.

using System;
using System.Collections.Generic;
using Kerbcam;

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

// --- Round-trip over the real shed-cascade sizes + edges. ---
// Operator 1024x576 through the cascade scales {1, .75, .5, .25}, plus a couple
// of awkward odd/edge sizes the packing must still survive.
var sizes = new (int w, int h)[]
{
    (1024, 576), (768, 432), (512, 288), (256, 144),
    (2, 2), (1920, 1080), (1280, 720), (640, 360),
};
foreach (var (w, h) in sizes)
{
    long key = RenderSizeKey.Pack(w, h);
    Check(RenderSizeKey.Width(key) == w && RenderSizeKey.Height(key) == h,
        $"round-trips {w}x{h}");
}

// --- No two distinct sizes share a key (collision-freedom). ---
{
    var seen = new Dictionary<long, (int, int)>();
    bool collision = false;
    foreach (var (w, h) in sizes)
    {
        long key = RenderSizeKey.Pack(w, h);
        if (seen.TryGetValue(key, out var other) && other != (w, h)) collision = true;
        seen[key] = (w, h);
    }
    Check(!collision, "distinct render sizes map to distinct keys");
    // Sanity: width and height of the same magnitude don't alias (the classic
    // bug if you OR'd without the 32-bit shift, e.g. 512x288 vs 288x512).
    Check(RenderSizeKey.Pack(512, 288) != RenderSizeKey.Pack(288, 512),
        "transposed dimensions are not aliased");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
