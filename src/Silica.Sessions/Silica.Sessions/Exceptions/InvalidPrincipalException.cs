using Silica.Exceptions;

namespace Silica.Sessions
{
    public sealed class InvalidPrincipalException : SilicaException
    {
        public InvalidPrincipalException()
            : base(
                code: SessionExceptions.InvalidPrincipal.Code,
                message: "Principal must be a non-empty string.",
                category: SessionExceptions.InvalidPrincipal.Category,
                exceptionId: SessionExceptions.Ids.InvalidPrincipal)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
