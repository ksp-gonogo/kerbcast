// ReadbackTargetTracker — keeps the "current" render-target size/scratch
// separate from the size/scratch a readback was *issued* against ("pending"),
// so a resolution change between issuing a readback and processing it cannot
// make the process step use mismatched (current) dimensions and corrupt the
// in-flight frame.
//
// Deliberately Unity-free and generic over the scratch reference type so a
// standalone test project can exercise the decoupling with a plain int id
// standing in for the Texture2D (same approach as ControlBlock.cs /
// ShedController.cs). KerbcamCamera uses ReadbackTargetTracker<Texture2D>.
//
// Correctness rests on KerbcamCamera's one-readback-in-flight-at-a-time
// invariant: CapturePending() is only called when no readback is pending, so
// the pending snapshot is never clobbered before it is consumed.

namespace Kerbcam
{
    public sealed class ReadbackTargetTracker<T>
    {
        public int CurrentWidth { get; private set; }
        public int CurrentHeight { get; private set; }
        public T CurrentScratch { get; private set; }

        public int PendingWidth { get; private set; }
        public int PendingHeight { get; private set; }
        public T PendingScratch { get; private set; }

        /// <summary>Record the render target that is now current (called when
        /// the pooled set is selected / resolution changes).</summary>
        public void SetCurrent(int width, int height, T scratch)
        {
            CurrentWidth = width;
            CurrentHeight = height;
            CurrentScratch = scratch;
        }

        /// <summary>Snapshot the current target as the one a readback is being
        /// issued against. The snapshot is independent storage — later
        /// SetCurrent calls (a resize) do not change it.</summary>
        public void CapturePending()
        {
            PendingWidth = CurrentWidth;
            PendingHeight = CurrentHeight;
            PendingScratch = CurrentScratch;
        }
    }
}
