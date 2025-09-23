using System;
using Silica.Authentication.Exceptions;
using Silica.Exceptions;

namespace Silica.Authentication
{
    /// <summary>
    /// Canonical, low-cardinality Authentication exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class AuthenticationExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            // Reserve a dedicated range for authentication: 2000–2999
            public const int InvalidCredentials = 2001;
            public const int AccountLocked = 2002;
            public const int TokenExpired = 2003;
            public const int UnsupportedAuthMethod = 2004;
            public const int ProviderUnavailable = 2005;
            public const int UnexpectedError = 2006;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        /// <summary>
        /// Reserved ExceptionId range for Silica.Authentication.
        /// </summary>
        public const int MinId = 2000;
        public const int MaxId = 2999;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.Authentication reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.Authentication.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------

        public static readonly ExceptionDefinition InvalidCredentials =
            ExceptionDefinition.Create(
                code: "AUTH.INVALID_CREDENTIALS",
                exceptionTypeName: typeof(InvalidCredentialsException).FullName!,
                category: FailureCategory.Validation,
                description: "The provided username or password is invalid."
            );

        public static readonly ExceptionDefinition AccountLocked =
            ExceptionDefinition.Create(
                code: "AUTH.ACCOUNT.LOCKED",
                exceptionTypeName: typeof(AccountLockedException).FullName!,
                category: FailureCategory.Validation, // previously Security
                description: "The account is locked due to policy or repeated failures."
            );

        public static readonly ExceptionDefinition TokenExpired =
            ExceptionDefinition.Create(
                code: "AUTH.TOKEN.EXPIRED",
                exceptionTypeName: typeof(TokenExpiredException).FullName!,
                category: FailureCategory.Validation, // previously Security
                description: "The authentication token has expired."
            );

        public static readonly ExceptionDefinition UnsupportedAuthMethod =
            ExceptionDefinition.Create(
                code: "AUTH.METHOD.UNSUPPORTED",
                exceptionTypeName: typeof(UnsupportedAuthMethodException).FullName!,
                category: FailureCategory.Configuration,
                description: "The requested authentication method is not supported."
            );

        public static readonly ExceptionDefinition ProviderUnavailable =
            ExceptionDefinition.Create(
                code: "AUTH.PROVIDER.UNAVAILABLE",
                exceptionTypeName: typeof(AuthenticationProviderUnavailableException).FullName!,
                category: FailureCategory.Internal, // previously External
                description: "The external authentication provider is unavailable."
            );

        public static readonly ExceptionDefinition UnexpectedError =
            ExceptionDefinition.Create(
                code: "AUTH.UNEXPECTED",
                exceptionTypeName: typeof(UnexpectedAuthenticationErrorException).FullName!,
                category: FailureCategory.Internal,
                description: "An unexpected internal error occurred during authentication."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                InvalidCredentials,
                AccountLocked,
                TokenExpired,
                UnsupportedAuthMethod,
                ProviderUnavailable,
                UnexpectedError
            });
        }
    }
}
