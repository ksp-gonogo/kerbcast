// Unit test for MmapFrameRing: the new unmanaged-pointer Produce overload must
// write byte-for-byte identically to the byte[] overload. That equivalence is
// what lets the capture path (#2) skip the Texture2D round-trip + GPU Apply()
// and write the AsyncGPUReadback NativeArray straight into the ring.
//
// Exit code 0 = pass, 1 = fail.

using System;
using System.IO;
using System.Linq;
using Kerbcam;

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

const int W = 32, H = 18, Slots = 4;
double ts = 1234.5;

// A recognisable pixel pattern (not all-zero, exercises every byte value).
var pixels = new byte[W * H * 4];
for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)((i * 7 + 13) & 0xFF);

string dir = Path.Combine(Path.GetTempPath(), "kc_ring_test_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
string pathArr = Path.Combine(dir, "arr.ring");
string pathPtr = Path.Combine(dir, "ptr.ring");

// Ring A: write via the byte[] overload.
{
    var ring = MmapFrameRing.Create(pathArr, Slots, W, H);
    ulong seq = ring.Produce(W, H, ts, pixels, 0, pixels.Length);
    Check(seq == 1, "byte[] Produce returns seq 1 on first write");
    ring.Dispose();
}

// Ring B: write the same frame via the unmanaged-pointer overload.
unsafe
{
    var ring = MmapFrameRing.Create(pathPtr, Slots, W, H);
    ulong seq;
    fixed (byte* p = pixels)
    {
        seq = ring.Produce(W, H, ts, p, pixels.Length);
    }
    Check(seq == 1, "byte* Produce returns seq 1 on first write");
    ring.Dispose();
}

// Same config + same single write + same seq/slot ⇒ the two ring files must be
// byte-for-byte identical (header + slot + pixels).
byte[] a = File.ReadAllBytes(pathArr);
byte[] b = File.ReadAllBytes(pathPtr);
Check(a.Length == b.Length, $"ring files same size ({a.Length} vs {b.Length})");
Check(a.SequenceEqual(b), "byte* Produce writes byte-for-byte identical bytes to byte[] Produce");

// Pixels must actually be present somewhere (guard against a no-op write).
Check(a.Any(x => x != 0), "ring file is not all-zero (pixels were written)");

try { Directory.Delete(dir, true); } catch { /* best effort */ }

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
