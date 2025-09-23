using Silica.Exceptions;

namespace Silica.Authentication.Exceptions
{
    public sealed class TokenExpiredException : SilicaException
    {
        public string TokenId { get; }

        public TokenExpiredException(string tokenId)
            : base(
                code: AuthenticationExceptions.TokenExpired.Code,
                message: $"Authentication token '{tokenId}' has expired.",
                category: AuthenticationExceptions.TokenExpired.Category,
                exceptionId: AuthenticationExceptions.Ids.TokenExpired)
        {
            AuthenticationExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            TokenId = tokenId ?? string.Empty;
        }
    }
}
