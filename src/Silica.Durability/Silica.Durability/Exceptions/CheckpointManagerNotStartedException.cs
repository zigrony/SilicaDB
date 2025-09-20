// Filename: Silica.Durability/Exceptions/CheckpointManagerNotStartedException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class CheckpointManagerNotStartedException : SilicaException
    {
        public CheckpointManagerNotStartedException()
            : base(
                code: DurabilityExceptions.CheckpointManagerNotStarted.Code,
                message: "CheckpointManager not started.",
                category: DurabilityExceptions.CheckpointManagerNotStarted.Category,
                exceptionId: DurabilityExceptions.Ids.CheckpointManagerNotStarted)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
