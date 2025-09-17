using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class UnterminatedDelimitedIdentifierException : SilicaException
    {
        public int StartPosition { get; }
        public int Line { get; }
        public int Column { get; }

        public UnterminatedDelimitedIdentifierException(int startPosition, int line, int column)
            : base(
                code: LexerExceptions.UnterminatedDelimitedIdentifier.Code,
                message: $"Unterminated delimited identifier starting at position {startPosition} (Line {line}, Col {column}).",
                category: LexerExceptions.UnterminatedDelimitedIdentifier.Category,
                exceptionId: LexerExceptions.Ids.UnterminatedDelimitedIdentifier)
        {
            LexerExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            StartPosition = startPosition;
            Line = line;
            Column = column;
        }
    }
}
