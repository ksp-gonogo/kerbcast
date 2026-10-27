// CaptureCounters: why a streaming camera did or did not capture on each game
// frame, and how many frames its readbacks took to land, summed over a few
// seconds and written to KSP.log as one line when EnableTelemetry is set. A
// camera below MaxCaptureFps then shows where its rate went: waiting out its
// interval, waiting for a readback, losing the frame's permits, or a render
// that bailed.
//
// Deliberately Unity-free (same approach as ControlBlock.cs / ShedController.cs)
// so the bookkeeping is unit-tested without KSP.

using System;
using System.Globalization;
using System.Text;

namespace Kerbcast
{
    /// <summary>Why a streaming camera did not capture on a frame.</summary>
    public enum CaptureMiss
    {
        /// <summary>Its capture interval had not elapsed.</summary>
        NotDue,
        /// <summary>Due, but no physics tick since its last capture, so the
        /// frame would repeat the last one's UT.</summary>
        SameUt,
        /// <summary>Due and ready, but the frame's permits went to cameras
        /// further behind.</summary>
        NoPermit,
        /// <summary>Due, but as many readbacks as it may have in flight had not
        /// landed yet.</summary>
        ReadbackPending,
    }

    public sealed class CaptureCounters
    {
        /// <summary>Seconds summed into one report.</summary>
        public const float ReportSeconds = 5f;

        /// <summary>A gap between streaming frames longer than this drops the
        /// window, so a camera that stopped streaming never reports a rate
        /// averaged over the time it was off.</summary>
        public const float GapSeconds = 1f;

        /// <summary>Latency buckets: 0, 1, 2, 3, 4 and 5 or more frames.</summary>
        public const int LatencyBuckets = 6;

        private readonly int[] _missed = new int[4];
        private readonly int[] _latency = new int[LatencyBuckets];
        private int _frames;
        private int _granted;
        private int _published;
        private int _overlapped;
        private long _latencySum;
        private int _landed;
        private float _windowStart = float.NaN;
        private float _lastFrame = float.NaN;

        public int Frames => _frames;
        public int Granted => _granted;
        public int Published => _published;
        public int Missed(CaptureMiss why) => _missed[(int)why];
        public int Landed => _landed;
        public int LatencyCount(int bucket) => _latency[bucket];

        /// <summary>A game frame on which this camera was streaming. Starts the
        /// report window on the first one, and again after a gap.</summary>
        public void Frame(float now)
        {
            if (!float.IsNaN(_lastFrame) && now - _lastFrame > GapSeconds) Reset();
            if (float.IsNaN(_windowStart)) _windowStart = now;
            _lastFrame = now;
            _frames++;
        }

        public void Miss(CaptureMiss why) => _missed[(int)why]++;

        /// <summary>The frame's permit went to this camera. A grant that
        /// publishes nothing is a render the camera skipped (its rig asked to
        /// skip, or a step threw).</summary>
        public void Grant() => _granted++;

        /// <summary>A frame was rendered and its readback issued, with
        /// <paramref name="othersInFlight"/> earlier readbacks still pending.</summary>
        public void Publish(int othersInFlight)
        {
            _published++;
            if (othersInFlight > 0) _overlapped++;
        }

        /// <summary>A readback landed <paramref name="frames"/> game frames after
        /// it was issued.</summary>
        public void Land(int frames)
        {
            if (frames < 0) frames = 0;
            _latency[Math.Min(frames, LatencyBuckets - 1)]++;
            _latencySum += frames;
            _landed++;
        }

        /// <summary>
        /// The window's report once <see cref="ReportSeconds"/> have passed since
        /// it opened, and a fresh window; null before then, and for a window in
        /// which the camera never streamed.
        /// </summary>
        public string TakeReport(float now, uint flightId)
        {
            if (float.IsNaN(_windowStart) || now - _windowStart < ReportSeconds) return null;
            float span = now - _windowStart;
            var sb = new StringBuilder(256);
            sb.Append("[Kerbcast] cam=").Append(flightId)
                .Append(" capture over ").Append(span.ToString("F1", CultureInfo.InvariantCulture)).Append(" s at ")
                .Append(Rate(_frames, span)).Append(" game fps: captured ")
                .Append(Rate(_published, span)).Append("/s; not captured: not due ")
                .Append(Rate(_missed[(int)CaptureMiss.NotDue], span)).Append("/s, same UT ")
                .Append(Rate(_missed[(int)CaptureMiss.SameUt], span)).Append("/s, no permit ")
                .Append(Rate(_missed[(int)CaptureMiss.NoPermit], span)).Append("/s, readback pending ")
                .Append(Rate(_missed[(int)CaptureMiss.ReadbackPending], span)).Append("/s, render skipped ")
                .Append(Rate(Math.Max(0, _granted - _published), span)).Append("/s; readback landed after 0/1/2/3/4/5+ frames: ");
            for (int i = 0; i < LatencyBuckets; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(_latency[i]);
            }
            sb.Append(" (mean ")
                .Append(_landed > 0 ? ((double)_latencySum / _landed).ToString("F2", CultureInfo.InvariantCulture) : "n/a")
                .Append("), issued with another in flight ")
                .Append(_published > 0 ? (100 * _overlapped / _published) : 0).Append('%');
            Reset();
            return sb.ToString();
        }

        private static string Rate(int count, float span) => (count / span).ToString("F1", CultureInfo.InvariantCulture);

        private void Reset()
        {
            Array.Clear(_missed, 0, _missed.Length);
            Array.Clear(_latency, 0, _latency.Length);
            _frames = _granted = _published = _overlapped = _landed = 0;
            _latencySum = 0;
            _windowStart = float.NaN;
            _lastFrame = float.NaN;
        }
    }
}
