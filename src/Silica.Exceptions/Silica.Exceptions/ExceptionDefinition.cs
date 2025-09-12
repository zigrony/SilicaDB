// Filename: Silica.Exceptions/Silica.Exceptions/ExceptionDefinition.cs
using System;

namespace Silica.Exceptions
{
    /// <summary>
    /// Immutable exception definition registered at startup by subsystems.
    /// Associates a stable code with a CLR type name, category, and description.
    /// </summary>
    public sealed record class ExceptionDefinition
    {
        public string Code { get; }
        public string ExceptionTypeName { get; }
        public FailureCategory Category { get; }
        public string Description { get; }

        // Private: enforces construction via validated or trusted factory methods only.
        private ExceptionDefinition(string code, string exceptionTypeName, FailureCategory category, string description)
        {
            Code = code;
            ExceptionTypeName = exceptionTypeName;
            Category = category;
            Description = description;
        }

        public ExceptionDefinition EnsureValid()
        {
            ExceptionPolicy.ValidateCode(Code, nameof(Code));
            ExceptionPolicy.ValidateTypeName(ExceptionTypeName, nameof(ExceptionTypeName));
            ExceptionPolicy.ValidateDescription(Description, nameof(Description));
            ExceptionPolicy.ValidateCategoryForDefinition(Category, nameof(Category));
            return this;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static ExceptionDefinition Create(string code, string exceptionTypeName, FailureCategory category, string description)
        {
            // Validate first, then canonicalize code to ensure consistent storage/snapshots.
            ExceptionPolicy.ValidateCode(code, nameof(code));
            ExceptionPolicy.ValidateTypeName(exceptionTypeName, nameof(exceptionTypeName));
            ExceptionPolicy.ValidateDescription(description, nameof(description));
            ExceptionPolicy.ValidateCategoryForDefinition(category, nameof(category));
            var canonicalCode = ExceptionPolicy.CanonicalizeCode(code);
            return new ExceptionDefinition(canonicalCode, exceptionTypeName, category, description);
        }

        // Internal, trusted construction: caller guarantees prior validation and canonicalization.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static ExceptionDefinition CreateTrusted(string canonicalCode, string exceptionTypeName, FailureCategory category, string description)
        {
            // Defensive minimal guards against null to avoid accidental misuse; avoid revalidation by design.
            if (canonicalCode == null) throw new ArgumentNullException(nameof(canonicalCode));
            if (exceptionTypeName == null) throw new ArgumentNullException(nameof(exceptionTypeName));
            if (description == null) throw new ArgumentNullException(nameof(description));
            return new ExceptionDefinition(canonicalCode, exceptionTypeName, category, description);
        }
    }
}
