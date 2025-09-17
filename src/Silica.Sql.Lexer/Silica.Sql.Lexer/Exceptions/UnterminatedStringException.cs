using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class UnterminatedStringException : SilicaException
    {
        public int StartPosition { get; }
        public int Line { get; }
        public int Column { get; }

        public UnterminatedStringException(int startPosition, int line, int column)
            : base(
                code: LexerExceptions.UnterminatedString.Code,
                message: $"Unterminated string literal starting at position {startPosition} (Line {line}, Col {column}).",
                category: LexerExceptions.UnterminatedString.Category,
                exceptionId: LexerExceptions.Ids.UnterminatedString)
        {
            LexerExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            StartPosition = startPosition;
            Line = line;
            Column = column;
        }
    }
}
