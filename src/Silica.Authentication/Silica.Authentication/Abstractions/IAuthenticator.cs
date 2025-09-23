using Silica.Authentication;

namespace Silica.Authentication.Abstractions
{
    /// <summary>
    /// Defines a contract for authenticating a principal using a specific strategy
    /// (e.g., Local, Kerberos, JWT, Certificate, Azure AD).
    /// </summary>
    public interface IAuthenticator
    {
        /// <summary>
        /// Asynchronously attempts to authenticate a request given the provided context.
        /// </summary>
        /// <param name="context">The authentication context (credentials, tokens, etc.).</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
        /// <returns>An <see cref="IAuthenticationResult"/> describing the outcome.</returns>
        /// <exception cref="Silica.Authentication.AuthenticationProviderUnavailableException">
        /// Thrown when the external authentication provider is unavailable.
        /// </exception>
        /// <exception cref="Silica.Exceptions.SilicaException">Thrown for unexpected internal faults.</exception>
        System.Threading.Tasks.Task<IAuthenticationResult> AuthenticateAsync(AuthenticationContext context, System.Threading.CancellationToken cancellationToken = default);

        /// <summary>
        /// A short identifier for this authenticator (e.g., "Local", "Kerberos", "Jwt").
        /// Useful for diagnostics and configuration.
        /// </summary>
        string Name { get; }
    }
}
