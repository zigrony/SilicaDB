// Filename: Silica.DiagnosticsCore/Exceptions/ShutdownTimeoutException.cs
using Silica.Exceptions;

namespace Silica.DiagnosticsCore
{
    public sealed class ShutdownTimeoutException : SilicaException
    {
        public int TimeoutMs { get; }
        public long RemainingQueueDepth { get; }
        public int PumpsRemaining { get; }

        public ShutdownTimeoutException(int timeoutMs, long remainingQueueDepth, int pumpsRemaining)
            : base(
                code: DiagnosticsCoreExceptions.ShutdownTimeout.Code,
                message: $"Dispatcher shutdown exceeded {timeoutMs} ms. remaining_depth={remainingQueueDepth}, pumps_remaining={pumpsRemaining}.",
                category: DiagnosticsCoreExceptions.ShutdownTimeout.Category,
                exceptionId: DiagnosticsCoreExceptions.Ids.ShutdownTimeout)
        {
            DiagnosticsCoreExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            TimeoutMs = timeoutMs;
            RemainingQueueDepth = remainingQueueDepth;
            PumpsRemaining = pumpsRemaining;
        }
    }
}
