// File: Silica.DiagnosticsCore/Instrumentation/TimingScope.cs
using System;
using System.Diagnostics;
using System.Threading;

namespace Silica.DiagnosticsCore.Instrumentation
{
    /// <summary>
    /// Measures the duration of an operation and emits its timing data
    /// when the scope completes or is disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Intended for use in a <c>using</c> statement or with explicit <see cref="Complete"/> calls.
    /// </para>
    /// <para>
    /// Thread-safe for concurrent reads of properties. Completion is idempotent and safe under race.
    /// The scope itself should be used by a single logical flow, but Complete/Dispose are safe
    /// if called from multiple threads.
    /// </para>
    /// </remarks>
    public sealed class TimingScope : ITimingScope
    {
        private readonly Action<TimingScope> _onComplete;
        private readonly Action<Exception>? _onError;
        private readonly Stopwatch? _stopwatch;
        private readonly DateTimeOffset _startWallClock;
        private readonly bool _useHighRes; private int _completed;

        /// <inheritdoc />
        public string Operation { get; }

        /// <inheritdoc />
        public string Component { get; }

        /// <inheritdoc />
        public DateTimeOffset StartTime { get; }

        /// <inheritdoc />
        public TimeSpan Elapsed => _useHighRes
            ? _stopwatch?.Elapsed ?? TimeSpan.Zero
            : DateTimeOffset.UtcNow - _startWallClock;
        /// <summary>
        /// Initializes a new timing scope and starts measuring immediately.
        /// </summary>
        /// <param name="component">The component or subsystem being measured. Cannot be null/empty.</param>
        /// <param name="operation">The operation name. Cannot be null/empty.</param>
        /// <param name="onComplete">
        /// A callback invoked exactly once when the scope completes.
        /// Receives the completed scope instance for emission to sinks.
        /// </param>
        public TimingScope(string component, string operation, Action<TimingScope> onComplete, bool useHighResolutionTiming = true)
        {
            if (string.IsNullOrWhiteSpace(component))
                throw new ArgumentException("Component cannot be null or whitespace.", nameof(component));
            if (string.IsNullOrWhiteSpace(operation))
                throw new ArgumentException("Operation cannot be null or whitespace.", nameof(operation));

            Component = component;
            Operation = operation;
            _onComplete = onComplete ?? throw new ArgumentNullException(nameof(onComplete));

            _useHighRes = useHighResolutionTiming;
            StartTime = DateTimeOffset.UtcNow;
            if (_useHighRes)
                _stopwatch = Stopwatch.StartNew();
            else
                _startWallClock = DateTimeOffset.UtcNow;        
        }

        /// <summary>
        /// Initializes a new timing scope with an error callback for completion failures.
        /// </summary>
        /// <param name="component">The component or subsystem being measured.</param>
        /// <param name="operation">The operation name.</param>
        /// <param name="onComplete">The completion callback.</param>
        /// <param name="onError">Optional callback for handling exceptions from <paramref name="onComplete"/>.</param>
        public TimingScope(string component, string operation, Action<TimingScope> onComplete, Action<Exception> onError)
            : this(component, operation, onComplete)
        {
            _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        }

        /// <inheritdoc />
        public void Complete()
        {
            // Make idempotent and thread-safe
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            if (_useHighRes)
                _stopwatch.Stop();
            try
            {
                _onComplete(this);
            }
            catch (Exception ex)
            {
                // Never allow instrumentation failures to bubble up
                _onError?.Invoke(ex);
                Debug.Fail($"TimingScope onComplete error for {Component}/{Operation}: {ex}");
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Complete();
            // No finalizer present; SuppressFinalize not needed
        }
    }
}
