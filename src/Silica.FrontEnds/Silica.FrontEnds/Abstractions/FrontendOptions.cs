// File: Silica.FrontEnds/Abstractions/FrontendOptions.cs
namespace Silica.FrontEnds.Abstractions
{
    /// <summary>
    /// Minimal options for frontends; extended by concrete frontends (e.g., Kestrel).
    /// </summary>
    public sealed class FrontendOptions
    {
        /// <summary>
        /// Whether to enable detailed diagnostics logging for this frontend.
        /// </summary>
        public bool EnableVerboseDiagnostics { get; init; } = false;

        /// <summary>
        /// Optional component name override used for metrics/diagnostics tagging.
        /// </summary>
        public string? ComponentName { get; init; }

        /// <summary>
        /// When true (default), the frontend requires a valid server certificate for TLS binding
        /// and will throw <see cref="CertificateAcquireFailedException"/> if acquisition fails.
        /// When false, the frontend will fall back to HTTP‑only operation instead of failing fast.
        /// </summary>
        public bool StrictTlsCertificate { get; init; } = true;
    }
}
