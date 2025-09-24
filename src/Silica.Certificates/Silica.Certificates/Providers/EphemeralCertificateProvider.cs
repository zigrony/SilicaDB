using System;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Silica.Certificates;
using Silica.Certificates.Abstractions;
using Silica.Certificates.Internal;
using Silica.Certificates.Metrics;
using Silica.DiagnosticsCore.Metrics;
using Silica.Certificates.Diagnostics;

namespace Silica.Certificates.Providers
{
    /// <summary>
    /// Generates an ephemeral, in-memory self-signed certificate.
    /// Never persisted to disk. Suitable for dev/test or short-lived workloads.
    /// </summary>
    public sealed class EphemeralCertificateProvider : ICertificateProvider, IDisposable
    {
        // Provider retains immutable PFX bytes and metadata; returns a fresh certificate per call
        private readonly byte[] _pfxBytes;
        private readonly DateTimeOffset _notBeforeUtc;
        private readonly DateTimeOffset _notAfterUtc;
        private readonly string _thumbprint;
        private readonly IMetricsManager? _metrics;
        private int _disposed; // 0 = false, 1 = true
        /// <summary>
        /// Creates a new ephemeral certificate provider.
        /// </summary>
        /// <param name="metrics">Optional metrics manager for recording certificate metrics.</param>
        /// <param name="options">Optional provider options for lifetime and skew tuning.</param>
        public EphemeralCertificateProvider(IMetricsManager? metrics = null, Silica.Certificates.Abstractions.CertificateProviderOptions? options = null)
        {
            _metrics = metrics;
            var sw = Stopwatch.StartNew();
            try
            {
                // Ensure metrics are registered and active state is tracked.
                CertificateDiagnostics.TryRegisterMetrics();
                var data = CertificateFactory.CreateEphemeralPfx(_metrics, options);
                // Retain immutable bytes and metadata
                _pfxBytes = data.PfxBytes;
                _notBeforeUtc = data.NotBeforeUtc;
                _notAfterUtc = data.NotAfterUtc;
                _thumbprint = data.Thumbprint;
                // Count as active provider only after successful preparation
                CertificateState.IncrementActive();

                // Metrics: successful generation
                if (_metrics is not null)
                {
                    // Provider owns lifecycle metrics; factory emits tracing only.
                    CertificateMetrics.IncrementGenerated(_metrics, "ephemeral");
                    CertificateMetrics.RecordLifetimeDays(
                        _metrics,
                        (_notAfterUtc - _notBeforeUtc).TotalDays,
                        "ephemeral");
                    CertificateMetrics.RecordGenerationLatencyMs(
                        _metrics,
                        sw.Elapsed.TotalMilliseconds,
                        "ephemeral");
                }

                // Tracing: success
                CertificateDiagnostics.Emit(
                    component: "Silica.Certificates",
                    operation: "Ephemeral.Generate",
                    level: "info",
                    message: $"Ephemeral certificate generated successfully. Lifetime={(_notAfterUtc - _notBeforeUtc).TotalDays:F1} days, Latency={sw.Elapsed.TotalMilliseconds:F2} ms");
            }
            catch (Exception ex)
            {
                // Metrics: failure
                if (_metrics is not null)
                {
                    CertificateMetrics.IncrementFailure(_metrics, "generate");
                }
                // Ensure active gauge is not skewed if we failed before increment or any partial state.
                // No-op if not incremented yet, but safe to guard through a compensating check.
                // We don't track a local flag; rely on CreateEphemeralCertificate throwing before increment.

                // Tracing: failure
                CertificateDiagnostics.Emit(
                    component: "Silica.Certificates",
                    operation: "Ephemeral.Generate",
                    level: "error",
                    message: "Ephemeral certificate generation failed.",
                    ex: ex);

                // Wrap in typed exception for parity with Concurrency
                throw new CertificateGenerationFailedException(
                    provider: "ephemeral",
                    subject: "CN=SilicaDB-Ephemeral, O=SilicaDB, OU=Development, C=US, L=Land O' Lakes, ST=Florida",
                    inner: ex);
            }
        }

        /// <summary>
        /// Returns the ephemeral certificate.
        /// </summary>
        public X509Certificate2 GetCertificate()
        {
            // Minimal thread-safety: guard disposed state
            if (System.Threading.Volatile.Read(ref _disposed) != 0)
            {
                CertificateDiagnostics.Emit(
                    component: "Silica.Certificates",
                    operation: "Ephemeral.GetCertificate",
                    level: "error",
                    message: "Attempted to retrieve certificate from a disposed provider.");
                throw new ObjectDisposedException(nameof(EphemeralCertificateProvider));
            }

            // Enforce validity window.
            var nowUtc = DateTimeOffset.UtcNow;
            if (nowUtc < _notBeforeUtc || nowUtc > _notAfterUtc)
            {
                if (_metrics is not null)
                {
                    CertificateMetrics.IncrementFailure(_metrics, "ephemeral.expired");
                }
                CertificateDiagnostics.Emit(
                    component: "Silica.Certificates",
                    operation: "Ephemeral.GetCertificate",
                    level: "error",
                    message: $"Certificate outside validity window. NotBefore={_notBeforeUtc:u}, NotAfter={_notAfterUtc:u}.");
                throw new CertificateExpiredException(
                    thumbprint: _thumbprint,
                    notBefore: _notBeforeUtc,
                    notAfter: _notAfterUtc);
            }

            CertificateDiagnostics.EmitDebug(
                component: "Silica.Certificates",
                operation: "Ephemeral.GetCertificate",
                message: "Ephemeral certificate retrieved from provider.");
            // Return a new instance each call: consumer disposal cannot affect provider
            return new X509Certificate2(
                _pfxBytes,
                (string?)null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }

        ~EphemeralCertificateProvider()
        {
            // Safety net: decrement active count only. Avoid managed metrics on finalizer thread.
            try
            {
                Dispose(false);
            }
            catch { }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // No certificate instance retained; provider retains bytes only.
            try
            {
                CertificateState.DecrementActive();
                if (disposing && _metrics is not null)
                {
                    CertificateMetrics.IncrementPruned(_metrics, 1);
                    CertificateDiagnostics.EmitDebug(
                        component: "Silica.Certificates",
                        operation: "Ephemeral.Dispose",
                        message: "Ephemeral certificate provider disposed.");
                }
                // Best-effort: wipe PFX bytes to minimize in-memory private key persistence
                try
                {
                    if (_pfxBytes is not null && _pfxBytes.Length > 0)
                    {
                        for (int i = 0; i < _pfxBytes.Length; i++) _pfxBytes[i] = 0;
                    }
                }
                catch { }
            }
            catch { }
        }
    }
}
