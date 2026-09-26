using ResourceManager.App.Application.ExternalInvocation;
using ResourceManager.App.Application.PublicServices;
using ResourceManager.App.Domain.ExternalInvocation;
using ResourceManager.App.Domain.PublicServices;

namespace ResourceManager.App.Infrastructure.ExternalInvocation.Modules.LocalServices;

public sealed class LocalServicesExternalInvocationModule : IExternalInvocationModule
{
    public const string ModuleId = "local-services";

    public LocalServicesExternalInvocationModule(ILocalServiceCatalogQueries catalog)
    {
        Operations =
        [
            new ExternalInvocationOperation<ExternalInvocationNoRequest, LocalPublicServiceDescriptor>(
                new ExternalInvocationOperationDescriptor(
                    "local-services.catalog.read",
                    ModuleId,
                    "1.0.0",
                    ExternalInvocationAccessClass.Open,
                    nameof(ILocalServiceCatalogQueries),
                    ExternalInvocationOperationCategory.Query,
                    ExternalInvocationRiskLevel.Low,
                    "Read the current local service capability catalog.",
                    "external-invocation.no-request.v1",
                    "local-public-service.descriptor.v1"),
                async (_, cancellationToken) => await catalog.GetCatalogAsync(cancellationToken)),
            new ExternalInvocationOperation<ExternalInvocationNoRequest, IReadOnlyList<LocalPublicServiceCapability>>(
                new ExternalInvocationOperationDescriptor(
                    "local-services.capabilities.list",
                    ModuleId,
                    "1.0.0",
                    ExternalInvocationAccessClass.Open,
                    nameof(ILocalServiceCatalogQueries),
                    ExternalInvocationOperationCategory.Query,
                    ExternalInvocationRiskLevel.Low,
                    "Read the currently registered local service capabilities.",
                    "external-invocation.no-request.v1",
                    "local-public-service.capabilities.v1"),
                async (_, cancellationToken) => await catalog.ListCapabilitiesAsync(cancellationToken))
        ];
    }

    public ExternalInvocationModuleDescriptor Descriptor { get; } = new(
        ModuleId,
        "1.0.0",
        "High-level local services exposed through the internal invocation pipeline.");

    public ExternalInvocationModuleAvailability Availability
        => ExternalInvocationModuleAvailability.Ready;

    public IReadOnlyList<IExternalInvocationOperation> Operations { get; }
}
