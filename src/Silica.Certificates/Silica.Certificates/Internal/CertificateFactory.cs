using System;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Silica.Certificates.Metrics;
using Silica.DiagnosticsCore.Metrics;
using Silica.Certificates.Diagnostics;

namespace Silica.Certificates.Internal
{
    internal static class CertificateFactory
    {
        /// <summary>
        /// Generates an ephemeral, exportable self-signed certificate and returns prebuilt PFX bytes with metadata.
        /// Consumers can import instances on demand, avoiding double export/import and minimizing resource churn.
        /// </summary>
        internal static EphemeralCertificateData CreateEphemeralPfx(IMetricsManager? metrics = null, Silica.Certificates.Abstractions.CertificateProviderOptions? options = null)
        {
            var sw = Stopwatch.StartNew();
            IDisposable? key = null; // <-- declare here, outer scope
            try
            {
                // Options and environment tuning
                var includeMachineNameSan = true;
                var lifetimeDays = 1.0;
                int skewMinutes = 5;
                string keyAlg = "rsa";
                int rsaKeySize = 2048;

                if (options is not null)
                {
                    includeMachineNameSan = options.IncludeMachineNameSan;
                    var ld = options.LifetimeDays;
                    if (ld > 0.0 && ld <= 3650.0) lifetimeDays = ld;
                    var sk = options.NotBeforeSkewMinutes;
                    if (sk >= 0 && sk <= 1440) skewMinutes = sk;
                    if (!string.IsNullOrWhiteSpace(options.KeyAlgorithm)) keyAlg = options.KeyAlgorithm;
                    if (options.RsaKeySize >= 2048 && options.RsaKeySize <= 4096) rsaKeySize = options.RsaKeySize;
                }
                try
                {
                    var envDays = Environment.GetEnvironmentVariable("SILICA_CERTIFICATES_EPHEMERAL_DAYS");
                    if (!string.IsNullOrEmpty(envDays))
                    {
                        double parsed = lifetimeDays;
                        if (double.TryParse(envDays.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                        {
                            if (parsed > 0.0 && parsed <= 3650.0) lifetimeDays = parsed;
                        }
                    }
                    var envSan = Environment.GetEnvironmentVariable("SILICA_CERTIFICATES_INCLUDE_MACHINE_NAME_SAN");
                    if (!string.IsNullOrEmpty(envSan))
                    {
                        var s = envSan.Trim();
                        if (string.Equals(s, "0", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s, "no", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s, "off", StringComparison.OrdinalIgnoreCase))
                        {
                            includeMachineNameSan = false;
                        }
                        else if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(s, "on", StringComparison.OrdinalIgnoreCase))
                        {
                            includeMachineNameSan = true;
                        }
                    }
                    var envAlg = Environment.GetEnvironmentVariable("SILICA_CERTIFICATES_KEY_ALGORITHM");
                    if (!string.IsNullOrWhiteSpace(envAlg))
                    {
                        var a = envAlg.Trim();
                        if (string.Equals(a, "ecdsa", StringComparison.OrdinalIgnoreCase)) keyAlg = "ecdsa";
                        else if (string.Equals(a, "rsa", StringComparison.OrdinalIgnoreCase)) keyAlg = "rsa";
                    }
                    var envRsaSize = Environment.GetEnvironmentVariable("SILICA_CERTIFICATES_RSA_KEYSIZE");
                    if (!string.IsNullOrWhiteSpace(envRsaSize))
                    {
                        int parsedSize;
                        if (int.TryParse(envRsaSize.Trim(), out parsedSize))
                        {
                            if (parsedSize >= 2048 && parsedSize <= 4096) rsaKeySize = parsedSize;
                        }
                    }
                }
                catch { /* keep defaults */ }

                // Distinguished Name with all common fields
                var subject = new X500DistinguishedName(
                    "CN=SilicaDB-Ephemeral, O=SilicaDB, OU=Development, C=US, L=Land O' Lakes, ST=Florida");

                CertificateRequest request;
                try
                {
                    if (string.Equals(keyAlg, "ecdsa", StringComparison.OrdinalIgnoreCase))
                    {
                        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                        key = ecdsa;
                        request = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
                    }
                    else
                    {
                        var rsa = RSA.Create(rsaKeySize);
                        key = rsa;
                        request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    }
                }
                catch
                {
                    // Fallback to RSA 2048 if algo selection fails
                    var rsa = RSA.Create(2048);
                    key = rsa;
                    request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }

                // Basic Constraints: not a CA
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, true));

                // Key Usage: digital signature + key encipherment
                try
                {
                    if (string.Equals(keyAlg, "ecdsa", StringComparison.OrdinalIgnoreCase))
                    {
                        request.CertificateExtensions.Add(
                            new X509KeyUsageExtension(
                                X509KeyUsageFlags.DigitalSignature,
                                critical: true));
                    }
                    else
                    {
                        request.CertificateExtensions.Add(
                            new X509KeyUsageExtension(
                                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                                critical: true));
                    }
                }
                catch { /* optional */ }

                // Enhanced Key Usage: server auth
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection
                        {
                            new Oid("1.3.6.1.5.5.7.3.1") // Server Authentication
                        },
                        critical: true));

                // Subject Key Identifier: aids chain building and tooling
                try
                {
                    var ski = new X509SubjectKeyIdentifierExtension(request.PublicKey, false);
                    request.CertificateExtensions.Add(ski);
                }
                catch { /* optional */ }

                // Subject Alternative Names (SANs)
                var sanBuilder = new SubjectAlternativeNameBuilder();
                sanBuilder.AddDnsName("localhost");
                sanBuilder.AddIpAddress(IPAddress.Loopback);
                try
                {
                    sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
                }
                catch { /* optional: IPv6 may be disabled */ }

                // Optional MachineName SAN (can be non-DNS compliant on some hosts)
                try
                {
                    if (includeMachineNameSan)
                    {
                        var name = Environment.MachineName;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            sanBuilder.AddDnsName(name);
                        }
                    }
                }
                catch { /* SAN addition optional */ }

                request.CertificateExtensions.Add(sanBuilder.Build());

                // Validity window (skew applied to NotBefore)
                var notBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(-skewMinutes);
                var notAfterUtc = notBeforeUtc.AddDays(lifetimeDays);

                using var cert = request.CreateSelfSigned(notBeforeUtc, notAfterUtc);
                byte[] pfx;
                try
                {
                    pfx = cert.Export(X509ContentType.Pfx);
                }
                catch (Exception exportEx)
                {
                    // Metrics: failure
                    if (metrics is not null)
                    {
                        CertificateMetrics.IncrementFailure(metrics, "factory.export");
                    }
                    CertificateDiagnostics.Emit(
                        component: "Silica.Certificates",
                        operation: "Factory.Export",
                        level: "error",
                        message: "Ephemeral certificate export failed.",
                        ex: exportEx);
                    throw new CertificateExportFailedException("pfx", SafeThumbprint(cert), exportEx);
                }

                // Tracing: success
                CertificateDiagnostics.Emit(
                    component: "Silica.Certificates",
                    operation: "Factory.Generate",
                    level: "info",
                    message: $"Ephemeral certificate generated. Lifetime={(notAfterUtc - notBeforeUtc).TotalDays:F1} days, Latency={sw.Elapsed.TotalMilliseconds:F2} ms");

                // Metrics: export success (lifecycle parity)
                if (metrics is not null)
                {
                    CertificateMetrics.IncrementExported(metrics, "pfx");
                }

                // Return metadata for consumers
                return new EphemeralCertificateData
                {
                    PfxBytes = pfx,
                    NotBeforeUtc = notBeforeUtc,
                    NotAfterUtc = notAfterUtc,
                    Thumbprint = SafeThumbprint(cert)
                };
            }
            catch (Exception ex)
            {
                // Metrics: failure
                if (metrics is not null)
                {
                    CertificateMetrics.IncrementFailure(metrics, "factory.generate");
                }

                // Tracing: failure
                CertificateDiagnostics.Emit(
                    component: "Silica.Certificates",
                    operation: "Factory.Generate",
                    level: "error",
                    message: "Ephemeral certificate generation failed.",
                    ex: ex);

                // Throw contract-first typed exception with context (provider + subject)
                throw new CertificateGenerationFailedException(
                    provider: "factory",
                    subject: "CN=SilicaDB-Ephemeral, O=SilicaDB, OU=Development, C=US, L=Land O' Lakes, ST=Florida",
                    inner: ex);
            }
            finally
            {
                try
                {
                    if (key is not null) key.Dispose();
                }
                catch { }
            }
        }

        internal sealed class EphemeralCertificateData
        {
            public byte[] PfxBytes { get; set; } = Array.Empty<byte>();
            public DateTimeOffset NotBeforeUtc { get; set; }
            public DateTimeOffset NotAfterUtc { get; set; }
            public string Thumbprint { get; set; } = string.Empty;
        }

        private static string SafeThumbprint(X509Certificate2 cert)
        {
            try { return cert?.Thumbprint ?? string.Empty; } catch { return string.Empty; }
        }
    }
}
