using System;

namespace Silica.Exceptions
{
    /// <summary>
    /// Central policy for exception code/type/description validation.
    /// Keep this free of LINQ/reflection and shared across throw/registry paths.
    /// </summary>
    internal static class ExceptionPolicy
    {
        // Keep limits small, deterministic, and index-friendly.
        internal const int MaxCodeLength = 64;
        internal const int MaxDescriptionLength = 1024;
        internal const int MaxTypeNameLength = 256;
        internal const int MaxMessageLength = 4096;

        /// <summary>
        /// Return the canonical form of a validated exception code.
        /// Assumes <paramref name="code"/> has already passed ValidateCode.
        /// Canonicalization is invariant uppercase to avoid drift across systems.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static string CanonicalizeCode(string code)
        {
            // Caller guarantees non-null and validated (ValidateCode rejects null/empty/whitespace).
            // Fast-path: if already uppercase (ASCII), return original to avoid allocation.
            int i = 0;
            while (i < code.Length)
            {
                char c = code[i];
                if (c >= 'a' && c <= 'z')
                {
                    return code.ToUpperInvariant();
                }
                i++;
            }
            return code;
        }

        /// <summary>
        /// Non-throwing validation for exception codes. Mirrors ValidateCode rules.
        /// Returns true when valid; false otherwise.
        /// </summary>
        internal static bool IsValidCodeNonThrowing(string code)
        {
            if (code == null) return false;
            // Reject empty/whitespace
            if (code.Length == 0) return false;
            int wsIdx = 0;
            bool anyNonWhitespace = false;
            while (wsIdx < code.Length)
            {
                if (!char.IsWhiteSpace(code[wsIdx])) { anyNonWhitespace = true; break; }
                wsIdx++;
            }
            if (!anyNonWhitespace) return false;
            if (code.Length > MaxCodeLength) return false;

            int i = 0;
            bool prevWasSep = false;
            while (i < code.Length)
            {
                char c = code[i];
                bool isUpper = (c >= 'A' && c <= 'Z');
                bool isLower = (c >= 'a' && c <= 'z');
                bool isDigit = (c >= '0' && c <= '9');
                bool isSep = (c == '.' || c == '-' || c == '_');
                if (!(isUpper || isLower || isDigit || isSep)) return false;
                if (i == 0 && isSep) return false;
                if (i == code.Length - 1 && isSep) return false;
                if (isSep && prevWasSep) return false;
                prevWasSep = isSep;
                i++;
            }
            return true;
        }


        internal static void ValidateCode(string code, string paramName)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("Exception code must be provided.", paramName);

            if (code.Length > MaxCodeLength)
                throw new ArgumentException("Exception code exceeds maximum length of " + MaxCodeLength.ToString() + ".", paramName);

            // Validate allowed characters and separator placement.
            // - Allowed: A-Z, a-z, 0-9, '.', '-', '_'
            // - First and last characters must be alphanumeric
            // - No consecutive separators
            int i = 0;
            bool prevWasSep = false;
            while (i < code.Length)
            {
                char c = code[i];
                bool isUpper = (c >= 'A' && c <= 'Z');
                bool isLower = (c >= 'a' && c <= 'z');
                bool isDigit = (c >= '0' && c <= '9');
                bool isSep = (c == '.' || c == '-' || c == '_');
                if (!(isUpper || isLower || isDigit || isSep))
                    throw new ArgumentException("Exception code contains invalid characters. Allowed: A-Z, a-z, 0-9, '.', '-', '_'.", paramName);
                if (i == 0 && isSep)
                    throw new ArgumentException("Exception code must not start with '.', '-', or '_'.", paramName);
                if (i == code.Length - 1 && isSep)
                    throw new ArgumentException("Exception code must not end with '.', '-', or '_'.", paramName);
                if (isSep && prevWasSep)
                    throw new ArgumentException("Exception code must not contain consecutive separators.", paramName);
                prevWasSep = isSep;
                i++;
            }
        }

        internal static void ValidateTypeName(string typeName, string paramName)
        {
            if (typeName == null)
                throw new ArgumentNullException(paramName);
            if (typeName.Length == 0)
                throw new ArgumentException("Exception type name must be provided.", paramName);
            if (typeName.Length > MaxTypeNameLength)
                throw new ArgumentException("Exception type name exceeds maximum length of " + MaxTypeNameLength.ToString() + ".", paramName);

            // Forbid any whitespace characters and control characters.
            int i = 0;
            while (i < typeName.Length)
            {
                char c = typeName[i];
                // Whitespace includes spaces, tabs, newlines; char.IsWhiteSpace avoids LINQ and reflection.
                if (char.IsWhiteSpace(c) || c <= 31 || c == 127) // also forbid DEL
                    throw new ArgumentException("Exception type name must not contain whitespace or control characters.", paramName);
                i++;
            }
            // Must be namespace-qualified to avoid bare type names.
            // Allow segments separated by '.' (namespaces/types) and '+' (nested types).
            // Enforce: contains at least one '.' overall; no leading/trailing separators; no consecutive separators ('.' or '+').
            // Each segment must be a valid identifier. Generic arity suffix is allowed ONLY on type segments:
            //  - the segment after the last '.' (root type), and
            //  - any nested type segments after '+'.
            // Generic arity suffix is NOT allowed on namespace segments.
            //   identifier: first char A-Z/a-z/'_', subsequent A-Z/a-z/0-9/'_'
            //   optional generic suffix on type segments: '`' followed by one or more digits, only at end of that segment
            int len = typeName.Length;
            char first = typeName[0];
            char last = typeName[len - 1];
            if (first == '.' || first == '+' || last == '.' || last == '+')
                throw new ArgumentException("Exception type name must not start or end with '.' or '+'.", paramName);

            // Pre-scan to locate the position of the last '.' so we can distinguish namespace vs type segments.
            int lastDotIndex = -1;
            {
                int scan = 0;
                while (scan < len)
                {
                    char c = typeName[scan];
                    if (c == '.') lastDotIndex = scan;
                    scan++;
                }
            }
            if (lastDotIndex < 0)
                throw new ArgumentException("Exception type name must be namespace-qualified and contain at least one '.'.", paramName);

            // We consider "type mode" active once we pass the last '.'; nested '+' segments are also type segments.

            int start = 0;
            int pos = 0;
            while (pos < len)
            {
                char c = typeName[pos];
                bool isSep = (c == '.' || c == '+');
                if (isSep)
                {
                    // segment: [start, pos)
                    if (pos == start)
                        throw new ArgumentException("Exception type name must not contain consecutive separators '.' or '+'.", paramName);
                    // Determine whether the prior segment is a namespace segment or a type segment.
                    bool segmentIsType =
                        // Any segment that starts after the last '.' is a type (root or nested chain).
                        (start > lastDotIndex);
                    // Validate prior segment with appropriate generic-suffix allowance.
                    ValidateTypeNameSegment(typeName, start, pos - start, allowGenericSuffix: segmentIsType, paramName: paramName);
                    start = pos + 1;
                }
                pos++;
            }
            // last segment
            if (start >= len)
                throw new ArgumentException("Exception type name must not contain empty segments.", paramName);
            // Allow optional generic suffix on the last segment only if it is a type segment (i.e., after last '.').
            bool lastSegmentIsType = (start > lastDotIndex);
            ValidateTypeNameSegment(typeName, start, len - start, allowGenericSuffix: lastSegmentIsType, paramName: paramName);
            // lastDotIndex check above ensures at least one '.' was present.
        }

        /// <summary>
        /// Non-throwing validator for CLR-like type names. Mirrors ValidateTypeName semantics.
        /// Returns true when valid; false otherwise.
        /// </summary>
        internal static bool IsValidTypeNameNonThrowing(string typeName)
        {
            if (typeName == null) return false;
            int len = typeName.Length;
            if (len == 0) return false;
            if (len > MaxTypeNameLength) return false;

            // Forbid whitespace and control characters.
            int i = 0;
            while (i < len)
            {
                char c = typeName[i];
                if (char.IsWhiteSpace(c) || c <= 31 || c == 127) return false;
                i++;
            }

            char first = typeName[0];
            char last = typeName[len - 1];
            if (first == '.' || first == '+' || last == '.' || last == '+') return false;

            // Locate last '.' to distinguish namespace vs type segments.
            int lastDotIndex = -1;
            {
                int scan = 0;
                while (scan < len)
                {
                    char c = typeName[scan];
                    if (c == '.') lastDotIndex = scan;
                    scan++;
                }
            }
            if (lastDotIndex < 0) return false;

            int start = 0;
            int pos = 0;
            while (pos < len)
            {
                char c = typeName[pos];
                bool isSep = (c == '.' || c == '+');
                if (isSep)
                {
                    if (pos == start) return false; // consecutive separators
                    bool segmentIsType = (start > lastDotIndex);
                    if (!IsValidTypeNameSegmentNonThrowing(typeName, start, pos - start, allowGenericSuffix: segmentIsType))
                        return false;
                    start = pos + 1;
                }
                pos++;
            }
            if (start >= len) return false; // empty last segment
            bool lastSegmentIsType = (start > lastDotIndex);
            return IsValidTypeNameSegmentNonThrowing(typeName, start, len - start, allowGenericSuffix: lastSegmentIsType);
        }

        // Non-throwing single-segment validator.
        private static bool IsValidTypeNameSegmentNonThrowing(string s, int offset, int length, bool allowGenericSuffix)
        {
            char c0 = s[offset];
            bool isLetter = (c0 >= 'A' && c0 <= 'Z') || (c0 >= 'a' && c0 <= 'z');
            bool isUnderscore = (c0 == '_');
            if (!(isLetter || isUnderscore)) return false;
            int end = offset + length;
            int pos = offset + 1;
            while (pos < end)
            {
                char c = s[pos];
                if (allowGenericSuffix && c == '`')
                {
                    int d = pos + 1;
                    if (d >= end) return false;
                    bool sawDigit = false;
                    while (d < end)
                    {
                        char cd = s[d];
                        if (cd < '0' || cd > '9') return false;
                        sawDigit = true;
                        d++;
                    }
                    if (!sawDigit) return false;
                    return true; // suffix must be terminal
                }
                bool isUpper = (c >= 'A' && c <= 'Z');
                bool isLower = (c >= 'a' && c <= 'z');
                bool isDigit = (c >= '0' && c <= '9');
                bool isUnd = (c == '_');
                if (!(isUpper || isLower || isDigit || isUnd)) return false;
                pos++;
            }
            return true;
        }

        // Validate a single identifier segment of a CLR-like type name without reflection.
        // When allowGenericSuffix is true (type segments), permit a backtick followed by 1+ digits at the end of the segment.
        private static void ValidateTypeNameSegment(string s, int offset, int length, bool allowGenericSuffix, string paramName)
        {
            // First char: A-Z, a-z, or '_'
            char c0 = s[offset];
            bool isLetter = (c0 >= 'A' && c0 <= 'Z') || (c0 >= 'a' && c0 <= 'z');
            bool isUnderscore = (c0 == '_');
            if (!(isLetter || isUnderscore))
                throw new ArgumentException("Each segment of the exception type name must start with a letter or underscore.", paramName);
            int end = offset + length;
            int pos = offset + 1;
            while (pos < end)
            {
                char c = s[pos];
                if (allowGenericSuffix && c == '`')
                {
                    // Backtick must be followed by 1+ digits and be the last characters in the segment.
                    int d = pos + 1;
                    if (d >= end) throw new ArgumentException("Generic arity suffix '`' must be followed by digits.", paramName);
                    bool sawDigit = false;
                    while (d < end)
                    {
                        char cd = s[d];
                        if (cd < '0' || cd > '9') throw new ArgumentException("Generic arity suffix must be digits after '`'.", paramName);
                        sawDigit = true;
                        d++;
                    }
                    if (!sawDigit) throw new ArgumentException("Generic arity suffix must include at least one digit.", paramName);
                    // Valid suffix consumes to end; we're done.
                    return;
                }
                bool isUpper = (c >= 'A' && c <= 'Z');
                bool isLower = (c >= 'a' && c <= 'z');
                bool isDigit = (c >= '0' && c <= '9');
                bool isUnd = (c == '_');
                if (!(isUpper || isLower || isDigit || isUnd))
                    throw new ArgumentException("Exception type name segments may only contain letters, digits, or underscore (with optional '`digits' suffix at the end of a type segment).", paramName);
                pos++;
            }
        }

        internal static void ValidateDescription(string description, string paramName)
        {
            if (description == null)
                throw new ArgumentNullException(paramName, "Use empty string when no description.");
            if (description.Length > MaxDescriptionLength)
                throw new ArgumentException("Description exceeds maximum length of " + MaxDescriptionLength.ToString() + ".", paramName);
            // No control denial here; descriptions may be displayed in docs. Keep them storage-safe elsewhere if needed.
        }

        internal static void ValidateMessage(string message, string paramName)
        {
            if (message == null)
                throw new ArgumentNullException(paramName);
            // Length guard first to fail fast on pathological input.
            if (message.Length > MaxMessageLength)
                throw new ArgumentException("Exception message exceeds maximum length of " + MaxMessageLength.ToString() + ".", paramName);
            // Require non-whitespace content to avoid useless messages in logs/telemetry.
            int i = 0;
            bool anyNonWhitespace = false;
            while (i < message.Length)
            {
                char c = message[i];
                if (!char.IsWhiteSpace(c)) { anyNonWhitespace = true; }
                // Forbid control characters (including CR/LF/TAB, NUL, etc.) to keep messages log/pipe-safe.
                // Allow extended Unicode but reject ASCII control range and DEL.
                if (c <= 31 || c == 127)
                    throw new ArgumentException("Exception message must not contain control characters.", paramName);
                i++;
            }
            if (!anyNonWhitespace)
                throw new ArgumentException("Exception message must contain non-whitespace characters.", paramName);
        }
        /// <summary>
        /// Non-throwing validator for exception messages. Mirrors ValidateMessage semantics.
        /// Returns true when valid; false otherwise.
        /// </summary>
        internal static bool IsValidMessageNonThrowing(string message)
        {
            if (message == null) return false;
            if (message.Length > MaxMessageLength) return false;
            int i = 0;
            bool anyNonWhitespace = false;
            while (i < message.Length)
            {
                char c = message[i];
                if (!char.IsWhiteSpace(c)) { anyNonWhitespace = true; }
                if (c <= 31 || c == 127) return false;
                i++;
            }
            return anyNonWhitespace;
        }

        /// <summary>
        /// Validate that a failure category value is one of the declared enum members.
        /// Use in runtime throw paths where Unknown may be acceptable.
        /// </summary>
        internal static void ValidateCategoryAny(FailureCategory category, string paramName)
        {
            // Avoid reflection or range-coupling. Accept only declared members explicitly.
            switch (category)
            {
                case FailureCategory.Unknown:
                case FailureCategory.Concurrency:
                case FailureCategory.IO:
                case FailureCategory.Validation:
                case FailureCategory.Configuration:
                case FailureCategory.Internal:
                case FailureCategory.Timeout:
                case FailureCategory.Cancelled:
                case FailureCategory.ResourceLimits:
                    return;
                default:
                    throw new ArgumentOutOfRangeException(paramName, "Invalid failure category value.");
            }
        }

        /// <summary>
        /// Validate category for registered definitions. Disallow Unknown to prevent analytic drift.
        /// </summary>
        internal static void ValidateCategoryForDefinition(FailureCategory category, string paramName)
        {
            ValidateCategoryAny(category, paramName);
            if (category == FailureCategory.Unknown)
                throw new ArgumentException("Failure category 'Unknown' is not allowed in exception definitions.", paramName);
        }

        internal static bool IsValidGuidN(string value)
        {
            // Validate exactly 32 hex chars (Guid "N" format).
            if (value == null) return false;
            if (value.Length != 32) return false;
            int i = 0;
            while (i < 32)
            {
                char c = value[i];
                bool hex =
                    (c >= '0' && c <= '9') ||
                    (c >= 'a' && c <= 'f') ||
                    (c >= 'A' && c <= 'F');
                if (!hex) return false;
                i++;
            }
            return true;
        }

        internal static void ValidateCorrelationId(string correlationId, string paramName)
        {
            if (!IsValidGuidN(correlationId))
                throw new ArgumentException("CorrelationId must be a 32-character hex GUID in 'N' format.", paramName);
        }

        /// <summary>
        /// Return canonical "N" format (lowercase) for a validated GUID string.
        /// Assumes input has passed ValidateCorrelationId (length/hex already checked).
        /// Avoids telemetry drift from case variance.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static string CanonicalizeGuidN(string value)
        {
            // Fast-path: if any A-F present, lowercase once; else return original.
            int i = 0;
            while (i < 32)
            {
                char c = value[i];
                if (c >= 'A' && c <= 'F')
                {
                    return value.ToLowerInvariant();
                }
                i++;
            }
            return value;
        }
        /// <summary>
        /// Validates that the given exceptionId is a positive integer and, if applicable,
        /// within the allowed range for the current subsystem.
        /// Throws <see cref="ArgumentOutOfRangeException"/> if invalid.
        /// </summary>
        /// <param name="exceptionId">The numeric exception identifier.</param>
        /// <param name="paramName">The parameter name for exception reporting.</param>
        public static void ValidateExceptionId(int exceptionId, string paramName)
        {
            if (exceptionId <= 0)
            {
                throw new ArgumentOutOfRangeException(paramName, exceptionId,
                    "ExceptionId must be a positive integer.");
            }

            // TODO: If you have subsystem-specific ranges, enforce them here.
            // Example:
            // if (exceptionId < MinIdForSubsystem || exceptionId > MaxIdForSubsystem)
            // {
            //     throw new ArgumentOutOfRangeException(paramName, exceptionId,
            //         $"ExceptionId must be between {MinIdForSubsystem} and {MaxIdForSubsystem} for this subsystem.");
            // }
        }
    }
}
