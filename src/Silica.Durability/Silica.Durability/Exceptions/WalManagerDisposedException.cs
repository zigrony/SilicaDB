// Filename: Silica.Durability/Exceptions/WalManagerDisposedException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class WalManagerDisposedException : SilicaException
    {
        public WalManagerDisposedException()
            : base(
                code: DurabilityExceptions.WalDisposed.Code,
                message: "Operation attempted on a disposed WAL manager.",
                category: DurabilityExceptions.WalDisposed.Category,
                exceptionId: DurabilityExceptions.Ids.WalDisposed)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
