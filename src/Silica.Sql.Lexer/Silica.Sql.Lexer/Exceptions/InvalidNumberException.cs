using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class InvalidNumberException : SilicaException
    {
        public string Input { get; }
        public int Position { get; }
        public int Line { get; }
        public int Column { get; }

        public InvalidNumberException(string input, int position, int line, int column)
            : base(
                code: LexerExceptions.InvalidNumber.Code,
                message: $"Invalid numeric literal '{input}' at position {position} (Line {line}, Col {column}).",
                category: LexerExceptions.InvalidNumber.Category,
                exceptionId: LexerExceptions.Ids.InvalidNumber)
        {
            LexerExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Input = input;
            Position = position;
            Line = line;
            Column = column;
        }
    }
}
