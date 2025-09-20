using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class WalSequenceOverflowException : SilicaException
    {
        public long CurrentValue { get; }

        public WalSequenceOverflowException(long currentValue)
            : base(
                code: DurabilityExceptions.WalSequenceOverflow.Code,
                message: "WAL sequence number overflow: cannot allocate a new LSN.",
                category: DurabilityExceptions.WalSequenceOverflow.Category,
                exceptionId: DurabilityExceptions.Ids.WalSequenceOverflow)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            CurrentValue = currentValue;
        }
    }
}
