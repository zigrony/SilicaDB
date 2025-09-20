// Filename: Silica.Durability/Exceptions/CheckpointFileCorruptException.cs
using Silica.Exceptions;

namespace Silica.Durability
{
    public sealed class CheckpointFileCorruptException : SilicaException
    {
        public string Path { get; }
        public long ExpectedBytes { get; }
        public long ActualBytes { get; }

        public CheckpointFileCorruptException(string path, long expectedBytes, long actualBytes)
            : base(
                code: DurabilityExceptions.CheckpointFileCorrupt.Code,
                message: "Checkpoint file is corrupt or has unexpected length.",
                category: DurabilityExceptions.CheckpointFileCorrupt.Category,
                exceptionId: DurabilityExceptions.Ids.CheckpointFileCorrupt)
        {
            DurabilityExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Path = path ?? string.Empty;
            ExpectedBytes = expectedBytes;
            ActualBytes = actualBytes;
        }
    }
}
