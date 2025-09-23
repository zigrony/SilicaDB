using Silica.Exceptions;

namespace Silica.Authentication
{
    /// <summary>
    /// Canonical wrapper for unexpected internal errors during authentication.
    /// Aligns with AuthenticationMetrics.UnexpectedErrorCount and provides a stable ExceptionId.
    /// </summary>
    public sealed class UnexpectedAuthenticationErrorException : SilicaException
    {
        public string Method { get; }

        public UnexpectedAuthenticationErrorException(string method)
            : base(
                code: AuthenticationExceptions.UnexpectedError.Code,
                message: $"Unexpected authentication error in method '{method}'.",
                category: AuthenticationExceptions.UnexpectedError.Category,
                exceptionId: AuthenticationExceptions.Ids.UnexpectedError)
        {
            AuthenticationExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Method = method ?? string.Empty;
        }
    }
}
