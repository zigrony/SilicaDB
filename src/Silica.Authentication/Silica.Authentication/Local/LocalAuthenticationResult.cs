using System;
using System.Collections.ObjectModel;
using Silica.Authentication.Abstractions;
using System;

namespace Silica.Authentication.Local
{
    /// <summary>
    /// Concrete result for local authentication implementing IAuthenticationResult.
    /// </summary>
    internal sealed class LocalAuthenticationResult : IAuthenticationResult
    {
        public bool Succeeded { get; init; }
        public string? Principal { get; init; }
        public IReadOnlyCollection<string> Roles { get; init; } = Array.Empty<string>();
        public string? FailureReason { get; init; }
    }
}
