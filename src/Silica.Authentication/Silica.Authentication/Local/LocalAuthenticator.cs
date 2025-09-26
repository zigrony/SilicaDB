using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Silica.Authentication.Abstractions;
using Silica.Authentication.Exceptions;
using Silica.Authentication.Metrics;
using Silica.Authentication.Diagnostics;
using Silica.DiagnosticsCore.Metrics;
using Silica.Exceptions;
using Silica.Sessions.Contracts;

namespace Silica.Authentication.Local
{
    /// <summary>
    /// Local authenticator implementation with metrics and diagnostics wired following Concurrency pattern.
    /// </summary>
    public sealed class LocalAuthenticator : IAuthenticator
    {
        private readonly ILocalUserStore _userStore;
        private readonly PasswordHasher _hasher;
        private readonly LocalAuthenticationOptions _options;
        private readonly IMetricsManager? _metrics;
        private readonly string _componentName;
        private readonly IPrincipalMapper _roles;
        private readonly ISessionProvider? _sessionProvider;

        // Reuse a stopwatch-style helper to measure elapsed ms deterministically
        private static class StopwatchUtil
        {
            public static long GetTimestamp() => Stopwatch.GetTimestamp();
            public static double GetElapsedMilliseconds(long startTicks)
                => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        }

        private Task ApplyFailureDelayAsync(System.Threading.CancellationToken cancellationToken)
        {
            // Apply a small, configurable delay on failures to reduce brute-force and timing signals.
            // Do not delay if disabled or if cancellation is requested.
            try
            {
                if (_options.FailureDelayMs > 0 && !cancellationToken.IsCancellationRequested)
                {
                    return Task.Delay(_options.FailureDelayMs, cancellationToken);
                }
            }
            catch { /* ignore delay errors */ }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Construct LocalAuthenticator. If metrics is non-null, AuthenticationMetrics.RegisterAll will be called with componentName.
        /// </summary>
        public LocalAuthenticator(
            ILocalUserStore userStore,
            PasswordHasher hasher,
            LocalAuthenticationOptions options,
            IMetricsManager? metrics = null,
            string componentName = "Silica.Authentication.Local",
            IPrincipalMapper? principalMapper = null,
            ISessionProvider? sessionProvider = null)
        {
            _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
            _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _metrics = metrics;
            _componentName = string.IsNullOrWhiteSpace(componentName) ? "Silica.Authentication.Local" : componentName;
            _sessionProvider = sessionProvider;
            // ADD THIS:
            _roles = principalMapper ?? new NoOpPrincipalMapper();

            if (_metrics is not null)
            {
                AuthenticationMetrics.RegisterAll(_metrics, _componentName);
            }

            if (_metrics is not null)
            {
                AuthenticationMetrics.RegisterAll(_metrics, _componentName);
            }
        }

        public string Name => "Local";

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
        public async System.Threading.Tasks.Task<IAuthenticationResult> AuthenticateAsync(AuthenticationContext context, System.Threading.CancellationToken cancellationToken = default)
        {
            var start = StopwatchUtil.GetTimestamp();

            // Minimal tag set for diagnostics
            var baseTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TagKeys.Component] = _componentName,
                ["auth.method"] = Name,
                [TagKeys.Field] = Name
            };

            try
            {
                AuthenticationMetrics.IncrementAttempt(_metrics, Name);
                AuthenticationDiagnostics.EmitDebug(_componentName, "Authenticate", "attempt_local_auth", null, baseTags, allowDebug: true);
                cancellationToken.ThrowIfCancellationRequested();

                if (context == null)
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidRequest);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "null_context", null, baseTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new LocalAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidRequest };
                }

                if (string.IsNullOrWhiteSpace(context.Username) || string.IsNullOrWhiteSpace(context.Password))
                {
                    var usernameForTag = context.Username ?? string.Empty;
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.MissingCredentials);

                    var missingTags = CloneTags(baseTags);
                    missingTags["username"] = usernameForTag;
                    missingTags["outcome"] = "failure";
                    missingTags["reason"] = "missing_credentials";
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "missing_credentials", null, missingTags);
                    // Fail fast on missing credentials
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new LocalAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.MissingCredentials };
                }

                // Enforce minimum password length policy before hashing
                if (context.Password!.Length < _options.MinPasswordLength)
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidCredentials);
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "password_too_short", null, baseTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new LocalAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidCredentials, SessionId = null };
                }

                var user = _userStore.FindByUsername(context.Username!);
                if (user == null)
                {
                    var usernameForTag = context.Username ?? string.Empty;
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidCredentials);

                    var notFoundTags = CloneTags(baseTags);
                    notFoundTags["username"] = usernameForTag;
                    notFoundTags["outcome"] = "failure";
                    notFoundTags["reason"] = "user_not_found";
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "user_not_found", null, notFoundTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new LocalAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidCredentials };
                }

                if (user.IsLocked)
                {
                    AuthenticationMetrics.IncrementLockout(_metrics, Name);

                    var lockedTags = CloneTags(baseTags);
                    lockedTags["username"] = user.Username;
                    lockedTags["outcome"] = "failure";
                    lockedTags["reason"] = "account_locked";
                    // Emit lockout diagnostic for trace parity with other outcomes
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "account_locked", null, lockedTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new LocalAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.AccountLocked };
                }

                // Use the stored per-user salt for hashing.
                var salt = user.Salt ?? System.Array.Empty<byte>();

                // Clamp iterations to minimum policy to avoid weak hashing from misconfiguration
                int iterations = _options.HashIterations < 310000 ? 310000 : _options.HashIterations;
                bool verified = false;
                try
                {
                    verified = _hasher.Verify(context.Password!, user.PasswordHash, salt, iterations);
                }
                catch
                {
                    // Treat any hasher failure as invalid credentials to avoid internal errors leaking.
                    verified = false;
                }
                if (!verified)
                {
                    _userStore.RecordFailedAttempt(user.Username);
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidCredentials);
                    var invalidTags = CloneTags(baseTags);
                    invalidTags["username"] = user.Username;
                    invalidTags["outcome"] = "failure";
                    invalidTags["reason"] = "invalid_password";
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "warn", "invalid_password", null, invalidTags);
                    await ApplyFailureDelayAsync(cancellationToken).ConfigureAwait(false);
                    var elapsedFail = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedFail, Name);
                    return new LocalAuthenticationResult { Succeeded = false, FailureReason = AuthenticationFailureReasons.InvalidCredentials };
                }

                _userStore.RecordSuccessfulLogin(user.Username);

                var elapsed = StopwatchUtil.GetElapsedMilliseconds(start);
                AuthenticationMetrics.IncrementSuccess(_metrics, Name);
                AuthenticationMetrics.RecordLatency(_metrics, elapsed, Name);

                // Emit successful trace (low cardinality tags)
                var successTags = CloneTags(baseTags);
                successTags["username"] = user.Username;
                successTags["outcome"] = "success";
                AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "info", "local_auth_succeeded", null, successTags);
                // Roles: merge store roles with mapper roles (manual union, no LINQ).
                string[] storeRoles = user.Roles ?? System.Array.Empty<string>();
                string[] mappedRoles = _roles.MapRoles(user.Username) ?? System.Array.Empty<string>();
                // Manual union preserving order: storeRoles first, then mappedRoles if not already present.
                // Use a simple list buffer and O(n*m) containment checks to avoid LINQ/HashSet.
                string[] buffer = new string[storeRoles.Length + mappedRoles.Length];
                int count = 0;
                // copy storeRoles
                for (int i = 0; i < storeRoles.Length; i++)
                {
                    var r = storeRoles[i] ?? string.Empty;
                    buffer[count++] = r;
                }
                // append mappedRoles if not present
                for (int j = 0; j < mappedRoles.Length; j++)
                {
                    var mr = mappedRoles[j] ?? string.Empty;
                    bool exists = false;
                    for (int k = 0; k < count; k++)
                    {
                        if (string.Equals(buffer[k], mr, StringComparison.Ordinal))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        buffer[count++] = mr;
                    }
                }
                string[] finalRoles;
                if (count == 0)
                {
                    finalRoles = System.Array.Empty<string>();
                }
                else
                {
                    finalRoles = new string[count];
                    for (int i = 0; i < count; i++) finalRoles[i] = buffer[i];
                }
                IReadOnlyCollection<string> rolesRo = System.Array.AsReadOnly(finalRoles);
                // Optional: create a session for the authenticated principal via the provider (DI).
                Guid? sessionId = null;
                try
                {
                    if (_sessionProvider != null)
                    {
                        var session = _sessionProvider.CreateSession(user.Username, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), coordinatorNode: "interface");
                        sessionId = session.SessionId;
                        session.Touch(); // promote to Active on activity
                    }
                }
                catch { /* do not fail authentication if session creation fails */ }

                return new LocalAuthenticationResult { Succeeded = true, Principal = user.Username, Roles = rolesRo, SessionId = sessionId };
            }
            catch (OperationCanceledException ocex)
            {
                try
                {
                    AuthenticationMetrics.IncrementFailure(_metrics, Name, AuthenticationFailureReasons.InvalidRequest);
                    var cancelTags = new Dictionary<string, string>(baseTags, StringComparer.OrdinalIgnoreCase)
                    {
                        ["username"] = context?.Username ?? string.Empty,
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
                try
                {
                    AuthenticationMetrics.IncrementUnexpectedError(_metrics, Name);
                    var errTags = CloneTags(baseTags);
                    errTags["username"] = context?.Username ?? string.Empty;
                    errTags["outcome"] = "failure";
                    errTags["reason"] = AuthenticationFailureReasons.UnexpectedError;
                    AuthenticationDiagnostics.Emit(_componentName, "Authenticate", "error", "unexpected_error", ex, errTags);
                    var elapsedErr = StopwatchUtil.GetElapsedMilliseconds(start);
                    AuthenticationMetrics.RecordLatency(_metrics, elapsedErr, Name);
                }
                catch { }
                throw new UnexpectedAuthenticationErrorException(Name);
            }
        }
    }
}
