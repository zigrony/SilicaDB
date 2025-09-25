using System;
using Silica.Exceptions;

namespace Silica.Sessions
{
    public sealed class NegativeTimeoutException : SilicaException
    {
        public string ParamName { get; }
        public TimeSpan Value { get; }

        public NegativeTimeoutException(string paramName, TimeSpan value)
            : base(
                code: SessionExceptions.NegativeTimeout.Code,
                message: $"Timeout '{paramName}' must be non-negative. Provided: {value}.",
                category: SessionExceptions.NegativeTimeout.Category,
                exceptionId: SessionExceptions.Ids.NegativeTimeout)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ParamName = paramName ?? "timeout";
            Value = value;
        }
    }
}
