using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Application.SoftwareMetadata;
using ResourceManager.App.Application.SoftwareIssues;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Adaptation.Transport;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.App.Infrastructure.SoftwareIdentity;
using ResourceManager.App.Infrastructure.SoftwareMetadata;
using ResourceManager.App.Infrastructure.SoftwareIssues;

namespace ResourceManager.App.Hosting;

public static partial class ResourceManagerServiceCollectionExtensions
{
    private static IServiceCollection AddResourceManagerAdaptation(this IServiceCollection services)
    {
        services.AddSingleton<IAdapterSoftwareRegistry, JsonAdapterSoftwareRegistry>();
        services.AddSingleton<ITrustedAdapterRegistrationCatalog, EmptyTrustedAdapterRegistrationCatalog>();
        services.AddSingleton<ITrustedAdapterLeaseGrantCompiler, TrustedAdapterLeaseGrantCompiler>();
        services.AddSingleton<IAdapterTransportCallerAttester, WindowsNamedPipeCallerAttester>();
        services.AddSingleton<TrustedAdapterControlPlaneAuthority>();
        services.AddSingleton<ITrustedAdapterLeaseIssuer, TrustedAdapterLeaseIssuer>();
        services.AddHttpClient<IAdapterResourceMarkerProbe, HttpAdapterResourceMarkerProbe>();
        services.AddSingleton<ResourceManagerSelfSchedulingControl>();
        services.AddSingleton<IResourceManagerSelfSchedulingControl>(static provider =>
            provider.GetRequiredService<ResourceManagerSelfSchedulingControl>());
        services.AddHttpClient<IAdapterPolicyDispatcher, HttpAdapterPolicyDispatcher>();
        services.AddSingleton<IControlledSoftwareRegistry, InMemoryControlledSoftwareRegistry>();
        return services;
    }

    private static IServiceCollection AddResourceManagerSoftware(this IServiceCollection services)
    {
        services.AddSingleton<ISoftwareIdentityCatalog, BundledSoftwareIdentityCatalog>();
        services.AddSingleton<ISoftwareMetadataCatalog, BundledSoftwareMetadataCatalog>();
        services.AddSingleton<ISoftwareIssueCatalog, BundledSoftwareIssueCatalog>();
        services.AddSingleton<ISoftwareIssueSource, StaticCatalogSoftwareIssueSource>();
        services.AddSingleton<ISoftwareIssueProjection, SoftwareIssueProjection>();
        services.AddSingleton<ISoftwareRegistryView, SoftwareRegistryView>();
        services.AddSingleton<IManualSoftwareRegistry, JsonManualSoftwareRegistry>();
        services.AddSingleton<ISoftwarePolicyProfileProvider, JsonSoftwarePolicyProfileProvider>();
        services.AddSingleton<IInstalledSoftwareInventory, WindowsInstalledSoftwareInventory>();
        services.AddSingleton<IRuntimeProcessAttributionCatalogProvider, RuntimeProcessAttributionCatalogProvider>();
        services.AddSingleton<ISoftwareOperationManager, SoftwareOperationManager>();
        services.AddResourceManagerSystem();
        return services;
    }
}
