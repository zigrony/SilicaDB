// Filename: Silica.Durability/Exceptions/CheckpointManagerDisposedException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class CheckpointManagerDisposedException : SilicaException
    {
        public CheckpointManagerDisposedException()
            : base(
                code: DurabilityExceptions.CheckpointManagerDisposed.Code,
                message: "Operation attempted on a disposed CheckpointManager.",
                category: DurabilityExceptions.CheckpointManagerDisposed.Category,
                exceptionId: DurabilityExceptions.Ids.CheckpointManagerDisposed)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
