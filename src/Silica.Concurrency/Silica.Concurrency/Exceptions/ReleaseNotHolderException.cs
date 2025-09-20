using System;
using Silica.Exceptions;

namespace Silica.Concurrency
{
    public sealed class ReleaseNotHolderException : SilicaException
    {
        public string ResourceId { get; }
        public long TransactionId { get; }

        public ReleaseNotHolderException(string resourceId, long txId)
            : base(
                code: ConcurrencyExceptions.ReleaseNotHolder.Code,
                message: $"Release called with a valid fencing token, but the transaction is not a holder of resource '{resourceId}'.",
                category: ConcurrencyExceptions.ReleaseNotHolder.Category,
                exceptionId: ConcurrencyExceptions.Ids.ReleaseNotHolder)
        {
            ConcurrencyExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ResourceId = resourceId ?? string.Empty;
            TransactionId = txId;
        }
    }
}
