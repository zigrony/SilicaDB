// Filename: Silica.Durability/Exceptions/CheckpointRenameNotVisibleException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class CheckpointRenameNotVisibleException : SilicaException
    {
        public string TargetPath { get; }

        public CheckpointRenameNotVisibleException(string targetPath)
            : base(
                code: DurabilityExceptions.CheckpointRenameNotVisible.Code,
                message: "Checkpoint rename not visible after retry.",
                category: DurabilityExceptions.CheckpointRenameNotVisible.Category,
                exceptionId: DurabilityExceptions.Ids.CheckpointRenameNotVisible)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            TargetPath = targetPath ?? string.Empty;
        }
    }
}
