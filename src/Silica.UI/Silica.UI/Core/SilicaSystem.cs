using System;
using Silica.UI.Auth;
using Silica.UI.Sessions;
using Silica.UI.FrontEnds;
using Silica.UI.Config;

namespace Silica.UI.Core
{
    /// <summary>
    /// The top-level orchestrator for Silica subsystems.
    /// Owns Authentication, Sessions, and FrontEnd lifecycle.
    /// </summary>
    public class SilicaSystem : IAsyncDisposable
    {
        private readonly AuthManager _auth;
        private readonly SessionManagerAdapter _sessions;
        private readonly FrontEndController _frontEnd;

        public SilicaSystem(SilicaOptions options)
        {
            // Register UI exception catalog once per process before subsystem construction.
            Silica.UI.Exceptions.UIExceptions.RegisterAll();

            _sessions = new SessionManagerAdapter(options.SessionConfig);
            _sessions.Initialize();
            _auth = new AuthManager(options.AuthConfig, _sessions);
            _frontEnd = new FrontEndController(options.FrontEndConfig, _auth, _sessions);
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
            => await _frontEnd.StartAsync(cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _frontEnd.StopAsync(cancellationToken);
            _sessions.Dispose();
        }

        public async Task RestartAsync(CancellationToken cancellationToken = default)
        {
            await StopAsync(cancellationToken);
            await StartAsync(cancellationToken);
        }

        // Return a new, started system. Caller is responsible for swapping references.
        public async Task<SilicaSystem> ReloadAsync(SilicaOptions options, CancellationToken cancellationToken = default)
        {
            await StopAsync(cancellationToken);
            var newSystem = new SilicaSystem(options);
            await newSystem.StartAsync(cancellationToken);
            return newSystem;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}
