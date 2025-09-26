// File: Silica.FrontEnds/Kestrel/KestrelFrontendOptions.cs
using Silica.Certificates.Abstractions;
using Silica.FrontEnds.Abstractions;
using System.Net;

namespace Silica.FrontEnds.Kestrel
{
    /// <summary>
    /// Options for KestrelFrontend binding and TLS setup.
    /// </summary>
    public sealed class KestrelFrontendOptions
    {
        public int HttpsPort { get; init; } = 5001;
        public int HttpPort { get; init; } = 0; // 0 disables HTTP

        /// <summary>
        /// When true (default), certificate acquisition failure causes fast-fail.
        /// When false, HTTPS bind is skipped and HTTP-only (if configured) proceeds.
        /// </summary>
        public bool StrictTlsCertificate { get; init; } = true;

        /// <summary>
        /// Optional component name override for metrics/diagnostics tagging.
        /// </summary>
        public string? ComponentName { get; init; }

        /// <summary>
        /// Diagnostics verbosity for this frontend instance.
        /// </summary>
        public bool EnableVerboseDiagnostics { get; init; } = false;

        /// <summary>
        /// Optional certificate provider; if null, EphemeralCertificateProvider is used.
        /// </summary>
        public Silica.Certificates.Abstractions.ICertificateProvider? CertificateProvider { get; init; }

        /// <summary>
        /// Optional provider options passed when creating ephemeral certificates.
        /// Ignored if CertificateProvider is provided.
        /// </summary>
        public CertificateProviderOptions? EphemeralOptions { get; init; }

        /// <summary>
        /// Whether to re-import the certificate with MachineKeySet on Windows.
        /// </summary>
        public bool EnableWindowsMachineKeySet { get; init; } = true;

        /// <summary>
        /// Optional endpoint mapping: path and fixed response string for simple smoke tests.
        /// </summary>
        public string? RootGetResponse { get; init; } = null; //"Hello from SilicaDB!";

        /// <summary>
        /// When true (default), this frontend builds and runs the host (blocking).
        /// When false, it configures Kestrel on the builder but does not run the app.
        /// </summary>
        public bool OwnsHostLifecycle { get; init; } = true;

        /// <summary>
        /// Bind address for endpoints. Defaults to 0.0.0.0 (ListenAnyIP).
        /// If set to a specific IPv4/IPv6 address, endpoints bind to that address only.
        /// </summary>
        public string? BindAddress { get; init; } = "0.0.0.0";

        /// <summary>
        /// When true, HTTP endpoint (if enabled) uses HTTP/1 + HTTP/2 (H2C).
        /// Defaults to false to avoid surprising clients that do not negotiate H2C.
        /// </summary>
        public bool EnableHttp2OnHttp { get; init; } = false;

        /// <summary>
        /// When true, HTTPS endpoint (if enabled) includes HTTP/3 alongside HTTP/1+2.
        /// Requires OS + Kestrel support and certificate. Defaults to false.
        /// </summary>
        public bool EnableHttp3 { get; init; } = false;

        /// <summary>
        /// When true (default), expose a basic health endpoint for smoke checks.
        /// </summary>
        public bool EnableHealthz { get; init; } = true;

        /// <summary>
        /// Health endpoint path. Defaults to "/healthz".
        /// Must be rooted; will be normalized if not.
        /// </summary>
        public string? HealthzPath { get; init; } = "/healthz";
    }
}
