namespace Silica.Authentication.Abstractions
{
    using System;
    /// <summary>
    /// Represents the outcome of an authentication attempt.
    /// </summary>
    public interface IAuthenticationResult
    {
        /// <summary>
        /// True if authentication succeeded.
        /// </summary>
        bool Succeeded { get; }

        /// <summary>
        /// The authenticated username or principal identifier.
        /// Null if authentication failed.
        /// </summary>
        string? Principal { get; }

        /// <summary>
        /// Roles or claims associated with the principal.
        /// May be empty in dev/local scenarios.
        /// </summary>
        IReadOnlyCollection<string> Roles { get; }

        /// <summary>
        /// Optional failure reason for diagnostics.
        /// </summary>
        string? FailureReason { get; }

        /// <summary>
        /// Optional session identifier when a session was created for this authentication.
        /// </summary>
        Guid? SessionId { get; }
    }
}
