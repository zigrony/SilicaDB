using System;
using System.Threading;
using Silica.Certificates.Metrics;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Certificates.Internal
{
    internal static class CertificateState
    {
        private static long _activeCount = 0;
        private static int _metricsRegistered = 0;

        internal static void IncrementActive()
        {
            Interlocked.Increment(ref _activeCount);
        }

        internal static void DecrementActive()
        {
            var v = Interlocked.Decrement(ref _activeCount);
            if (v < 0) Interlocked.Exchange(ref _activeCount, 0);
        }

        internal static long GetActiveCount()
        {
            return Interlocked.Read(ref _activeCount);
        }

        internal static void TryRegisterMetricsOnce()
        {
            // Ensure we only register once per process lifetime.
            if (Interlocked.CompareExchange(ref _metricsRegistered, 1, 0) != 0) return;

            try
            {
                if (!DiagnosticsCoreBootstrap.IsStarted)
                {
                    // Bootstrap not ready yet; allow a later retry.
                    Interlocked.Exchange(ref _metricsRegistered, 0);
                    return;
                }
                IMetricsManager metrics = null;
                try { metrics = DiagnosticsCoreBootstrap.Instance.Metrics; } catch { }
                if (metrics == null)
                {
                    // Allow later registration if metrics manager not available yet.
                    Interlocked.Exchange(ref _metricsRegistered, 0);
                    return;
                }

                CertificateMetrics.RegisterAll(
                    metrics,
                    componentName: "Silica.Certificates",
                    activeCountProvider: GetActiveCount);
            }
            catch
            {
                // Allow a later retry if registration failed.
                Interlocked.Exchange(ref _metricsRegistered, 0);
            }
        }
    }
}
