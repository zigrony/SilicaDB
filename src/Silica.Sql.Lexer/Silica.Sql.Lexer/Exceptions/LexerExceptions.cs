using System;
using Silica.Exceptions;

namespace Silica.Sql.Lexer
{
    public static class LexerExceptions
    {
        public static class Ids
        {
            public const int UnexpectedCharacter = 2100;
            public const int UnterminatedString = 2101;
            public const int InvalidNumber = 2102;
            public const int InvalidEscapeSequence = 2103;
            public const int UnknownToken = 2104;
            public const int UnterminatedComment = 2105;
            public const int KeywordConflict = 2106;
            public const int UnterminatedDelimitedIdentifier = 2107;
            public const int TokenLimitExceeded = 2108;
            public const int InputSizeLimitExceeded = 2109;
        }

        public const int MinId = 2100;
        public const int MaxId = 2199;

        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(
                    paramName,
                    id,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.Sql.Lexer.");
            }
        }

        public static readonly ExceptionDefinition UnexpectedCharacter =
            ExceptionDefinition.Create(
                code: "LEXER.UNEXPECTED_CHARACTER",
                exceptionTypeName: "Silica.Sql.Lexer.Exceptions.UnexpectedCharacterException",
                category: FailureCategory.Validation,
                description: "Unexpected character in input stream."
            );

        public static readonly ExceptionDefinition UnterminatedString =
            ExceptionDefinition.Create(
                code: "LEXER.UNTERMINATED_STRING",
                exceptionTypeName: "Silica.Sql.Lexer.Exceptions.UnterminatedStringException",
                category: FailureCategory.Validation,
                description: "Unterminated string literal."
            );

        public static readonly ExceptionDefinition InvalidNumber =
            ExceptionDefinition.Create(
                code: "LEXER.INVALID_NUMBER",
                exceptionTypeName: "Silica.Sql.Lexer.Exceptions.InvalidNumberException",
                category: FailureCategory.Validation,
                description: "Invalid numeric literal."
            );

        public static readonly ExceptionDefinition InvalidEscapeSequence =
            ExceptionDefinition.Create(
                code: "LEXER.INVALID_ESCAPE_SEQUENCE",
                exceptionTypeName: "Silica.Sql.Lexer.Exceptions.InvalidEscapeSequenceException",
                category: FailureCategory.Validation,
                description: "Invalid escape sequence in literal."
            );

        public static readonly ExceptionDefinition UnknownToken =
            ExceptionDefinition.Create(
                code: "LEXER.UNKNOWN_TOKEN",
                exceptionTypeName: "Silica.Sql.Lexer.Exceptions.UnknownTokenException",
                category: FailureCategory.Validation,
                description: "Unknown or unsupported token."
            );

        public static readonly ExceptionDefinition UnterminatedComment =
            ExceptionDefinition.Create(
                code: "LEXER.UNTERMINATED_COMMENT",
                exceptionTypeName: "Silica.Sql.Lexer.Exceptions.UnterminatedCommentException",
                category: FailureCategory.Validation,
                description: "Unterminated block comment."
            );

        public static readonly ExceptionDefinition KeywordConflict =
            ExceptionDefinition.Create(
                code: "LEXER.KEYWORD_CONFLICT",
                exceptionTypeName: "Silica.Sql.Lexer.Exceptions.KeywordConflictException",
                category: FailureCategory.Validation,
                description: "Reserved keyword used in invalid context."
            );


        public static readonly ExceptionDefinition UnterminatedDelimitedIdentifier =
            ExceptionDefinition.Create(
                code: "LEXER.UNTERMINATED_DELIMITED_IDENTIFIER",
                exceptionTypeName: "Silica.Sql.Lexer.Exceptions.UnterminatedDelimitedIdentifierException",
                category: FailureCategory.Validation,
                description: "Unterminated bracketed or quoted identifier."
            );

        public static readonly ExceptionDefinition TokenLimitExceeded =
            ExceptionDefinition.Create(
                code: "LEXER.TOKEN_LIMIT_EXCEEDED",
                exceptionTypeName: "Silica.Sql.Lexer.Exceptions.TokenLimitExceededException",
                category: FailureCategory.ResourceLimits,
                description: "The lexer emitted more tokens than allowed by configuration."
            );

        public static readonly ExceptionDefinition InputSizeLimitExceeded =
            ExceptionDefinition.Create(
                code: "LEXER.INPUT_SIZE_LIMIT_EXCEEDED",
                exceptionTypeName: "Silica.Sql.Lexer.Exceptions.InputSizeLimitExceededException",
                category: FailureCategory.ResourceLimits,
                description: "The input size exceeded the configured maximum."
            );

        /// <summary>
        /// Registers all Lexer exception definitions with the global registry.
        /// Call this once during subsystem initialization.
        /// </summary>
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                UnexpectedCharacter,
                UnterminatedString,
                InvalidNumber,
                InvalidEscapeSequence,
                UnknownToken,
                UnterminatedComment,
                KeywordConflict,
                UnterminatedDelimitedIdentifier,
                TokenLimitExceeded,
                InputSizeLimitExceeded
            });
        }
    }
}
