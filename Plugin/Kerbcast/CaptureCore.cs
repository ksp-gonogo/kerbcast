/* Reusable frame-capture tail shared by every kerbcast camera type.

   Owns the pooled capture/readback RenderTexture pair, the in-flight
   readbacks (InFlightReadbacks) and the mmap ring write. A camera renders
   its own layer stack into CaptureRt, then hands the tail here: Publish
   blits CaptureRt -> ReadbackRt (via the caller's filter/nightvision/plain
   blit), applies the vertical-flip correction, and issues one
   AsyncGPUReadback while fewer than MaxInFlight are pending; Drain copies
   completed readbacks, oldest first, straight into the ring.

   Deck-critical details preserved verbatim from KerbcastCamera: the
   readback RT is depth=0 (clean GL_TEXTURE_2D handle on Mesa), the flip
   gate is SystemInfo.graphicsUVStartsAtTop, and the drain takes the
   zero-copy TryGetRawPtr path before the GetData fallback. */

using System;
using System.Diagnostics;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Yangrc.OpenGLAsyncReadback;

namespace Kerbcast
{
    internal sealed class CaptureCore : IDisposable
    {
        // The *current* render-target pair (also held in _rtPool). The owning
        // camera renders + blits + issues new readbacks against these.
        private RenderTexture _captureRt;
        private RenderTexture _readbackRt; // depth=0, GL_TEXTURE_2D-clean

        private struct RenderTargetSet
        {
            public RenderTexture Capture;
            public RenderTexture Readback;
        }

        // RenderTexture pool keyed by render size (RenderSizeKey.Pack). Adaptive
        // shedding switches between pooled sets instead of destroying and
        // reallocating: destroying a RenderTexture while an AsyncGPUReadback is
        // still in flight against it orphans the native readback task (the
        // bundled OpenGL plugin exposes no cancel/free API), and that orphan is
        // then walked every frame by the DontDestroyOnLoad updater for the rest
        // of the process. Pooling never destroys mid-flight (so nothing is
        // orphaned) and removes the per-change realloc hitch.
        private readonly System.Collections.Generic.Dictionary<long, RenderTargetSet> _rtPool =
            new System.Collections.Generic.Dictionary<long, RenderTargetSet>();

        /* Readbacks a camera may have in flight at once. */
        internal const int MaxInFlight = 1;

        /* Each in-flight readback's size and issue frame, described
           independently of the *current* target so a resolution change
           mid-readback still drains the pending frame at its original
           dimensions. The rest of its data sits in _pending under the same slot. */
        private readonly InFlightReadbacks _inFlight = new InFlightReadbacks(MaxInFlight);
        private readonly Pending[] _pending = new Pending[MaxInFlight];

        private struct Pending
        {
            public UniversalAsyncGPUReadbackRequest Request;
            public double CaptureTsMs;
            // Mission time and UT epoch at render, carried with the readback so each ring frame names the UT it shows.
            public double CaptureUt;
            public uint CaptureEpoch;
            public double LiveLightTimeS;
            // The frame's fov and anchor results, copied at render so the readback carries what that frame showed.
            public float Fov;
            public byte[] Meta;
            public int MetaLength;
        }

        private readonly MmapFrameRing _ring;
        private readonly CaptureCounters _counters;

        // Shared telemetry sink (owned by the camera) plus the failure-streak
        // callbacks: the camera keeps _consecutiveErrors and LogRateLimited, we
        // just invoke them so a readback error and a render error share one
        // rate-limit counter.
        private readonly PhaseTimings _phaseTimings;
        private readonly Action<string> _logRateLimited;
        private readonly Action _resetErrorStreak;
        /* The camera's live bound, read at render beside the UT so a frame carries the value of its own instant. */
        private readonly Func<double> _liveLightTimeS;

        // Cached per Refresh tick via BeginTick. When false every timing bracket
        // is skipped so the OFF path makes zero GetTimestamp calls. When true the
        // drain (ProcessReadback) timing accumulates here so it can be combined
        // with the Request-issue timing into one Readback sample per tick.
        private bool _telemetry;
        private double _readbackDrainMs;
        private static readonly double _msPerTick =
            1000.0 / Stopwatch.Frequency;

        internal CaptureCore(
            MmapFrameRing ring,
            PhaseTimings phaseTimings,
            Action<string> logRateLimited,
            Action resetErrorStreak,
            Func<double> liveLightTimeS,
            CaptureCounters counters)
        {
            _ring = ring;
            _counters = counters;
            _phaseTimings = phaseTimings;
            _logRateLimited = logRateLimited;
            _resetErrorStreak = resetErrorStreak;
            _liveLightTimeS = liveLightTimeS;
        }

        internal RenderTexture CaptureRt => _captureRt;
        internal RenderTexture ReadbackRt => _readbackRt;
        /* For granting a permit before the camera refreshes: a slot is free, or
           the oldest has landed and the Drain at the top of Refresh will free
           it. Not the check before rendering, since the render thread can land
           a readback between a Drain and this read; that is HasFreeSlot. */
        internal bool CanIssue => !_inFlight.Full || _pending[_inFlight.Oldest].Request.done;

        /* Whether Publish has a slot now. Only Publish fills one, so a true read
           after Drain holds until the camera publishes. */
        internal bool HasFreeSlot => !_inFlight.Full;

        /* The UT of the last frame issued, NaN before the first. */
        internal double LastPublishedUt { get; private set; } = double.NaN;

        // Select (or lazily create) the pooled render-target set for this size
        // and make it current. Never destroys — see the _rtPool comment.
        internal void BuildTargets(int width, int height)
        {
            long key = RenderSizeKey.Pack(width, height);
            if (!_rtPool.TryGetValue(key, out var set))
            {
                var capture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    antiAliasing = 1,
                };
                capture.Create();

                // depth=0 so GetNativeTexturePtr returns a vanilla GL_TEXTURE_2D
                // handle on Mesa OpenGL. With depth=24 (the capture RT) the
                // yangrc plugin's glGetTexLevelParameteriv reads back zero
                // dimensions and silently does nothing.
                var readback = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                readback.Create();

                set = new RenderTargetSet { Capture = capture, Readback = readback };
                _rtPool[key] = set;
            }
            _captureRt = set.Capture;
            _readbackRt = set.Readback;
            _inFlight.SetCurrent(width, height);
        }

        // Reset the per-tick drain accumulator and snapshot the telemetry gate,
        // mirroring the top-of-Refresh reset so a mid-tick settings flip cannot
        // split the recording within one tick.
        internal void BeginTick(bool telemetry)
        {
            _telemetry = telemetry;
            _readbackDrainMs = 0.0;
        }

        // Blit CaptureRt -> ReadbackRt (caller supplies the filter/nightvision/
        // plain pass), apply the vertical-flip correction, then issue one
        // readback into a free slot. Records the Blit and Readback phase
        // timings into the shared PhaseTimings.
        internal void Publish(double captureTsMs, Action<RenderTexture, RenderTexture> blit)
        {
            Publish(captureTsMs, blit, default);
        }

        /* Publish with the frame's metadata (fov and anchor results), which is
           copied now and written beside the pixels when the readback lands. */
        internal void Publish(double captureTsMs, Action<RenderTexture, RenderTexture> blit, FrameMeta meta)
        {
            /* The callers check HasFreeSlot after draining, so this only guards a
               caller that did not: drop the frame before any GPU work is queued
               rather than have Issue throw after a Request. */
            if (_inFlight.Full) Drain();
            if (_inFlight.Full) return;

            // Blit phase: the filter/nv/plain capture->readback blit AND the
            // vertical-flip correction below, all the main-thread Blit dispatch
            // this tick.
            long blitStart = _telemetry ? Stopwatch.GetTimestamp() : 0;
            blit(_captureRt, _readbackRt);

            // Vertical-flip correction, in CaptureOrientation so the headless
            // orientation proof compiles the same source. Behaviour unchanged:
            // bottom-left-origin APIs (OpenGL, the Deck) read back inverted and
            // get one mirroring blit; top-left-origin APIs are a no-op here.
            CaptureOrientation.ApplyReadbackFlip(_readbackRt);
            if (_telemetry)
                _phaseTimings.Record(RenderPhase.Blit,
                    (Stopwatch.GetTimestamp() - blitStart) * _msPerTick);

            /* A slot is free (checked above, and only Publish fills one). It is
               taken only once the request exists: a Request that throws leaves
               nothing in flight to wait on. */
            long reqStart = _telemetry ? Stopwatch.GetTimestamp() : 0;
            var request = UniversalAsyncGPUReadbackRequest.Request(_readbackRt, 0);
            int others = _inFlight.Count;
            int slot = _inFlight.Issue(Time.frameCount);
            ref Pending p = ref _pending[slot];
            p.Request = request;
            p.CaptureTsMs = captureTsMs;
            p.CaptureUt = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : double.NaN;
            p.CaptureEpoch = KerbcastCore.Instance != null ? KerbcastCore.Instance.CaptureEpochValue : 0;
            p.LiveLightTimeS = LiveLightTimeNow();
            p.Fov = meta.FovDeg;
            p.MetaLength = meta.Json != null ? meta.Length : 0;
            if (p.MetaLength > 0)
            {
                if (p.Meta == null || p.Meta.Length < p.MetaLength)
                    p.Meta = new byte[Math.Max(p.MetaLength, 1024)];
                Buffer.BlockCopy(meta.Json, 0, p.Meta, 0, p.MetaLength);
            }
            LastPublishedUt = p.CaptureUt;
            _counters.Publish(others);
            if (_telemetry)
            {
                // Readback phase = this tick's drain of the PREVIOUS frame's
                // capture (the ring memcpy in Drain, accumulated into
                // _readbackDrainMs) + the cheap Request() issue here. Recording
                // them combined keeps the single "readback" column meaningful even
                // though the work straddles two frames.
                double issueMs = (Stopwatch.GetTimestamp() - reqStart) * _msPerTick;
                _phaseTimings.Record(RenderPhase.Readback, _readbackDrainMs + issueMs);
                _phaseTimings.FrameComplete();
            }
        }

        /* Drain every landed readback into the ring, oldest first, stopping at
           the first still in flight so frames reach the ring in capture order. */
        internal void Drain()
        {
            while (_inFlight.Count > 0)
            {
                int slot = _inFlight.Oldest;
                if (!_pending[slot].Request.done) return;
                _counters.Land(Time.frameCount - _inFlight.IssuedFrame(slot));
                ProcessReadback(slot);
                _pending[slot].Request = default;
                _inFlight.Land();
            }
        }

        /* A read that throws is unknown, never no delay, which could release the frame early. */
        private double LiveLightTimeNow()
        {
            if (_liveLightTimeS == null) return double.NaN;
            try { return _liveLightTimeS(); }
            catch (Exception ex)
            {
                _logRateLimited($"live light time threw: {ex.GetType().Name}: {ex.Message}");
                return double.NaN;
            }
        }

        private unsafe void ProcessReadback(int slot)
        {
            ref Pending p = ref _pending[slot];
            var request = p.Request;
            // Time the drain (the full-frame ring memcpy in Produce) and
            // accumulate into the per-tick drain total. The Readback-phase sample
            // is recorded once at the Request site, combining this with the issue.
            long drainStart = _telemetry ? Stopwatch.GetTimestamp() : 0;
            try
            {
                if (request.hasError)
                {
                    _logRateLimited("AsyncGPUReadback returned hasError");
                    return;
                }

                // Write the readback bytes straight into the ring — no
                // intermediate Texture2D, no pointless GPU Apply(). The readback
                // target is RGBA32, so the bytes are already the exact pixel
                // layout the ring expects. Dimensions come from the snapshot taken
                // when the readback was issued, since the current render size may
                // have changed in the meantime (pooled-set switch).
                //
                // On the OpenGL plugin path (the Deck), read the native plugin
                // buffer pointer directly — skipping GetData's Allocator.Temp
                // NativeArray + MemMove (one of two full-frame copies per
                // readback). Pointer is valid only until the next readback Update,
                // so Produce (which copies into the ring) runs now. On the
                // Unity-native path GetData is already a zero-copy view — use it.
                byte* src;
                int length;
                if (request.TryGetRawPtr(out var rawPtr, out length))
                {
                    src = (byte*)rawPtr;
                }
                else
                {
                    var data = request.GetData<byte>();
                    src = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(data);
                    length = data.Length;
                }
                var meta = new FrameMeta
                {
                    FovDeg = p.Fov,
                    Json = p.MetaLength > 0 ? p.Meta : null,
                    Length = p.MetaLength,
                    LiveLightTimeS = p.LiveLightTimeS,
                };
                _ring.Produce(_inFlight.Width(slot), _inFlight.Height(slot), p.CaptureTsMs, p.CaptureUt, p.CaptureEpoch, meta, src, length);
                _resetErrorStreak();
            }
            catch (Exception ex)
            {
                _logRateLimited($"readback callback threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (_telemetry)
                    _readbackDrainMs +=
                        (Stopwatch.GetTimestamp() - drainStart) * _msPerTick;
            }
        }

        // Destroy every pooled render-target set (the current _captureRt /
        // _readbackRt are members of one of these, so this covers them). Safe
        // only at teardown — no further readbacks will be issued. The ring is
        // owned and disposed by the camera, not here.
        public void Dispose()
        {
            foreach (var set in _rtPool.Values)
            {
                if (set.Capture != null) { set.Capture.Release(); UnityEngine.Object.Destroy(set.Capture); }
                if (set.Readback != null) { set.Readback.Release(); UnityEngine.Object.Destroy(set.Readback); }
            }
            _rtPool.Clear();
        }
    }
}
