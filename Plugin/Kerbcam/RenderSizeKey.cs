// RenderSizeKey — packs a (width, height) render size into a single long key
// for the per-camera RenderTexture pool (KerbcamCamera._rtPool). Deliberately
// Unity-free so it can be unit-tested standalone (same approach as
// ControlBlock.cs / ShedController.cs).
//
// Width and height are bounded by KerbcamCamera.MaxWidth/MaxHeight — both far
// below 2^31 — so packing width into the high 32 bits and height into the low
// 32 bits is a collision-free, reversible mapping. Collision-freedom matters:
// a key clash would alias two different render sizes onto the same pooled
// RenderTexture and produce corrupt / wrong-sized frames.

namespace Kerbcam
{
    public static class RenderSizeKey
    {
        public static long Pack(int width, int height) =>
            ((long)width << 32) | (uint)height;

        public static int Width(long key) => (int)(key >> 32);

        public static int Height(long key) => (int)(key & 0xFFFFFFFFL);
    }
}
