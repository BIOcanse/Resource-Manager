using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Infrastructure.SoftwareIdentity;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

internal static class SoftwareIdentityCatalogTestData
{
    internal static HostManagerSoftwareIdentityOwner Owner { get; } =
        HostManagerTestPlanFactory.CreateSoftwareIdentityOwner();

    internal static SoftwareIdentityCatalogCompiler Empty { get; } = Create();

    internal static SoftwareIdentityCatalogCompiler Create(params SoftwareIdentityCatalogEntry[] entries)
    {
        return new SoftwareIdentityCatalogCompiler(
            new SoftwareIdentityCatalogDocument("1.0.0", entries),
            HostManagerTestPlanFactory.CreatePlan().SoftwareIdentityCatalog);
    }
}
