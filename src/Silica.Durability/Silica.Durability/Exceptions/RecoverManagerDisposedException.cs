// Filename: Silica.Durability/Exceptions/RecoverManagerDisposedException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class RecoverManagerDisposedException : SilicaException
    {
        public RecoverManagerDisposedException()
            : base(
                code: DurabilityExceptions.RecoverManagerDisposed.Code,
                message: "Operation attempted on a disposed RecoverManager.",
                category: DurabilityExceptions.RecoverManagerDisposed.Category,
                exceptionId: DurabilityExceptions.Ids.RecoverManagerDisposed)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
