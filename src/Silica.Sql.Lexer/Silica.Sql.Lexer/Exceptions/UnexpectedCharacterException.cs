using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class UnexpectedCharacterException : SilicaException
    {
        public char Character { get; }
        public int Position { get; }
        public int Line { get; }
        public int Column { get; }

        public UnexpectedCharacterException(char character, int position, int line, int column)
            : base(
                code: LexerExceptions.UnexpectedCharacter.Code,
                message: $"Unexpected character '{character}' at position {position} (Line {line}, Col {column}).",
                category: LexerExceptions.UnexpectedCharacter.Category,
                exceptionId: LexerExceptions.Ids.UnexpectedCharacter)
        {
            LexerExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Character = character;
            Position = position;
            Line = line;
            Column = column;
        }
    }
}
