using System;

namespace Silica.Common.Grammar
{
    /// <summary>
    /// Represents an error encountered while parsing an EBNF grammar file.
    /// </summary>
    public sealed class EbnfParseException : Exception
    {
        public EbnfParseException(string message) : base(message) { }
    }
}
