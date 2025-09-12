using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionLfuInvariantViolationException : SilicaException
    {
        public EvictionLfuInvariantViolationException(string? details = null)
            : base(
                code: EvictionExceptions.LfuInvariantViolation.Code,
                message: details ?? "LFU cache internal state is inconsistent.",
                category: EvictionExceptions.LfuInvariantViolation.Category,
                exceptionId: EvictionExceptions.Ids.LfuInvariantViolation)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
