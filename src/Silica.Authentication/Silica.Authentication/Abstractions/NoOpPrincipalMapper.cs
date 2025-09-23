namespace Silica.Authentication.Abstractions
{
    /// <summary>
    /// Default mapper returning no roles.
    /// </summary>
    public sealed class NoOpPrincipalMapper : IPrincipalMapper
    {
        public string[] MapRoles(string principal)
        {
            return System.Array.Empty<string>();
        }
    }
}
