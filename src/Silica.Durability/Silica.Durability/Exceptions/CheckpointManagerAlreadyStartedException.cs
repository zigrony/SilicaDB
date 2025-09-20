// Filename: Silica.Durability/Exceptions/CheckpointManagerAlreadyStartedException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class CheckpointManagerAlreadyStartedException : SilicaException
    {
        public CheckpointManagerAlreadyStartedException()
            : base(
                code: DurabilityExceptions.CheckpointManagerAlreadyStarted.Code,
                message: "CheckpointManager already started.",
                category: DurabilityExceptions.CheckpointManagerAlreadyStarted.Category,
                exceptionId: DurabilityExceptions.Ids.CheckpointManagerAlreadyStarted)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
