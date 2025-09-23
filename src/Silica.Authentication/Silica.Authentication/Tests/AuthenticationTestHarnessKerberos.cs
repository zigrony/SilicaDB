// AuthenticationTestHarnessKerberos.cs
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silica.Authentication.Abstractions;
using Silica.Authentication.Kerberos;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Authentication.Tests
{
    public static class AuthenticationTestHarnessKerberos
    {
        public static async Task Run()
        {
            Console.WriteLine("=== Kerberos Authentication Test Harness ===");

            IMetricsManager metrics = new DummyMetricsManager();

            var provider = new DummyGssapiProvider();
            var options = new KerberosOptions();
            var auth = new KerberosAuthenticator(provider, options, metrics: metrics, componentName: "Silica.Authentication.Kerberos");

            await RunTest("[Kerberos] Success with VALID token", () => TestSuccess(auth));
            await RunTest("[Kerberos] Failure with BAD token", () => TestInvalidToken(auth));
            await RunTest("[Kerberos] Missing token", () => TestMissingToken(auth));
            await RunTest("[Kerberos] Malformed base64 token", () => TestMalformedBase64(auth));
            await RunTest("[Kerberos] Provider unavailable", () => TestProviderUnavailable());

            Console.WriteLine("=== Kerberos Authentication Test Harness Complete ===");
        }

        private static async Task RunTest(string name, Func<Task> test)
        {
            Console.WriteLine(name);
            var sw = Stopwatch.StartNew();
            await test();
            sw.Stop();
            Console.WriteLine(name + " passed in " + sw.Elapsed.TotalMilliseconds + " ms.");
        }

        private static async Task TestSuccess(KerberosAuthenticator auth)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("VALID"));
            var ctx = new AuthenticationContext { Token = token };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (!res.Succeeded) throw new InvalidOperationException("Expected success for VALID token.");
            if (res.Principal != "user@REALM") throw new InvalidOperationException("Unexpected principal returned.");
        }

        private static async Task TestInvalidToken(KerberosAuthenticator auth)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("BAD"));
            var ctx = new AuthenticationContext { Token = token };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for BAD token.");
            if (res.FailureReason != "invalid_token") throw new InvalidOperationException("Unexpected failure reason for BAD token.");
        }

        private static async Task TestMissingToken(KerberosAuthenticator auth)
        {
            var ctx = new AuthenticationContext { Token = null, CertificateBytes = null };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure when token is missing.");
            if (res.FailureReason != "missing_token") throw new InvalidOperationException("Unexpected failure reason for missing token.");
        }

        private static async Task TestMalformedBase64(KerberosAuthenticator auth)
        {
            var ctx = new AuthenticationContext { Token = "!!!!notbase64!!!!" };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for malformed base64 token.");
            if (res.FailureReason != "invalid_token_base64") throw new InvalidOperationException("Unexpected failure reason for malformed token.");
        }

        private static async Task TestProviderUnavailable()
        {
            var unavailable = new UnavailableGssapiProvider();
            var options = new KerberosOptions();
            var metrics = new DummyMetricsManager();
            var auth = new KerberosAuthenticator(unavailable, options, metrics: metrics, componentName: "Silica.Authentication.Kerberos");

            bool threw = false;
            try
            {
                await auth.AuthenticateAsync(
                    new AuthenticationContext { Token = Convert.ToBase64String(Encoding.UTF8.GetBytes("VALID")) },
                    CancellationToken.None);
            }
            catch (Silica.Authentication.AuthenticationProviderUnavailableException)
            {
                threw = true;
            }
            if (!threw) throw new InvalidOperationException("Expected AuthenticationProviderUnavailableException when provider is unavailable.");
        }

        // Simple test helpers copied from other harnesses
        private sealed class DummyMetricsManager : IMetricsManager
        {
            public void Register(Silica.DiagnosticsCore.Metrics.MetricDefinition def) { }
            public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, double value, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, long value, params KeyValuePair<string, object>[] tags) { }
        }

        private sealed class UnavailableGssapiProvider : IGssapiProvider
        {
            public bool IsAvailable => false;
            public Task<string?> ValidateTokenAsync(byte[] token, string? expectedService, CancellationToken cancellationToken = default)
                => Task.FromResult<string?>(null);
        }
    }
}
