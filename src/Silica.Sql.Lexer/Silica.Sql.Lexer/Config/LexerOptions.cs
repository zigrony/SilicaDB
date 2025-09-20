using System.ComponentModel;

namespace Silica.Sql.Lexer.Config
{
    // Dialect hint for presets and diagnostics; behavior is governed by explicit toggles.
    public enum SqlDialect
    {
        IsoSql,
        TSql
    }

    /// <summary>
    /// Explicit runtime options for SqlLexer.
    /// </summary>
    public sealed class LexerOptions
    {
        public SqlDialect Dialect { get; }
        // Identifier/variable surface toggles
        public bool AllowDollarInIdentifiers { get; }
        public bool AllowAtVariables { get; }
        public bool AllowHashTempNames { get; }
        // Double-quoted segments: identifier (ANSI) vs string literal
        public bool TreatDoubleQuotedAsIdentifier { get; }
        public bool IncludeDialectTagInDiagnostics { get; }
        // String literal behavior
        public bool AllowNewlinesInStringLiteral { get; }
        // Trivia emission behavior
        public bool EmitTriviaTokens { get; }
        // Delimited identifier behavior: [name] and "name"
        public bool AllowNewlinesInDelimitedIdentifier { get; }
        // Comment behavior
        public bool EnableNestedBlockComments { get; }
        // Diagnostics verbosity (optional per-instance enable; does not force-disable)
        public bool EnableVerboseDiagnostics { get; }
        // Diagnostics/metrics component name for tag consistency
        public string ComponentName { get; }
        // Identifier start constraints (explicit, versioned; default relaxed for compatibility)
        // When true, bare identifiers must start with a letter or underscore. Dollar is allowed only when AllowDollarInIdentifiers is true.
        public bool RequireLetterStartForIdentifiers { get; }
        // When true, @variables and #temp names must start (after @/## prefix) with a letter or underscore.
        // Dollar is allowed only when AllowDollarInIdentifiers is true.
        public bool RequireLetterStartForVariablesAndTemps { get; }

        // Safety limits
        public int MaxTokens { get; }
        // Explicitly in UTF-8 bytes
        public int MaxInputBytes { get; }
        // EOF behavior
        public bool EmitEndOfFileToken { get; }
        public bool CountEndOfFileTowardsLimit { get; }

        public LexerOptions(
            bool allowNewlinesInStringLiteral,
            bool emitTriviaTokens = false,
            bool allowNewlinesInDelimitedIdentifier = false,
            bool enableNestedBlockComments = false,
            string? componentName = null,
            bool enableVerboseDiagnostics = false,
            int maxTokens = 1_000_000,
            int maxInputBytes = 5_000_000,
            bool requireLetterStartForIdentifiers = false,
            bool requireLetterStartForVariablesAndTemps = false,
            SqlDialect dialect = default,
            // put the new toggles *after* the required ones
            bool allowDollarInIdentifiers = true,
            bool allowAtVariables = true,
            bool allowHashTempNames = true,
            bool treatDoubleQuotedAsIdentifier = true,
            bool includeDialectTagInDiagnostics = true,
            // EOF behavior toggles (explicit, versioned)
            bool emitEndOfFileToken = true,
            bool countEndOfFileTowardsLimit = false)

        {
            AllowDollarInIdentifiers = allowDollarInIdentifiers;
            AllowAtVariables = allowAtVariables;
            AllowHashTempNames = allowHashTempNames;
            TreatDoubleQuotedAsIdentifier = treatDoubleQuotedAsIdentifier;
            IncludeDialectTagInDiagnostics = includeDialectTagInDiagnostics;
            AllowNewlinesInStringLiteral = allowNewlinesInStringLiteral;
            EmitTriviaTokens = emitTriviaTokens;
            AllowNewlinesInDelimitedIdentifier = allowNewlinesInDelimitedIdentifier;
            EnableNestedBlockComments = enableNestedBlockComments;
            EnableVerboseDiagnostics = enableVerboseDiagnostics;
            RequireLetterStartForIdentifiers = requireLetterStartForIdentifiers;
            RequireLetterStartForVariablesAndTemps = requireLetterStartForVariablesAndTemps;
            ComponentName = string.IsNullOrWhiteSpace(componentName)
                ? "Silica.Sql.Lexer"
                : componentName;
            // Normalize safety limits
            if (maxTokens <= 0) maxTokens = 1_000_000;
            if (maxTokens > 10_000_000) maxTokens = 10_000_000;
            if (maxInputBytes <= 0) maxInputBytes = 5_000_000;
            // Cap at 128MB maximum to prevent runaway memory usage
            if (maxInputBytes > 134_217_728) maxInputBytes = 134_217_728;
            MaxTokens = maxTokens;
            MaxInputBytes = maxInputBytes;
            Dialect = dialect;
            EmitEndOfFileToken = emitEndOfFileToken;
            CountEndOfFileTowardsLimit = countEndOfFileTowardsLimit;
        }

        // Presets
        public static readonly LexerOptions IsoSql =
            new LexerOptions(
                requireLetterStartForIdentifiers: false,
                requireLetterStartForVariablesAndTemps: false,
                allowDollarInIdentifiers: false,
                allowAtVariables: false,
                allowHashTempNames: false,
                treatDoubleQuotedAsIdentifier: true, // ANSI behavior
                includeDialectTagInDiagnostics: true,
                allowNewlinesInStringLiteral: false,
                emitTriviaTokens: false,
                allowNewlinesInDelimitedIdentifier: false,
                enableNestedBlockComments: false,
                componentName: "Silica.Sql.Lexer",
                enableVerboseDiagnostics: false,
                maxTokens: 1_000_000,
                maxInputBytes: 5_000_000,
                dialect: SqlDialect.IsoSql);

        public static readonly LexerOptions TSql =
            new LexerOptions(
                requireLetterStartForIdentifiers: false, // keep relaxed for compatibility; can tighten later
                requireLetterStartForVariablesAndTemps: false,
                allowDollarInIdentifiers: true,
                allowAtVariables: true,
                allowHashTempNames: true,
                treatDoubleQuotedAsIdentifier: true, // QUOTED_IDENTIFIER ON semantics by default
                includeDialectTagInDiagnostics: true,
                allowNewlinesInStringLiteral: true,
                emitTriviaTokens: false,
                allowNewlinesInDelimitedIdentifier: false,
                enableNestedBlockComments: false,
                componentName: "Silica.Sql.Lexer",
                enableVerboseDiagnostics: false,
                maxTokens: 1_000_000,
                maxInputBytes: 5_000_000,
                dialect: SqlDialect.TSql);
    }
}
