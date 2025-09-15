using Silica.Exceptions;
using Silica.Storage.Encryption.Exceptions;
using System;

namespace Silica.Storage.Encryption.Exceptions
{
    public sealed class MismatchedBufferLengthException : SilicaException
    {
        public int InputLength { get; }
        public int OutputLength { get; }

        public MismatchedBufferLengthException(int inputLength, int outputLength)
            : base(EncryptionExceptions.MismatchedBufferLength.Code,
                   $"Output length ({outputLength}) must equal input length ({inputLength}).",
        EncryptionExceptions.MismatchedBufferLength.Category,
                   EncryptionExceptions.Ids.MismatchedBufferLength)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            InputLength = inputLength;
            OutputLength = outputLength;
        }
    }
}