using System;
using Silica.Exceptions;

namespace Silica.BufferPool
{
    /// <summary>
    /// Thrown when a frame's Unpin() would underflow the pin count.
    /// </summary>
    public sealed class UnbalancedPinException : SilicaException
    {
        public int PinCountAfterDecrement { get; }

        public UnbalancedPinException(int pinCountAfterDecrement)
            : base(
                code: BufferPoolExceptions.UnbalancedPin.Code,
                message: "Unbalanced pin/unpin detected. PinCount after decrement=" + pinCountAfterDecrement.ToString() + ".",
                category: BufferPoolExceptions.UnbalancedPin.Category,
                exceptionId: BufferPoolExceptions.Ids.UnbalancedPin)
        {
            BufferPoolExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            PinCountAfterDecrement = pinCountAfterDecrement;
        }
    }
}
