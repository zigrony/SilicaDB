using System;
using Silica.Exceptions;

namespace Silica.Sessions
{
    public sealed class SessionExpiredException : SilicaException
    {
        public Guid SessionId { get; }

        public SessionExpiredException(Guid sessionId)
            : base(
                code: SessionExceptions.SessionExpired.Code,
                message: $"Session {sessionId} expired by admin or non-idle policy.",
                category: SessionExceptions.SessionExpired.Category,
                exceptionId: SessionExceptions.Ids.SessionExpired)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            SessionId = sessionId;
        }
    }
}
