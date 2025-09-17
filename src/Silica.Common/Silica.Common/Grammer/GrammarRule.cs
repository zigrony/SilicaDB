using System.Collections.Generic;

namespace Silica.Common.Grammar
{
    /// <summary>
    /// Represents a grammar rule consisting of a name and one or more productions.
    /// </summary>
    public sealed class GrammarRule
    {
        public string Name { get; }
        public IReadOnlyList<GrammarProduction> Productions { get; }

        public GrammarRule(string name, IEnumerable<GrammarProduction> productions)
        {
            Name = name;
            Productions = new List<GrammarProduction>(productions);
        }

        public override string ToString()
        {
            return $"{Name} ::= {string.Join(" | ", Productions)}";
        }
    }
}
