// Filename: Silica.Durability/Exceptions/CheckpointReadFailedException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class CheckpointReadFailedException : SilicaException
    {
        public CheckpointReadFailedException()
            : base(
                code: DurabilityExceptions.CheckpointReadFailed.Code,
                message: "Failed to read latest checkpoint after retries.",
                category: DurabilityExceptions.CheckpointReadFailed.Category,
                exceptionId: DurabilityExceptions.Ids.CheckpointReadFailed)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
