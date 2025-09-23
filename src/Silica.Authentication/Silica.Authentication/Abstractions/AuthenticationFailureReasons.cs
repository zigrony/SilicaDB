namespace Silica.Authentication.Abstractions
{
    /// <summary>
    /// Low-cardinality canonical failure reasons used across authenticators.
    /// </summary>
    public static class AuthenticationFailureReasons
    {
        public const string InvalidRequest = "invalid_request";
        public const string MissingCredentials = "missing_credentials";
        public const string InvalidCredentials = "invalid_credentials";
        public const string AccountLocked = "account_locked";

        public const string MissingToken = "missing_token";
        public const string InvalidToken = "invalid_token";
        public const string TokenTooLong = "token_too_long";
        public const string TokenExpired = "token_expired";
        public const string InvalidTokenBase64 = "invalid_token_base64";

        public const string MissingCertificate = "missing_certificate";
        public const string MalformedCertificate = "malformed_certificate";
        public const string InvalidCertificate = "invalid_certificate";

        // For diagnostics use; not typically bubbled as FailureReason unless appropriate.
        public const string ProviderUnavailable = "provider_unavailable";
        public const string UnexpectedError = "unexpected_error";
    }
}
