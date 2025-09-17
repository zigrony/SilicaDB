using System.Collections.Generic;

namespace Silica.Common.Grammar
{
    /// <summary>
    /// Represents a complete grammar definition loaded from an EBNF file.
    /// </summary>
    public sealed class GrammarDefinition
    {
        public IReadOnlyList<GrammarRule> Rules { get; }

        public GrammarDefinition(IEnumerable<GrammarRule> rules)
        {
            Rules = new List<GrammarRule>(rules);
        }

        public override string ToString()
        {
            return string.Join("\n", Rules);
        }
    }
}
