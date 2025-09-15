using Silica.Exceptions;
using Silica.Storage.Encryption.Exceptions;
using System;

namespace Silica.Storage.Encryption.Exceptions
{
    public sealed class EncryptionSaltNullException : SilicaException
    {
        public EncryptionSaltNullException()
            : base(EncryptionExceptions.EncryptionSaltNull.Code,
                   "Encryption salt is null.",
        EncryptionExceptions.EncryptionSaltNull.Category,
                   EncryptionExceptions.Ids.EncryptionSaltNull)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}