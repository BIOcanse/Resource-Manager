using System.Diagnostics.CodeAnalysis;
using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Application.ExternalInvocation;

public sealed record ExternalInvocationRegistration(
    IExternalInvocationModule Owner,
    IExternalInvocationOperation Operation)
{
    public ExternalInvocationModuleDescriptor Module => Owner.Descriptor;

    public ExternalInvocationModuleAvailability Availability => Owner.Availability;
}

public interface IExternalInvocationRegistry
{
    bool TryGetOperation(
        string operationId,
        [NotNullWhen(true)]
        out ExternalInvocationRegistration? registration);

    ExternalInvocationCatalogSnapshot GetCatalog(ExternalInvocationRuntimePlan plan);
}
