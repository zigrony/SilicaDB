using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class UnterminatedCommentException : SilicaException
    {
        public int StartPosition { get; }
        public int Line { get; }
        public int Column { get; }

        public UnterminatedCommentException(int startPosition, int line, int column)
            : base(
                code: LexerExceptions.UnterminatedComment.Code,
                message: $"Unterminated block comment starting at position {startPosition} (Line {line}, Col {column}).",
                category: LexerExceptions.UnterminatedComment.Category,
                exceptionId: LexerExceptions.Ids.UnterminatedComment)
        {
            LexerExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            StartPosition = startPosition;
            Line = line;
            Column = column;
        }
    }
}
