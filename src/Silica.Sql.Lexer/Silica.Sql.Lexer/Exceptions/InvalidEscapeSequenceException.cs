using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class InvalidEscapeSequenceException : SilicaException
    {
        public string Sequence { get; }
        public int Position { get; }
        public int Line { get; }
        public int Column { get; }

        public InvalidEscapeSequenceException(string sequence, int position, int line, int column)
            : base(
                code: LexerExceptions.InvalidEscapeSequence.Code,
                message: $"Invalid escape sequence '{sequence}' at position {position} (Line {line}, Col {column}).",
                category: LexerExceptions.InvalidEscapeSequence.Category,
                exceptionId: LexerExceptions.Ids.InvalidEscapeSequence)
        {
            LexerExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Sequence = sequence;
            Position = position;
            Line = line;
            Column = column;
        }
    }
}
