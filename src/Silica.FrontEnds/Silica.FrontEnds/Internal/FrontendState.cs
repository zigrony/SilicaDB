// File: Silica.FrontEnds/Internal/FrontendState.cs
using System.Threading;

namespace Silica.FrontEnds.Internal
{
    /// <summary>
    /// Internal active frontend count to back the observable gauge.
    /// </summary>
    internal static class FrontendState
    {
        private static long _activeCount = 0;

        internal static void IncrementActive() => Interlocked.Increment(ref _activeCount);

        internal static void DecrementActive()
        {
            var v = Interlocked.Decrement(ref _activeCount);
            if (v < 0) Interlocked.Exchange(ref _activeCount, 0);
        }

        internal static long GetActiveCount() => Interlocked.Read(ref _activeCount);
    }
}
