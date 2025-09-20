using System;
using Silica.Exceptions;

namespace Silica.Concurrency
{
    public sealed class FencingTokenOverflowException : SilicaException
    {
        public string ResourceId { get; }

        public FencingTokenOverflowException(string resourceId, long lastIssued)
            : base(
                code: ConcurrencyExceptions.FencingTokenOverflow.Code,
                message: $"Fencing token overflow for resource '{resourceId}'. LastIssued={lastIssued}.",
                category: ConcurrencyExceptions.FencingTokenOverflow.Category,
                exceptionId: ConcurrencyExceptions.Ids.FencingTokenOverflow)
        {
            ConcurrencyExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ResourceId = resourceId ?? string.Empty;
        }
    }
}
