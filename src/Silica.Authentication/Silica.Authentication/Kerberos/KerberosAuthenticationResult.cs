using System.Collections.ObjectModel;
using Silica.Authentication.Abstractions;
using System;

namespace Silica.Authentication.Kerberos
{
    internal sealed class KerberosAuthenticationResult : IAuthenticationResult
    {
        public bool Succeeded { get; init; }
        public string? Principal { get; init; }
        public IReadOnlyCollection<string> Roles { get; init; } = Array.Empty<string>();
        public string? FailureReason { get; init; }
        public Guid? SessionId { get; init; }
    }
}
