using System.Collections.Generic;

namespace Silica.Common.Grammar
{
    /// <summary>
    /// Represents a single production (right-hand side) of a grammar rule.
    /// </summary>
    public sealed class GrammarProduction
    {
        public IReadOnlyList<GrammarSymbol> Symbols { get; }

        public GrammarProduction(IEnumerable<GrammarSymbol> symbols)
        {
            Symbols = new List<GrammarSymbol>(symbols);
        }

        public override string ToString()
        {
            return string.Join(" ", Symbols);
        }
    }
}
