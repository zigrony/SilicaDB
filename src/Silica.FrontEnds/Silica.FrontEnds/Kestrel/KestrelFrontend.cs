// File: Silica.FrontEnds/Kestrel/KestrelFrontend.cs
using System;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Silica.FrontEnds.Abstractions;
using Silica.FrontEnds.Diagnostics;
using Silica.FrontEnds.Internal;
using Silica.FrontEnds.Metrics;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Microsoft.AspNetCore.Hosting;
using Silica.FrontEnds.Exceptions;


namespace Silica.FrontEnds.Kestrel
{
    /// <summary>
    /// Default built-in frontend: configures Kestrel and binds HTTPS (and optional HTTP).
    /// </summary>
    public sealed class KestrelFrontend : IFrontend, Silica.FrontEnds.Abstractions.IFrontendLifecycle
    {
        // Static initializer guarantees exception definitions are registered exactly once.
        static KestrelFrontend()
        {
            try
            {
                FrontendExceptions.RegisterAll();
            }
            catch
            { /* never throw from type initializer */ }
        }
        public string Name => "Kestrel";

        private readonly KestrelFrontendOptions _options;
        private readonly IMetricsManager? _metrics;
        private readonly string _componentName;
        public bool OwnsHostLifecycle => _options.OwnsHostLifecycle;

        public KestrelFrontend(KestrelFrontendOptions options, IMetricsManager? metrics = null, string componentName = "Silica.FrontEnds.Kestrel")
        {
            _options = options ?? new KestrelFrontendOptions();
            _metrics = metrics;
            // Prefer options.ComponentName if provided; fall back to ctor arg then default.
            if (!string.IsNullOrWhiteSpace(_options.ComponentName))
                _componentName = _options.ComponentName!;
            else
                _componentName = string.IsNullOrWhiteSpace(componentName) ? "Silica.FrontEnds.Kestrel" : componentName;

            // Per-frontend metrics (counters) will be registered lazily on increment.
            // Observable gauge is registered centrally by the registry.
            // Align diagnostics verbosity from options
            FrontendDiagnostics.AlignVerboseFromOptions(_options.EnableVerboseDiagnostics);
        }

        // Normalize endpoint tag for display/metrics (handles IPv6 bracketed form)
        private static string MakeEndpointTag(string scheme, string bindAddressText, int port)
        {
            string host = bindAddressText;
            // Treat AnyIP literals as-is for tags; bracket IPv6 non-Any
            bool isV6Any = string.Equals(host, "::", StringComparison.OrdinalIgnoreCase);
            bool isV4Any = string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase);
            bool isStar = string.Equals(host, "*", StringComparison.OrdinalIgnoreCase);
            if (!isV4Any && !isV6Any && !isStar)
            {
                // If it appears to be IPv6 (colon present) but not AnyIP, bracket it for clarity
                bool seemsV6 = false;
                for (int i = 0; i < host.Length; i++)
                {
                    if (host[i] == ':') { seemsV6 = true; break; }
                }
                if (seemsV6) host = "[" + host + "]";
            }
            return scheme + "://" + host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        public void ConfigureBuilder(WebApplicationBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));

            // Basic TLS certificate setup
            X509Certificate2? cert = null;
            string lastEndpointTag = string.Empty;

            try
            {
                // Try certificate acquisition with explicit handling of strict TLS fallback
                try
                {
                    cert = GetServerCertificate();
                    // Treat null as a structured acquisition failure when TLS is required
                    if (cert == null && _options.StrictTlsCertificate && _options.HttpsPort > 0)
                    {
                        var nullEx = new CertificateAcquireFailedException(
                            providerName: (_options.CertificateProvider != null) ? "CustomCertificateProvider" : "EphemeralCertificateProvider",
                            inner: null);
                        FrontendDiagnostics.Emit(_componentName, "Kestrel.Cert", "error", "certificate_acquire_failed_null", nullEx);
                        throw nullEx;
                    }
                }
                catch (CertificateAcquireFailedException certEx)
                {
                    if (_options.StrictTlsCertificate)
                    {
                        // Strict: surface failure
                        FrontendDiagnostics.Emit(_componentName, "Kestrel.Cert", "error", "certificate_acquire_failed_strict", certEx);
                        throw;
                    }
                    // Non-strict: proceed without HTTPS
                    FrontendDiagnostics.Emit(_componentName, "Kestrel.Cert", "warn", "certificate_acquire_failed_non_strict_http_fallback", certEx);
                    cert = null;
                }

                if (OperatingSystem.IsWindows() && _options.EnableWindowsMachineKeySet && cert != null)
                {
                    // Re-import with MachineKeySet so SChannel can access private key.
                    // Fully wrapped to avoid export/reimport throwing.
                    try
                    {
                        bool hasKey = false;
                        try { hasKey = cert.HasPrivateKey; } catch { hasKey = false; }
                        if (hasKey)
                        {
                            byte[]? pfxBytes = null;
                            try
                            {
                                pfxBytes = cert.Export(X509ContentType.Pfx);
                                cert.Dispose();
                                cert = new X509Certificate2(
                                    pfxBytes,
                                    (string?)null,
                                    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
                            }
                            finally
                            {
                                try
                                {
                                    if (pfxBytes != null) CryptographicOperations.ZeroMemory(pfxBytes);
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception reimportEx)
                    {
                        // Best-effort only; proceed without reimport if it fails.
                        FrontendDiagnostics.Emit(_componentName, "Kestrel.Cert", "warn", "certificate_machinekeyset_reimport_failed", reimportEx);
                    }
                }

                // Validate ports before binding
                if (_options.HttpsPort < 0) throw new ArgumentOutOfRangeException(nameof(KestrelFrontendOptions.HttpsPort), "HttpsPort must be >= 0.");
                if (_options.HttpPort < 0) throw new ArgumentOutOfRangeException(nameof(KestrelFrontendOptions.HttpPort), "HttpPort must be >= 0.");
                // Upper bound checks: avoid late Kestrel failures and keep contracts explicit
                if (_options.HttpsPort > 65535) throw new ArgumentOutOfRangeException(nameof(KestrelFrontendOptions.HttpsPort), "HttpsPort must be <= 65535.");
                if (_options.HttpPort > 65535) throw new ArgumentOutOfRangeException(nameof(KestrelFrontendOptions.HttpPort), "HttpPort must be <= 65535.");
                if (_options.HttpPort > 0 && _options.HttpsPort > 0 && _options.HttpPort == _options.HttpsPort)
                {
                    throw new ArgumentException("HttpPort and HttpsPort must not be equal.", nameof(KestrelFrontendOptions.HttpPort));
                }
                if (_options.HttpPort == 0 && (_options.HttpsPort == 0 || (_options.HttpsPort > 0 && cert == null)))
                {
                    // No endpoints: either both disabled, or HTTPS requested but cert unavailable with no HTTP fallback.
                    throw new ArgumentException("At least one endpoint must be configured (HTTP or HTTPS with a certificate).", nameof(KestrelFrontendOptions.HttpsPort));
                }

                // Resolve bind address
                IPAddress? bindAddress = null;
                string bindAddressText = _options.BindAddress ?? "0.0.0.0";
                if (string.IsNullOrWhiteSpace(bindAddressText)) bindAddressText = "0.0.0.0";
                bool useAny = false;
                try
                {
                    if (string.Equals(bindAddressText, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(bindAddressText, "::", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(bindAddressText, "*", StringComparison.OrdinalIgnoreCase))
                    {
                        useAny = true;
                    }
                    else
                    {
                        IPAddress parsed;
                        if (!IPAddress.TryParse(bindAddressText, out parsed))
                        {
                            throw new ArgumentException("BindAddress must be a valid IPv4 or IPv6 address, or 0.0.0.0/::/* for AnyIP.", nameof(KestrelFrontendOptions.BindAddress));
                        }
                        bindAddress = parsed;
                    }
                }
                catch (Exception ex)
                {
                    FrontendDiagnostics.Emit(_componentName, "Kestrel.Bind", "error", "bind_address_invalid", ex);
                    throw;
                }

                builder.WebHost.ConfigureKestrel(options =>
                {
                    // HTTPS endpoint
                    if (_options.HttpsPort > 0 && cert != null)
                    {
                        lastEndpointTag = MakeEndpointTag("https", bindAddressText, _options.HttpsPort);

                        Action<ListenOptions> httpsConfigure = listenOptions =>
                        {
                            // Negotiate (Kerberos/NTLM) requires ordered, single-stream handshake; force HTTP/1.1.
                            // This avoids HTTP/2 multiplexing that can interleave anonymous requests mid-handshake.
                            listenOptions.Protocols = HttpProtocols.Http1;
                            listenOptions.UseHttps(cert);
                        };
                        if (useAny)
                            options.ListenAnyIP(_options.HttpsPort, httpsConfigure);
                        else
                            options.Listen(bindAddress!, _options.HttpsPort, httpsConfigure);
                        if (_metrics is not null)
                        {
                            FrontendMetrics.IncrementBind(_metrics, _componentName, Name, lastEndpointTag);
                        }
                        FrontendDiagnostics.EmitDebug(_componentName, "Kestrel.Bind", "https_bound", null,
                            new System.Collections.Generic.Dictionary<string, string> { ["endpoint"] = lastEndpointTag }, allowDebug: true);
                    }

                    // Optional HTTP endpoint
                    if (_options.HttpPort > 0)
                    {
                        string httpTag = MakeEndpointTag("http", bindAddressText, _options.HttpPort);
                        Action<ListenOptions> httpConfigure = listenOptions =>
                        {
                            // Keep HTTP/1 for parity; Negotiate should not run over H2C here either.
                            listenOptions.Protocols = HttpProtocols.Http1;
                        };
                        if (useAny)
                            options.ListenAnyIP(_options.HttpPort, httpConfigure);
                        else
                            options.Listen(bindAddress!, _options.HttpPort, httpConfigure);
                        if (_metrics is not null)
                        {
                            FrontendMetrics.IncrementBind(_metrics, _componentName, Name, httpTag);
                        }
                        FrontendDiagnostics.EmitDebug(_componentName, "Kestrel.Bind", "http_bound", null,
                            new System.Collections.Generic.Dictionary<string, string> { ["endpoint"] = httpTag }, allowDebug: true);
                        lastEndpointTag = httpTag;
                    }
                });
            }
            catch (Exception ex)
            {
                if (_metrics is not null)
                {
                    var tag = string.IsNullOrWhiteSpace(lastEndpointTag) ? "<unknown>" : lastEndpointTag;
                    FrontendMetrics.IncrementFailure(_metrics, _componentName, Name, tag);
                }
                FrontendDiagnostics.Emit(_componentName, "Kestrel.Bind", "error", "kestrel_bind_failed", ex);
                // Surface as a structured SilicaException for diagnostics parity.
                throw new FrontendBindFailedException(
                    frontendName: Name,
                    endpoint: string.IsNullOrWhiteSpace(lastEndpointTag) ? "<unknown>" : lastEndpointTag,
                    inner: ex);
            }
        }

        public void ConfigureApp(WebApplication app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            try
            {
                if (_options.RootGetResponse != null)
                {
                    app.MapGet("/", () => _options.RootGetResponse);
                }
                // Optional: small enrichment path for operators; safe and static
                // e.g., /healthz returns OK along with a hint of configured endpoints
                // (below, existing healthz mapping will return, see block further down)

                // Decrement active on graceful shutdown to keep gauge accurate
                try
                {
                    var lifetime = app.Lifetime;
                    // One-time decrement guard to avoid double-decrement across both events
                    int decremented = 0;
                    void TryDecrement()
                    {
                        if (System.Threading.Interlocked.Exchange(ref decremented, 1) == 0)
                        {
                            FrontendState.DecrementActive();
                            try
                            {
                                FrontendDiagnostics.EmitDebug(_componentName, "Kestrel.Lifecycle", "active_count_decremented", null, null, allowDebug: true);
                            }
                            catch { }
                        }
                    }
                    lifetime.ApplicationStopped.Register(TryDecrement);
                    lifetime.ApplicationStopping.Register(TryDecrement);
                }
                catch { /* Lifetime not available or registration failed; best-effort only */ }

                // Active count update after successful configuration of app
                FrontendState.IncrementActive();
                FrontendDiagnostics.Emit(_componentName, "Kestrel.Bind", "info", "kestrel_app_configured");

                // Optional health endpoint for basic readiness checks (opt-out via options)
                if (_options.EnableHealthz)
                {
                    string path = _options.HealthzPath ?? "/healthz";
                    path = path.Trim();
                    if (path.Length == 0 || path[0] != '/')
                    {
                        // Normalize to a rooted path without throwing hard
                        path = "/" + path;
                    }
                    // Keep this simple and static; no dynamic dependency checks here.
                    // Enrich for operators: indicate intended endpoint configuration in the payload.
                    string mode;
                    if (_options.HttpsPort > 0 && _options.HttpPort > 0)
                        mode = "OK (http+https)";
                    else if (_options.HttpsPort > 0)
                        mode = "OK (https)";
                    else if (_options.HttpPort > 0)
                        mode = "OK (http)";
                    else
                        mode = "OK (no_endpoints_configured)";
                    app.MapGet(path, () => mode);
                    FrontendDiagnostics.EmitDebug(_componentName, "Kestrel.Bind", "healthz_bound", null,
                        new System.Collections.Generic.Dictionary<string, string> { ["path"] = path }, allowDebug: true);
                }

            }
            catch (Exception ex)
            {
                // On configuration failure, best-effort decrement without double-decrement guard needed here
                FrontendState.DecrementActive();
                FrontendDiagnostics.Emit(_componentName, "Kestrel.Bind", "error", "kestrel_app_configuration_failed", ex);
                throw new FrontendConfigurationFailedException(Name, ex);
            }
        }

        private X509Certificate2? GetServerCertificate()
        {
            try
            {
                if (_options.CertificateProvider != null)
                {
                    var c = _options.CertificateProvider.GetCertificate();
                    FrontendDiagnostics.EmitDebug(_componentName, "Kestrel.Cert", "provider_cert_obtained", null, null, allowDebug: true);
                    // Normalize null to structured failure when TLS is required; caller will enforce strict policy.
                    return c;
                }

                // Ephemeral fallback
                var provider = new Silica.Certificates.Providers.EphemeralCertificateProvider(
                    metrics: _metrics,
                    options: _options.EphemeralOptions);
                var cert = provider.GetCertificate();
                FrontendDiagnostics.EmitDebug(_componentName, "Kestrel.Cert", "ephemeral_cert_generated", null, null, allowDebug: true);
                return cert;
            }
            catch (Exception ex)
            {
                FrontendDiagnostics.Emit(_componentName, "Kestrel.Cert", "error", "certificate_acquire_failed", ex);
                // Upgrade from silent-null to a structured exception so callers can enforce TLS requirements.
                // No reflection: use explicit names based on presence of provider.
                string providerNameText = (_options.CertificateProvider != null) ? "CustomCertificateProvider" : "EphemeralCertificateProvider";
                throw new CertificateAcquireFailedException(providerNameText, ex);
            }
        }
    }
}
