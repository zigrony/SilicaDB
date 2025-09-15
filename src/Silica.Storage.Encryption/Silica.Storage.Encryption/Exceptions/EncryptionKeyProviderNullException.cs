using Silica.Exceptions;
using Silica.Storage.Encryption.Exceptions;
using System;
namespace Silica.Storage.Encryption.Exceptions
{
    public sealed class EncryptionKeyProviderNullException : SilicaException
    {
        public EncryptionKeyProviderNullException()
            : base(EncryptionExceptions.EncryptionKeyProviderNull.Code,
                   "Encryption key provider instance is null.",
        EncryptionExceptions.EncryptionKeyProviderNull.Category,
                   EncryptionExceptions.Ids.EncryptionKeyProviderNull)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}