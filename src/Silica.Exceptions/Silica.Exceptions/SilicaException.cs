// Filename: Silica.Exceptions/Silica.Exceptions/SilicaException.cs
using System;
using System.Runtime.Serialization;
using System.Runtime.CompilerServices;
using System.Globalization;

namespace Silica.Exceptions
{
    /// <summary>
    /// Base class for all Silica exceptions. Enforces consistent metadata and cause-chain preservation.
    /// </summary>
    [Serializable]
    public abstract class SilicaException : Exception
    {
        // Cache of validated declared type names keyed by the actual runtime type.
        // Avoids repeated validation in hot throw paths while keeping enforcement strict.
        // ConditionalWeakTable ensures the cache does not prevent type collection in unloadable contexts.
        private static readonly ConditionalWeakTable<Type, string> s_validatedDeclaredNames =
            new ConditionalWeakTable<Type, string>();

        public string Code { get; }
        public FailureCategory Category { get; }
        /// <summary>
        /// Numeric identifier for this exception instance, unique within its subsystem range.
        /// Useful for analytics, correlation, and registry enforcement.
        /// </summary>
        public int ExceptionId { get; }

        public string CorrelationId { get; }
        public DateTimeOffset OccurredAt { get; }
        // Ensure observers are notified at most once, even if derived types opt to notify later.
        // Use an int for Interlocked semantics: 0 = not notified, 1 = notified.
        private int _creationNotified;

        /// <summary>
        /// Optional self-declared CLR type name string for enforcement without runtime reflection.
        /// Derived exceptions can override to return a constant like "My.Product.Subsystem.MySpecificException".
        /// </summary>
        protected virtual string? DeclaredExceptionTypeName => null;

        protected SilicaException(
            string code,
            string message,
            FailureCategory category,
            int exceptionId,
            string? correlationId = null,
            Exception? innerException = null)
            : this(code, message, category, exceptionId, notifyObservers: true, correlationId: correlationId, innerException: innerException)
        {
        }

        /// <summary>
        /// Base constructor with control over observer notification timing. Use notifyObservers: false
        /// when derived constructors need to initialize additional state before observers are notified.
        /// Call NotifyCreated() once after derived initialization completes.
        /// </summary>
        protected SilicaException(
            string code,
            string message,
            FailureCategory category,
            int exceptionId,
            bool notifyObservers,
            string? correlationId = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            ExceptionPolicy.ValidateMessage(message, nameof(message));
            // Canonicalize first, then validate the canonical value to ensure enforcement aligns with the normalized form.
            var canonicalCode = ExceptionPolicy.CanonicalizeCode(code);
            ExceptionPolicy.ValidateCode(canonicalCode, nameof(code));
            Code = canonicalCode;
            ExceptionPolicy.ValidateCategoryAny(category, nameof(category));
            Category = category;

            // Validate ExceptionId (positive, within subsystem range if applicable)
            ExceptionPolicy.ValidateExceptionId(exceptionId, nameof(exceptionId));
            ExceptionId = exceptionId;
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                // Normalize even generated IDs to avoid any drift (be explicit, not implicit).
                // This ensures lowercase 'N' format consistently across runtimes/environments.
                var gen = Guid.NewGuid().ToString("N");
                // CanonicalizeGuidN will return the same string or lowercased if needed.
                CorrelationId = ExceptionPolicy.CanonicalizeGuidN(gen);
            }
            else
            {
                ExceptionPolicy.ValidateCorrelationId(correlationId, nameof(correlationId));
                CorrelationId = ExceptionPolicy.CanonicalizeGuidN(correlationId);
            }
            OccurredAt = DateTimeOffset.UtcNow;

            // Optional enforcement to catch unregistered codes early in throw paths.
            var enforce = ExceptionRegistry.EnforceRegisteredCodes;
            if (enforce)
            {
                // Code is already canonicalized above; use the internal fast-path.
                if (!ExceptionRegistry.TryGetByCanonicalCode(Code, out var def) || def == null)
                {
                    // Include canonicalized code for triage and consistency.
                    throw new ArgumentException("Exception code is not registered: " + Code, nameof(code));
                }
                // Enforce category consistency with the registered definition to prevent drift.
                if (def.Category != Category)
                {
                    // Provide expected vs actual details.
                    throw new ArgumentException(
                        "Exception category does not match the registered definition. " +
                        "Code=" + Code + ", Expected=" + def.Category.ToString() + ", Actual=" + Category.ToString(),
                        nameof(category));
                }
                // Optionally enforce that the thrown exception type matches the registered schema, without reflection.
                // Only applies when the derived type opts in by overriding DeclaredExceptionTypeName.
                var declared = DeclaredExceptionTypeName;
                if (!string.IsNullOrEmpty(declared))
                {
                    // Validate once per derived runtime type and cache the declared string without exceptions or reflection.
                    var runtimeType = GetType();
                    // Use GetValue factory to avoid try/catch races on Add.
                    string cached = s_validatedDeclaredNames.GetValue(
                        runtimeType,
                        // Factory validates and returns the declared string for this runtime type.
                        _ =>
                        {
                            ExceptionPolicy.ValidateTypeName(declared!, nameof(DeclaredExceptionTypeName));
                            return declared!;
                        });
                    // If the override value ever changes for this runtime type, fail fast to avoid silent drift.
                    if (!string.Equals(declared, cached, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "DeclaredExceptionTypeName changed for runtime type '" +
                            (runtimeType.FullName ?? "UnknownType") + "'. Cached='" + cached + "', Current='" + declared + "'.",
                            nameof(DeclaredExceptionTypeName));
                    }

                    // Compare against the registered definition.
                    if (!string.Equals(def.ExceptionTypeName, cached, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "Exception CLR type does not match the registered definition. " +
                            "Code=" + Code + ", Expected=" + def.ExceptionTypeName + ", Declared=" + cached,
                            nameof(DeclaredExceptionTypeName));
                    }
                }
            }

            // Best-effort enrichment for sinks that depend on Exception.Data.
            try
            {
                Data[nameof(Code)] = Code;
                Data[nameof(Category)] = (int)Category;
                Data[nameof(ExceptionId)] = ExceptionId;
                Data[nameof(CorrelationId)] = CorrelationId;
                Data[nameof(OccurredAt)] = OccurredAt.ToString("o", CultureInfo.InvariantCulture);
                // Add type identity details for faster field triage without reflection-based enrichers.
                var declaredTypeName = DeclaredExceptionTypeName;
                if (!string.IsNullOrEmpty(declaredTypeName))
                {
                    Data[nameof(DeclaredExceptionTypeName)] = declaredTypeName!;
                }
                var rt = GetType().FullName;
                if (!string.IsNullOrEmpty(rt))
                {
                    Data["RuntimeExceptionTypeName"] = rt!;
                }
            }
            catch { }

            if (notifyObservers)
            {
                NotifyCreated();
            }
        }

        /// <summary>
        /// Notify registered observers that this exception instance was created.
        /// Safe to call at most once; subsequent calls are ignored.
        /// Derived classes should call this when constructed with notifyObservers: false.
        /// </summary>
        protected void NotifyCreated()
        {
            // Fast, thread-safe one-shot. Only the first caller transitions 0->1.
            if (System.Threading.Interlocked.Exchange(ref _creationNotified, 1) != 0) return;
            try { ExceptionNotifier.NotifyCreated(this); } catch { }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string SanitizeSingleLine(string s, int maxLen)
        {
            if (s == null) return string.Empty;
            if (maxLen <= 0) return string.Empty;
            int len = s.Length;
            if (len == 0) return string.Empty;
            int take = len <= maxLen ? len : maxLen;

            // Scan once to see if any control characters appear within the slice.
            int i = 0;
            while (i < take)
            {
                char c = s[i];
                // Treat ASCII controls and common Unicode line separators as control-like for single-line logs.
                bool isControl =
                    (c <= (char)31) ||
                    (c == (char)127) ||
                    (c == (char)0x0085) /* NEL */ ||
                    (c == (char)0x2028) /* LS */ ||
                    (c == (char)0x2029) /* PS */;

                if (isControl)
                {
                    // Allocate one buffer and copy with replacements from here on.
                    var buf = new char[take];
                    int j = 0;
                    while (j < i) { buf[j] = s[j]; j++; }
                    buf[i] = ' ';
                    j = i + 1;
                    while (j < take)
                    {
                        char cj = s[j];
                        bool replace =
                            (cj <= (char)31) ||
                            (cj == (char)127) ||
                            (cj == (char)0x0085) ||
                            (cj == (char)0x2028) ||
                            (cj == (char)0x2029);
                        buf[j] = replace ? ' ' : cj;
                        j++;
                    }
                    return new string(buf);
                }
                i++;
            }
            // No control characters within slice; return original or truncated substring without allocating when possible.
            if (take == len) return s;
            return s.Substring(0, take);
        }

        // Serialization constructor for completeness in enterprise environments.
        protected SilicaException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            var code = info.GetString(nameof(Code));
            if (code == null) throw new SerializationException("Missing Code during deserialization.");
            // Canonicalize first, then validate the canonical value.
            var canonicalCode = ExceptionPolicy.CanonicalizeCode(code);
            ExceptionPolicy.ValidateCode(canonicalCode, nameof(Code));
            Code = canonicalCode;

            int cat = info.GetInt32(nameof(Category));
            var category = (FailureCategory)cat;
            ExceptionPolicy.ValidateCategoryAny(category, nameof(Category));
            Category = category;

            ExceptionId = info.GetInt32(nameof(ExceptionId));
            ExceptionPolicy.ValidateExceptionId(ExceptionId, nameof(ExceptionId));

            var cid = info.GetString(nameof(CorrelationId));
            if (cid == null || !ExceptionPolicy.IsValidGuidN(cid))
                throw new SerializationException("Invalid CorrelationId during deserialization.");
            CorrelationId = ExceptionPolicy.CanonicalizeGuidN(cid);

            long utcTicks = info.GetInt64(nameof(OccurredAt));
            // Guard against invalid ticks to avoid ArgumentOutOfRangeException.
            long min = DateTimeOffset.UnixEpoch.UtcTicks;
            long max = DateTimeOffset.MaxValue.UtcTicks;
            if (utcTicks < min) utcTicks = min;
            else if (utcTicks > max) utcTicks = max;
            OccurredAt = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            base.GetObjectData(info, context);
            info.AddValue(nameof(Code), Code);
            info.AddValue(nameof(Category), (int)Category);
            info.AddValue(nameof(ExceptionId), ExceptionId);
            info.AddValue(nameof(CorrelationId), CorrelationId);
            info.AddValue(nameof(OccurredAt), OccurredAt.UtcTicks);
        }

        public override string ToString()
        {
            // Include core metadata upfront to aid triage even without observers.
            // Keep format simple and log-friendly.
            var declared = DeclaredExceptionTypeName;
            // Prefer the actual runtime type when no declaration is provided.
            var runtimeType = GetType().FullName;
            var typeDisplay = string.IsNullOrEmpty(declared) ? (runtimeType ?? "SilicaException") : declared!;
            // Frontload the message to make triage faster in single-line log viewers.
            var msg = SanitizeSingleLine(Message ?? string.Empty, maxLen: 512);
            var header =
                typeDisplay +
                " [" + Code + "]" +
                " (" + Category.ToString() + ")" +
                " ExceptionId=" + ExceptionId +
                " Message=" + msg +
                " CorrelationId=" + CorrelationId +
                " OccurredAt=" + OccurredAt.ToString("o", CultureInfo.InvariantCulture);
            // Append base ToString for stack and inner exception chain.
            return header + Environment.NewLine + base.ToString();
        }
    }
}
