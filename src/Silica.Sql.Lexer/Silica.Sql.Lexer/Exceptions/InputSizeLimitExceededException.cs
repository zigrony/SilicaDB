using Silica.Exceptions;

namespace Silica.Sql.Lexer.Exceptions
{
    public sealed class InputSizeLimitExceededException : SilicaException
    {
        public int MaxBytes { get; }
        public int ActualBytes { get; }

        public InputSizeLimitExceededException(int maxBytes, int actualBytes)
            : base(
                code: LexerExceptions.InputSizeLimitExceeded.Code,
                message: $"Input size limit exceeded: {actualBytes} bytes, limit {maxBytes} bytes.",
                category: LexerExceptions.InputSizeLimitExceeded.Category,
                exceptionId: LexerExceptions.Ids.InputSizeLimitExceeded)
        {
            LexerExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            MaxBytes = maxBytes;
            ActualBytes = actualBytes;
        }
    }
}
