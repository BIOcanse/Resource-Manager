using ResourceManager.App.Application.BrowserRuntimes;
using ResourceManager.App.Infrastructure.BrowserRuntimes;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerBrowserRuntimes(this IServiceCollection services)
    {
        services.AddSingleton<IBrowserRuntimeCatalog, WindowsBrowserRuntimeCatalog>();
        return services;
    }
}
