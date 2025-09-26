using System;
using Silica.Exceptions;

namespace Silica.UI.Exceptions
{
    public sealed class InvalidThemeVariableException : SilicaException
    {
        public string VariableName { get; }

        public InvalidThemeVariableException(string variableName)
            : base(
                code: UIExceptions.InvalidThemeVariable.Code,
                message: $"Invalid or missing theme variable '{variableName}'.",
                category: UIExceptions.InvalidThemeVariable.Category,
                exceptionId: UIExceptions.Ids.InvalidThemeVariable)
        {
            UIExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            VariableName = variableName ?? string.Empty;
        }
    }
}
