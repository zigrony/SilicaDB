using Silica.Exceptions;
using Silica.Storage.Encryption.Exceptions;
using System;
namespace Silica.Storage.Encryption.Exceptions
{
    public sealed class EncryptionKeyNullException : SilicaException
    {
        public EncryptionKeyNullException()
            : base(EncryptionExceptions.EncryptionKeyNull.Code,
                   "Encryption key is null.",
        EncryptionExceptions.EncryptionKeyNull.Category,
                   EncryptionExceptions.Ids.EncryptionKeyNull)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}