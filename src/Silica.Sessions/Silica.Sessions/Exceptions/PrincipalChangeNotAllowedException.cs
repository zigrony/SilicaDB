using Silica.Exceptions;

namespace Silica.Sessions
{
    public sealed class PrincipalChangeNotAllowedException : SilicaException
    {
        public PrincipalChangeNotAllowedException()
            : base(
                code: SessionExceptions.PrincipalChangeNotAllowed.Code,
                message: "Principal cannot be changed after authentication.",
                category: SessionExceptions.PrincipalChangeNotAllowed.Category,
                exceptionId: SessionExceptions.Ids.PrincipalChangeNotAllowed)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
