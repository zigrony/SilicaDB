// AuthenticationTestHarnessJwt.cs
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Silica.Authentication.Abstractions;
using Silica.Authentication.Jwt;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Authentication.Tests
{
    public static class AuthenticationTestHarnessJwt
    {
        public static async Task Run()
        {
            Console.WriteLine("=== JWT Authentication Test Harness ===");

            IMetricsManager metrics = new DummyMetricsManager();

            var validator = new DummyJwtValidator();
            var options = new JwtOptions { Issuer = "test-issuer", Audience = "test-audience", ClockSkewSeconds = 60 };
            var auth = new JwtAuthenticator(validator, options, metrics: metrics, componentName: "Silica.Authentication.Jwt");

            await RunTest("[JWT] Success with VALID token", () => TestSuccess(auth));
            await RunTest("[JWT] Invalid token", () => TestInvalid(auth));
            await RunTest("[JWT] Missing token", () => TestMissing(auth));
            await RunTest("[JWT] Malformed token", () => TestMalformed(auth));

            Console.WriteLine("=== JWT Authentication Test Harness Complete ===");
        }

        private static async Task RunTest(string name, Func<Task> test)
        {
            Console.WriteLine(name);
            var sw = Stopwatch.StartNew();
            await test();
            sw.Stop();
            Console.WriteLine(name + " passed in " + sw.Elapsed.TotalMilliseconds + " ms.");
        }

        private static async Task TestSuccess(JwtAuthenticator auth)
        {
            var ctx = new AuthenticationContext { Token = "VALID" };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (!res.Succeeded) throw new InvalidOperationException("Expected success for VALID token.");
            if (res.Principal != "testuser") throw new InvalidOperationException("Unexpected principal returned.");
        }

        private static async Task TestInvalid(JwtAuthenticator auth)
        {
            var ctx = new AuthenticationContext { Token = "BAD" };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for BAD token.");
        }

        private static async Task TestMissing(JwtAuthenticator auth)
        {
            var ctx = new AuthenticationContext { Token = null };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for missing token.");
        }

        private static async Task TestMalformed(JwtAuthenticator auth)
        {
            var ctx = new AuthenticationContext { Token = "!!!notjwt!!!" };
            var res = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            if (res.Succeeded) throw new InvalidOperationException("Expected failure for malformed token.");
        }

        private sealed class DummyMetricsManager : IMetricsManager
        {
            public void Register(Silica.DiagnosticsCore.Metrics.MetricDefinition def) { }
            public void Increment(string name, long value = 1, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, double value, params KeyValuePair<string, object>[] tags) { }
            public void Record(string name, long value, params KeyValuePair<string, object>[] tags) { }
        }
    }
}
