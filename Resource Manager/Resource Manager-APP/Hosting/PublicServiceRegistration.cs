using ResourceManager.App.Application.PublicServices;
using ResourceManager.App.Infrastructure.PublicServices;
using ResourceManager.App.Application.PublicServices.Sqlite;
using ResourceManager.App.Infrastructure.PublicServices.Sqlite;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Infrastructure.PublicServices.AiModels;
using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Infrastructure.PublicServices.AiGateway;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerPublicServices(
        this IServiceCollection services,
        StartupCapabilitySet startupCapabilities)
    {
        if (!startupCapabilities.Allows(
                StartupCapability.PublicServiceCoordination))
        {
            services.AddSingleton<
                ILocalPublicServiceAccessPolicy,
                StartupDisabledLocalPublicServiceAccessPolicy>();
            return services;
        }

        services.AddSingleton<IPublicSqliteDatabaseService, PublicSqliteDatabaseService>();
        services.AddSingleton<IAiGatewayCredentialStore, SqliteAiGatewayCredentialStore>();
        services.AddSingleton<IAiGatewayCredentialService, AiGatewayCredentialService>();
        services.AddHttpClient("lm-studio", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IAiModelRuntimeProvider>(serviceProvider =>
            new LmStudioAiModelService(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("lm-studio"),
                serviceProvider.GetRequiredService<ResourceManager.App.Application.Settings.IAppSettingsStore>(),
                serviceProvider.GetRequiredService<ILogger<LmStudioAiModelService>>()));
        services.AddSingleton<ILocalServiceCatalogQueries>(static provider =>
            provider.GetRequiredService<HostManagerPublicServiceCoordinatorOwner>());
        services.AddSingleton<ILocalPublicServiceAccessPolicy>(static provider =>
            provider.GetRequiredService<HostManagerPublicServiceCoordinatorOwner>());
        services.AddSingleton<ILocalAiModelService>(static provider =>
            provider.GetRequiredService<HostManagerPublicServiceCoordinatorOwner>());
        services.AddSingleton<IAiGatewayLocalModelPolicy>(static provider =>
            provider.GetRequiredService<HostManagerPublicServiceCoordinatorOwner>());
        return services;
    }
}
