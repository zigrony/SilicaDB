using Microsoft.Extensions.DependencyInjection;
using Silica.UI.Core;
using Silica.UI.Config;

namespace Silica.UI.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddSilicaUI(this IServiceCollection services, SilicaOptions options)
        {
            services.AddSingleton(new SilicaSystem(options));
            return services;
        }
    }
}
