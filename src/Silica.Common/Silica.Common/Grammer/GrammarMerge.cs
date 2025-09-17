using System.Collections.Generic;

namespace Silica.Common.Grammar
{
    /// <summary>
    /// Utility to merge two GrammarDefinition instances into one.
    /// </summary>
    public static class GrammarMerge
    {
        public static GrammarDefinition Merge(GrammarDefinition a, GrammarDefinition b)
        {
            if (a == null) return b;
            if (b == null) return a;

            var all = new List<GrammarRule>(a.Rules.Count + b.Rules.Count);
            all.AddRange(a.Rules);
            all.AddRange(b.Rules);
            return new GrammarDefinition(all);
        }
    }
}
