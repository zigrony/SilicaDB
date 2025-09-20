// Filename: Silica.Durability/Exceptions/WalNotStartedException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class WalNotStartedException : SilicaException
    {
        public WalNotStartedException()
            : base(
                code: DurabilityExceptions.WalNotStarted.Code,
                message: "WAL not started.",
                category: DurabilityExceptions.WalNotStarted.Category,
                exceptionId: DurabilityExceptions.Ids.WalNotStarted)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
