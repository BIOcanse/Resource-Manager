using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Infrastructure.ProcessIdentity;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerRuntimeIdentity(this IServiceCollection services)
    {
        services.AddSingleton<IRuntimePackageIdentityResolver, WindowsPackageIdentityResolver>();
        services.AddSingleton<IRuntimeServiceIdentityResolver, WindowsServiceIdentityResolver>();
        services.AddSingleton<IRuntimeRootIdentityResolver, WindowsRuntimeRootIdentityResolver>();
        services.AddSingleton<IRuntimeSystemProcessClassifier, WindowsSystemProcessClassifier>();
        return services;
    }
}
