using Silica.Exceptions;

namespace Silica.Authentication
{
    public sealed class UnsupportedAuthMethodException : SilicaException
    {
        public string Method { get; }

        public UnsupportedAuthMethodException(string method)
            : base(
                code: AuthenticationExceptions.UnsupportedAuthMethod.Code,
                message: $"Authentication method '{method}' is not supported.",
                category: AuthenticationExceptions.UnsupportedAuthMethod.Category,
                exceptionId: AuthenticationExceptions.Ids.UnsupportedAuthMethod)
        {
            AuthenticationExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Method = method ?? string.Empty;
        }
    }
}
