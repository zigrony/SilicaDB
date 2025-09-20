// Filename: Silica.Durability/Exceptions/RecoverManagerAlreadyStartedException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class RecoverManagerAlreadyStartedException : SilicaException
    {
        public RecoverManagerAlreadyStartedException()
            : base(
                code: DurabilityExceptions.RecoverManagerAlreadyStarted.Code,
                message: "RecoverManager already started.",
                category: DurabilityExceptions.RecoverManagerAlreadyStarted.Category,
                exceptionId: DurabilityExceptions.Ids.RecoverManagerAlreadyStarted)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
