using System;
using Silica.Exceptions;

namespace Silica.Concurrency
{
    public sealed class ReentrantHoldOverflowException : SilicaException
    {
        public long TransactionId { get; }
        public string ResourceId { get; }

        public ReentrantHoldOverflowException(long txId, string resourceId)
            : base(
                code: ConcurrencyExceptions.ReentrantHoldOverflow.Code,
                message: $"Reentrant hold count overflow for tx {txId} on resource '{resourceId}'.",
                category: ConcurrencyExceptions.ReentrantHoldOverflow.Category,
                exceptionId: ConcurrencyExceptions.Ids.ReentrantHoldOverflow)
        {
            ConcurrencyExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            TransactionId = txId;
            ResourceId = resourceId ?? string.Empty;
        }
    }
}
