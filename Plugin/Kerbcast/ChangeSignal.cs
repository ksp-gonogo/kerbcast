using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Kerbcast
{
    /* A counter the sidecar bumps after each write of a file the plugin
       polls, read from shared memory in one load. It replaces comparing the
       file's write time, which Mono on Linux reports to the whole second.
       The layout matches sidecar/src/shared_mem/signal.rs: a u64 magic, then
       the u64 count. */
    internal sealed class ChangeSignal : IDisposable
    {
        public const ulong Magic = 0x0000_3147_4953_434BUL; // "KCSIG1\0\0" LE
        public const int Size = 16;

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly unsafe long* _count;

        private unsafe ChangeSignal(MemoryMappedFile mmf, MemoryMappedViewAccessor view)
        {
            _mmf = mmf;
            _view = view;
            byte* basePtr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
            _count = (long*)(basePtr + 8);
        }

        /* Null while the file is missing, short or not yet stamped with the
           magic: the caller tries again later. */
        public static ChangeSignal Open(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length < Size) return null;
            FileStream fs = null;
            MemoryMappedFile mmf = null;
            MemoryMappedViewAccessor view = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                mmf = MemoryMappedFile.CreateFromFile(fs, mapName: null, capacity: Size,
                    access: MemoryMappedFileAccess.ReadWrite, inheritability: HandleInheritability.None, leaveOpen: false);
                fs = null;
                view = mmf.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
                bool stamped = view.ReadUInt64(0) == Magic;
                /* Pairs with the sidecar's release store of the magic: the count read after this is the one it zeroed or later. */
                Thread.MemoryBarrier();
                if (!stamped)
                {
                    view.Dispose();
                    mmf.Dispose();
                    return null;
                }
                return new ChangeSignal(mmf, view);
            }
            catch
            {
                view?.Dispose();
                mmf?.Dispose();
                fs?.Dispose();
                throw;
            }
        }

        public unsafe long Count => Interlocked.Read(ref *_count);

        public void Dispose()
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mmf.Dispose();
        }
    }
}
