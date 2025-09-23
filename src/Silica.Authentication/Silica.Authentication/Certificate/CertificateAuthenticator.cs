using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Silica.Authentication.Abstractions;
using Silica.Authentication.Diagnostics;
using Silica.Authentication.Metrics;
using Silica.DiagnosticsCore.Metrics;
using Silica.Exceptions;

namespace Silica.Authentication.Certificate
{
    public sealed class CertificateAuthenticator : IAuthenticator
    {
        private readonly ICertificateValidator _validator;
        private readonly CertificateOptions _options;
        private readonly IMetricsManager? _metrics;
        private readonly string _componentName;
        private readonly IPrincipalMapper _roles;

        private static class StopwatchUtil
        {
            public static long GetTimestamp() => Stopwatch.GetTimestamp();
            public static double GetElapsedMilliseconds(long startTicks)
                => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        }

        public CertificateAuthenticator(ICertificateValidator validator, CertificateOptions options, IMetricsManager? metrics = null, string componentName = "Silica.Authentication.Certificate", IPrincipalMapper? principalMapper = null)
        {
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _options = options ?? new CertificateOptions();
            _metrics = metrics;
            _componentName = string.IsNullOrWhiteSpace(componentName) ? "Silica.Authentication.Certificate" : componentName;
            _roles = principalMapper ?? new NoOpPrincipalMapper();

            if (_metrics is not null)
            {
                AuthenticationMetrics.RegisterAll(_metrics, _componentName);
            }
        }

        public string Name => "Certificate";

        private static Dictionary<string, string> CloneTags(IReadOnlyDictionary<string, string> src)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (src != null)
            {
                var e = src.GetEnumerator();
                try
                {
                    while (e.MoveNext())
                    {
                        var kv = e.Current;
                        d[kv.Key] = kv.Value;
                    }
                }
                finally { e.Dispose(); }
            }
            return d;
        }

        private System.Threading.Tasks.Task ApplyFailureDelayAsync(System.Threading.CancellationToken cancellationToken)
        {
            try
            {
                if (_options.FailureDelayMs > 0 && !cancellationToken.IsCancellationRequested)
                {
                    return System.Threading.Tasks.Task.Delay(_options.FailureDelayMs, cancellationToken);
                }
            }
            catch { }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public async System.Threading.Tasks.Task<IAuthenticationResult> AuthenticateAsync(AuthenticationContext context, System.Threading.CancellationToken cancellationToken = default)
        {
            var start = StopwatchUtil.GetTimestamp();
            var baseTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TagKeys.Component] = _componentName,
                ["auth.method"] = Name,
                [TagKeys.Field] = Name
            };
            try
            {
                AuthenticationMetrics.IncrementAttempt(_metrics, Name);
                AuthenticationDiagnostics.EmitDebug(_componentName, "Authenticate", "attempt_certificate", null, baseTags, allowDebug: true);
                cancellationToken.ThrowIfCancellationRequested();
                if (context == null)
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidRequest);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "null_context", null, baseTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new CertificateAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidRequest };
                }
                if (!_validator.IsAvailable)
                {
                    var ex = new Silica.Authentication.AuthenticationProviderUnavailableException("Certificate");
                    AuthenticationMetrics.IncrementProviderUnavailable(_metrics, Name);
                    var unavailableTags = CloneTags(baseTags);
                    unavailableTags["outcome"] = "failure";
                    unavailableTags["reason"] = AuthenticationFailureReasons.ProviderUnavailable;
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "error", "provider_unavailable", ex, unavailableTags);
                    var elapsedProv = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedProv, Name);
                    throw ex;
                }
                if (context.CertificateBytes == null || context.CertificateBytes.Length == 0)
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.MissingCertificate);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "missing_certificate", null, baseTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new CertificateAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.MissingCertificate };
                }
                X509Certificate2? cert = null;
                try
                {
                    cert = new X509Certificate2(context.CertificateBytes);
                }
                catch (Exception ex)
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.MalformedCertificate);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "malformed_certificate", ex, baseTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new CertificateAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.MalformedCertificate };
                }
                CertificateValidationResult vresult;
                try
                {
                    try
                    {
                        vresult = await _validator.ValidateAsync(cert, _options, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        cert.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    AuthenticationMetrics.IncrementProviderUnavailable(_metrics, Name);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "error", "validator_exception", ex, baseTags);
                    var elapsedProv = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedProv, Name);
                    throw new Silica.Authentication.AuthenticationProviderUnavailableException("Certificate");
                }
                if (!vresult.IsValid)
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidCertificate);
                    var failTags = CloneTags(baseTags);
                    failTags["outcome"] = "failure";
                    failTags["reason"] = vresult.FailureReason ?? AuthenticationFailureReasons.InvalidCertificate;
                    if (!string.IsNullOrEmpty(vresult.Principal))
                        failTags["username"] = vresult.Principal;
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "certificate_validation_failed", null, failTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new CertificateAuthenticationResult { Succeeded = false, FailureReason = vresult.FailureReason ?? AuthenticationFailureReasons.InvalidCertificate };
                }
                // Enterprise safety: authenticated success must have a principal.
                if (string.IsNullOrWhiteSpace(vresult.Principal))
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidCertificate);
                    var missingPrincipalTags = CloneTags(baseTags);
                    missingPrincipalTags["outcome"] = "failure";
                    missingPrincipalTags["reason"] = "missing_principal";
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "missing_principal", null, missingPrincipalTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new CertificateAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidCertificate };
                }
                var principal = vresult.Principal!;
                var mapped = _roles.MapRoles(principal);
                IReadOnlyCollection<string> roles;
                if (mapped == null || mapped.Length == 0)
                {
                    roles = System.Array.AsReadOnly(System.Array.Empty<string>());
                }
                else
                {
                    roles = System.Array.AsReadOnly((string[])mapped.Clone());
                }

                var elapsed = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.IncrementSuccess(_metrics, Name);
                AuthenticationMetrics.RecordLatency(_metrics, elapsed, Name);
                var successTags = new Dictionary<string, string>(baseTags, StringComparer.OrdinalIgnoreCase)
                {
                    ["username"] = principal,
                    ["outcome"] = "success"
                };
                // Ensure comparer is correct
                successTags = CloneTags(successTags);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "info", "certificate_auth_succeeded", null, successTags);
                return new CertificateAuthenticationResult { Succeeded = true, Principal = principal, Roles = roles };
            }
            catch (OperationCanceledException ocex)
            {
                try
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidRequest);
                    var cancelTags = new Dictionary<string, string>(baseTags, StringComparer.OrdinalIgnoreCase)
                    {
                        ["outcome"] = "failure",
                        ["reason"] = "cancelled"
                    };
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "cancelled", ocex, cancelTags);
                    var elapsedCancel = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedCancel, Name);
                }
                catch { }
                throw;
            }
            catch (SilicaException)
            {
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    AuthenticationMetrics.IncrementUnexpectedError(_metrics, Name);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "error", "unexpected_error", ex, baseTags);
                    var elapsedErr = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedErr, Name);
                }
                catch { }
                throw new UnexpectedAuthenticationErrorException(Name);
            }
        }
    }
}
