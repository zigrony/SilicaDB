using System.Collections.Generic;

namespace Silica.Sql.Lexer
{
    /// <summary>
    /// Defines the contract for a SQL lexer implementation.
    /// </summary>
    public interface ILexer
    {
        /// <summary>
        /// Tokenizes the given SQL text into a sequence of tokens.
        /// Throws on lexical errors with typed Silica.Sql.Lexer.Exceptions.* to ensure
        /// auditable and diagnosable failures. Resource-limit protection:
        /// - InputSizeLimitExceededException when UTF-8 bytes exceed configured bounds.
        /// - TokenLimitExceededException when token count exceeds configured bounds.
        /// - Unterminated*Exception for strings, comments, and delimited identifiers.
        /// - UnexpectedCharacterException for otherwise unknown characters.
        /// - InvalidNumberException for malformed numeric/binary literals.
        /// Diagnostics and metrics are emitted under the configured component name.
        /// This base contract does not include cancellation; a cancellable overload
        /// is provided on SqlLexer for callers that hold a concrete instance.
        /// </summary>
        IEnumerable<Token> Tokenize(string sqlText);
    }
}
