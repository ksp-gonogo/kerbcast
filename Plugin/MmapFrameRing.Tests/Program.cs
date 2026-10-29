// Unit + contract tests for MmapFrameRing.
//
// 1. The unmanaged-pointer Produce overload must write byte-for-byte
//    identically to the byte[] overload. That equivalence is what lets the
//    capture path (#2) skip the Texture2D round-trip + GPU Apply() and write
//    the AsyncGPUReadback NativeArray straight into the ring.
// 2. Cross-language layout contract: regenerating the committed golden ring
//    (sidecar/testdata/frame_ring_v2.ring) must be byte-identical. The Rust
//    reader test (sidecar/src/shared_mem/mmap.rs,
//    csharp_written_fixture_reads_back_exactly) consumes the same file, so
//    the two language sides of the ring layout cannot silently drift.
//
// 3. The same for the 1.12 metadata (fov plus anchor results after the
//    slots): sidecar/testdata/frame_ring_v2_meta.ring, read by
//    csharp_written_meta_fixture_reads_back_exactly. The v2 fixture above
//    must stay byte-identical, which is what keeps older sidecars reading
//    rings from a 1.12 plugin.
//
// Regenerate the fixtures (only on a deliberate, versioned layout change):
//   dotnet run -- --write-fixture ../../sidecar/testdata/frame_ring_v2.ring
//   dotnet run -- --write-meta-fixture ../../sidecar/testdata/frame_ring_v2_meta.ring
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
    ring.Produce(W, H, 1234.5, 86400.0, 7, f1, 0, f1.Length);
    var f2 = new byte[W * H * 4];
    for (int i = 0; i < f2.Length; i++) f2[i] = (byte)((i * 11 + 29) & 0xFF);
    ring.Produce(W, H, 1235.5, 86400.25, 7, f2, 0, f2.Length);
    ring.Dispose();
}

/* Two frames, the second with fov 42.5, a live bound of 2.5 s, a boundary
   distance, a command camera's state and one anchor result; the Rust test
   reads the second back field by field. */
static void WriteMetaFixture(string path)
{
    var ring = MmapFrameRing.Create(path, Slots, W, H, 1024);
    var f1 = new byte[W * H * 4];
    ring.Produce(W, H, 1234.5, 86400.0, 7, f1, 0, f1.Length);
    /* Every field of the command state set, through the encoder the plugin uses, so the Rust side checks each key round-trips. */
    var command = new CommandFrameState
    {
        HasFix = true, RangeM = 1500.5, BracketHalfPx = 12.25, FrameHeightPx = 18, FramingFits = false,
        TrueOffsetPx = 3.5, Occluded = true, Occluder = "Mun", Sunlit = true, Blank = "Held",
        AuthorityOwner = "mission", SubjectPersistentId = 42, AimKind = "vessel", Refusal = "refused",
        RefusalCode = "noSignal", RefusalText = "No signal", SubjectUt = 86398.75, WorldModel = "model",
        WorldModelSeq = 9, KnowledgeUt = 86390, Scope = "centre", WaitingForTarget = true,
    };
    var anchors = new System.Collections.Generic.List<AnchorResult>
    {
        new AnchorResult
        {
            Id = "port", Owner = "peer:7:3", X = 16.5, Y = 9.25, InFrame = true, DistanceM = 120, ApparentRadiusPx = 4,
            AngleOffAxisDeg = 0.5, HasOrientation = true, Forward = new DVec3(0, 0, 1), Up = new DVec3(0, 1, 0),
            HasOcclusion = true, Occluded = false,
        },
    };
    byte[] json = null;
    int len = FrameMetaJson.Encode(0.05, command, anchors, new System.Text.StringBuilder(), ref json, 1024);
    var meta = new FrameMeta { FovDeg = 42.5f, Json = json, Length = len, LiveLightTimeS = 2.5 };
    ring.Produce(W, H, 1235.5, 86400.25, 7, meta, f1, 0, f1.Length);
    ring.Dispose();
}

if (args.Length == 2 && args[0] == "--write-fixture")
{
    WriteFixture(args[1]);
    Console.WriteLine($"fixture written to {args[1]}");
    return 0;
}
if (args.Length == 2 && args[0] == "--write-meta-fixture")
{
    WriteMetaFixture(args[1]);
    Console.WriteLine($"meta fixture written to {args[1]}");
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
    ulong seq = ring.Produce(W, H, ts, 5000.5, 2, pixels, 0, pixels.Length);
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
        seq = ring.Produce(W, H, ts, 5000.5, 2, p, pixels.Length);
    }
    Check(seq == 1, "byte* Produce returns seq 1 on first write");
    ring.Dispose();
}

// Ring C: the same frame through a caller-supplied copy, as the game passes Unity's memcpy.
string pathCopy = Path.Combine(dir, "copy.ring");
int copies = 0, copiedLength = 0;
unsafe
{
    var ring = MmapFrameRing.Create(pathCopy, Slots, W, H);
    MmapFrameRing.PixelCopy copy = (dst, src, length) =>
    {
        copies++;
        copiedLength = length;
        for (int i = 0; i < length; i++) dst[i] = src[i];
    };
    fixed (byte* p = pixels)
    {
        ring.Produce(W, H, ts, 5000.5, 2, default, p, pixels.Length, copy);
    }
    ring.Dispose();
}
Check(copies == 1 && copiedLength == pixels.Length, $"the supplied copy runs once over the frame ({copies} x {copiedLength})");

// Same config + same single write + same seq/slot ⇒ the two ring files must be
// byte-for-byte identical (header + slot + pixels).
byte[] a = File.ReadAllBytes(pathArr);
byte[] b = File.ReadAllBytes(pathPtr);
Check(a.Length == b.Length, $"ring files same size ({a.Length} vs {b.Length})");
Check(a.SequenceEqual(b), "byte* Produce writes byte-for-byte identical bytes to byte[] Produce");
Check(File.ReadAllBytes(pathCopy).SequenceEqual(a), "Produce with a supplied copy writes the same bytes");

// Pixels must actually be present somewhere (guard against a no-op write).
Check(a.Any(x => x != 0), "ring file is not all-zero (pixels were written)");

// Cross-language golden fixture: regenerating it must reproduce the
// committed bytes exactly. A mismatch means the C# writer's layout moved —
// which silently breaks the Rust reader unless both sides change together.
{
    string committed = Path.Combine(AppContext.BaseDirectory, "frame_ring_v2.ring");
    string regen = Path.Combine(dir, "regen.ring");
    WriteFixture(regen);
    Check(File.Exists(committed), "committed frame_ring_v2.ring fixture present");
    if (File.Exists(committed))
    {
        byte[] want = File.ReadAllBytes(committed);
        byte[] got = File.ReadAllBytes(regen);
        Check(got.Length == want.Length, $"fixture same size ({got.Length} vs {want.Length})");
        Check(got.SequenceEqual(want), "C# writer reproduces the committed cross-language fixture byte-for-byte");
    }
}

{
    string committed = Path.Combine(AppContext.BaseDirectory, "frame_ring_v2_meta.ring");
    string regen = Path.Combine(dir, "regen_meta.ring");
    WriteMetaFixture(regen);
    Check(File.Exists(committed), "committed frame_ring_v2_meta.ring fixture present");
    if (File.Exists(committed))
    {
        byte[] want = File.ReadAllBytes(committed);
        byte[] got = File.ReadAllBytes(regen);
        Check(got.SequenceEqual(want), "C# writer reproduces the committed metadata fixture byte-for-byte");
    }
}

// Metadata too long for the ring is dropped whole, and a frame without any
// clears the previous slot's length so stale anchors never resurface.
{
    string path = Path.Combine(dir, "meta.ring");
    var ring = MmapFrameRing.Create(path, 1, W, H, 16);
    var big = new byte[17];
    ring.Produce(W, H, 1, 1, 0, new FrameMeta { FovDeg = 30f, Json = big, Length = big.Length }, pixels, 0, pixels.Length);
    ring.Dispose();
    byte[] bytes = File.ReadAllBytes(path);
    Check(BitConverter.ToUInt32(bytes, 4096 + 40) == 0, "oversized metadata writes no length");
    Check(BitConverter.ToSingle(bytes, 4096 + 44) == 30f, "fov is written regardless");
    Check(bytes.Length == 4096 + (64 + W * H * 4) + 16, "the trailer follows the slots");
    Check(BitConverter.ToDouble(bytes, 4096 + 48) == 0, "no live bound given writes 0, no delay");
}

/* The live bound rides the slot beside the capture UT, on rings with or without metadata. */
{
    string path = Path.Combine(dir, "light.ring");
    var ring = MmapFrameRing.Create(path, 1, W, H);
    ring.Produce(W, H, 1, 1, 0, new FrameMeta { LiveLightTimeS = 23.25 }, pixels, 0, pixels.Length);
    ring.Dispose();
    byte[] bytes = File.ReadAllBytes(path);
    Check(BitConverter.ToDouble(bytes, 4096 + 48) == 23.25, "the live bound is written on a ring with no metadata");
    Check(BitConverter.ToDouble(bytes, 4096 + 32) == 1, "beside the capture UT");
}

/* A ring left behind by a camera that failed to clean up is replaced with a
   new file, not truncated, so a reader still holding the old one never sees
   it shrink or its sequence run backwards. */
{
    string path = Path.Combine(dir, "left.ring");
    var old = MmapFrameRing.Create(path, Slots, W, H);
    old.Produce(W, H, 1, 1, 0, pixels, 0, pixels.Length);
    old.Produce(W, H, 2, 2, 0, pixels, 0, pixels.Length);
    old.Dispose();
    byte[] before = File.ReadAllBytes(path);
    using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
    {
        var fresh = MmapFrameRing.Create(path, Slots, W, H);
        ulong seq = fresh.Produce(W, H, 3, 3, 0, pixels, 0, pixels.Length);
        fresh.Dispose();
        var held = new byte[before.Length];
        int read = 0;
        while (read < held.Length)
        {
            int n = reader.Read(held, read, held.Length - read);
            if (n == 0) break;
            read += n;
        }
        Check(read == before.Length && held.SequenceEqual(before), "a reader of the old ring still sees it whole");
        Check(seq == 1, "the new ring starts its own sequence");
    }
}

try { Directory.Delete(dir, true); } catch { /* best effort */ }

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
