using System.Collections.ObjectModel;
using Silica.Authentication.Abstractions;

namespace Silica.Authentication.Jwt
{
    internal sealed class JwtAuthenticationResult : IAuthenticationResult
    {
        public bool Succeeded { get; init; }
        public string? Principal { get; init; }
        public IReadOnlyCollection<string> Roles { get; init; } = Array.Empty<string>();
        public string? FailureReason { get; init; }
    }
}
