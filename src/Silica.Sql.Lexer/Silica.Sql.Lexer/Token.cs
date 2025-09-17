namespace Silica.Sql.Lexer
{
    /// <summary>
    /// Represents a single lexical token with its type, value, and source position.
    /// </summary>
    public sealed class Token : System.IEquatable<Token>
    {
        public TokenKind Kind { get; }
        public string Value { get; }
        public int Line { get; }
        public int Column { get; }
        /// <summary>
        /// Zero-based absolute character offset from the start of the input.
        /// </summary>
        public int Position { get; }

        public Token(TokenKind kind, string value, int line, int column, int position)
        {
            Kind = kind;
            Value = value ?? string.Empty;
            Line = line;
            Column = column;
            Position = position;
        }

        public override string ToString()
        {
            return $"{Kind} \"{Value}\" (Line {Line}, Col {Column})";
        }

        public bool Equals(Token? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            // Structural equality: kind, value, and precise source coordinates
            return Kind == other.Kind
                && string.Equals(Value, other.Value, System.StringComparison.Ordinal)
                && Line == other.Line
                && Column == other.Column
                && Position == other.Position;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Token);
        }

        public override int GetHashCode()
        {
            // Order and prime multipliers chosen to balance collisions without allocations
            unchecked
            {
                int h = 17;
                h = (h * 31) + (int)Kind;
                h = (h * 31) + (Value != null ? Value.GetHashCode() : 0);
                h = (h * 31) + Line;
                h = (h * 31) + Column;
                h = (h * 31) + Position;
                return h;
            }
        }
    }
}
