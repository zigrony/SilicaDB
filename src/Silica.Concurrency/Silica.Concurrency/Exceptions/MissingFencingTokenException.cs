using System;
using Silica.Exceptions;

namespace Silica.Concurrency
{
    public sealed class MissingFencingTokenException : SilicaException
    {
        public string ResourceId { get; }
        public long TransactionId { get; }

        public MissingFencingTokenException(string resourceId, long txId)
            : base(
                code: ConcurrencyExceptions.MissingFencingToken.Code,
                message: $"Missing fencing token mapping for non-first hold on resource '{resourceId}', tx {txId}.",
                category: ConcurrencyExceptions.MissingFencingToken.Category,
                exceptionId: ConcurrencyExceptions.Ids.MissingFencingToken)
        {
            ConcurrencyExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ResourceId = resourceId ?? string.Empty;
            TransactionId = txId;
        }
    }
}
