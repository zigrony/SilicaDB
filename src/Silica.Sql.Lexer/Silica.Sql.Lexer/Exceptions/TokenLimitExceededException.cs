using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class TokenLimitExceededException : SilicaException
    {
        public int MaxTokens { get; }
        public int EmittedTokens { get; }
        public int Line { get; }
        public int Column { get; }

        // EmittedTokens reflects the attempted token count, i.e., the number of tokens
        // that would exist if the next token (or EOF when configured to count) were added.
        // This makes the exception self-descriptive in "would exceed" scenarios.
        public TokenLimitExceededException(int maxTokens, int emittedTokens, int line, int column)
            : base(
                code: LexerExceptions.TokenLimitExceeded.Code,
                message: $"Token limit would be exceeded: attempted {emittedTokens} tokens, limit {maxTokens} (Line {line}, Col {column}).",
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
