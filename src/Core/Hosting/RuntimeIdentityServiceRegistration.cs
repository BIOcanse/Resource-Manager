using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Application.BrowserRuntimes;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerRuntimeIdentity(this IServiceCollection services)
    {
        uint kind = 0;
        while (kind < 4)
        {
            var selected = kind++;
            services.AddSingleton<INativeHttpFamilyBinding>(provider => new NativeRuntimeHttpBinding(selected,
                () => new NativeRuntimeHttpSources(selected >= 2 ? provider.GetRequiredService<IBrowserRuntimeCatalog>() : null,
                    selected == 1 ? provider.GetRequiredService<HostManagerRuntimeIdentity>() : null)));
        }
        services.AddSingleton<ResourceManager.App.Infrastructure.ResourceBreakdown.InteractiveDesktopObservationStore>();
        services.AddSingleton<IRuntimePackageIdentityResolver, WindowsPackageIdentityResolver>();
        services.AddSingleton<IRuntimeServiceIdentityResolver, WindowsServiceIdentityResolver>();
        services.AddSingleton<IRuntimeRootIdentityResolver, WindowsRuntimeRootIdentityResolver>();
        services.AddSingleton<IRuntimeSystemProcessClassifier, WindowsSystemProcessClassifier>();
        return services;
    }
}
