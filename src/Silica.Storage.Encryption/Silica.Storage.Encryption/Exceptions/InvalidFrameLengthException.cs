using Silica.Exceptions;
using Silica.Storage.Encryption.Exceptions;
using System;

namespace Silica.Storage.Encryption.Exceptions
{
    public sealed class InvalidFrameLengthException : SilicaException
    {
        public int Length { get; }
        public int Expected { get; }

        public InvalidFrameLengthException(int length, int expected)
            : base(EncryptionExceptions.InvalidFrameLength.Code,
                   $"Frame length is invalid. Length={length}, Expected={expected}.",
        EncryptionExceptions.InvalidFrameLength.Category,
                   EncryptionExceptions.Ids.InvalidFrameLength)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Length = length;
            Expected = expected;
        }
    }
}