using System;
using Silica.Exceptions;

namespace Silica.BufferPool
{
    /// <summary>
    /// Thrown when the reader count underflows during latch release.
    /// </summary>
    public sealed class LatchReaderUnderflowException : SilicaException
    {
        public LatchReaderUnderflowException()
            : base(
                code: BufferPoolExceptions.LatchReaderUnderflow.Code,
                message: "Reader latch underflow detected during release.",
                category: BufferPoolExceptions.LatchReaderUnderflow.Category,
                exceptionId: BufferPoolExceptions.Ids.LatchReaderUnderflow)
        {
            BufferPoolExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
