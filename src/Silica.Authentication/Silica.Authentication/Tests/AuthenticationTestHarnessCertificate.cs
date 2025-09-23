// AuthenticationTestHarnessCertificate.cs
// Place this file in your Silica.Authentication.Tests project and call AuthenticationTestHarnessCertificate.Run() from Test.App

using System;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Authentication.Tests
{
    public static class AuthenticationTestHarnessCertificate
    {
        public static async Task Run()
        {
            Console.WriteLine("=== Certificate Authentication Test Harness ===");

            IMetricsManager metrics = new DummyMetricsManager();

            var validator = new Silica.Authentication.Certificate.DummyCertificateValidator();
            var options = new Silica.Authentication.Certificate.CertificateOptions();
            var auth = new Silica.Authentication.Certificate.CertificateAuthenticator(validator, options, metrics: metrics, componentName: "Silica.Authentication.Certificate");

            await RunTest("[CERT] Success with VALID cert", () => TestSuccess(auth));
            await RunTest("[CERT] Expired cert", () => TestExpired(auth));
            await RunTest("[CERT] Bad chain cert", () => TestBadChain(auth));
            await RunTest("[CERT] Missing cert", () => TestMissing(auth));
            await RunTest("[CERT] Malformed cert bytes", () => TestMalformed(auth));

            Console.WriteLine("=== Certificate Authentication Test Harness Complete ===");
        }

        private static async Task RunTest(string name, Func<Task> test)
        {
            Console.WriteLine(name);
            var sw = Stopwatch.StartNew();
            await test();
            sw.Stop();
            Console.WriteLine(name + " passed in " + sw.Elapsed.TotalMilliseconds + " ms.");
        }

        private static async Task TestSuccess(Silica.Authentication.Certificate.CertificateAuthenticator auth)
        {
            var cert = MakeCert("CN=VALID");
            var ctx = new Silica.Authentication.Abstractions.AuthenticationContext { CertificateBytes = cert.Export(X509ContentType.Cert) };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (!res.Succeeded) throw new InvalidOperationException("Expected success for VALID cert.");
            if (res.Principal != "cert-user") throw new InvalidOperationException("Unexpected principal returned.");
        }

        private static async Task TestExpired(Silica.Authentication.Certificate.CertificateAuthenticator auth)
        {
            var cert = MakeCert("CN=EXPIRED");
            var ctx = new Silica.Authentication.Abstractions.AuthenticationContext { CertificateBytes = cert.Export(X509ContentType.Cert) };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for expired cert.");
        }

        private static async Task TestBadChain(Silica.Authentication.Certificate.CertificateAuthenticator auth)
        {
            var cert = MakeCert("CN=BADCHAIN");
            var ctx = new Silica.Authentication.Abstractions.AuthenticationContext { CertificateBytes = cert.Export(X509ContentType.Cert) };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for bad chain cert.");
        }

        private static async Task TestMissing(Silica.Authentication.Certificate.CertificateAuthenticator auth)
        {
            var ctx = new Silica.Authentication.Abstractions.AuthenticationContext { CertificateBytes = null };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for missing cert.");
        }

        private static async Task TestMalformed(Silica.Authentication.Certificate.CertificateAuthenticator auth)
        {
            var ctx = new Silica.Authentication.Abstractions.AuthenticationContext { CertificateBytes = new byte[] { 1, 2, 3, 4, 5 } };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for malformed cert bytes.");
        }

        // Helper: generate a minimal self-signed certificate with the given subject for deterministic tests.
        private static X509Certificate2 MakeCert(string subject)
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(subject, rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
            return new X509Certificate2(cert.Export(X509ContentType.Cert));
        }

        // Simple DummyMetricsManager for harness use
        private sealed class DummyMetricsManager : IMetricsManager
        {
            public void Register(Silica.DiagnosticsCore.Metrics.MetricDefinition def) { }
            public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, double value, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, long value, params KeyValuePair<string, object>[] tags) { }
        }
    }
}
