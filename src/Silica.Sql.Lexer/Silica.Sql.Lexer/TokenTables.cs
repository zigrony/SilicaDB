using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Silica.Sql.Lexer
{
    /// <summary>
    /// Provides static lookup tables for SQL keywords, operators, and punctuation.
    /// These can be generated from the grammar terminals and cached here.
    /// </summary>
    public static class TokenTables
    {
        // Backing stores (mutable internally; never exposed directly)
        private static readonly Dictionary<string, TokenKind> _keywords;
        private static readonly List<string> _operators;
        private static readonly HashSet<char> _punctuation;
        private static readonly Dictionary<char, List<string>> _operatorsByFirst;

        // Read-only projections (safe to expose)
        private static readonly IReadOnlyDictionary<string, TokenKind> _keywordsRo;
        private static readonly IReadOnlyList<string> _operatorsRo;
        private static readonly ReadOnlyDictionary<char, IReadOnlyList<string>> _operatorsByFirstRo;

        static TokenTables()
        {
            // Initialize keyword dictionary (case-insensitive)
            _keywords = new Dictionary<string, TokenKind>(StringComparer.OrdinalIgnoreCase);

            // Example keywords (expand this list from GrammarAnalyzer.Terminals)
            _keywords["SELECT"] = TokenKind.Keyword;
            _keywords["FROM"] = TokenKind.Keyword;
            _keywords["WHERE"] = TokenKind.Keyword;
            _keywords["INSERT"] = TokenKind.Keyword;
            _keywords["UPDATE"] = TokenKind.Keyword;
            _keywords["DELETE"] = TokenKind.Keyword;

            _keywords["WITH"] = TokenKind.Keyword;
            _keywords["INTO"] = TokenKind.Keyword;
            _keywords["VALUES"] = TokenKind.Keyword;
            _keywords["AS"] = TokenKind.Keyword;
            _keywords["TOP"] = TokenKind.Keyword;

            // Joins and related clauses
            _keywords["JOIN"] = TokenKind.Keyword;
            _keywords["INNER"] = TokenKind.Keyword;
            _keywords["LEFT"] = TokenKind.Keyword;
            _keywords["RIGHT"] = TokenKind.Keyword;
            _keywords["FULL"] = TokenKind.Keyword;
            _keywords["OUTER"] = TokenKind.Keyword;
            _keywords["ON"] = TokenKind.Keyword;
            _keywords["GROUP"] = TokenKind.Keyword;
            _keywords["BY"] = TokenKind.Keyword;
            _keywords["ORDER"] = TokenKind.Keyword;
            _keywords["HAVING"] = TokenKind.Keyword;

            // Core DDL/DML/Control-flow and common surface (T-SQL oriented)
            _keywords["CREATE"] = TokenKind.Keyword;
            _keywords["ALTER"] = TokenKind.Keyword;
            _keywords["DROP"] = TokenKind.Keyword;
            _keywords["TRUNCATE"] = TokenKind.Keyword;
            _keywords["MERGE"] = TokenKind.Keyword;
            _keywords["TABLE"] = TokenKind.Keyword;
            _keywords["VIEW"] = TokenKind.Keyword;
            _keywords["FUNCTION"] = TokenKind.Keyword;
            _keywords["PROCEDURE"] = TokenKind.Keyword;
            _keywords["INDEX"] = TokenKind.Keyword;
            _keywords["SCHEMA"] = TokenKind.Keyword;
            _keywords["DATABASE"] = TokenKind.Keyword;
            _keywords["USE"] = TokenKind.Keyword;

            _keywords["DECLARE"] = TokenKind.Keyword;
            _keywords["SET"] = TokenKind.Keyword;
            _keywords["EXEC"] = TokenKind.Keyword;
            _keywords["EXECUTE"] = TokenKind.Keyword;
            _keywords["BEGIN"] = TokenKind.Keyword;
            _keywords["END"] = TokenKind.Keyword;
            _keywords["IF"] = TokenKind.Keyword;
            _keywords["ELSE"] = TokenKind.Keyword;
            _keywords["WHILE"] = TokenKind.Keyword;
            _keywords["RETURN"] = TokenKind.Keyword;
            _keywords["TRY"] = TokenKind.Keyword;
            _keywords["CATCH"] = TokenKind.Keyword;
            _keywords["THROW"] = TokenKind.Keyword;
            _keywords["RAISERROR"] = TokenKind.Keyword;
            _keywords["OUTPUT"] = TokenKind.Keyword;

            _keywords["CASE"] = TokenKind.Keyword;
            _keywords["WHEN"] = TokenKind.Keyword;
            _keywords["THEN"] = TokenKind.Keyword;
            // ELSE and END already covered above

            _keywords["DISTINCT"] = TokenKind.Keyword;
            _keywords["UNION"] = TokenKind.Keyword;
            _keywords["EXCEPT"] = TokenKind.Keyword;
            _keywords["INTERSECT"] = TokenKind.Keyword;
            _keywords["ALL"] = TokenKind.Keyword;
            _keywords["EXISTS"] = TokenKind.Keyword;
            _keywords["ANY"] = TokenKind.Keyword;
            _keywords["SOME"] = TokenKind.Keyword;
            _keywords["IN"] = TokenKind.Keyword;
            _keywords["LIKE"] = TokenKind.Keyword;
            _keywords["BETWEEN"] = TokenKind.Keyword;
            _keywords["IS"] = TokenKind.Keyword;
            _keywords["NULL"] = TokenKind.Keyword;
            _keywords["NOT"] = TokenKind.Keyword;
            _keywords["AND"] = TokenKind.Keyword;
            _keywords["OR"] = TokenKind.Keyword;
            _keywords["CROSS"] = TokenKind.Keyword;
            _keywords["APPLY"] = TokenKind.Keyword;
            _keywords["OVER"] = TokenKind.Keyword;
            _keywords["PARTITION"] = TokenKind.Keyword;
            _keywords["OFFSET"] = TokenKind.Keyword;
            _keywords["FETCH"] = TokenKind.Keyword;
            _keywords["ROWS"] = TokenKind.Keyword;
            _keywords["ONLY"] = TokenKind.Keyword;
            _keywords["CAST"] = TokenKind.Keyword;
            _keywords["CONVERT"] = TokenKind.Keyword;


            // SQL Server DBCC surface (commands)
            _keywords["DBCC"] = TokenKind.Keyword;
            _keywords["CHECKDB"] = TokenKind.Keyword;
            _keywords["CHECKTABLE"] = TokenKind.Keyword;
            _keywords["CHECKCATALOG"] = TokenKind.Keyword;
            _keywords["CHECKIDENT"] = TokenKind.Keyword;
            _keywords["UPDATEUSAGE"] = TokenKind.Keyword;
            _keywords["PAGE"] = TokenKind.Keyword;
            _keywords["SHOW_STATISTICS"] = TokenKind.Keyword;
            _keywords["INPUTBUFFER"] = TokenKind.Keyword;
            _keywords["OPENTRAN"] = TokenKind.Keyword;
            _keywords["SQLPERF"] = TokenKind.Keyword;
            _keywords["TRACEON"] = TokenKind.Keyword;
            _keywords["TRACEOFF"] = TokenKind.Keyword;

            // Common DBCC WITH options and arguments
            _keywords["NO_INFOMSGS"] = TokenKind.Keyword;
            _keywords["ALL_ERRORMSGS"] = TokenKind.Keyword;
            _keywords["PHYSICAL_ONLY"] = TokenKind.Keyword;
            _keywords["EXTENDED_LOGICAL_CHECKS"] = TokenKind.Keyword;
            _keywords["TABLOCK"] = TokenKind.Keyword;
            _keywords["ESTIMATEONLY"] = TokenKind.Keyword;
            _keywords["DATA_PURITY"] = TokenKind.Keyword;
            _keywords["NORESEED"] = TokenKind.Keyword;
            _keywords["RESEED"] = TokenKind.Keyword;
            _keywords["NOINDEX"] = TokenKind.Keyword;
            _keywords["MAXDOP"] = TokenKind.Keyword;
            _keywords["REPAIR_ALLOW_DATA_LOSS"] = TokenKind.Keyword;
            _keywords["REPAIR_REBUILD"] = TokenKind.Keyword;
            _keywords["REPAIR_FAST"] = TokenKind.Keyword;
            _keywords["LOGSPACE"] = TokenKind.Keyword;
            _keywords["CLEAR"] = TokenKind.Keyword;

            // Batch separator commonly seen in SQL Server tooling
            _keywords["GO"] = TokenKind.Keyword;

            // Operators sorted by descending length for maximal munch
            _operators = new List<string>
            {
                // multi-char first
                "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
                ">=", "<=", "<>", "!=", "||", "->",
                // ISO/EBNF and extensions
                "::",        // double colon
                "..",        // double period
                "=>",        // named argument assignment
                "{-", "-}",  // row pattern braces
                "&&", "==",  // JSON path logical/eq (harmless at SQL layer)
                "??(", "??)",// bracket trigraphs
                // single-char
                "+", "-", "*", "/", "%", "=", ">", "<", "&", "|", "^", "~", "!"
            };

            // Punctuation (single characters)
            _punctuation = new HashSet<char>
            {
                '(', ')', ',', ';', '.', '[', ']',
                // Add standard delimiter characters used across ISO grammar
                '{', '}', ':', '?'
            };
            // Build first-character index for operators (preserve longest-first inside each bucket).
            _operatorsByFirst = new Dictionary<char, List<string>>();
            for (int i = 0; i < _operators.Count; i++)
            {
                string op = _operators[i];
                if (string.IsNullOrEmpty(op)) continue;
                char first = op[0];
                if (!_operatorsByFirst.TryGetValue(first, out var list))
                {
                    list = new List<string>();
                    _operatorsByFirst[first] = list;
                }
                list.Add(op);
            }
            // Ensure each bucket is already longest-first (global list already is); no LINQ sorting here.
            // Build read-only projections
            _keywordsRo = new ReadOnlyDictionary<string, TokenKind>(_keywords);
            _operatorsRo = new ReadOnlyCollection<string>(_operators);
            // Wrap each bucket with a read-only view; the outer dictionary is also read-only.
            var roBuckets = new Dictionary<char, IReadOnlyList<string>>(_operatorsByFirst.Count);
            var e = _operatorsByFirst.GetEnumerator();
            try
            {
                while (e.MoveNext())
                {
                    var kv = e.Current;
                    // Wrap with ReadOnlyCollection to avoid exposing List<T>
                    var copy = new List<string>(kv.Value);
                    roBuckets[kv.Key] = new ReadOnlyCollection<string>(copy);
                }
            }
            finally
            {
                e.Dispose();
            }
            _operatorsByFirstRo = new ReadOnlyDictionary<char, IReadOnlyList<string>>(roBuckets);
        }

        /// <summary>
        /// Exposes the keyword dictionary as read-only.
        /// </summary>
        public static IReadOnlyDictionary<string, TokenKind> GetKeywords() => _keywordsRo;

        /// <summary>
        /// Try to resolve a word as a keyword.
        /// </summary>
        public static bool TryGetKeyword(string word, out TokenKind kind)
        {
            return _keywords.TryGetValue(word, out kind);
        }

        /// <summary>
        /// Operators, ordered longest-first.
        /// </summary>
        public static IReadOnlyList<string> Operators => _operatorsRo;

        /// <summary>
        /// Operator candidates bucketed by first character (values keep longest-first order).
        /// </summary>
        public static IReadOnlyDictionary<char, IReadOnlyList<string>> OperatorsByFirst => _operatorsByFirstRo;

        /// <summary>
        /// Returns a read-only view of operator candidates for the given first character,
        /// ordered longest-first, without copying. Returns false when no bucket exists.
        /// </summary>
        internal static bool TryGetOperatorsView(char first, out IReadOnlyList<string> ops)
        {
            // Return the read-only wrapper from _operatorsByFirstRo to avoid exposing mutable lists.
            if (_operatorsByFirstRo.TryGetValue(first, out var roList))
            {
                ops = roList;
                return true;
            }
            ops = Array.Empty<string>();
            return false;
        }

        /// <summary>
        /// O(1) test for punctuation membership.
        /// </summary>
        public static bool IsPunctuation(char c)
        {
            // HashSet<char>.Contains is O(1); no allocations.
            return _punctuation.Contains(c);
        }

        /// <summary>
        /// Returns a copy of the operator candidates for the given first character,
        /// ordered longest-first. Returns false when no bucket exists.
        /// </summary>
        public static bool TryGetOperatorsFor(char first, out string[] ops)
        {
            if (_operatorsByFirst.TryGetValue(first, out var list))
            {
                var arr = new string[list.Count];
                for (int i = 0; i < list.Count; i++) arr[i] = list[i];
                ops = arr; // return a copy to preserve internal immutability
                return true;
            }
            ops = Array.Empty<string>();
            return false;
        }
    }
}
