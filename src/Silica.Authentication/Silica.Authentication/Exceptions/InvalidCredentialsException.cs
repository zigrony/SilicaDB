using Silica.Exceptions;

namespace Silica.Authentication
{
    public sealed class InvalidCredentialsException : SilicaException
    {
        public string Username { get; }

        public InvalidCredentialsException(string username)
            : base(
                code: AuthenticationExceptions.InvalidCredentials.Code,
                message: $"Invalid credentials for user '{username}'.",
                category: AuthenticationExceptions.InvalidCredentials.Category,
                exceptionId: AuthenticationExceptions.Ids.InvalidCredentials)
        {
            AuthenticationExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Username = username ?? string.Empty;
        }
    }
}
