// Filename: Silica.Durability/Exceptions/CheckpointNotWritableStateException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class CheckpointNotWritableStateException : SilicaException
    {
        public CheckpointNotWritableStateException()
            : base(
                code: DurabilityExceptions.CheckpointNotWritableState.Code,
                message: "CheckpointManager not in a writable state.",
                category: DurabilityExceptions.CheckpointNotWritableState.Category,
                exceptionId: DurabilityExceptions.Ids.CheckpointNotWritableState)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
