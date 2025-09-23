namespace Silica.Authentication.Abstractions
{
    /// <summary>
    /// Maps an authenticated principal identifier into roles/claims.
    /// Implementations can consult local stores, directories, or static maps.
    /// </summary>
    public interface IPrincipalMapper
    {
        /// <summary>
        /// Returns roles for the given principal. May return an empty array.
        /// </summary>
        string[] MapRoles(string principal);
    }
}
