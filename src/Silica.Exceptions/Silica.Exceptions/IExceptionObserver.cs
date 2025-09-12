// Filename: Silica.Exceptions/Silica.Exceptions/Observers/IExceptionObserver.cs
using System;
using System.Threading;

namespace Silica.Exceptions
{
    /// <summary>
    /// Optional observer for exception lifecycle events. Useful to auto-emit traces/metrics.
    /// Keep observers lightweight; push heavy work to background pipelines.
    /// </summary>
    public interface IExceptionObserver
    {
        void OnCreated(SilicaException ex);
    }

    internal static class ExceptionNotifier
    {
        private static volatile IExceptionObserver[] _observers = Array.Empty<IExceptionObserver>();
        private static readonly object _gate = new();
        private static long _observerErrorCount; // monotonically increasing
        // Monotonic observer lifecycle version: bumps on add/remove and on registry freeze of observers.
        private static long _observerVersion;
        // Freeze state is owned here to avoid circular dependency on the registry.
        private static volatile bool _frozen;
        // Timestamp for freeze events in UTC ticks for diagnostics/correlation.
        private static long _frozenAtUtcTicks;
        // Optional safety fuse: when > 0, reaching this many cumulative observer errors auto-freezes observers.
        private static int _errorAutoFreezeThreshold; // 0 = disabled
        // Reason/metadata for the last freeze to aid diagnostics.
        private enum FrozenReason { None = 0, Manual = 1, AutoThreshold = 2 }
        private static FrozenReason _frozenReason;
        private static int _frozenThresholdAtFreeze;

        public static void AddObserver(IExceptionObserver observer)
        {
            if (observer is null) return;
            lock (_gate)
            {
                if (_frozen) return;
                var current = _observers;
                // Prevent duplicates
                int i = 0;
                while (i < current.Length)
                {
                    if (object.ReferenceEquals(current[i], observer)) return;
                    i++;
                }
                var updated = new IExceptionObserver[current.Length + 1];
                Array.Copy(current, updated, current.Length);
                updated[current.Length] = observer;
                _observers = updated;
                IncrementSaturating(ref _observerVersion);
            }
        }

        public static bool TryAddObserver(IExceptionObserver observer)
        {
            if (observer is null) return false;
            lock (_gate)
            {
                if (_frozen) return false;
                var current = _observers;
                int i = 0;
                while (i < current.Length)
                {
                    if (object.ReferenceEquals(current[i], observer)) return false;
                    i++;
                }
                var updated = new IExceptionObserver[current.Length + 1];
                Array.Copy(current, updated, current.Length);
                updated[current.Length] = observer;
                _observers = updated;
                IncrementSaturating(ref _observerVersion);
                return true;
            }
        }

        public static void RemoveObserver(IExceptionObserver observer)
        {
            if (observer is null) return;
            lock (_gate)
            {
                if (_frozen) return;
                var current = _observers;
                int idx = -1;
                int i = 0;
                while (i < current.Length)
                {
                    if (object.ReferenceEquals(current[i], observer)) { idx = i; break; }
                    i++;
                }
                if (idx < 0) return;
                if (current.Length == 1) 
                { 
                    _observers = Array.Empty<IExceptionObserver>();
                    IncrementSaturating(ref _observerVersion);
                    return; 
                }
                var updated = new IExceptionObserver[current.Length - 1];
                if (idx > 0) Array.Copy(current, 0, updated, 0, idx);
                if (idx < current.Length - 1) Array.Copy(current, idx + 1, updated, idx, current.Length - idx - 1);
                _observers = updated;
                IncrementSaturating(ref _observerVersion);
            }
        }

        public static bool TryRemoveObserver(IExceptionObserver observer)
        {
            if (observer is null) return false;
            lock (_gate)
            {
                if (_frozen) return false;
                var current = _observers;
                int idx = -1;
                int i = 0;
                while (i < current.Length)
                {
                    if (object.ReferenceEquals(current[i], observer)) { idx = i; break; }
                    i++;
                }
                if (idx < 0) return false;
                if (current.Length == 1) 
                { 
                    _observers = Array.Empty<IExceptionObserver>();
                    IncrementSaturating(ref _observerVersion);
                    return true; 
                }
                var updated = new IExceptionObserver[current.Length - 1];
                if (idx > 0) Array.Copy(current, 0, updated, 0, idx);
                if (idx < current.Length - 1) Array.Copy(current, idx + 1, updated, idx, current.Length - idx - 1);
                _observers = updated;
                IncrementSaturating(ref _observerVersion);
                return true;
            }
        }

        internal static void NotifyCreated(SilicaException ex)
        {
            if (ex is null) return;
            // If observers are frozen, suppress notifications entirely to honor the safety fuse and explicit freezes.
            if (_frozen) return;

            var snapshot = _observers; // volatile read
            if (snapshot.Length == 0) return;
            int i = 0;
            while (i < snapshot.Length)
            {
                try { snapshot[i].OnCreated(ex); }
                catch
                {
                    var after = IncrementSaturating(ref _observerErrorCount);
                    // Safety fuse: auto-freeze observers once the threshold is crossed.
                    // Read threshold once without extra interlocked ops; check > 0 to keep disabled-by-default.
                    var threshold = Volatile.Read(ref _errorAutoFreezeThreshold);
                    if (threshold > 0 && after >= threshold)
                    {
                        // Avoid repeated locks on every error after trip: quick check first.
                        if (!_frozen)
                        {
                            FreezeObserversCore(FrozenReason.AutoThreshold);
                            // Stop notifying further observers in this call after the fuse trips.
                            break;
                        }
                    }
                }
                i++;
            }
        }

        // Expose a cheap, thread-safe view for diagnostics.
        public static long ObserverErrorCount => Volatile.Read(ref _observerErrorCount);
        public static int ObserverCount
        {
            get
            {
                // Volatile array ensures visibility and length is atomic read.
                var snapshot = _observers;
                return snapshot.Length;
            }
        }

        // Internal bridge for the registry to read/bump this version without duplicating counters.
        internal static long ObserverVersion => Volatile.Read(ref _observerVersion);
        internal static bool AreFrozen => Volatile.Read(ref _frozen);
        internal static long FrozenAtUtcTicks => Volatile.Read(ref _frozenAtUtcTicks);
        internal static bool AreFrozenDueToAutoThreshold
        {
            get
            {
                // Reason is mutated under _gate; reads are rare, cost of lock is acceptable and consistent with other snapshots.
                lock (_gate) { return _frozenReason == FrozenReason.AutoThreshold; }
            }
        }
        internal static int FrozenThresholdAtFreeze
        {
            get
            {
                lock (_gate) { return _frozenThresholdAtFreeze; }
            }
        }

        internal static void FreezeObservers()
        {
            FreezeObserversCore(FrozenReason.Manual);
        }

        internal static bool TryFreezeObservers()
        {
            lock (_gate)
            {
                if (_frozen) return false;
                // Core will set state and clear observers with the correct reason.
                FreezeObserversCoreLocked(FrozenReason.Manual);
                return true;
            }
        }

        // Internal core to set freeze state with a reason and snapshot the active threshold.
        private static void FreezeObserversCore(FrozenReason reason)
        {
            lock (_gate)
            {
                if (_frozen) return;
                FreezeObserversCoreLocked(reason);
            }
        }
        private static void FreezeObserversCoreLocked(FrozenReason reason)
        {
            // Requires caller to hold _gate.
            _frozen = true;
            _frozenAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
            _frozenReason = reason;
            _frozenThresholdAtFreeze = Volatile.Read(ref _errorAutoFreezeThreshold);
            // Clear observer list to release references and guarantee no further notifications.
            _observers = Array.Empty<IExceptionObserver>();
            // Bump lifecycle version so readers can invalidate caches on freeze and state change.
            IncrementSaturating(ref _observerVersion);
        }

        internal static void ClearForTests()
        {
            lock (_gate)
            {
                _observers = Array.Empty<IExceptionObserver>();
                _observerErrorCount = 0;
                _observerVersion = 0;
                _frozen = false;
                _frozenAtUtcTicks = 0;
                _errorAutoFreezeThreshold = 0;
                _frozenReason = FrozenReason.None;
                _frozenThresholdAtFreeze = 0;
            }
        }

        // Configuration surface for the safety fuse (registry exposes this).
        internal static int ObserverErrorAutoFreezeThreshold
        {
            get { return Volatile.Read(ref _errorAutoFreezeThreshold); }
            set
            {
                // 0 disables. Negative coerced to 0 to avoid undefined behavior.
                if (value < 0) value = 0;
                Volatile.Write(ref _errorAutoFreezeThreshold, value);
                // If enabling or lowering the threshold and we've already passed it, freeze immediately
                // to avoid waiting for the next observer error event (faster risk containment).
                if (value > 0)
                {
                    var count = Volatile.Read(ref _observerErrorCount);
                    if (count >= value && !_frozen)
                    {
                        FreezeObservers();
                    }
                }
            }
        }

        // Provide a coherent state snapshot for diagnostics/registries that need consistency
        // across count/version/frozen/errorCount. Locking _gate keeps this cheap and reliable.
        internal static void GetObserverState(
            out int count,
            out long version,
            out bool frozen,
            out long errorCount)
        {
            lock (_gate)
            {
                // Read all under the same lock used for lifecycle mutations.
                var snapshot = _observers;
                count = snapshot.Length;
                version = _observerVersion;
                frozen = _frozen;
                errorCount = _observerErrorCount;
            }
        }
        // Overload including freeze reason and threshold-at-freeze for richer diagnostics (non-breaking addition).
        internal static void GetObserverState(
            out int count,
            out long version,
            out bool frozen,
            out long errorCount,
            out bool frozenDueToAutoThreshold,
            out int thresholdAtFreeze)
        {
            lock (_gate)
            {
                var snapshot = _observers;
                count = snapshot.Length;
                version = _observerVersion;
                frozen = _frozen;
                errorCount = _observerErrorCount;
                frozenDueToAutoThreshold = _frozenReason == FrozenReason.AutoThreshold;
                thresholdAtFreeze = _frozenThresholdAtFreeze;
            }
        }

        // Monotonic saturating increment to avoid wraparound on very long-lived processes/tests.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static long IncrementSaturating(ref long location)
        {
            while (true)
            {
                long current = Volatile.Read(ref location);
                if (current == long.MaxValue) return long.MaxValue;
                long next = current + 1;
                long observed = Interlocked.CompareExchange(ref location, next, current);
                if (observed == current) return next;
            }
        }
    }
}
