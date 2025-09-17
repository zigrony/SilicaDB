using System;
using System.IO;
using Silica.Common.Grammar;
using System.Collections.Generic;
using System.Diagnostics;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.Sql.Lexer.Metrics;
using Silica.Sql.Lexer.Diagnostics;
using Silica.Exceptions;
using Silica.Sql.Lexer.Config;

namespace Silica.Sql.Lexer.Tests
{
    /// <summary>
    /// Provides an interactive harness for exercising the SQL lexer.
    /// Now also loads the EBNF grammar and prints a summary via GrammarAnalyzer.
    /// </summary>
    public static class LexerTestHarness
    {
        private static bool UseBatchSeparators()
        {
            try
            {
                var v = Environment.GetEnvironmentVariable("SILICA_SQL_SPLIT_GO");
                if (string.IsNullOrWhiteSpace(v)) return false;
                // Any non-empty value enables simple GO-based batch separation in harness.
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void MaybeEnableVerboseDiagnostics()
        {
            try
            {
                var v = Environment.GetEnvironmentVariable("SILICA_SQL_LEXER_VERBOSE");
                if (!string.IsNullOrWhiteSpace(v))
                {
                    // Any non-empty value enables verbose
                    LexerDiagnostics.EnableVerbose = true;
                }
            }
            catch { }
        }
        private static string ResolveBaseGrammarPath()
        {
            var env = Environment.GetEnvironmentVariable("SILICA_SQL_GRAMMAR_BASE");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return @"C:\GitHubRepos\SilicaDB\Datafiles\SQL-2016.ebnf";
        }

        private static string ResolveExtGrammarPath()
        {
            var env = Environment.GetEnvironmentVariable("SILICA_SQL_GRAMMAR_EXT");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return @"C:\GitHubRepos\SilicaDB\Datafiles\SQLServer-Extensions.ebnf";
        }

        public static void Run()
        {
            // Honor environment verbosity toggle for debug-level signals
            MaybeEnableVerboseDiagnostics();
            // Ensure exception definitions are registered for harness sessions.
            try { LexerExceptions.RegisterAll(); } catch { }
            // Harness-scoped diagnostics
            IMetricsManager metrics = null;
            const string HarnessComponent = "SqlLexer.Harness";
            if (DiagnosticsCoreBootstrap.IsStarted)
            {
                try
                {
                    metrics = DiagnosticsCoreBootstrap.Instance.Metrics;
                    LexerMetrics.RegisterAll(metrics, HarnessComponent);
                    LexerDiagnostics.Emit(HarnessComponent, "harness.start", "info", "begin", null,
                        new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase)
                        {
                            { "hostStarted", "true" }
                        });
                }
                catch { /* swallow diagnostics init faults */ }
            }

            Console.WriteLine("Silica.Sql.Lexer Test Harness");
            Console.WriteLine();

            // Load base grammar
            GrammarDefinition baseGrammar = null;
            var basePath = ResolveBaseGrammarPath();
            if (File.Exists(basePath))
            {
                try
                {
                    baseGrammar = EbnfLoader.LoadFromFile(basePath);
                    try
                    {
                        LexerDiagnostics.Emit(HarnessComponent, "grammar.load.base", "info", "loaded", null,
                            new Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase)
                            {
                                { "path", basePath },
                                { "ok", "true" }
                            });
                    }
                    catch { }
                }
                catch (EbnfParseException ex)
                {
                    Console.WriteLine($"BASE GRAMMAR ERROR: {ex.Message}");
                    try
                    {
                        LexerDiagnostics.Emit(HarnessComponent, "grammar.load.base", "error", "parse_error", ex,
                            new Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase)
                            {
                                { "path", basePath },
                                { "ok", "false" }
                            });
                    }
                    catch { }
                }
            }
            else
            {
                Console.WriteLine($"Base grammar file not found: {basePath}");
                try
                {
                    LexerDiagnostics.Emit(HarnessComponent, "grammar.load.base", "info", "not_found", null,
                        new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase)
                        {
                            { "path", basePath }
                        });
                }
                catch { }
            }

            // Load extension grammar (optional)
            GrammarDefinition extGrammar = null;
            var extPath = ResolveExtGrammarPath();
            if (File.Exists(extPath))
            {
                try
                {
                    extGrammar = EbnfLoader.LoadFromFile(extPath);
                    try
                    {
                        LexerDiagnostics.Emit(HarnessComponent, "grammar.load.ext", "info", "loaded", null,
                            new Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase)
                            {
                                { "path", extPath },
                                { "ok", "true" }
                            });
                    }
                    catch { }
                }
                catch (EbnfParseException ex)
                {
                    Console.WriteLine($"EXTENSION GRAMMAR ERROR: {ex.Message}");
                    try
                    {
                        LexerDiagnostics.Emit(HarnessComponent, "grammar.load.ext", "error", "parse_error", ex,
                            new Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase)
                            {
                                { "path", extPath },
                                { "ok", "false" }
                            });
                    }
                    catch { }
                }
            }
            else
            {
                try
                {
                    LexerDiagnostics.Emit(HarnessComponent, "grammar.load.ext", "debug", "not_found", null,
                        new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase)
                        {
                            { "path", extPath }
                        });
                }
                catch { }
            }

            // Merge grammars
            var grammar = GrammarMerge.Merge(baseGrammar, extGrammar);
            if (grammar != null)
            {
                var analyzer = new GrammarAnalyzer();

                // Build known terminals from TokenTables keywords (no LINQ)
                var known = new List<string>();
                var kw = TokenTables.GetKeywords();
                var e = kw.GetEnumerator();
                try
                {
                    while (e.MoveNext())
                    {
                        known.Add(e.Current.Key);
                    }
                }
                finally
                {
                    e.Dispose();
                }

                analyzer.Analyze(grammar, known);
                Console.WriteLine("=== Combined Grammar ===");
                analyzer.PrintSummary();
                Console.WriteLine();

                try
                {
                    LexerDiagnostics.Emit(HarnessComponent, "grammar.merge.analyze", "info", "done", null,
                        new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase)
                        {
                            { "known.terminals", known.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                        });
                }
                catch { }
            }
            // Interactive loop
            // Choose dialect based on whether the SQL Server extension grammar is present.
            LexerOptions options = extGrammar != null ? LexerOptions.TSql : LexerOptions.IsoSql;
            // Ensure consistent component name for both diagnostics and metrics tags; use subcomponent to distinguish harness vs lexer
            // Also propagate per-instance verbose based on the same env var used by the harness (best-effort).
            bool perInstanceVerbose = false;
            try
            {
                var v = Environment.GetEnvironmentVariable("SILICA_SQL_LEXER_VERBOSE");
                perInstanceVerbose = !string.IsNullOrWhiteSpace(v);
            }
            catch { perInstanceVerbose = false; }
            options = new LexerOptions(
                allowNewlinesInStringLiteral: options.AllowNewlinesInStringLiteral,
                emitTriviaTokens: options.EmitTriviaTokens,
                allowNewlinesInDelimitedIdentifier: options.AllowNewlinesInDelimitedIdentifier,
                enableNestedBlockComments: options.EnableNestedBlockComments,
                componentName: "Silica.Sql.Lexer",
                enableVerboseDiagnostics: perInstanceVerbose,
                maxTokens: options.MaxTokens,
                maxInputBytes: options.MaxInputBytes);

            var lexer = new SqlLexer(options);
            Console.WriteLine(extGrammar != null
                ? "Dialect: T-SQL (newlines in string literals allowed)"
                : "Dialect: ISO SQL (newlines in string literals disallowed)");
            Console.WriteLine("Type SQL statements and press Enter. Empty line exits.");
            Console.WriteLine();

            bool batchSplit = UseBatchSeparators();

            while (true)
            {
                Console.Write("> ");
                var input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                    break;

                // Optional harness-only handling of batch separator "GO" on a line by itself.
                if (batchSplit)
                {
                    string trimmed = input.Trim();
                    if (trimmed.Length == 2 &&
                        (trimmed[0] == 'G' || trimmed[0] == 'g') &&
                        (trimmed[1] == 'O' || trimmed[1] == 'o'))
                    {
                        Console.WriteLine("(batch separator)");
                        Console.WriteLine();
                        continue;
                    }
                }

                // Per-input diagnostics timing/metrics (harness-scoped)
                var loopSw = Stopwatch.StartNew();
                int printed = 0;
                int lines = 1;
                int bytes = 0;
                try
                {
                    // cheap line count without allocations
                    for (int i = 0; i < input.Length; i++) if (input[i] == '\n') lines++;
                    try { bytes = System.Text.Encoding.UTF8.GetByteCount(input); } catch { bytes = input.Length; }
                }
                catch { lines = 1; }

                try
                {
                    LexerDiagnostics.Emit(HarnessComponent, "input.begin", "debug", "begin", null,
                        new Dictionary<string, string>(3, StringComparer.OrdinalIgnoreCase)
                        {
                            { "bytes", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            { "lines", lines.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            { "ts", DateTime.UtcNow.ToString("o") }
                        });
                }
                catch { }

                try
                {
                    foreach (var token in lexer.Tokenize(input))
                    {
                        Console.WriteLine(token.ToString());
                        printed++;
                        if (token.Kind == TokenKind.EndOfFile)
                            break;
                    }
                }
                catch (SilicaException ex)
                {
                    Console.WriteLine($"LEXICAL ERROR: {ex.Message}");
                    try
                    {
                        if (metrics != null) LexerMetrics.IncrementErrorCount(metrics);
                        // Extract best-effort line/col via known types (no reflection)
                        string errLine = "0";
                        string errCol = "0";
                        {
                            var uc = ex as Exceptions.UnexpectedCharacterException;
                            if (uc != null)
                            {
                                errLine = uc.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                errCol = uc.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                var us = ex as Exceptions.UnterminatedStringException;
                                if (us != null)
                                {
                                    errLine = us.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    errCol = us.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                }
                                else
                                {
                                    var un = ex as Exceptions.UnterminatedCommentException;
                                    if (un != null)
                                    {
                                        errLine = un.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                        errCol = un.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    }
                                    else
                                    {
                                        var inum = ex as Exceptions.InvalidNumberException;
                                        if (inum != null)
                                        {
                                            errLine = inum.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                            errCol = inum.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                        }
                                        else
                                        {
                                            var unk = ex as Exceptions.UnknownTokenException;
                                            if (unk != null)
                                            {
                                                errLine = unk.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                                errCol = unk.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                            }
                                            else
                                            {
                                                var ie = ex as Exceptions.InvalidEscapeSequenceException;
                                                if (ie != null)
                                                {
                                                    errLine = ie.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                                    errCol = ie.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        LexerDiagnostics.Emit(HarnessComponent, "input.error", "error", "lexical_error", ex,
                            new Dictionary<string, string>(5, StringComparer.OrdinalIgnoreCase)
                            {
                                { "line", errLine },
                                { "col", errCol },
                                { "bytes", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                                { "lines", lines.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                                { "printed", printed.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                            });
                    }
                    catch { }
                }
                finally
                {
                    loopSw.Stop();
                    try
                    {
                        if (metrics != null)
                        {
                            // harness-scoped metrics: tagged with SqlLexer.Harness
                            LexerMetrics.RecordLexingLatency(metrics, loopSw.Elapsed.TotalMilliseconds);
                            LexerMetrics.RecordBytesProcessed(metrics, bytes);
                            LexerMetrics.RecordLinesProcessed(metrics, lines);
                        }
                        LexerDiagnostics.Emit(HarnessComponent, "input.done", "info", "done", null,
                            new Dictionary<string, string>(5, StringComparer.OrdinalIgnoreCase)
                            {
                                { "ms", loopSw.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) },
                                { "printed", printed.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                                { "bytes", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                                { "lines", lines.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                                { "ts", DateTime.UtcNow.ToString("o") }
                            });
                    }
                    catch { }
                }

                Console.WriteLine();
            }

            Console.WriteLine("Exiting harness.");
            try
            {
                LexerDiagnostics.Emit(HarnessComponent, "harness.stop", "info", "end", null,
                    new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase)
                    {
                        { "ts", DateTime.UtcNow.ToString("o") }
                    });
            }
            catch { }
        }
    }
}
