using System;
using Silica.Exceptions;

namespace Silica.BufferPool
{
    /// <summary>
    /// Thrown when an operation is attempted after the BufferPoolManager has been disposed.
    /// </summary>
    public sealed class BufferPoolDisposedException : SilicaException
    {
        public BufferPoolDisposedException()
            : base(
                code: BufferPoolExceptions.BufferPoolDisposed.Code,
                message: "Operation attempted on a disposed BufferPoolManager.",
                category: BufferPoolExceptions.BufferPoolDisposed.Category,
                exceptionId: BufferPoolExceptions.Ids.BufferPoolDisposed)
        {
            BufferPoolExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}
