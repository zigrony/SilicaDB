using System;
using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    public sealed class EvictionInvalidIdleTimeoutException : SilicaException
    {
        public TimeSpan IdleTimeout { get; }

        public EvictionInvalidIdleTimeoutException(TimeSpan idleTimeout)
            : base(
                code: EvictionExceptions.InvalidIdleTimeout.Code,
                message: $"Invalid idle timeout: {idleTimeout}",
                category: EvictionExceptions.InvalidIdleTimeout.Category,
                exceptionId: EvictionExceptions.Ids.InvalidIdleTimeout)
        {
            EvictionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            IdleTimeout = idleTimeout;
        }
    }
}
