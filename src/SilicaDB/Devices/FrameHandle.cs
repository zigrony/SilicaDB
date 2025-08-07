// FrameSlot holds the bytes plus metadata
using SilicaDB.Devices.Interfaces;
using SilicaDB.Devices;
using SilicaDB.Evictions.Interfaces;
using SilicaDB.Evictions;
using SilicaDB.BufferPool;

namespace SilicaDB.BufferPool
{
    // The handle you hand callers—they must Dispose() it when done
    class FrameHandle : IDisposable
    {
        public long FrameId { get; }
        public FrameSlot Slot { get; }
        private readonly Action<FrameHandle, bool> _onUnfix;

        public FrameHandle(long id, FrameSlot slot, Action<FrameHandle, bool> onUnfix)
        {
            FrameId = id; Slot = slot; _onUnfix = onUnfix;
        }

        // Marks page dirty and unpins
        public void Dispose() => _onUnfix(this, true);
    }
}