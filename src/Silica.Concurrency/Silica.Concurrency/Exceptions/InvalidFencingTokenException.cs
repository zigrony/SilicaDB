using System;
using Silica.Exceptions;

namespace Silica.Concurrency
{
    public sealed class InvalidFencingTokenException : SilicaException
    {
        public string ResourceId { get; }
        public long Expected { get; }
        public long Provided { get; }

        public InvalidFencingTokenException(string resourceId, long expected, long provided)
            : base(
                code: ConcurrencyExceptions.InvalidFencingToken.Code,
                message: $"Invalid or stale fencing token for resource '{resourceId}'. Expected {expected}, got {provided}.",
                category: ConcurrencyExceptions.InvalidFencingToken.Category,
                exceptionId: ConcurrencyExceptions.Ids.InvalidFencingToken)
        {
            ConcurrencyExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ResourceId = resourceId ?? string.Empty;
            Expected = expected;
            Provided = provided;
        }
    }
}
