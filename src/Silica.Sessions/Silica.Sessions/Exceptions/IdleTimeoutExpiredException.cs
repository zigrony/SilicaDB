using System;
using Silica.Exceptions;

namespace Silica.Sessions
{
    public sealed class IdleTimeoutExpiredException : SilicaException
    {
        public Guid SessionId { get; }
        public TimeSpan IdleTimeout { get; }

        public IdleTimeoutExpiredException(Guid sessionId, TimeSpan idleTimeout)
            : base(
                code: SessionExceptions.IdleTimeoutExpired.Code,
                message: $"Session {sessionId} expired due to idle timeout ({idleTimeout}).",
                category: SessionExceptions.IdleTimeoutExpired.Category,
                exceptionId: SessionExceptions.Ids.IdleTimeoutExpired)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            SessionId = sessionId;
            IdleTimeout = idleTimeout;
        }
    }
}
