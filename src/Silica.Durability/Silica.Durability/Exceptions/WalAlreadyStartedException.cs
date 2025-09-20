// Filename: Silica.Durability/Exceptions/WalAlreadyStartedException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class WalAlreadyStartedException : SilicaException
    {
        public WalAlreadyStartedException()
            : base(
                code: DurabilityExceptions.WalAlreadyStarted.Code,
                message: "WAL already started.",
                category: DurabilityExceptions.WalAlreadyStarted.Category,
                exceptionId: DurabilityExceptions.Ids.WalAlreadyStarted)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
