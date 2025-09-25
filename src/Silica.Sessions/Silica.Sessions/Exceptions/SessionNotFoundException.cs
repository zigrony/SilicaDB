using System;
using Silica.Exceptions;

namespace Silica.Sessions
{
    public sealed class SessionNotFoundException : SilicaException
    {
        public Guid SessionId { get; }

        public SessionNotFoundException(Guid sessionId)
            : base(
                code: SessionExceptions.SessionNotFound.Code,
                message: $"Session not found: {sessionId}",
                category: SessionExceptions.SessionNotFound.Category,
                exceptionId: SessionExceptions.Ids.SessionNotFound)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            SessionId = sessionId;
        }
    }
}
