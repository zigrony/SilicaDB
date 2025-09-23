using Silica.Exceptions;

namespace Silica.Authentication
{
    public sealed class AuthenticationProviderUnavailableException : SilicaException
    {
        public string Provider { get; }

        public AuthenticationProviderUnavailableException(string provider)
            : base(
                code: AuthenticationExceptions.ProviderUnavailable.Code,
                message: $"Authentication provider '{provider}' is unavailable.",
                category: AuthenticationExceptions.ProviderUnavailable.Category,
                exceptionId: AuthenticationExceptions.Ids.ProviderUnavailable)
        {
            AuthenticationExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Provider = provider ?? string.Empty;
        }
    }
}
