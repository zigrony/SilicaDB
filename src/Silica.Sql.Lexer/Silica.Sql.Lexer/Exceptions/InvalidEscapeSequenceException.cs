using Silica.Exceptions;
using System.ComponentModel;

namespace Silica.Sql.Lexer.Exceptions
{
    /// <summary>
    /// Reserved for future grammar surfaces (kept for contract stability).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
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
