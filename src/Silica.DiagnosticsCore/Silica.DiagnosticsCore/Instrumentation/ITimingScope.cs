// File: Silica.DiagnosticsCore/Instrumentation/ITimingScope.cs
using System;

namespace Silica.DiagnosticsCore.Instrumentation
{
    /// <summary>
    /// Represents a timing scope that measures the duration of an operation,
    /// intended for use in a <c>using</c> block or via an explicit <see cref="Complete"/> call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations should be thread-safe for concurrent reads of properties,
    /// but the scope itself is expected to be used by a single logical flow.
    /// </para>
    /// <para>
    /// Callers should dispose or complete scopes promptly to avoid skewed metrics.
    /// Implementations must ensure that <see cref="Complete"/> is idempotent
    /// and safe to call from <see cref="Dispose"/>.
    /// </para>
    /// </remarks>
    public interface ITimingScope : IDisposable
    {
        /// <summary>
        /// Gets the name or identifier of the operation being measured.
        /// Guaranteed non-null and non-empty in well-formed scopes.
        /// </summary>
        string Operation { get; }

        /// <summary>
        /// Gets the component or subsystem emitting this timing measurement.
        /// Guaranteed non-null and non-empty in well-formed scopes.
        /// </summary>
        string Component { get; }

        /// <summary>
        /// Gets the UTC start time of the timing scope.
        /// </summary>
        DateTimeOffset StartTime { get; }

        /// <summary>
        /// Gets the elapsed time for the operation so far.
        /// If called after completion/disposal, returns the final recorded duration.
        /// </summary>
        TimeSpan Elapsed { get; }

        /// <summary>
        /// Marks the scope as complete, stopping measurement and emitting timing data
        /// to the configured instrumentation sinks.
        /// </summary>
        /// <remarks>
        /// Implementations must be idempotent: multiple calls to <see cref="Complete"/>
        /// should have no additional effect after the first, and must not throw.
        /// </remarks>
        void Complete();
    }
}
