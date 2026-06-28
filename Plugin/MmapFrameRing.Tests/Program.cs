// Unit + contract tests for MmapFrameRing.
//
// 1. The unmanaged-pointer Produce overload must write byte-for-byte
//    identically to the byte[] overload. That equivalence is what lets the
//    capture path (#2) skip the Texture2D round-trip + GPU Apply() and write
//    the AsyncGPUReadback NativeArray straight into the ring.
// 2. Cross-language layout contract: regenerating the committed golden ring
//    (sidecar/testdata/frame_ring_v1.ring) must be byte-identical. The Rust
//    reader test (sidecar/src/shared_mem/mmap.rs,
//    csharp_written_fixture_reads_back_exactly) consumes the same file, so
//    the two language sides of the ring layout cannot silently drift.
//
// Regenerate the fixture (only on a deliberate, versioned layout change):
//   dotnet run -- --write-fixture ../../sidecar/testdata/frame_ring_v1.ring
//
// Exit code 0 = pass, 1 = fail.

using System;
using System.IO;
using System.Linq;
using Kerbcast;

const int W = 32, H = 18, Slots = 4;

/* The fixture recipe. Two frames so write_index / sequence advance is part
   of the contract; distinct per-frame patterns so reading the wrong slot
   can't pass. Any change here is a layout-contract change: bump the fixture
   filename version and update the Rust test in lockstep. */
static void WriteFixture(string path)
{
    var ring = MmapFrameRing.Create(path, Slots, W, H);
    var f1 = new byte[W * H * 4];
    for (int i = 0; i < f1.Length; i++) f1[i] = (byte)((i * 7 + 13) & 0xFF);
    ring.Produce(W, H, 1234.5, f1, 0, f1.Length);
    var f2 = new byte[W * H * 4];
    for (int i = 0; i < f2.Length; i++) f2[i] = (byte)((i * 11 + 29) & 0xFF);
    ring.Produce(W, H, 1235.5, f2, 0, f2.Length);
    ring.Dispose();
}

if (args.Length == 2 && args[0] == "--write-fixture")
{
    WriteFixture(args[1]);
    Console.WriteLine($"fixture written to {args[1]}");
    return 0;
}

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

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

// Cross-language golden fixture: regenerating it must reproduce the
// committed bytes exactly. A mismatch means the C# writer's layout moved —
// which silently breaks the Rust reader unless both sides change together.
{
    string committed = Path.Combine(AppContext.BaseDirectory, "frame_ring_v1.ring");
    string regen = Path.Combine(dir, "regen.ring");
    WriteFixture(regen);
    Check(File.Exists(committed), "committed frame_ring_v1.ring fixture present");
    if (File.Exists(committed))
    {
        byte[] want = File.ReadAllBytes(committed);
        byte[] got = File.ReadAllBytes(regen);
        Check(got.Length == want.Length, $"fixture same size ({got.Length} vs {want.Length})");
        Check(got.SequenceEqual(want), "C# writer reproduces the committed cross-language fixture byte-for-byte");
    }
}

try { Directory.Delete(dir, true); } catch { /* best effort */ }

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
