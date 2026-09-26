using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Application.ProcessAttribution;

public sealed class RuntimeProcessAttributionPipeline : IDisposable
{
    private readonly NativeRuntimeProcessAttributionResolver resolver;

    public RuntimeProcessAttributionPipeline(
        IReadOnlyList<SoftwareRecord> softwareRecords,
        IReadOnlyList<AdapterSoftwareRegistration> adapterRegistrations,
        IReadOnlyList<ControlledSoftwareRegistration> controlledRegistrations,
        IRuntimePackageIdentityResolver packageIdentityResolver,
        IRuntimeServiceIdentityResolver serviceIdentityResolver,
        IRuntimeRootIdentityResolver rootIdentityResolver,
        IRuntimeSystemProcessClassifier systemClassifier,
        ISoftwareIdentityCatalog softwareIdentityCatalog,
        HostManagerSoftwareIdentityOwner softwareIdentityOwner)
    {
        resolver = new NativeRuntimeProcessAttributionResolver(
            softwareRecords,
            adapterRegistrations,
            controlledRegistrations,
            packageIdentityResolver,
            serviceIdentityResolver,
            rootIdentityResolver,
            systemClassifier,
            softwareIdentityCatalog,
            softwareIdentityOwner);
    }

    public RuntimeSoftwareAttribution Match(RuntimeProcessIdentity process)
        => resolver.Resolve(process);

    public void Dispose() => resolver.Dispose();
}
