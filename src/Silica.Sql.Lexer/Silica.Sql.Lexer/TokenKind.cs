namespace Silica.Sql.Lexer
{
    /// <summary>
    /// Enumerates the different categories of tokens recognized by the SQL lexer.
    /// </summary>
    public enum TokenKind
    {
        Unknown = 0,
        Keyword,
        Identifier,
        Number,
        StringLiteral,
        BinaryLiteral,
        Operator,
        Punctuation,
        Comment,
        Whitespace,
        EndOfFile
    }
}
