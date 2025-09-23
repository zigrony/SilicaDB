using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Silica.Authentication.Abstractions;
using Silica.Authentication.Metrics;
using Silica.Authentication.Diagnostics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Authentication.Ntlm
{
    public sealed class NtlmAuthenticator : IAuthenticator
    {
        private readonly INtlmSecurityProvider _security;
        private readonly NtlmOptions _options;
        private readonly IMetricsManager? _metrics;
        private readonly string _componentName;
        private readonly IPrincipalMapper _roles;

        private static class StopwatchUtil
        {
            public static long GetTimestamp() => Stopwatch.GetTimestamp();
            public static double GetElapsedMilliseconds(long startTicks)
                => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        }

        public NtlmAuthenticator(INtlmSecurityProvider security, NtlmOptions options, IMetricsManager? metrics = null, string componentName = "Silica.Authentication.Ntlm", IPrincipalMapper? principalMapper = null)
        {
            _security = security ?? throw new ArgumentNullException(nameof(security));
            _options = options ?? new NtlmOptions();
            _metrics = metrics;
            _componentName = string.IsNullOrWhiteSpace(componentName) ? "Silica.Authentication.Ntlm" : componentName;

            _roles = principalMapper ?? new NoOpPrincipalMapper();

            if (_metrics is not null)
            {
                AuthenticationMetrics.RegisterAll(_metrics, _componentName);
            }
        }

        public string Name => "Ntlm";
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

        public async System.Threading.Tasks.Task<IAuthenticationResult> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
        {
            long start = StopwatchUtil.GetTimestamp();
            var baseTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TagKeys.Component] = _componentName,
                ["auth.method"] = Name,
                [TagKeys.Field] = Name
            };
            AuthenticationMetrics.IncrementAttempt(_metrics, Name);
            AuthenticationDiagnostics.EmitDebug(_componentName, "Authenticate", "attempt_ntlm", null, baseTags, allowDebug: true);
            cancellationToken.ThrowIfCancellationRequested();
            if (context == null)
            {
                AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidRequest);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "null_context", null, baseTags);
                await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                return new NtlmAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidRequest };
            }
            if (!_security.IsAvailable)
            {
                var ex = new Silica.Authentication.AuthenticationProviderUnavailableException("NTLM");
                AuthenticationMetrics.IncrementProviderUnavailable(_metrics, Name);
                var unavailableTags = CloneTags(baseTags);
                unavailableTags["outcome"] = "failure";
                unavailableTags["reason"] = AuthenticationFailureReasons.ProviderUnavailable;
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "error", "provider_unavailable", ex, unavailableTags);
                var elapsedProv = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedProv, Name);
                throw ex;
            }
            byte[] tokenBytes;
            if (context.TokenBytes is not null && context.TokenBytes.Length > 0)
            {
                if (_options.MaxTokenBytes > 0 && context.TokenBytes.Length > _options.MaxTokenBytes)
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.TokenTooLong);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "token_too_long", null, baseTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new NtlmAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.TokenTooLong };
                }
                tokenBytes = context.TokenBytes;
            }
            else if (!string.IsNullOrWhiteSpace(context.Token))
            {
                try { tokenBytes = Convert.FromBase64String(context.Token!); }
                catch
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidTokenBase64);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "invalid_token_base64", null, baseTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new NtlmAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidTokenBase64 };
                }
                if (_options.MaxTokenBytes > 0 && tokenBytes.Length > _options.MaxTokenBytes)
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.TokenTooLong);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "token_too_long", null, baseTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new NtlmAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.TokenTooLong };
                }
            }
            else
            {
                AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.MissingToken);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "missing_token", null, baseTags);
                await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                return new NtlmAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.MissingToken };
            }
            string? principal = null;
            try
            {
                principal = await _security.ValidateTokenAsync(tokenBytes, _options.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ocex)
            {
                AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidRequest);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "cancelled", ocex, baseTags);
                var elapsedCancel = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedCancel, Name);
                throw;
            }
            catch (TimeoutException tex)
            {
                AuthenticationMetrics.IncrementProviderUnavailable(_metrics, Name);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "error", "provider_timeout", tex, baseTags);
                var elapsedTimeout = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedTimeout, Name);
                throw new Silica.Authentication.AuthenticationProviderUnavailableException("NTLM");
            }
            catch (Exception ex)
            {
                try
                {
                    AuthenticationMetrics.IncrementUnexpectedError(_metrics, Name);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "error", "unexpected_validation_error", ex, baseTags);
                    var elapsedErr = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedErr, Name);
                }
                catch { }
                throw new UnexpectedAuthenticationErrorException(Name);
            }
            if (principal == null)
            {
                AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidToken);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "invalid_ntlm_token", null, baseTags);
                await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                return new NtlmAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidToken };
            }
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
            var okTags = CloneTags(baseTags);
            okTags["username"] = principal;
            okTags["outcome"] = "success";
            AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "info", "ntlm_auth_succeeded", null, okTags);
            return new NtlmAuthenticationResult { Succeeded = true, Principal = principal, Roles = roles };
        }
    }
}
