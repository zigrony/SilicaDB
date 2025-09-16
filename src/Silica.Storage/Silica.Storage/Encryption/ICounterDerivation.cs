using System;

namespace Silica.Storage.Encryption
{
    internal interface ICounterDerivation
    {
        // Fills a 16-byte counter block for the given frameId using the 16-byte device salt.
        void Derive(ReadOnlySpan<byte> salt16, long frameId, Span<byte> counter16);
    }
}
