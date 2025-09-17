using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class KeywordConflictException : SilicaException
    {
        public string Keyword { get; }
        public int Position { get; }
        public int Line { get; }
        public int Column { get; }

        public KeywordConflictException(string keyword, int position, int line, int column)
            : base(
                code: LexerExceptions.KeywordConflict.Code,
                message: $"Reserved keyword '{keyword}' used in invalid context at position {position} (Line {line}, Col {column}).",
                category: LexerExceptions.KeywordConflict.Category,
                exceptionId: LexerExceptions.Ids.KeywordConflict)
        {
            LexerExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Keyword = keyword;
            Position = position;
            Line = line;
            Column = column;
        }
    }
}
