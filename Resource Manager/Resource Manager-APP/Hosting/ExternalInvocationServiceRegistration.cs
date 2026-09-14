using ResourceManager.App.Application.ExternalInvocation;
using ResourceManager.App.Infrastructure.ExternalInvocation;
using ResourceManager.App.Infrastructure.ExternalInvocation.Audit;
using ResourceManager.App.Infrastructure.ExternalInvocation.Modules.LocalServices;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerExternalInvocation(
        this IServiceCollection services)
    {
        services.AddSingleton<IExternalInvocationModule, LocalServicesExternalInvocationModule>();
        services.AddSingleton<IExternalInvocationRegistry, ExternalInvocationRegistry>();
        services.AddSingleton<ExternalInvocationRuntimePlanProvider>();
        services.AddSingleton<IExternalInvocationRuntimePlanProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<ExternalInvocationRuntimePlanProvider>());
        services.AddSingleton<IExternalInvocationRuntimePlanPublisher>(serviceProvider =>
            serviceProvider.GetRequiredService<ExternalInvocationRuntimePlanProvider>());
        services.AddSingleton<IExternalInvocationPolicy, ExternalInvocationPolicy>();
        services.AddSingleton<IExternalInvocationAuditSink, LoggingExternalInvocationAuditSink>();
        services.AddSingleton<IExternalInvoker, ExternalInvoker>();
        return services;
    }
}
