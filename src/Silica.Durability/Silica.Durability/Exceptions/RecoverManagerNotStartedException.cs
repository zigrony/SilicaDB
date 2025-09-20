// Filename: Silica.Durability/Exceptions/RecoverManagerNotStartedException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class RecoverManagerNotStartedException : SilicaException
    {
        public RecoverManagerNotStartedException()
            : base(
                code: DurabilityExceptions.RecoverManagerNotStarted.Code,
                message: "RecoverManager not started.",
                category: DurabilityExceptions.RecoverManagerNotStarted.Category,
                exceptionId: DurabilityExceptions.Ids.RecoverManagerNotStarted)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
