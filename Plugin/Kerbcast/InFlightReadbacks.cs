// InFlightReadbacks: the readbacks one camera has issued and not yet drained,
// oldest first, each with the render-target size and game frame it was issued
// at. A resolution change while readbacks are in flight therefore cannot make
// the drain write a frame at the wrong (current) size.
//
// More than one readback may be in flight because on the Deck a readback takes
// two to four game frames to land. With one at a time a camera could capture
// on at most every third or fourth frame however few cameras were streaming,
// which held every feed near 17fps on a 50fps game. Readbacks complete in the
// order they were issued, so they drain from the front.
//
// Deliberately Unity-free so a standalone test project can exercise it (same
// approach as ControlBlock.cs / ShedController.cs). The caller keeps the rest
// of each readback's data in its own arrays, indexed by the slot returned here.

using System;

namespace Kerbcast
{
    public sealed class InFlightReadbacks
    {
        private readonly int[] _width;
        private readonly int[] _height;
        private readonly int[] _issuedFrame;
        private int _oldest;
        private int _count;

        public InFlightReadbacks(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _width = new int[capacity];
            _height = new int[capacity];
            _issuedFrame = new int[capacity];
        }

        public int Capacity => _width.Length;
        public int Count => _count;
        public bool Full => _count == _width.Length;

        public int CurrentWidth { get; private set; }
        public int CurrentHeight { get; private set; }

        /// <summary>Record the render-target size that is now current (called
        /// when the pooled set is selected / resolution changes).</summary>
        public void SetCurrent(int width, int height)
        {
            CurrentWidth = width;
            CurrentHeight = height;
        }

        /// <summary>The slot of the oldest readback in flight, or -1 when none.</summary>
        public int Oldest => _count > 0 ? _oldest : -1;

        public int Width(int slot) => _width[slot];
        public int Height(int slot) => _height[slot];
        public int IssuedFrame(int slot) => _issuedFrame[slot];

        /// <summary>Take the next slot for a readback issued on <paramref
        /// name="frame"/> against the current size, and return it. The size is a
        /// snapshot: a later SetCurrent does not change it.</summary>
        public int Issue(int frame)
        {
            if (Full) throw new InvalidOperationException("every readback slot is in flight");
            int slot = (_oldest + _count) % _width.Length;
            _width[slot] = CurrentWidth;
            _height[slot] = CurrentHeight;
            _issuedFrame[slot] = frame;
            _count++;
            return slot;
        }

        /// <summary>Retire the oldest readback, once drained.</summary>
        public void Land()
        {
            if (_count == 0) return;
            _oldest = (_oldest + 1) % _width.Length;
            _count--;
        }
    }
}
