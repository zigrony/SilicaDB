using Silica.Exceptions;
using Silica.Storage.Encryption.Exceptions;
using System;

namespace Silica.Storage.Encryption.Exceptions
{
    public sealed class TransformObjectDisposedException : SilicaException
    {
        public TransformObjectDisposedException()
            : base(EncryptionExceptions.TransformObjectDisposed.Code,
                   "AES-CTR transform was used after being disposed.",
        EncryptionExceptions.TransformObjectDisposed.Category,
                   EncryptionExceptions.Ids.TransformObjectDisposed)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }
}