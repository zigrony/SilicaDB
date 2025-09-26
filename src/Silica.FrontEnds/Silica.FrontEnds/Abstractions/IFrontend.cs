// File: Silica.FrontEnds/Abstractions/IFrontend.cs
using Microsoft.AspNetCore.Builder;

namespace Silica.FrontEnds.Abstractions
{
    /// <summary>
    /// Contract for a frontend that participates in hosting SilicaDB.
    /// Two-phase configuration:
    ///  - ConfigureBuilder: transport/protocol/ports on the builder (no blocking).
    ///  - ConfigureApp: endpoint mappings and lifetime hooks (no blocking).
    /// </summary>
    public interface IFrontend
    {
        /// <summary>
        /// Short identifier for the frontend (e.g., "Kestrel").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Configure the WebApplicationBuilder (transport/protocols/ports). Called once.
        /// Must be idempotent and non-blocking.
        /// </summary>
        void ConfigureBuilder(WebApplicationBuilder builder);

        /// <summary>
        /// Configure the built WebApplication (routes and lifetime hooks). Called once.
        /// Must be idempotent and non-blocking.
        /// </summary>
        void ConfigureApp(WebApplication app);
    }

    /// <summary>
    /// Optional lifecycle contract: frontends may declare ownership of the host run.
    /// </summary>
    public interface IFrontendLifecycle
    {
        /// <summary>
        /// When true, the registry should run the built app (blocking).
        /// </summary>
        bool OwnsHostLifecycle { get; }
    }
}
