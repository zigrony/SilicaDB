using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class AlignmentRequiredException : SilicaException
    {
        public long Offset { get; }
        public int Length { get; }
        public int LogicalBlockSize { get; }

        public AlignmentRequiredException(long offset, int length, int logicalBlockSize)
            : base(
                code: StorageExceptions.AlignmentRequired.Code,
                message: $"Offset and length must be multiples of LogicalBlockSize={logicalBlockSize}. Offset={offset}, Length={length}",
                category: StorageExceptions.AlignmentRequired.Category,
                exceptionId: StorageExceptions.Ids.AlignmentRequired)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Offset = offset;
            Length = length;
            LogicalBlockSize = logicalBlockSize;
        }
    }
}
