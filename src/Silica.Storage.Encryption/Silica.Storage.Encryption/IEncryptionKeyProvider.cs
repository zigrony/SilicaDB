using System;

namespace Silica.Storage.Encryption
{
    public interface IEncryptionKeyProvider
    {
        // Returns a 16, 24, or 32-byte AES key (recommended: 32 bytes).
        ReadOnlySpan<byte> GetKey();

        // Returns a 16-byte device salt used to derive per-frame counters.
        // Must be stable across process restarts for the same device.
        ReadOnlySpan<byte> GetDeviceSalt();
    }
}
