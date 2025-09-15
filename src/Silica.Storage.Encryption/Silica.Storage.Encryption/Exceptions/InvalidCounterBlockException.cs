using Silica.Exceptions;
using Silica.Storage.Encryption.Exceptions;
using System;
namespace Silica.Storage.Encryption.Exceptions
{
    public sealed class InvalidCounterBlockException : SilicaException
    {
        public int Length { get; }

        public InvalidCounterBlockException(int length)
            : base(EncryptionExceptions.InvalidCounterBlock.Code,
                   $"Counter block length is invalid. Length={length}",
                   EncryptionExceptions.InvalidCounterBlock.Category,
                   EncryptionExceptions.Ids.InvalidCounterBlock)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Length = length;
        }
    }
}