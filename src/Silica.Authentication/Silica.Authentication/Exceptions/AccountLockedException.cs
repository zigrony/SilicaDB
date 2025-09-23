using Silica.Exceptions;

namespace Silica.Authentication
{
    public sealed class AccountLockedException : SilicaException
    {
        public string Username { get; }

        public AccountLockedException(string username)
            : base(
                code: AuthenticationExceptions.AccountLocked.Code,
                message: $"Account '{username}' is locked.",
                category: AuthenticationExceptions.AccountLocked.Category,
                exceptionId: AuthenticationExceptions.Ids.AccountLocked)
        {
            AuthenticationExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Username = username ?? string.Empty;
        }
    }
}
