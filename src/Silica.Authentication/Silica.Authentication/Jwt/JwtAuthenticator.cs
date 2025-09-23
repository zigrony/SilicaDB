using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Silica.Authentication.Abstractions;
using Silica.Authentication.Metrics;
using Silica.Authentication.Diagnostics;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Authentication.Jwt
{
    public sealed class JwtAuthenticator : IAuthenticator
    {
        private readonly IJwtValidator _validator;
        private readonly JwtOptions _options;
        private readonly IMetricsManager? _metrics;
        private readonly string _componentName;
        private readonly IPrincipalMapper _roles;

        private static class StopwatchUtil
        {
            public static long GetTimestamp() => Stopwatch.GetTimestamp();
            public static double GetElapsedMilliseconds(long startTicks)
                => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        }

        public JwtAuthenticator(IJwtValidator validator, JwtOptions options, IMetricsManager? metrics = null, string componentName = "Silica.Authentication.Jwt", IPrincipalMapper? principalMapper = null)
        {
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _options = options ?? new JwtOptions();
            _metrics = metrics;
            _componentName = string.IsNullOrWhiteSpace(componentName) ? "Silica.Authentication.Jwt" : componentName;
            // store the principal mapper for later role mapping
            _roles = principalMapper ?? new NoOpPrincipalMapper();

            if (_metrics is not null)
            {
                AuthenticationMetrics.RegisterAll(_metrics, _componentName);
            }
        }

        public string Name => "Jwt";
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
            var start = StopwatchUtil.GetTimestamp();
            var baseTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TagKeys.Component] = _componentName,
                ["auth.method"] = Name,
                [TagKeys.Field] = Name
            };

            AuthenticationMetrics.IncrementAttempt(_metrics, Name);
            AuthenticationDiagnostics.EmitDebug(_componentName, "Authenticate", "attempt_jwt", null, baseTags, allowDebug: true);
            cancellationToken.ThrowIfCancellationRequested();

            if (context == null)
            {
                AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidRequest);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "null_context", null, baseTags);
                await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                return new JwtAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidRequest };
            }

            if (string.IsNullOrWhiteSpace(context.Token))
            {
                AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.MissingToken);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "missing_token", null, baseTags);
                await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                return new JwtAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.MissingToken };
            }

            if (_options.MaxTokenLength > 0 && context.Token!.Length > _options.MaxTokenLength)
            {
                AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.TokenTooLong);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "token_too_long", null, baseTags);
                await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                return new JwtAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.TokenTooLong };
            }

            if (_options.EnableExpiredSentinel && string.Equals(context.Token, "EXPIRED", StringComparison.OrdinalIgnoreCase))
            {
                AuthenticationMetrics.IncrementTokenExpired(_metrics, Name);
                AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.TokenExpired);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "token_expired", null, baseTags);
                await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                return new JwtAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.TokenExpired };
            }

            IDictionary<string, string>? claims = null;
            try
            {
                claims = await _validator.ValidateAsync(context.Token!, _options, cancellationToken).ConfigureAwait(false);
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
            catch (Exception ex)
            {
                AuthenticationMetrics.IncrementProviderUnavailable(_metrics, Name);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "error", "validation_exception", ex, baseTags);
                var elapsedErr = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedErr, Name);
                throw new Silica.Authentication.AuthenticationProviderUnavailableException("Jwt");
            }

            if (claims == null)
            {
                AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidToken);
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "invalid_token", null, baseTags);
                await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                return new JwtAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidToken };
            }

            string? principal = null;
            string v;
            if (claims.TryGetValue("sub", out v)) principal = v;
            else if (claims.TryGetValue("preferred_username", out v)) principal = v;
            else if (claims.TryGetValue("name", out v)) principal = v;

            // Enterprise safety: authenticated success must have a principal.
            // If we cannot derive one from claims, treat as invalid token.
            if (principal == null)
            {
                AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidToken);
                var missingPrincipalTags = CloneTags(baseTags);
                missingPrincipalTags["outcome"] = "failure";
                missingPrincipalTags["reason"] = "missing_principal_claim";
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "missing_principal_claim", null, missingPrincipalTags);
                await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                return new JwtAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidToken };
            }

            var roles = principal == null
                ? System.Array.Empty<string>()
                : _roles.MapRoles(principal) ?? System.Array.Empty<string>();

            var elapsed = StopwatchUtil.GetElapsedMilliseconds(start);
            AuthenticationMetrics.IncrementSuccess(_metrics, Name);
            AuthenticationMetrics.RecordLatency(_metrics, elapsed, Name);

            var okTags = CloneTags(baseTags);
            okTags["username"] = principal;
            okTags["outcome"] = "success";
            AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "info", "jwt_auth_succeeded", null, okTags);

            return new JwtAuthenticationResult
            {
                Succeeded = true,
                Principal = principal,
                Roles = roles.Length == 0 ? System.Array.AsReadOnly(System.Array.Empty<string>()) : System.Array.AsReadOnly((string[])roles.Clone())
            };
        }
    }
}
