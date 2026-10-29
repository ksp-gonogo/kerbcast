namespace Kerbcast
{
    /*
     * Monotonic counter stamped into global.status.json's captureEpoch. The
     * plugin bumps it on a KSP mission-time discontinuity (revert, quickload,
     * scene reload) so a consumer of the capture clock can flush its buffer
     * and resync rather than wait for a universal time that will never arrive.
     * Only the change is meaningful; the absolute value carries no meaning.
     *
     * Unity-free by design so it unit-tests standalone (CaptureEpoch.Tests).
     */
    public sealed class CaptureEpoch
    {
        /* The game's one epoch. It outlives the per-scene addons that bump and
         * stamp it, so a scene load or revert moves it on rather than starting
         * the next scene back at the value the last one began with. */
        public static readonly CaptureEpoch Game = new CaptureEpoch();

        private uint _value;

        /* Current epoch. Stable until the next Bump(). */
        public uint Value => _value;

        /* Record a discontinuity. Wraps harmlessly at uint.MaxValue since
         * consumers only compare for inequality. */
        public void Bump()
        {
            unchecked { _value++; }
        }
    }
}
