using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerComponentsAndTasks(this IServiceCollection services)
    {
        services.AddSingleton<IComponentManager, ComponentManager>();
        services.AddSingleton<IProviderRuntimeProbe, ProviderRuntimeProbe>();
        return services;
    }

    private static IServiceCollection AddResourceManagerDependencies(this IServiceCollection services)
    {
        services.AddHttpClient<IOptionalDependencyManager, OptionalDependencyManager>();
        return services;
    }
}
