using System;
using Silica.Exceptions;

namespace Silica.Storage.Encryption.Exceptions
{
    /// <summary>
    /// Canonical, low-cardinality Encryption exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class EncryptionExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            // Reserve a dedicated range for encryption: 3000–3099
            public const int InvalidKey = 3001;
            public const int InvalidSalt = 3002;
            public const int WrongKeyOrSalt = 3003;
            public const int TransformFailed = 3004;
            public const int InvalidCounterBlock = 3005;
            public const int TransformObjectDisposed = 3006;
            public const int MismatchedBufferLength = 3007;
            public const int EncryptionKeyProviderNull = 3008;
            public const int InvalidFrameLength = 3009;
            public const int EncryptionKeyNull = 3010;
            public const int EncryptionSaltNull = 3011;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        /// <summary>
        /// Reserved ExceptionId range for Silica.Storage.Encryption.
        /// </summary>
        public const int MinId = 3000;
        public const int MaxId = 3099;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.Storage.Encryption reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.Storage.Encryption.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------

        /// <summary>
        /// AES key length is invalid.
        /// </summary>
        public static readonly ExceptionDefinition InvalidKey =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.INVALID_KEY",
                exceptionTypeName: typeof(InvalidKeyException).FullName!,
                category: FailureCategory.Configuration,
                description: "AES key length is invalid. Must be 16, 24, or 32 bytes."
            );

        /// <summary>
        /// Device salt length is invalid.
        /// </summary>
        public static readonly ExceptionDefinition InvalidSalt =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.INVALID_SALT",
                exceptionTypeName: typeof(InvalidSaltException).FullName!,
                category: FailureCategory.Configuration,
                description: "Device salt length is invalid. Must be exactly 16 bytes."
            );

        /// <summary>
        /// Data could not be decrypted with the provided key/salt.
        /// </summary>
        public static readonly ExceptionDefinition WrongKeyOrSalt =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.WRONG_KEY_OR_SALT",
                exceptionTypeName: typeof(WrongKeyOrSaltException).FullName!,
                category: FailureCategory.Validation,
                description: "Data could not be decrypted with the provided key/salt."
            );

        /// <summary>
        /// AES-CTR transform failed during encryption or decryption.
        /// </summary>
        public static readonly ExceptionDefinition TransformFailed =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.TRANSFORM_FAILED",
                exceptionTypeName: typeof(TransformFailedException).FullName!,
                category: FailureCategory.IO,
                description: "AES-CTR transform failed during encryption or decryption."
            );
        public static readonly ExceptionDefinition InvalidCounterBlock =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.INVALID_COUNTER_BLOCK",
                exceptionTypeName: typeof(InvalidCounterBlockException).FullName!,
                category: FailureCategory.Configuration,
                description: "Counter block length is invalid. Must be exactly 16 bytes."
            );

        public static readonly ExceptionDefinition TransformObjectDisposed =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.TRANSFORM_OBJECT_DISPOSED",
                exceptionTypeName: typeof(TransformObjectDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "AES-CTR transform was used after being disposed."
            );

        public static readonly ExceptionDefinition MismatchedBufferLength =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.MISMATCHED_BUFFER_LENGTH",
                exceptionTypeName: typeof(MismatchedBufferLengthException).FullName!,
                category: FailureCategory.Validation,
                description: "Output buffer length must equal input length."
            );

        public static readonly ExceptionDefinition EncryptionKeyProviderNull =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.KEY_PROVIDER_NULL",
                exceptionTypeName: typeof(EncryptionKeyProviderNullException).FullName!,
                category: FailureCategory.Configuration,
                description: "Encryption key provider instance is null."
            );

        public static readonly ExceptionDefinition InvalidFrameLength =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.INVALID_FRAME_LENGTH",
                exceptionTypeName: typeof(InvalidFrameLengthException).FullName!,
                category: FailureCategory.Validation,
                description: "Frame length must equal LogicalBlockSize."
            );

        public static readonly ExceptionDefinition EncryptionKeyNull =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.KEY_NULL",
                exceptionTypeName: typeof(EncryptionKeyNullException).FullName!,
                category: FailureCategory.Configuration,
                description: "Encryption key is null."
            );

        public static readonly ExceptionDefinition EncryptionSaltNull =
            ExceptionDefinition.Create(
                code: "ENCRYPTION.SALT_NULL",
                exceptionTypeName: typeof(EncryptionSaltNullException).FullName!,
                category: FailureCategory.Configuration,
                description: "Encryption salt is null."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                InvalidKey,
                InvalidSalt,
                WrongKeyOrSalt,
                TransformFailed,
                InvalidCounterBlock,
                TransformObjectDisposed,
                MismatchedBufferLength,
                EncryptionKeyProviderNull,
                InvalidFrameLength,
                EncryptionKeyNull,
                EncryptionSaltNull
            });
        }
    }

    // --------------------------
    // EXCEPTION TYPES
    // --------------------------

    public sealed class InvalidKeyException : SilicaException
    {
        public int Length { get; }

        public InvalidKeyException(int length)
            : base(
                code: EncryptionExceptions.InvalidKey.Code,
                message: $"AES key length is invalid. Length={length}",
                category: EncryptionExceptions.InvalidKey.Category,
                exceptionId: EncryptionExceptions.Ids.InvalidKey)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Length = length;
        }
    }

    public sealed class InvalidSaltException : SilicaException
    {
        public int Length { get; }

        public InvalidSaltException(int length)
            : base(
                code: EncryptionExceptions.InvalidSalt.Code,
                message: $"Device salt length is invalid. Length={length}",
                category: EncryptionExceptions.InvalidSalt.Category,
                exceptionId: EncryptionExceptions.Ids.InvalidSalt)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Length = length;
        }
    }

    public sealed class WrongKeyOrSaltException : SilicaException
    {
        public WrongKeyOrSaltException()
            : base(
                code: EncryptionExceptions.WrongKeyOrSalt.Code,
                message: "Data could not be decrypted with the provided key/salt.",
                category: EncryptionExceptions.WrongKeyOrSalt.Category,
                exceptionId: EncryptionExceptions.Ids.WrongKeyOrSalt)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
        }
    }

    public sealed class TransformFailedException : SilicaException
    {
        public string Operation { get; }

        public TransformFailedException(string operation, Exception? inner = null)
            : base(
                code: EncryptionExceptions.TransformFailed.Code,
                message: $"AES-CTR transform failed during {operation}.",
                category: EncryptionExceptions.TransformFailed.Category,
                exceptionId: EncryptionExceptions.Ids.TransformFailed,
                innerException: inner)
        {
            EncryptionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Operation = operation ?? string.Empty;
        }
    }
}
