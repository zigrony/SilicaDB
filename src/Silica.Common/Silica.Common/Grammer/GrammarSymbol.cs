namespace Silica.Common.Grammar
{
    /// <summary>
    /// Represents a symbol in an EBNF grammar rule.
    /// </summary>
    public sealed class GrammarSymbol
    {
        public string Value { get; }
        public bool IsTerminal { get; }

        public GrammarSymbol(string value, bool isTerminal)
        {
            Value = value ?? string.Empty;
            IsTerminal = isTerminal;
        }

        public override string ToString()
        {
            return IsTerminal ? $"\"{Value}\"" : Value;
        }
    }
}
