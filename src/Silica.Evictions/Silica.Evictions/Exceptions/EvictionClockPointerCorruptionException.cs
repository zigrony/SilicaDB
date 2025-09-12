using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionClockPointerCorruptionException : SilicaException
    {
        public EvictionClockPointerCorruptionException(string? details = null)
            : base(
                code: EvictionExceptions.ClockPointerCorruption.Code,
                message: details ?? "CLOCK cache pointer state is invalid.",
                category: EvictionExceptions.ClockPointerCorruption.Category,
                exceptionId: EvictionExceptions.Ids.ClockPointerCorruption)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
