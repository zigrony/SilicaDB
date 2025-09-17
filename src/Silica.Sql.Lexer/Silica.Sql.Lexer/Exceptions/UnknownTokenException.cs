using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class UnknownTokenException : SilicaException
    {
        public string Input { get; }
        public int Position { get; }
        public int Line { get; }
        public int Column { get; }

        public UnknownTokenException(string input, int position, int line, int column)
            : base(
                code: LexerExceptions.UnknownToken.Code,
                message: $"Unknown or unsupported token '{input}' at position {position} (Line {line}, Col {column}).",
                category: LexerExceptions.UnknownToken.Category,
                exceptionId: LexerExceptions.Ids.UnknownToken)
        {
            LexerExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Input = input;
            Position = position;
            Line = line;
            Column = column;
        }
    }
}
