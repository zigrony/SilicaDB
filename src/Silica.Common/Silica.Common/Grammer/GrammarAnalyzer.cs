using System;
using System.Collections.Generic;

namespace Silica.Common.Grammar
{
    /// <summary>
    /// Analyzes a GrammarDefinition and produces summary statistics.
    /// </summary>
    public sealed class GrammarAnalyzer
    {
        public int RuleCount { get; private set; }
        public int ProductionCount { get; private set; }
        public int EmptyProductionCount { get; private set; }

        public List<string> Terminals { get; } = new List<string>();
        public List<string> UndefinedNonterminals { get; } = new List<string>();

        public void Analyze(GrammarDefinition grammar)
        {
            Analyze(grammar, null);
        }

        /// <summary>
        /// Analyze with an optional set of extra terminals (e.g., vendor-specific keywords)
        /// that should be treated as terminals even if unquoted in the EBNF.
        /// </summary>
        public void Analyze(GrammarDefinition grammar, IEnumerable<string> extraTerminals)
        {
            if (grammar == null)
                throw new ArgumentNullException(nameof(grammar));

            RuleCount = 0;
            ProductionCount = 0;
            EmptyProductionCount = 0;
            Terminals.Clear();
            UndefinedNonterminals.Clear();

            var defined = new HashSet<string>(StringComparer.Ordinal);
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            var terminals = new HashSet<string>(StringComparer.Ordinal);

            // Build extra terminal lookup (case-insensitive)
            HashSet<string> extra = null;
            if (extraTerminals != null)
            {
                extra = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var iter = extraTerminals.GetEnumerator();
                try
                {
                    while (iter.MoveNext())
                    {
                        var v = iter.Current;
                        if (!string.IsNullOrEmpty(v))
                            extra.Add(v);
                    }
                }
                finally
                {
                    if (iter != null) iter.Dispose();
                }
            }

            // Collect definitions and references
            for (int i = 0; i < grammar.Rules.Count; i++)
            {
                GrammarRule rule = grammar.Rules[i];
                RuleCount++;

                if (!string.IsNullOrEmpty(rule.Name))
                {
                    var normName = NormalizeSymbol(rule.Name);
                    if (!string.IsNullOrEmpty(normName))
                        defined.Add(normName);
                }

                for (int j = 0; j < rule.Productions.Count; j++)
                {
                    GrammarProduction prod = rule.Productions[j];
                    ProductionCount++;

                    if (prod.Symbols.Count == 0)
                    {
                        EmptyProductionCount++;
                        continue;
                    }

                    for (int k = 0; k < prod.Symbols.Count; k++)
                    {
                        GrammarSymbol sym = prod.Symbols[k];
                        if (sym.IsTerminal)
                        {
                            var tv = sym.Value ?? string.Empty;
                            if (!terminals.Contains(tv))
                                terminals.Add(tv);
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(sym.Value))
                            {
                                var norm = NormalizeSymbol(sym.Value);
                                if (string.IsNullOrEmpty(norm))
                                    continue;
                                if (IsStructural(norm))
                                    continue;
                                // If this unquoted symbol is a known keyword (from extra terminals), treat it as a terminal.
                                if (extra != null && extra.Contains(norm))
                                {
                                    if (!terminals.Contains(norm))
                                        terminals.Add(norm);
                                    continue;
                                }
                                referenced.Add(norm);
                            }
                        }
                    }
                }
            }

            // Compute undefined nonterminals
            foreach (string sym in referenced)
            {
                if (!defined.Contains(sym))
                    UndefinedNonterminals.Add(sym);
            }

            // Copy terminals into list
            foreach (string t in terminals)
                Terminals.Add(t);

            Terminals.Sort(StringComparer.Ordinal);
            UndefinedNonterminals.Sort(StringComparer.Ordinal);
        }

        public void PrintSummary()
        {
            Console.WriteLine("=== Grammar Summary ===");
            Console.WriteLine("Rules: " + RuleCount);
            Console.WriteLine("Productions: " + ProductionCount);
            Console.WriteLine("Empty productions: " + EmptyProductionCount);
            Console.WriteLine("Distinct terminals: " + Terminals.Count);
            Console.WriteLine("Undefined nonterminals: " + UndefinedNonterminals.Count);

            if (UndefinedNonterminals.Count > 0)
            {
                Console.WriteLine("First few undefined symbols:");
                int max = UndefinedNonterminals.Count < 10 ? UndefinedNonterminals.Count : 10;
                for (int i = 0; i < max; i++)
                    Console.WriteLine("  " + UndefinedNonterminals[i]);
            }
        }

        // ------------------------
        // Normalization helpers
        // ------------------------
        private static string NormalizeSymbol(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            // Trim spaces
            string x = s.Trim();

            // Remove surrounding parentheses repeatedly: (X) -> X
            // Avoid removing if it's just a single paren.
            while (x.Length >= 2 && x[0] == '(' && x[x.Length - 1] == ')')
            {
                x = x.Substring(1, x.Length - 2).Trim();
            }

            // Strip trailing EBNF quantifiers (non-standard suffix usage like AS?, NAME+, FLAG*)
            while (x.Length > 0)
            {
                char last = x[x.Length - 1];
                if (last == '?' || last == '*' || last == '+')
                {
                    x = x.Substring(0, x.Length - 1).Trim();
                    continue;
                }
                break;
            }

            return x;
        }

        private static bool IsStructural(string s)
        {
            // Skip purely structural/meta artifacts: empty, bare parens, bar, comma, comment markers, etc.
            if (string.IsNullOrEmpty(s)) return true;
            if (s == "(" || s == ")" || s == "|" || s == "," || s == "/*" || s == "*/") return true;
            // Also ignore lone dots of double_period if they leaked unquoted (defensive)
            if (s == "." || s == "..") return true;
            return false;
        }
    }
}
