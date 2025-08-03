using System;
using System.Threading;
using System.Threading.Tasks;

namespace SilicaDB.Devices.Interfaces
{
    /// <summary>
    /// A block‐based storage device organized in fixed‐size frames.
    /// </summary>
    public interface IStorageDevice : IAsyncDisposable
    {
        /// <summary>
        /// Prepare the device for I/O.  Must be called before any Read/WriteFrameAsync calls.
        /// </summary>
        Task MountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Tear down the device, releasing all resources.  No further I/O calls are allowed.
        /// </summary>
        Task UnmountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads exactly FrameSize bytes from frame &apos;frameId&apos;. 
        /// If the underlying medium is shorter, the remainder is zero‐filled.
        /// </summary>
        Task<byte[]> ReadFrameAsync(long frameId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes exactly FrameSize bytes into frame &apos;frameId&apos;, overwriting any existing data.
        /// </summary>
        Task WriteFrameAsync(long frameId, byte[] data, CancellationToken cancellationToken = default);
    }
}
