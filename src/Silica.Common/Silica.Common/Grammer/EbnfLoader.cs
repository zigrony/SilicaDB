using System;
using System.Collections.Generic;
using System.IO;

namespace Silica.Common.Grammar
{
    internal static class EbnfParsingHelpers
    {
        internal static string StripCommentsAndTrim(string line, ref bool inBlock)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            int i = 0;
            int len = line.Length;
            bool inSingle = false;
            bool inDouble = false;
            var result = new System.Text.StringBuilder(line.Length);

            while (i < len)
            {
                if (inBlock)
                {
                    // find end of block */
                    int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        // entire remainder is inside comment
                        return result.ToString().Trim();
                    }
                    inBlock = false;
                    i = end + 2;
                    continue;
                }

                char c = line[i];

                // handle entering/exiting quotes
                if (c == '\'' && !inDouble)
                {
                    inSingle = !inSingle;
                    result.Append(c);
                    i++;
                    continue;
                }
                if (c == '"' && !inSingle)
                {
                    inDouble = !inDouble;
                    result.Append(c);
                    i++;
                    continue;
                }

                // block comment start
                if (!inSingle && !inDouble && i + 1 < len && line[i] == '/' && line[i + 1] == '*')
                {
                    inBlock = true;
                    i += 2;
                    continue;
                }

                // line comments (//, --, #) when not inside quotes
                if (!inSingle && !inDouble)
                {
                    // //
                    if (i + 1 < len && line[i] == '/' && line[i + 1] == '/')
                    {
                        break;
                    }
                    // --
                    if (i + 1 < len && line[i] == '-' && line[i + 1] == '-')
                    {
                        break;
                    }
                    // #
                    if (line[i] == '#')
                    {
                        break;
                    }
                }

                result.Append(c);
                i++;
            }

            return result.ToString().Trim();
        }

        internal static bool ContainsRuleDelimiterOutsideQuotes(string text)
        {
            bool inSingle = false, inDouble = false;
            for (int i = 0; i < text.Length - 2; i++)
            {
                char c = text[i];
                if (c == '\'' && !inDouble) inSingle = !inSingle;
                else if (c == '"' && !inSingle) inDouble = !inDouble;
                if (!inSingle && !inDouble)
                {
                    if (text[i] == ':' && text[i + 1] == ':' && text[i + 2] == '=')
                        return true;
                }
            }
            return false;
        }

        internal static void SplitRuleHeader(string ruleLine, out string name, out string rhs)
        {
            // Find ::= outside quotes
            bool inSingle = false, inDouble = false;
            int pos = -1;
            for (int i = 0; i < ruleLine.Length - 2; i++)
            {
                char c = ruleLine[i];
                if (c == '\'' && !inDouble) inSingle = !inSingle;
                else if (c == '"' && !inSingle) inDouble = !inDouble;
                if (!inSingle && !inDouble)
                {
                    if (ruleLine[i] == ':' && ruleLine[i + 1] == ':' && ruleLine[i + 2] == '=')
                    {
                        pos = i;
                        break;
                    }
                }
            }
            if (pos < 0)
                throw new Silica.Common.Grammar.EbnfParseException($"Invalid rule syntax: {ruleLine.Trim()}");

            name = ruleLine.Substring(0, pos).Trim();
            rhs = ruleLine.Substring(pos + 3).Trim();
            if (name.Length == 0)
                throw new Silica.Common.Grammar.EbnfParseException($"Invalid rule syntax: {ruleLine.Trim()}");
            // rhs may be empty: that represents an epsilon (empty) production
        }

        internal static List<string> SplitProductions(string rhs)
        {
            var result = new List<string>();
            bool inSingle = false, inDouble = false;
            var sb = new System.Text.StringBuilder(rhs.Length);
            for (int i = 0; i < rhs.Length; i++)
            {
                char c = rhs[i];
                if (c == '\'' && !inDouble) { inSingle = !inSingle; sb.Append(c); continue; }
                if (c == '"' && !inSingle) { inDouble = !inDouble; sb.Append(c); continue; }
                if (!inSingle && !inDouble && c == '|')
                {
                    var part = sb.ToString().Trim();
                    if (part.Length > 0) result.Add(part);
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            var last = sb.ToString().Trim();
            if (last.Length > 0) result.Add(last);
            return result;
        }

        internal static List<string> TokenizeRhs(string production)
        {
            var tokens = new List<string>();
            bool inSingle = false, inDouble = false;
            var sb = new System.Text.StringBuilder(production.Length);
            for (int i = 0; i < production.Length; i++)
            {
                char c = production[i];
                if (c == '\'' && !inDouble)
                {
                    inSingle = !inSingle;
                    sb.Append(c);
                    continue;
                }
                if (c == '"' && !inSingle)
                {
                    inDouble = !inDouble;
                    sb.Append(c);
                    continue;
                }
                if (!inSingle && !inDouble && char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        internal static bool IsQuotedTerminal(string token)
        {
            if (token.Length >= 2)
            {
                if (token[0] == '"' && token[token.Length - 1] == '"') return true;
                if (token[0] == '\'' && token[token.Length - 1] == '\'') return true;
            }
            return false;
        }

        internal static string Unquote(string token)
        {
            if (token.Length >= 2)
            {
                char first = token[0];
                char last = token[token.Length - 1];
                if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                {
                    return token.Substring(1, token.Length - 2);
                }
            }
            return token;
        }
    }

    /// <summary>
    /// Loads an EBNF grammar file into a GrammarDefinition.
    /// Handles:
    /// - Multi-line rules (accumulates lines until next ::= or EOF)
    /// - Block comments /* ... *&#47; and inline //, --, # outside quotes
    /// - Terminals in "double quotes" and 'single quotes'
    /// - Production splitting on | outside quotes
    /// </summary>
    public static class EbnfLoader
    {
        public static GrammarDefinition LoadFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path must not be null or empty.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("EBNF file not found.", path);

            var rules = new List<GrammarRule>();
            var lines = File.ReadAllLines(path);

            bool inBlockComment = false;
            string currentRule = string.Empty;

            for (int idx = 0; idx < lines.Length; idx++)
            {
                string raw = lines[idx] ?? string.Empty;
                string line = EbnfParsingHelpers.StripCommentsAndTrim(raw, ref inBlockComment);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                bool containsDelim = EbnfParsingHelpers.ContainsRuleDelimiterOutsideQuotes(line);
                if (containsDelim)
                {
                    // flush any pending rule before starting a new header
                    if (!string.IsNullOrWhiteSpace(currentRule))
                    {
                        ParseAndAddRule(currentRule, rules);
                        currentRule = string.Empty;
                    }
                    currentRule = line;
                }
                else
                {
                    // continuation of current rule body
                    if (string.IsNullOrWhiteSpace(currentRule))
                    {
                        // This is a stray non-header line; ignore safely.
                        // The SQL-2016 EBNF has section headings and “Format” blocks.
                        continue;
                    }
                    currentRule += " " + line;
                }
            }

            // flush last rule
            if (!string.IsNullOrWhiteSpace(currentRule))
            {
                ParseAndAddRule(currentRule, rules);
            }

            return new GrammarDefinition(rules);
        }

        private static void ParseAndAddRule(string ruleLine, List<GrammarRule> rules)
        {
            EbnfParsingHelpers.SplitRuleHeader(ruleLine, out var name, out var rhs);

            var productions = new List<GrammarProduction>();
            var prodStrings = EbnfParsingHelpers.SplitProductions(rhs);
            if (prodStrings.Count == 0)
            {
                // empty production (epsilon)
                productions.Add(new GrammarProduction(new List<GrammarSymbol>()));
            }
            else
            {
                for (int i = 0; i < prodStrings.Count; i++)
                {
                    var symbols = new List<GrammarSymbol>();
                    var tokens = EbnfParsingHelpers.TokenizeRhs(prodStrings[i]);
                    for (int j = 0; j < tokens.Count; j++)
                    {
                        string tok = tokens[j];
                        if (tok.Length == 0) continue;
                        if (EbnfParsingHelpers.IsQuotedTerminal(tok))
                        {
                            string unq = EbnfParsingHelpers.Unquote(tok);
                            symbols.Add(new GrammarSymbol(unq, isTerminal: true));
                        }
                        else
                        {
                            symbols.Add(new GrammarSymbol(tok, isTerminal: false));
                        }
                    }
                    productions.Add(new GrammarProduction(symbols));
                }
            }

            rules.Add(new GrammarRule(name, productions));
        }
    }
}
