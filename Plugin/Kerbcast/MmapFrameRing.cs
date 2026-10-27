// MmapFrameRing — C# writer side of the kerbcast plugin <-> sidecar frame
// handoff. Binary layout is the cross-language contract; this file MUST
// stay in lockstep with `sidecar/src/shared_mem/mmap.rs`. Any field
// reorder or addition is a `LAYOUT_VERSION` bump on both sides.
//
// Layout (single-writer, one-or-more readers, seqlock sync):
//
//   header (4096 bytes, page-aligned):
//     [0..8]   u64 magic = 0x4B45524243414D31 ("KERBCAM1") little-endian
//     [8..12]  u32 version = 2
//     [12..16] u32 slot_count
//     [16..20] u32 max_width
//     [20..24] u32 max_height
//     [24..28] u32 atomic write_index (most-recently-written slot)
//     [28..32] padding
//     [32..40] u64 atomic sequence (monotonic; wraps only after 2^64)
//     [40..44] u32 meta_slot_bytes (per-slot metadata capacity; 0 = none)
//     [44..4096] padding
//
//   per slot (starting at 4096 + slot_idx * slot_bytes):
//     [+0..4]   u32 width
//     [+4..8]   u32 height
//     [+8..12]  u32 stride_bytes
//     [+12..16] u32 capture_epoch (UT discontinuity counter at render)
//     [+16..24] f64 capture_ts_ms
//     [+24..32] u64 sequence (matches header.sequence when written)
//     [+32..40] f64 capture_ut (KSP universal time at render; NaN = unknown)
//     [+40..44] u32 meta_len (bytes of this frame's metadata; 0 = none)
//     [+44..48] f32 fov_deg (vertical field of view at render; 0 = unknown)
//     [+48..56] f64 live_light_time_s (the frame's live bound; 0 = no delay, NaN = unknown)
//     [+56..64] padding
//     [+64..]   RGBA8 pixels, max_width * max_height * 4 bytes
//
//   per-slot metadata (starting at 4096 + slot_count * slot_bytes
//   + slot_idx * meta_slot_bytes): meta_len bytes of UTF-8 JSON, one
//   object with the frame's boundary distance, command camera state and
//   anchor results.
//
//   The metadata fields arrived in 1.12 without a version bump: a writer
//   that reserves no metadata leaves them zero, and an older reader maps
//   only the slots and ignores the three slot fields. A writer older than
//   1.12 leaves live_light_time_s zero, which is right for it: it had no
//   signal speed, so nothing it drew was delayed.
//
// Sync protocol (writer side):
//   1. Bump `header.sequence` atomically (Interlocked.Increment).
//   2. Pick next slot index = (write_index + 1) % slot_count.
//   3. Write the whole slot body (non-atomic — readers won't trust this
//      slot until its `sequence` is published below).
//   4. Publish `slot.sequence` = new sequence value (Interlocked.Exchange).
//   5. Publish `header.write_index` = new slot index (Interlocked.Exchange).
//
// On the reader side (sidecar Rust), the corresponding seqlock retry
// protocol verifies `slot.sequence` doesn't change mid-read.

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Kerbcast
{
    public sealed class MmapFrameRing : IDisposable
    {
        /* "KERBCAM1" legacy ring magic; value pinned for plugin wire-format compat, not renamed. */
        public const ulong Magic = 0x4B45_5242_4341_4D31UL;
        public const uint LayoutVersion = 2;
        public const int HeaderSize = 4096;

        private const int HeaderOffMagic = 0;
        private const int HeaderOffVersion = 8;
        private const int HeaderOffSlotCount = 12;
        private const int HeaderOffMaxWidth = 16;
        private const int HeaderOffMaxHeight = 20;
        private const int HeaderOffWriteIndex = 24;
        private const int HeaderOffSequence = 32;
        private const int HeaderOffMetaSlotBytes = 40;

        private const int SlotOffWidth = 0;
        private const int SlotOffHeight = 4;
        private const int SlotOffStride = 8;
        private const int SlotOffCaptureEpoch = 12;
        private const int SlotOffCaptureTs = 16;
        private const int SlotOffSequence = 24;
        private const int SlotOffCaptureUt = 32;
        private const int SlotOffMetaLen = 40;
        private const int SlotOffFov = 44;
        private const int SlotOffLiveLightTime = 48;
        private const int SlotOffPixels = 64;

        private readonly int _slotCount;
        private readonly int _maxWidth;
        private readonly int _maxHeight;
        private readonly int _slotBytes;
        private readonly int _metaSlotBytes;
        private readonly long _metaStart;
        private readonly long _totalBytes;
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;

        // Base address of the mapped view, acquired once for the ring's
        // lifetime. The mapping never relocates, so re-fetching it per access
        // (the old per-call AcquirePointer) just returned this same value.
        // Acquired in the constructor, released once in Dispose so the
        // SafeHandle ref-count stays balanced.
        private unsafe byte* _basePtr;

        /// <summary>
        /// Create or truncate-and-replace the ring file at <paramref name="path"/>
        /// and stamp the header. Call this from the writer side (KSP plugin).
        /// <paramref name="metaSlotBytes"/> reserves that much per-frame
        /// metadata after the slots; 0 reserves none.
        /// </summary>
        public static MmapFrameRing Create(string path, int slotCount, int maxWidth, int maxHeight, int metaSlotBytes = 0)
        {
            if (metaSlotBytes < 0) throw new ArgumentOutOfRangeException(nameof(metaSlotBytes));
            if (slotCount <= 0) throw new ArgumentOutOfRangeException(nameof(slotCount));
            if (maxWidth <= 0 || maxWidth % 2 != 0)
                throw new ArgumentOutOfRangeException(nameof(maxWidth), "must be > 0 and even");
            if (maxHeight <= 0 || maxHeight % 2 != 0)
                throw new ArgumentOutOfRangeException(nameof(maxHeight), "must be > 0 and even");

            int slotBytes = SlotOffPixels + maxWidth * maxHeight * 4;
            long total = HeaderSize + (long)slotCount * slotBytes + (long)slotCount * metaSlotBytes;

            // Create or truncate the regular file. Using FileShare.ReadWrite
            // so the sidecar can map it concurrently from another process.
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                fs.SetLength(total);
            }

            // CreateFromFile (with mapName=null) on Linux/Mono backs the map
            // with the regular file we just created — NOT POSIX shm_open.
            // That's the whole reason we use a file path: shm_open's
            // /dev/shm/ namespace isn't reachable from Mono's MMF API.
            var mmf = MemoryMappedFile.CreateFromFile(
                new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite),
                mapName: null,
                capacity: total,
                access: MemoryMappedFileAccess.ReadWrite,
                inheritability: HandleInheritability.None,
                leaveOpen: false);
            var view = mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);

            var ring = new MmapFrameRing(mmf, view, slotCount, maxWidth, maxHeight, slotBytes, metaSlotBytes, total);
            ring.WriteHeader();
            return ring;
        }

        private MmapFrameRing(
            MemoryMappedFile mmf,
            MemoryMappedViewAccessor view,
            int slotCount,
            int maxWidth,
            int maxHeight,
            int slotBytes,
            int metaSlotBytes,
            long totalBytes)
        {
            _mmf = mmf;
            _view = view;
            _slotCount = slotCount;
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
            _slotBytes = slotBytes;
            _metaSlotBytes = metaSlotBytes;
            _metaStart = HeaderSize + (long)slotCount * slotBytes;
            _totalBytes = totalBytes;
            unsafe
            {
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);
            }
        }

        public int SlotCount => _slotCount;
        public int MaxWidth => _maxWidth;
        public int MaxHeight => _maxHeight;
        public int MetaSlotBytes => _metaSlotBytes;

        private void WriteHeader()
        {
            // Zero the whole header so the seqlock fields start clean
            // (write_index = 0, sequence = 0 = "no frames yet").
            for (int i = 0; i < HeaderSize; i++) _view.Write(i, (byte)0);
            _view.Write(HeaderOffMagic, Magic);
            _view.Write(HeaderOffVersion, LayoutVersion);
            _view.Write(HeaderOffSlotCount, (uint)_slotCount);
            _view.Write(HeaderOffMaxWidth, (uint)_maxWidth);
            _view.Write(HeaderOffMaxHeight, (uint)_maxHeight);
            _view.Write(HeaderOffMetaSlotBytes, (uint)_metaSlotBytes);
        }

        /// <summary>
        /// Write one frame into the next slot. <paramref name="rgba"/> length
        /// must equal width * height * 4. Returns the monotonic sequence
        /// number assigned to this frame; consumers detect dropped frames by
        /// sequence-number gaps. <paramref name="captureUt"/> is the KSP
        /// universal time the frame was rendered at (NaN when unknown) and
        /// <paramref name="captureEpoch"/> the UT discontinuity epoch then.
        /// </summary>
        public ulong Produce(int width, int height, double captureTsMs, double captureUt, uint captureEpoch, byte[] rgba, int rgbaOffset, int rgbaLength)
        {
            return Produce(width, height, captureTsMs, captureUt, captureEpoch, default, rgba, rgbaOffset, rgbaLength);
        }

        /// <summary>
        /// <c>Produce</c> with the frame's metadata: its field of view and
        /// anchor results. Metadata longer than the ring reserves is dropped
        /// whole, never cut short.
        /// </summary>
        public ulong Produce(int width, int height, double captureTsMs, double captureUt, uint captureEpoch, FrameMeta meta, byte[] rgba, int rgbaOffset, int rgbaLength)
        {
            ValidateFrame(width, height, rgbaLength);
            var (slot, slotStart, newSeq) = BeginSlot(width, height, captureTsMs, captureUt, captureEpoch, meta);
            _view.WriteArray(slotStart + SlotOffPixels, rgba, rgbaOffset, rgbaLength);
            PublishSlot(slot, slotStart, newSeq);
            return (ulong)newSeq;
        }

        /// <summary>
        /// Produce a frame straight from an unmanaged pixel buffer (e.g. the
        /// AsyncGPUReadback NativeArray), copying directly into the mapped slot.
        /// Lets the capture path skip the intermediate Texture2D round-trip
        /// (LoadRawTextureData + a pointless GPU Apply() + GetRawTextureData).
        /// Byte-for-byte identical to the <c>byte[]</c> overload — see
        /// MmapFrameRing.Tests.
        /// </summary>
        public unsafe ulong Produce(int width, int height, double captureTsMs, double captureUt, uint captureEpoch, byte* src, int length)
        {
            return Produce(width, height, captureTsMs, captureUt, captureEpoch, default, src, length);
        }

        public unsafe ulong Produce(int width, int height, double captureTsMs, double captureUt, uint captureEpoch, FrameMeta meta, byte* src, int length)
        {
            ValidateFrame(width, height, length);
            var (slot, slotStart, newSeq) = BeginSlot(width, height, captureTsMs, captureUt, captureEpoch, meta);
            Buffer.MemoryCopy(src, _basePtr + slotStart + SlotOffPixels, length, length);
            PublishSlot(slot, slotStart, newSeq);
            return (ulong)newSeq;
        }

        private void ValidateFrame(int width, int height, int length)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (width > _maxWidth || height > _maxHeight)
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    $"frame {width}x{height} exceeds capacity {_maxWidth}x{_maxHeight}");
            int expected = width * height * 4;
            if (length != expected)
                throw new ArgumentException(
                    $"length {length} != width*height*4 ({expected})", nameof(length));
        }

        // Steps 1-2 of the seqlock write plus the slot header. The pixel copy
        // (step 3) is the caller's job — the only part that differs between the
        // byte[] and unmanaged-pointer overloads. PublishSlot does steps 4-5.
        private (int slot, int slotStart, long seq) BeginSlot(int width, int height, double captureTsMs, double captureUt, uint captureEpoch, FrameMeta meta)
        {
            // Interlocked.Increment on Int64 is an atomic add + load — same
            // semantics as Rust's AtomicU64.fetch_add(1, Release) cross-process.
            long newSeq = AtomicIncrementHeaderSequence();
            uint currentIdx = (uint)Interlocked.CompareExchange(
                ref AsRefInt32(HeaderOffWriteIndex), 0, 0);
            uint nextSlot = (currentIdx + 1) % (uint)_slotCount;
            int slotStart = HeaderSize + (int)nextSlot * _slotBytes;
            _view.Write(slotStart + SlotOffWidth, (uint)width);
            _view.Write(slotStart + SlotOffHeight, (uint)height);
            _view.Write(slotStart + SlotOffStride, (uint)(width * 4));
            _view.Write(slotStart + SlotOffCaptureEpoch, captureEpoch);
            _view.Write(slotStart + SlotOffCaptureTs, captureTsMs);
            _view.Write(slotStart + SlotOffCaptureUt, captureUt);
            int metaLen = meta.Json != null && meta.Length > 0 && meta.Length <= _metaSlotBytes ? meta.Length : 0;
            _view.Write(slotStart + SlotOffMetaLen, (uint)metaLen);
            _view.Write(slotStart + SlotOffFov, meta.FovDeg > 0f && !float.IsNaN(meta.FovDeg) && !float.IsInfinity(meta.FovDeg) ? meta.FovDeg : 0f);
            _view.Write(slotStart + SlotOffLiveLightTime, meta.LiveLightTimeS >= 0 && !double.IsInfinity(meta.LiveLightTimeS) ? meta.LiveLightTimeS : double.NaN);
            if (metaLen > 0)
                _view.WriteArray(_metaStart + nextSlot * (long)_metaSlotBytes, meta.Json, 0, metaLen);
            return ((int)nextSlot, slotStart, newSeq);
        }

        private void PublishSlot(int slot, int slotStart, long newSeq)
        {
            // Step 4: publish slot.sequence (Release). Step 5: publish
            // header.write_index (Release). Readers gate on slot.sequence.
            Interlocked.Exchange(ref AsRefInt64(slotStart + SlotOffSequence), newSeq);
            Interlocked.Exchange(ref AsRefInt32(HeaderOffWriteIndex), slot);
        }

        private long AtomicIncrementHeaderSequence()
        {
            return Interlocked.Increment(ref AsRefInt64(HeaderOffSequence));
        }

        // Bridge Interlocked.* (which take `ref Int32/Int64`) into the
        // mapped view via the base pointer cached at construction. We cast to
        // the right primitive at the offset. Mono's IL2CPP on the Steam Deck
        // Unity runtime supports `unsafe` blocks for IntPtr arithmetic; same
        // as the OCISLY-fork KerbcastBaseline path.
        private unsafe ref int AsRefInt32(int offset)
        {
            int* p = (int*)(_basePtr + offset);
            return ref *p;
        }

        private unsafe ref long AsRefInt64(int offset)
        {
            long* p = (long*)(_basePtr + offset);
            return ref *p;
        }

        public unsafe void Dispose()
        {
            // Balance the constructor's AcquirePointer before tearing down the
            // view, so the SafeHandle ref-count returns to zero and the handle
            // can actually free.
            if (_basePtr != null)
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _basePtr = null;
            }
            _view.Dispose();
            _mmf.Dispose();
        }
    }

    /* What the game reports about one frame beside its pixels. The default
       carries nothing: fov unknown, no metadata, no delay. */
    public struct FrameMeta
    {
        /* Vertical field of view the frame was rendered with, degrees; 0 when unknown. */
        public float FovDeg;
        /* The frame's metadata object as UTF-8 JSON, the first Length bytes; null for none. */
        public byte[] Json;
        public int Length;
        /* The frame's live bound in seconds (CommandCameraMath.LiveLightTimeS); 0 with no signal speed, NaN when it could not be read, which a reader takes as unknown and holds the frame. */
        public double LiveLightTimeS;
    }
}
