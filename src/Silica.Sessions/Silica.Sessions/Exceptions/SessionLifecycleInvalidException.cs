// File: Silica.Sessions\Exceptions\SessionLifecycleInvalidException.cs

using Silica.Exceptions;
using Silica.Sessions;
using Silica.Sessions.Contracts;


namespace Silica.Sessions
{
    public sealed class SessionLifecycleInvalidException : SilicaException
    {
        public SessionState CurrentState { get; }
        public string Operation { get; }

        public SessionLifecycleInvalidException(SessionState state, string operation)
            : base(
                code: SessionExceptions.SessionLifecycleInvalid.Code,
                message: $"Operation '{operation}' is invalid when session is {state}.",
                category: SessionExceptions.SessionLifecycleInvalid.Category,
                exceptionId: SessionExceptions.Ids.SessionLifecycleInvalid)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            CurrentState = state;
            Operation = string.IsNullOrWhiteSpace(operation) ? "unknown" : operation;
        }
    }
}
