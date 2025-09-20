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
            // Produce a stable, single-line, invariant representation for diagnostics.
            // Escape control characters that would break logs and cap length for readability.
            string v = Value ?? string.Empty;
            var sb = new System.Text.StringBuilder(v.Length > 96 ? 96 : v.Length + 8);
            int max = 80; // cap display length
            int count = 0;
            for (int i = 0; i < v.Length; i++)
            {
                if (count >= max) { sb.Append("..."); break; }
                char ch = v[i];
                if (ch == '\r') { sb.Append("\\r"); count += 2; continue; }
                if (ch == '\n') { sb.Append("\\n"); count += 2; continue; }
                if (ch == '\t') { sb.Append("\\t"); count += 2; continue; }
                if (ch == '\"') { sb.Append("\\\""); count += 2; continue; }
                if (ch == '\\') { sb.Append("\\\\"); count += 2; continue; }
                // Keep printables; replace other control chars with hex escape
                if (ch < ' ')
                {
                    // \xHH (2 hex digits)
                    int hi = (ch >> 4) & 0xF;
                    int lo = ch & 0xF;
                    sb.Append("\\x");
                    sb.Append((char)(hi < 10 ? ('0' + hi) : ('A' + (hi - 10))));
                    sb.Append((char)(lo < 10 ? ('0' + lo) : ('A' + (lo - 10))));
                    count += 4;
                    continue;
                }
                sb.Append(ch);
                count++;
            }
            string disp = sb.ToString();
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} \"{1}\" (Line {2}, Col {3})",
                Kind, disp, Line, Column);
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
