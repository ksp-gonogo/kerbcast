using System;

namespace Kerbcast
{
    /* One cost as the telemetry block reports it, read once per status write. */
    internal struct CostReading
    {
        /* The latest value. */
        public double Last;
        /* Smoothed over about ten values, as PhaseTimings smooths a phase. */
        public double Ema;
        /* The largest value since the previous write. */
        public double Max;
        /* Values recorded since the previous write. */
        public int Count;
        /* Any value has ever been recorded. */
        public bool Seen;
    }

    /*
     * A cost recorded as last value, EMA and the largest value since the last
     * read, like PhaseTimings. Reading takes the interval and starts the next,
     * so a harness that deduplicates the status file by content sees each
     * interval's max once. Safe on any thread: the world model's fill is
     * recorded on its publisher's thread.
     */
    internal sealed class CostMeter
    {
        private const double EmaAlpha = 0.1;

        private readonly object _gate = new object();
        private double _last, _ema, _max;
        private int _count;
        private bool _seen;

        public void Record(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            lock (_gate)
            {
                _last = value;
                _ema = _seen ? _ema + EmaAlpha * (value - _ema) : value;
                _seen = true;
                if (value > _max) _max = value;
                _count++;
            }
        }

        public CostReading TakeInterval()
        {
            lock (_gate)
            {
                var r = new CostReading { Last = _last, Ema = _ema, Max = _max, Count = _count, Seen = _seen };
                _max = 0;
                _count = 0;
                return r;
            }
        }
    }

    /* A count that only grows, read as a rate per second between reads. */
    internal sealed class RateMeter
    {
        private long _lastTotal = -1;
        private double _lastSeconds;

        /* The rate since the previous read; 0 on the first read, or when time did not move or the total went back. */
        public double TakeInterval(long total, double nowSeconds)
        {
            double rate = 0;
            if (_lastTotal >= 0 && nowSeconds > _lastSeconds && total >= _lastTotal)
                rate = (total - _lastTotal) / (nowSeconds - _lastSeconds);
            _lastTotal = total;
            _lastSeconds = nowSeconds;
            return rate;
        }
    }

    /*
     * The 1.12 costs the telemetry block reports. Each site records only
     * while KerbcastSettings.EnableTelemetry is on, so with telemetry off they
     * cost one bool check. Field names and meanings are documented on
     * KerbcastCore.AppendTelemetry.
     */
    internal static class Telemetry
    {
        public static readonly CostMeter RingTick = new CostMeter();
        public static readonly CostMeter Picture = new CostMeter();
        public static readonly CostMeter MetaBytes = new CostMeter();
        public static readonly CostMeter MetaEncode = new CostMeter();
        public static readonly CostMeter WorldModelFill = new CostMeter();
        public static readonly CostMeter LiveLight = new CostMeter();
        public static readonly CostMeter Save = new CostMeter();
        public static readonly CostMeter Load = new CostMeter();
        public static readonly RateMeter RingEntries = new RateMeter();

        public static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }
}
