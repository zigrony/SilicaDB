// FrameSlot holds the bytes plus metadata
using SilicaDB.Devices.Interfaces;
using SilicaDB.Devices;
using SilicaDB.Evictions.Interfaces;
using SilicaDB.Evictions;
using SilicaDB.Storage;

namespace SilicaDB.BufferPool
{
    /// <summary>
    /// Holds one loaded page, tracks its pin count & dirty bit.
    /// Made public so BufferPoolManager ctor stays public.
    /// </summary>
    public class FrameSlot
    {
        private int _pinCount;

        public Page Page { get; }
        public bool IsDirty { get; private set; }

        public FrameSlot(Page page)
        {
            Page = page ?? throw new ArgumentNullException(nameof(page));
        }

        public void Pin() => Interlocked.Increment(ref _pinCount);

        public void Unpin(bool dirty)
        {
            if (dirty) IsDirty = true;
            var cnt = Interlocked.Decrement(ref _pinCount);
            if (cnt < 0)
                throw new InvalidOperationException("Unbalanced Unpin");
        }

        public void MarkClean() => IsDirty = false;
    }
}