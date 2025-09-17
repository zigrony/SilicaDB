using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class TokenLimitExceededException : SilicaException
    {
        public int MaxTokens { get; }
        public int EmittedTokens { get; }
        public int Line { get; }
        public int Column { get; }

        public TokenLimitExceededException(int maxTokens, int emittedTokens, int line, int column)
            : base(
                code: LexerExceptions.TokenLimitExceeded.Code,
                message: $"Token limit exceeded: emitted {emittedTokens} tokens, limit {maxTokens} (Line {line}, Col {column}).",
                category: LexerExceptions.TokenLimitExceeded.Category,
                exceptionId: LexerExceptions.Ids.TokenLimitExceeded)
        {
            LexerExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            MaxTokens = maxTokens;
            EmittedTokens = emittedTokens;
            Line = line;
            Column = column;
        }
    }
}
