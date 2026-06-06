// ReadbackTargetTracker — keeps the "current" render-target size separate from
// the size a readback was *issued* against ("pending"), so a resolution change
// between issuing a readback and processing it cannot make the process step use
// mismatched (current) dimensions and write a wrong-sized frame to the ring.
//
// Deliberately Unity-free so a standalone test project can exercise the
// decoupling (same approach as ControlBlock.cs / ShedController.cs).
//
// Correctness rests on KerbcamCamera's one-readback-in-flight-at-a-time
// invariant: CapturePending() is only called when no readback is pending, so
// the pending snapshot is never clobbered before it is consumed.

namespace Kerbcam
{
    public sealed class ReadbackTargetTracker
    {
        public int CurrentWidth { get; private set; }
        public int CurrentHeight { get; private set; }

        public int PendingWidth { get; private set; }
        public int PendingHeight { get; private set; }

        /// <summary>Record the render-target size that is now current (called
        /// when the pooled set is selected / resolution changes).</summary>
        public void SetCurrent(int width, int height)
        {
            CurrentWidth = width;
            CurrentHeight = height;
        }

        /// <summary>Snapshot the current size as the one a readback is being
        /// issued against. The snapshot is independent storage — later
        /// SetCurrent calls (a resize) do not change it.</summary>
        public void CapturePending()
        {
            PendingWidth = CurrentWidth;
            PendingHeight = CurrentHeight;
        }
    }
}
