using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Threading;
using Silica.DiagnosticsCore; // DiagnosticsCoreBootstrap
using Silica.DiagnosticsCore.Metrics; // IMetricsManager, NoOpMetricsManager
using Silica.Sql.Lexer.Metrics; // LexerMetrics
using Silica.Sql.Lexer.Diagnostics; // LexerDiagnostics
using Silica.Sql.Lexer.Exceptions;
using Silica.Sql.Lexer.Config;
using Silica.Exceptions;

namespace Silica.Sql.Lexer
{
    /// <summary>
    /// A simple SQL lexer that tokenizes input into keywords, identifiers,
    /// operators, punctuation, numbers, and string literals.
    /// </summary>
    public sealed class SqlLexer : ILexer
    {
        // Ensure exception definitions are registered once per AppDomain.
        /// <summary>
        /// Snapshot of the effective options for this instance. Exposed
        /// to improve diagnosability and test clarity. Immutable.
        /// </summary>
        public LexerOptions Options { get; }

        static SqlLexer()
        {
            try { LexerExceptions.RegisterAll(); } catch { }
            // Harness-level verbose diagnostics toggle (does not force-disable per-instance)
            try
            {
                string? v = null;
                try { v = Environment.GetEnvironmentVariable("SILICA_SQL_LEXER_VERBOSE"); } catch { }
                if (!string.IsNullOrEmpty(v))
                {
                    // Accept common truthy values: 1, true, yes, on (case-insensitive, trims)
                    var s = v.Trim();
                    if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "on", StringComparison.OrdinalIgnoreCase))
                    {
                        LexerDiagnostics.EnableVerbose = true;
                    }
                }
            }
            catch { }
        }

        // DiagnosticsCore wiring
        private IMetricsManager _metrics;
        private readonly object _metricsLock = new object();
        private volatile bool _metricsRegistered;
        private readonly string _componentName;
        private readonly bool _verboseEnabled;
        // Policy toggles (kept internal here; wire to config later if desired)
        private readonly bool _allowNewlinesInStringLiteral;
        private readonly bool _emitTriviaTokens;
        private readonly bool _allowNewlinesInDelimitedIdentifier;
        private readonly bool _enableNestedBlockComments;
        private readonly bool _allowDollarInIdentifiers;
        private readonly bool _allowAtVariables;
        private readonly bool _allowHashTempNames;
        private readonly bool _treatDoubleQuotedAsIdentifier;
        private readonly bool _includeDialectTagInDiagnostics;
        private readonly bool _requireLetterStartForIdentifiers;
        private readonly bool _requireLetterStartForVariablesAndTemps;
        // Limits
        private readonly int _maxTokens;
        private readonly int _maxInputBytes;

        // EOF behavior
        private readonly bool _emitEndOfFileToken;
        private readonly bool _countEofTowardsLimit;
        private readonly int _preAddTokenLimit; // loop-time limit based on EOF policy

        public SqlLexer(LexerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            Options = options;
            _allowNewlinesInStringLiteral = options.AllowNewlinesInStringLiteral;
            _emitTriviaTokens = options.EmitTriviaTokens;
            _allowNewlinesInDelimitedIdentifier = options.AllowNewlinesInDelimitedIdentifier;
            _enableNestedBlockComments = options.EnableNestedBlockComments;
            _allowDollarInIdentifiers = options.AllowDollarInIdentifiers;
            _allowAtVariables = options.AllowAtVariables;
            _allowHashTempNames = options.AllowHashTempNames;
            _treatDoubleQuotedAsIdentifier = options.TreatDoubleQuotedAsIdentifier;
            _includeDialectTagInDiagnostics = options.IncludeDialectTagInDiagnostics;
            _requireLetterStartForIdentifiers = options.RequireLetterStartForIdentifiers;
            _requireLetterStartForVariablesAndTemps = options.RequireLetterStartForVariablesAndTemps;
            _componentName = string.IsNullOrWhiteSpace(options.ComponentName) ? "Silica.Sql.Lexer" : options.ComponentName;
            _maxTokens = options.MaxTokens;
            _maxInputBytes = options.MaxInputBytes;
            _emitEndOfFileToken = options.EmitEndOfFileToken;
            _countEofTowardsLimit = options.CountEndOfFileTowardsLimit;
            // If EOF counts, allow up to (_maxTokens - 1) before EOF in the main loop.
            // If EOF does not count, allow up to _maxTokens in the main loop.
            // Guard against underflow if MaxTokens was normalized small.
            if (_countEofTowardsLimit)
            {
                int lim = _maxTokens - 1;
                _preAddTokenLimit = lim > 0 ? lim : 0;
            }
            else
            {
                _preAddTokenLimit = _maxTokens > 0 ? _maxTokens : 0;
            }
            _metrics = DiagnosticsCoreBootstrap.IsStarted
                ? DiagnosticsCoreBootstrap.Instance.Metrics
                : new NoOpMetricsManager();
            // Lazy registration in EnsureMetricsRegistered to tolerate bootstrap races.

            // Optional, explicit per-instance enable (does not force-disable global flag)
            try
            {
                _verboseEnabled = options.EnableVerboseDiagnostics;
            }
            catch { }
        }

        public SqlLexer()
            : this(LexerOptions.TSql) // Default to T‑SQL behavior (newlines allowed in string literals)
        {
        }

        private void EnsureMetricsRegistered()
        {
            if (_metricsRegistered || !DiagnosticsCoreBootstrap.IsStarted) return;
            lock (_metricsLock)
            {
                if (_metricsRegistered) return;
                try
                {
                    var m = DiagnosticsCoreBootstrap.Instance.Metrics;
                    LexerMetrics.RegisterAll(m, _componentName);
                    _metrics = m;
                    _metricsRegistered = true;
                }
                catch
                {
                    // Keep _metrics as is (NoOp or prior), and leave _metricsRegistered = false
                    // so future attempts can retry if bootstrap stabilizes.
                }
            }
        }

        /// <summary>
        /// Reads a single-quoted literal starting at the opening quote position.
        /// Advances pos/line/column and returns the literal contents (with doubled quotes collapsed).
        /// Cancellation is polled cooperatively to avoid starvation on very large literals.
        /// </summary>
        private static string ReadSingleQuotedLiteral(
            string input,
            int length,
            ref int pos,
            ref int line,
            ref int column,
            bool allowNewlines,
            int errorStartPos,
            int errorStartLine,
            int errorStartCol,
            CancellationToken cancellationToken)
        {
            // Preconditions: input[pos] == '\''
            pos++;
            column++;
            var sb = new System.Text.StringBuilder();
            bool closed = false;
            // Poll cancellation every N characters to keep overhead low.
            int cancelBudget = 2048;
            while (pos < length)
            {
                if ((--cancelBudget) == 0)
                {
                    cancelBudget = 2048;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Surface as OperationCanceledException without wrapping to respect caller semantics.
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                char sc = input[pos];
                if (sc == '\'')
                {
                    if (pos + 1 < length && input[pos + 1] == '\'')
                    {
                        sb.Append('\'');
                        pos += 2;
                        column += 2;
                        continue;
                    }
                    pos++;
                    column++;
                    closed = true;
                    break;
                }
                if (sc == '\r')
                {
                    if (!allowNewlines)
                    {
                        throw new UnterminatedStringException(errorStartPos, errorStartLine, errorStartCol);
                    }
                    if (pos + 1 < length && input[pos + 1] == '\n')
                    {
                        // Append CRLF to content
                        sb.Append('\r');
                        sb.Append('\n');
                        pos += 2;
                    }
                    else
                    {
                        // Append CR to content
                        sb.Append('\r');
                        pos += 1;
                    }
                    line++;
                    column = 1;
                    continue;
                }
                if (sc == '\n')
                {
                    if (!allowNewlines)
                    {
                        throw new UnterminatedStringException(errorStartPos, errorStartLine, errorStartCol);
                    }
                    // Append LF to content
                    sb.Append('\n');
                    pos++;
                    line++;
                    column = 1;
                    continue;
                }
                sb.Append(sc);
                pos++;
                column++;
            }
            if (!closed)
            {
                throw new UnterminatedStringException(errorStartPos, errorStartLine, errorStartCol);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Tokenizes the given SQL text into a sequence of tokens.
        /// </summary>
        public IEnumerable<Token> Tokenize(string input)
        {
            // Delegate to the cancellable overload with CancellationToken.None to preserve ILexer contract.
            return Tokenize(input, CancellationToken.None);
        }

        /// <summary>
        /// Tokenizes the given SQL text into a sequence of tokens with cooperative cancellation.
        /// This overload does not alter the ILexer contract; callers can opt into cancellation
        /// when they hold a concrete SqlLexer reference.
        /// </summary>
        public IEnumerable<Token> Tokenize(string input, CancellationToken cancellationToken)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            // Compute UTF-8 byte count once for consistent metrics/limits.
            int byteCount;
            try { byteCount = System.Text.Encoding.UTF8.GetByteCount(input); } catch { byteCount = input.Length; }
            // Early input size guard (bytes)
            if (byteCount > _maxInputBytes)
            {
                var ex = new InputSizeLimitExceededException(_maxInputBytes, byteCount);
                try
                {
                    EnsureMetricsRegistered();
                    LexerMetrics.IncrementErrorCount(_metrics);
                    LexerDiagnostics.Emit(_componentName, "tokenize", "error", "input_size_limit_exceeded", ex,
                        new System.Collections.Generic.Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase)
                        {
                            { "bytes", byteCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            { "limit", _maxInputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                        });
                }
                catch { }
                throw ex;
            }

            EnsureMetricsRegistered();
            // Trace: begin
            try
            {
                if (_verboseEnabled)
                {
                    // Per-call debug emission; does not mutate global/static verbosity.
                    var beginTags = new System.Collections.Generic.Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase);
                    beginTags["bytes"] = byteCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    TryAddDialectTag(beginTags);
                    LexerDiagnostics.EmitDebug(_componentName, "tokenize", "begin", null, beginTags, allowDebug: true);
                }
            }
            catch { }

            var sw = Stopwatch.StartNew();
            // Buffer tokens to avoid yield inside try/catch (CS1626)
            var tokens = new List<Token>(input.Length > 0 ? 32 : 0);

            int pos = 0;
            int length = input.Length;
            int line = 1;
            int column = 1;
            // Poll cancellation on the outer loop at a steady cadence.
            int outerCancelBudget = 2048;
            bool canceled = false;
            // removed unused lastLineStartPos (kept minimal state)
            bool succeeded = false;

            try
            {
                while (pos < length)
                {
                    if ((--outerCancelBudget) == 0)
                    {
                        outerCancelBudget = 2048;
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    char c = input[pos];
                    // Safety: pre-check capacity before appending any token
                    if (tokens.Count >= _preAddTokenLimit)
                    {
                        // Report the attempted token count (current + 1) for audit clarity
                        var tex = new TokenLimitExceededException(_maxTokens, tokens.Count + 1, line, column);
                        throw tex;
                    }

                    // --- Handle newlines ---
                    if (c == '\r' || c == '\n')
                    {
                        if (_emitTriviaTokens)
                        {
                            int wsStartPos = pos; int wsStartCol = column; int wsStartLine = line;
                            // Normalize CRLF as one logical newline token value "\r\n" if present, or single '\r'/'\n'
                            if (c == '\r' && pos + 1 < length && input[pos + 1] == '\n')
                            {
                                pos += 2;
                                // Value includes CRLF
                                var tNL = new Token(TokenKind.Whitespace, "\r\n", wsStartLine, wsStartCol, wsStartPos);
                                tokens.Add(tNL);
                                try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                            }
                            else
                            {
                                pos += 1;
                                var v = c == '\r' ? "\r" : "\n";
                                var tNL = new Token(TokenKind.Whitespace, v, wsStartLine, wsStartCol, wsStartPos);
                                tokens.Add(tNL);
                                try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                            }
                            line++;
                            column = 1;
                        }
                        else
                        {
                            if (c == '\r' && pos + 1 < length && input[pos + 1] == '\n')
                                pos++; // CRLF
                            pos++;
                            line++;
                            column = 1;
                        }
                        continue;
                    }

                    // --- Skip whitespace ---
                    if (char.IsWhiteSpace(c))
                    {
                        if (_emitTriviaTokens)
                        {
                            int wsStartPos = pos;
                            int wsStartCol = column;
                            int wsStartLine = line;
                            // Consume contiguous non-newline whitespace
                            int cancelBudgetWS = 2048;
                            while (pos < length)
                            {
                                char wc = input[pos];
                                if (wc == '\r' || wc == '\n') break;
                                if (!char.IsWhiteSpace(wc)) break;
                                pos++;
                                column++;
                                if ((--cancelBudgetWS) == 0)
                                {
                                    cancelBudgetWS = 2048;
                                    if (cancellationToken.IsCancellationRequested)
                                        cancellationToken.ThrowIfCancellationRequested();
                                }
                            }
                            string ws = input.Substring(wsStartPos, pos - wsStartPos);
                            if (ws.Length > 0)
                            {
                                var tWS = new Token(TokenKind.Whitespace, ws, wsStartLine, wsStartCol, wsStartPos);
                                tokens.Add(tWS);
                                try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                            }
                        }
                        else
                        {
                            // Consume all contiguous non-newline whitespace in one pass (performance)
                            int cancelBudgetWS = 2048;
                            while (pos < length)
                            {
                                char wc = input[pos];
                                if (wc == '\r' || wc == '\n') break;
                                if (!char.IsWhiteSpace(wc)) break;
                                pos++;
                                column++;
                                if ((--cancelBudgetWS) == 0)
                                {
                                    cancelBudgetWS = 2048;
                                    if (cancellationToken.IsCancellationRequested)
                                        cancellationToken.ThrowIfCancellationRequested();
                                }
                            }
                        }
                        continue;
                    }

                    // --- Line comment (--) ---
                    if (c == '-' && pos + 1 < length && input[pos + 1] == '-')
                    {
                        int commentStartPos = pos;
                        int commentStartCol = column;
                        int commentStartLine = line;
                        pos += 2;
                        column += 2;
                        int cancelBudgetLC = 2048;
                        while (pos < length && input[pos] != '\r' && input[pos] != '\n')
                        {
                            pos++;
                            column++;
                            if ((--cancelBudgetLC) == 0)
                            {
                                cancelBudgetLC = 2048;
                                if (cancellationToken.IsCancellationRequested)
                                    cancellationToken.ThrowIfCancellationRequested();
                            }
                        }
                        if (_emitTriviaTokens)
                        {
                            // Emit full line comment including the leading "--"
                            string cm = input.Substring(commentStartPos, pos - commentStartPos);
                            var tC = new Token(TokenKind.Comment, cm, commentStartLine, commentStartCol, commentStartPos);
                            tokens.Add(tC);
                            try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        }
                        continue;
                    }

                    // --- Block comment (/* ... */) ---
                    if (c == '/' && pos + 1 < length && input[pos + 1] == '*')
                    {
                        int commentStartPos = pos;
                        int commentStartCol = column;
                        int commentStartLine = line;
                        pos += 2;
                        column += 2;
                        bool closed = false;
                        int depth = 1;
                        int cancelBudget = 2048;
                        while (pos < length)
                        {
                            if ((--cancelBudget) == 0)
                            {
                                cancelBudget = 2048;
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                }
                            }
                            if (_enableNestedBlockComments && pos + 1 < length && input[pos] == '/' && input[pos + 1] == '*')
                            {
                                depth++;
                                pos += 2;
                                column += 2;
                                continue;
                            }
                            if (pos + 1 < length && input[pos] == '*' && input[pos + 1] == '/')
                            {
                                pos += 2;
                                column += 2;
                                depth--;
                                if (depth == 0)
                                {
                                    closed = true;
                                    break;
                                }
                                continue;
                            }
                            char cc = input[pos];
                            if (cc == '\r')
                            {
                                if (pos + 1 < length && input[pos + 1] == '\n')
                                {
                                    pos += 2;
                                }
                                else
                                {
                                    pos += 1;
                                }
                                line++;
                                column = 1;
                                continue;
                            }
                            if (cc == '\n')
                            {
                                pos++;
                                line++;
                                column = 1;
                                continue;
                            }
                            pos++;
                            column++;
                        }
                        if (!closed)
                        {
                            // Unterminated block comment
                            throw new UnterminatedCommentException(commentStartPos, commentStartLine, commentStartCol);
                        }
                        if (_emitTriviaTokens)
                        {
                            // Emit full block comment including delimiters
                            string cm = input.Substring(commentStartPos, pos - commentStartPos);
                            var tC = new Token(TokenKind.Comment, cm, commentStartLine, commentStartCol, commentStartPos);
                            tokens.Add(tC);
                            try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        }
                        continue;
                    }
                    // --- Bracketed identifier: [name] with escaping via ]]
                    if (c == '[')
                    {
                        int startLine = line;
                        int startCol = column;
                        int startPos = pos;
                        pos++;
                        column++;
                        var sb = new StringBuilder();
                        bool closed = false;
                        while (pos < length)
                        {
                            char bc = input[pos];
                            if (bc == ']')
                            {
                                if (pos + 1 < length && input[pos + 1] == ']')
                                {
                                    // Escaped closing bracket
                                    sb.Append(']');
                                    pos += 2;
                                    column += 2;
                                    continue;
                                }
                                pos++;
                                column++;
                                closed = true;
                                break;
                            }
                            if (bc == '\r')
                            {
                                if (!_allowNewlinesInDelimitedIdentifier)
                                {
                                    throw new UnterminatedDelimitedIdentifierException(startPos, startLine, startCol);
                                }
                                if (pos + 1 < length && input[pos + 1] == '\n')
                                {
                                    // Append CRLF to identifier content
                                    sb.Append('\r');
                                    sb.Append('\n');
                                    pos += 2;
                                }
                                else
                                {
                                    // Append CR to identifier content
                                    sb.Append('\r');
                                    pos += 1;
                                }
                                line++;
                                column = 1;
                                continue;
                            }
                            if (bc == '\n')
                            {
                                if (!_allowNewlinesInDelimitedIdentifier)
                                {
                                    throw new UnterminatedDelimitedIdentifierException(startPos, startLine, startCol);
                                }
                                // Append LF to identifier content
                                sb.Append('\n');
                                pos++;
                                line++;
                                column = 1;
                                continue;
                            }
                            sb.Append(bc);
                            pos++;
                            column++;
                        }
                        if (!closed)
                        {
                            // Unterminated bracketed identifier
                            throw new UnterminatedDelimitedIdentifierException(startPos, startLine, startCol);
                        }
                        var t = new Token(TokenKind.Identifier, sb.ToString(), startLine, startCol, startPos);
                        tokens.Add(t);
                        try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        continue;
                    }
                    // --- Double-quoted segment: identifier or string literal based on options ---
                    if (c == '"')
                    {
                        int startLine = line;
                        int startCol = column;
                        int startPos = pos;
                        if (_treatDoubleQuotedAsIdentifier)
                        {
                            // ANSI delimited identifier: newlines per delimited-identifier policy
                            string ident = ReadDoubleQuotedCore(
                                input, length, ref pos, ref line, ref column,
                                allowNewlines: _allowNewlinesInDelimitedIdentifier,
                                errorStartPos: startPos, errorStartLine: startLine, errorStartCol: startCol,
                                asStringLiteral: false,
                                cancellationToken: cancellationToken);
                            var t = new Token(TokenKind.Identifier, ident, startLine, startCol, startPos);
                            tokens.Add(t);
                            try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        }
                        else
                        {
                            // Treat as ANSI-style string literal using "" escapes; newlines per string literal policy
                            string str = ReadDoubleQuotedCore(
                                input, length, ref pos, ref line, ref column,
                                allowNewlines: _allowNewlinesInStringLiteral,
                                errorStartPos: startPos, errorStartLine: startLine, errorStartCol: startCol,
                                asStringLiteral: true,
                                cancellationToken: cancellationToken);
                            var t = new Token(TokenKind.StringLiteral, str, startLine, startCol, startPos);
                            tokens.Add(t);
                            try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        }
                        continue;
                    }


                    // --- Variables: @name or @@name (T-SQL)
                    if (c == '@' && _allowAtVariables)
                    {
                        int start = pos;
                        int startCol = column;
                        pos++;
                        column++;
                        // Optional second '@'
                        if (pos < length && input[pos] == '@')
                        {
                            pos++;
                            column++;
                        }
                        // Enforce first body char if configured
                        if (_requireLetterStartForVariablesAndTemps)
                        {
                            int bodyStart = pos;
                            if (bodyStart < length)
                            {
                                char firstBody = input[bodyStart];
                                bool okStart = char.IsLetter(firstBody) || firstBody == '_' || (_allowDollarInIdentifiers && firstBody == '$');
                                if (!okStart)
                                {
                                    string sigil = input.Substring(start, pos - start);
                                    var tSig = new Token(TokenKind.Identifier, sigil, line, startCol, start);
                                    tokens.Add(tSig);
                                    try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                                    continue;
                                }
                            }
                        }
                        // Identifier body: letters, digits, underscore
                        int cancelBudgetVar = 2048;
                        while (pos < length)
                        {
                            char vc = input[pos];
                            // Allow $, which is seen in some variable/object naming patterns in the wild
                            if (char.IsLetterOrDigit(vc) || vc == '_' || (_allowDollarInIdentifiers && vc == '$'))
                            {
                                pos++;
                                column++;
                                continue;
                            }
                            // no-op
                            if ((--cancelBudgetVar) == 0)
                            {
                                cancelBudgetVar = 2048;
                                if (cancellationToken.IsCancellationRequested)
                                    cancellationToken.ThrowIfCancellationRequested();
                            }
                            break;
                        }
                        string variable = input.Substring(start, pos - start);
                        var t = new Token(TokenKind.Identifier, variable, line, startCol, start);
                        tokens.Add(t);
                        try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        continue;
                    }

                    // --- Temp object names: #name or ##name (SQL Server temp tables/procs) ---
                    if (c == '#' && _allowHashTempNames)
                    {
                        int start = pos;
                        int startCol = column;
                        pos++;
                        column++;
                        if (pos < length && input[pos] == '#')
                        {
                            pos++;
                            column++;
                        }
                        // Enforce first body char if configured
                        if (_requireLetterStartForVariablesAndTemps)
                        {
                            int bodyStart = pos;
                            if (bodyStart < length)
                            {
                                char firstBody = input[bodyStart];
                                bool okStart = char.IsLetter(firstBody) || firstBody == '_' || (_allowDollarInIdentifiers && firstBody == '$');
                                if (!okStart)
                                {
                                    string sigils = input.Substring(start, pos - start);
                                    var tSig = new Token(TokenKind.Identifier, sigils, line, startCol, start);
                                    tokens.Add(tSig);
                                    try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                                    continue;
                                }
                            }
                        }
                        int cancelBudgetTemp = 2048;
                        while (pos < length)
                        {
                            char tc = input[pos];
                            // Allow $, which often appears in generated temp names
                            if (char.IsLetterOrDigit(tc) || tc == '_' || (_allowDollarInIdentifiers && tc == '$'))
                            {
                                pos++;
                                column++;
                                continue;
                            }
                            // no-op
                            if ((--cancelBudgetTemp) == 0)
                            {
                                cancelBudgetTemp = 2048;
                                if (cancellationToken.IsCancellationRequested)
                                    cancellationToken.ThrowIfCancellationRequested();
                            }
                            break;
                        }
                        string tempName = input.Substring(start, pos - start);
                        var t = new Token(TokenKind.Identifier, tempName, line, startCol, start);
                        tokens.Add(t);
                        try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        continue;
                    }

                    // --- Operators (longest match first) ---
                    bool matchedOp = false;
                    {
                        if (TokenTables.TryGetOperatorsView(c, out var opsView))
                        {
                            // opsView is read-only, ordered longest-first
                            for (int i = 0; i < opsView.Count; i++)
                            {
                                var op = opsView[i];
                                var opLen = op.Length;
                                if (pos + opLen <= length &&
                                    string.Compare(input, pos, op, 0, opLen, StringComparison.Ordinal) == 0)
                                {
                                    var t = new Token(TokenKind.Operator, op, line, column, pos);
                                    tokens.Add(t);
                                    try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                                    pos += opLen;
                                    column += opLen;
                                    matchedOp = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (matchedOp) continue;

                    // --- Unicode-prefixed string literal: N'...' or n'...' ---
                    if ((c == 'N' || c == 'n') && pos + 1 < length && input[pos + 1] == '\'')
                    {
                        int startLine = line;
                        int startCol = column;
                        if (_requireLetterStartForIdentifiers)
                        {
                            bool okStart = char.IsLetter(c) || c == '_' || (_allowDollarInIdentifiers && c == '$');
                            if (!okStart)
                            {
                                // Defensive fallback: emit as single-character punctuation
                                var tP = new Token(TokenKind.Punctuation, c.ToString(), line, column, pos);
                                tokens.Add(tP);
                                try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                                pos++; column++;
                                continue;
                            }
                        }
                        int startPos = pos;
                        // Consume the N/n and then read the quoted literal immediately.
                        pos += 1;
                        column += 1;
                        // Now input[pos] must be the opening quote.
                        if (pos >= length || input[pos] != '\'')
                        {
                            // Defensive: treat as unexpected character if quote vanished.
                            throw new UnexpectedCharacterException(pos < length ? input[pos] : '\0', pos, line, column);
                        }
                        string text = ReadSingleQuotedLiteral(input, length, ref pos, ref line, ref column, _allowNewlinesInStringLiteral, startPos, startLine, startCol, cancellationToken);
                        var t = new Token(TokenKind.StringLiteral, text, startLine, startCol, startPos);
                        tokens.Add(t);
                        try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        continue;
                    }

                    // --- ISO binary string literal: X'...hex...' or x'...hex...' ---
                    if ((c == 'X' || c == 'x') && pos + 1 < length && input[pos + 1] == '\'')
                    {
                        int startLine = line;
                        int startCol = column;
                        int startPos = pos;
                        pos += 2; // consume X and opening quote
                        column += 2;
                        var sbHex = new System.Text.StringBuilder();
                        bool closed = false;
                        int cancelBudget = 2048;
                        while (pos < length)
                        {
                            if ((--cancelBudget) == 0) { cancelBudget = 2048; if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested(); }
                            char hc = input[pos];
                            if (hc == '\'')
                            {
                                pos++;
                                column++;
                                closed = true;
                                break;
                            }
                            if (hc == '\r' || hc == '\n')
                            {
                                // ISO grammar allows spaces between hexits, not newlines
                                throw new UnterminatedStringException(startPos, startLine, startCol);
                            }
                            // allow spaces inside (space/tab etc.), but not control newlines
                            if (hc == ' ' || hc == '\t' || hc == '\f' || hc == '\v')
                            {
                                pos++;
                                column++;
                                continue;
                            }
                            bool isHex =
                                (hc >= '0' && hc <= '9') ||
                                (hc >= 'a' && hc <= 'f') ||
                                (hc >= 'A' && hc <= 'F');
                            if (!isHex)
                            {
                                string invalid = input.Substring(startPos, pos - startPos + 1);
                                throw new InvalidNumberException(invalid, startPos, startLine, startCol);
                            }
                            sbHex.Append(hc);
                            pos++;
                            column++;
                        }
                        if (!closed)
                        {
                            throw new UnterminatedStringException(startPos, startLine, startCol);
                        }
                        // must be pairs of hex digits
                        if ((sbHex.Length & 1) != 0)
                        {
                            string invalid = input.Substring(startPos, pos - startPos);
                            throw new InvalidNumberException(invalid, startPos, startLine, startCol);
                        }
                        // Return binary literal with normalized hex content (no quotes/prefix)
                        var t = new Token(TokenKind.BinaryLiteral, sbHex.ToString(), startLine, startCol, startPos);
                        tokens.Add(t);
                        try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        continue;
                    }

                    // --- String literal (single quotes) ---
                    if (c == '\'')
                    {
                        int startLine = line;
                        int startCol = column;
                        int startPos = pos;
                        string text = ReadSingleQuotedLiteral(input, length, ref pos, ref line, ref column, _allowNewlinesInStringLiteral, startPos, startLine, startCol, cancellationToken);
                        var t = new Token(TokenKind.StringLiteral, text, startLine, startCol, startPos);
                        tokens.Add(t);
                        try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        continue;
                    }

                    // --- Hex literal: 0x... ---
                    if (c == '0' && pos + 1 < length && (input[pos + 1] == 'x' || input[pos + 1] == 'X'))
                    {
                        int start = pos;
                        int startCol = column;
                        // Consume "0x" prefix, then collect hex digits
                        pos += 2;
                        column += 2;
                        var sbHex = new System.Text.StringBuilder();
                        int digits = 0;
                        while (pos < length)
                        {
                            char hc = input[pos];
                            bool isHex =
                                (hc >= '0' && hc <= '9') ||
                                (hc >= 'a' && hc <= 'f') ||
                                (hc >= 'A' && hc <= 'F');
                            if (!isHex) break;
                            sbHex.Append(hc);
                            pos++;
                            column++;
                            digits++;
                        }
                        if (digits == 0)
                        {
                            string invalidHex = input.Substring(start, pos - start);
                            throw new InvalidNumberException(invalidHex, start, line, startCol);
                        }
                        // Enforce even number of hex digits for 0x literals to match SQL Server semantics
                        if ((digits & 1) != 0)
                        {
                            string invalidHex = input.Substring(start, pos - start);
                            throw new InvalidNumberException(invalidHex, start, line, startCol);
                        }
                        // Return binary literal with normalized hex content (no "0x" prefix)
                        var t = new Token(TokenKind.BinaryLiteral, sbHex.ToString(), line, startCol, start);
                        tokens.Add(t);
                        try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        continue;
                    }

                    // --- Number literal (decimal, optional fraction, optional exponent) ---
                    if (char.IsDigit(c) || (c == '.' && pos + 1 < length && char.IsDigit(input[pos + 1])))
                    {
                        int start = pos;
                        int startCol = column;
                        bool hasDot = false;
                        bool hasExp = false;
                        bool expHasDigits = false;
                        int cancelBudgetNum = 2048;
                        // If starting with '.', consume it as part of number
                        if (c == '.')
                        {
                            hasDot = true;
                            pos++;
                            column++;
                        }
                        while (pos < length)
                        {
                            char nc = input[pos];
                            if (char.IsDigit(nc))
                            {
                                pos++;
                                column++;
                                if (hasExp) expHasDigits = true;
                                continue;
                            }
                            if (nc == '.' && !hasDot && !hasExp)
                            {
                                hasDot = true;
                                pos++;
                                column++;
                                continue;
                            }
                            if ((nc == 'e' || nc == 'E') && !hasExp)
                            {
                                hasExp = true;
                                expHasDigits = false;
                                pos++;
                                column++;
                                // optional sign
                                if (pos < length && (input[pos] == '+' || input[pos] == '-'))
                                {
                                    pos++;
                                    column++;
                                }
                                continue;
                            }
                            // no-op
                            if ((--cancelBudgetNum) == 0)
                            {
                                cancelBudgetNum = 2048;
                                if (cancellationToken.IsCancellationRequested)
                                    cancellationToken.ThrowIfCancellationRequested();
                            }

                            break;
                        }
                        // Validate exponent digits if exponent present
                        if (hasExp && !expHasDigits)
                        {
                            string invalid = input.Substring(start, pos - start);
                            throw new InvalidNumberException(invalid, start, line, startCol);
                        }

                        // If the last consumed char is '.' with no digits after and no exponent, step back so '.' becomes punctuation
                        if (!hasExp && pos - 1 >= start && input[pos - 1] == '.')
                        {
                            // Only step back when we have at least one digit before the dot (not ".")
                            // If started with '.', ensure we had digits after it; otherwise rollback entirely
                            if (start < pos - 1)
                            {
                                pos -= 1;
                                column -= 1;
                            }
                            else
                            {
                                // nothing valid consumed; emit '.' as punctuation here
                                var tDot = new Token(TokenKind.Punctuation, ".", line, startCol, start);
                                tokens.Add(tDot);
                                try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                                pos = start + 1;
                                column = startCol + 1;
                                continue;
                            }
                        }
                        if (pos > start)
                        {
                            string num = input.Substring(start, pos - start);
                            var t = new Token(TokenKind.Number, num, line, startCol, start);
                            tokens.Add(t);
                            try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                            continue;
                        }
                    }

                    // --- Punctuation ---
                    if (TokenTables.IsPunctuation(c))
                    {
                        var t = new Token(TokenKind.Punctuation, c.ToString(), line, column, pos);
                        tokens.Add(t);
                        try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        pos++;
                        column++;
                        continue;
                    }


                    // --- Identifier or keyword ---
                    // Allow starting with letter, underscore, or (optionally) dollar-sign
                    if (char.IsLetter(c) || c == '_' || (_allowDollarInIdentifiers && c == '$'))
                    {
                        int start = pos;
                        int startCol = column;
                        pos++;
                        column++;
                        int cancelBudgetId = 2048;
                        while (pos < length)
                        {
                            char ic = input[pos];
                            if (char.IsLetterOrDigit(ic) || ic == '_' || (_allowDollarInIdentifiers && ic == '$'))
                            {
                                pos++;
                                column++;
                                continue;
                            }
                            // no-op
                            if ((--cancelBudgetId) == 0)
                            {
                                cancelBudgetId = 2048;
                                if (cancellationToken.IsCancellationRequested)
                                    cancellationToken.ThrowIfCancellationRequested();
                            }
                            break;
                        }
                        string word = input.Substring(start, pos - start);
                        Token tkn;
                        if (TokenTables.TryGetKeyword(word, out var kind))
                            tkn = new Token(kind, word, line, startCol, start);
                        else
                            tkn = new Token(TokenKind.Identifier, word, line, startCol, start);
                        tokens.Add(tkn);
                        try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                        continue;
                    }

                    // --- Unknown character ---
                    throw new UnexpectedCharacterException(c, pos, line, column);
                }
                // EOF emission based on policy
                if (_emitEndOfFileToken)
                {
                    if (_countEofTowardsLimit)
                    {
                        // If EOF counts toward the limit, enforce total <= _maxTokens
                        if (tokens.Count >= _maxTokens)
                        {
                            // Attempting to add EOF would exceed the limit; report attempted total
                            throw new TokenLimitExceededException(_maxTokens, tokens.Count + 1, line, column);
                        }
                    }
                    var eof = new Token(TokenKind.EndOfFile, string.Empty, line, column, pos);
                    tokens.Add(eof);
                    try { LexerMetrics.IncrementTokenCount(_metrics); } catch { }
                }
                succeeded = true;
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                try
                {
                    // Treat cancellation as a non-error from the lexer's perspective; emit a trace.
                    LexerDiagnostics.Emit(_componentName, "tokenize", "warn", "canceled", null, null);
                }
                catch { }
                throw;
            }
            catch (Silica.Exceptions.SilicaException ex)
            {
                try
                {
                    LexerMetrics.IncrementErrorCount(_metrics);
                    // Extract precise error coordinates when available (no reflection)
                    ExtractErrorCoordinates(ex, out string errLine, out string errCol);
                    var errTags = new System.Collections.Generic.Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase);
                    errTags["line"] = errLine;
                    errTags["col"] = errCol;
                    errTags["bytes"] = byteCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    TryAddDialectTag(errTags);
                    LexerDiagnostics.Emit(_componentName, "tokenize", "error", "lexical_error", ex, errTags);
                }
                catch { }
                throw;
            }
            finally
            {
                sw.Stop();
                try
                {
                    LexerMetrics.RecordLexingLatency(_metrics, sw.Elapsed.TotalMilliseconds);
                    LexerMetrics.RecordBytesProcessed(_metrics, (long)byteCount);
                    LexerMetrics.RecordLinesProcessed(_metrics, line);
                    var tags = new System.Collections.Generic.Dictionary<string, string>(6, StringComparer.OrdinalIgnoreCase);
                    tags["ms"] = sw.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                    tags["lines"] = line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    tags["bytes"] = byteCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    tags["ok"] = succeeded ? "true" : "false";
                    if (succeeded)
                    {
                        tags["tokens"] = tokens.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    if (canceled)
                    {
                        // Explicit signal for downstream correlation.
                        tags["canceled"] = "true";
                    }
                    TryAddDialectTag(tags);
                    LexerDiagnostics.Emit(_componentName, "tokenize", "info", "done", null, tags);
                }
                catch { }
            }

            // Emit buffered tokens after metrics/tracing to avoid yield inside try/catch
            for (int i = 0; i < tokens.Count; i++)
            {
                yield return tokens[i];
            }
        }
        private static void ExtractErrorCoordinates(SilicaException ex, out string line, out string col)
        {
            line = "0";
            col = "0";
            // Emit invariant strings to keep diagnostics stable across locales.
            if (ex is UnexpectedCharacterException uc)
            {
                line = uc.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                col = uc.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            if (ex is UnterminatedStringException us)
            {
                line = us.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                col = us.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            if (ex is UnterminatedCommentException un)
            {
                line = un.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                col = un.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            if (ex is UnterminatedDelimitedIdentifierException udi)
            {
                line = udi.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                col = udi.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            if (ex is InvalidNumberException inum)
            {
                line = inum.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                col = inum.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            if (ex is UnknownTokenException unk)
            {
                line = unk.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                col = unk.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            if (ex is InvalidEscapeSequenceException ie)
            {
                line = ie.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                col = ie.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            if (ex is TokenLimitExceededException tle)
            {
                line = tle.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                col = tle.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            // InputSizeLimitExceededException and other resource errors don't carry coordinates; default "0","0" is fine.
        }
        /// <summary>
        /// Reads a double-quoted segment using ANSI "" escaping. Depending on mode,
        /// it can serve as an identifier (returns inner content) or a string literal (returns content).
        /// Newlines are allowed only when allowNewlines is true.
        /// </summary>
        private static string ReadDoubleQuotedCore(
            string input,
            int length,
            ref int pos,
            ref int line,
            ref int column,
            bool allowNewlines,
            int errorStartPos,
            int errorStartLine,
            int errorStartCol,
            bool asStringLiteral,
            CancellationToken cancellationToken)
        {
            // Preconditions: input[pos] == '"'
            pos++;
            column++;
            var sb = new StringBuilder();
            bool closed = false;
            int cancelBudget = 2048;
            while (pos < length)
            {
                if ((--cancelBudget) == 0)
                {
                    cancelBudget = 2048;
                    if (cancellationToken.IsCancellationRequested)
                        cancellationToken.ThrowIfCancellationRequested();
                }
                char qc = input[pos];
                if (qc == '"')
                {
                    if (pos + 1 < length && input[pos + 1] == '"')
                    {
                        sb.Append('"');
                        pos += 2;
                        column += 2;
                        continue;
                    }
                    pos++;
                    column++;
                    closed = true;
                    break;
                }
                if (qc == '\r')
                {
                    if (!allowNewlines)
                        throw asStringLiteral
                            ? new UnterminatedStringException(errorStartPos, errorStartLine, errorStartCol)
                            : new UnterminatedDelimitedIdentifierException(errorStartPos, errorStartLine, errorStartCol);
                    if (pos + 1 < length && input[pos + 1] == '\n')
                    {
                        sb.Append('\r');
                        sb.Append('\n');
                        pos += 2;
                    }
                    else
                    {
                        sb.Append('\r');
                        pos += 1;
                    }
                    line++;
                    column = 1;
                    continue;
                }
                if (qc == '\n')
                {
                    if (!allowNewlines)
                        throw asStringLiteral
                            ? new UnterminatedStringException(errorStartPos, errorStartLine, errorStartCol)
                            : new UnterminatedDelimitedIdentifierException(errorStartPos, errorStartLine, errorStartCol);
                    sb.Append('\n');
                    pos++;
                    line++;
                    column = 1;
                    continue;
                }
                sb.Append(qc);
                pos++;
                column++;
            }
            if (!closed)
                throw asStringLiteral
                    ? new UnterminatedStringException(errorStartPos, errorStartLine, errorStartCol)
                    : new UnterminatedDelimitedIdentifierException(errorStartPos, errorStartLine, errorStartCol);
            return sb.ToString();
        }

        private void TryAddDialectTag(System.Collections.Generic.IDictionary<string, string> tags)
        {
            if (tags == null) return;
            if (_includeDialectTagInDiagnostics)
            {
                // Stable, low-cardinality value
                string d = Options != null ? Options.Dialect.ToString() : "Unknown";
                // Avoid overwriting if caller already provided one
                if (!tags.ContainsKey("dialect"))
                {
                    tags["dialect"] = d;
                }
            }
        }
    }
}
