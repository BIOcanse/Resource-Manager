using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareDiscovery;


namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private SoftwareRecord ToPortableSoftwareRecord(PortableSoftwareRegistration registration)
    {
        var entry = softwareIdentityCatalog.Entries.FirstOrDefault(entry =>
            entry.Id.Equals(registration.CatalogEntryId, StringComparison.Ordinal));
        var projectedKind = registration.IdentityConfirmed ? entry?.Kind ?? SoftwareKinds.Other : SoftwareKinds.Other;
        return new SoftwareRecord(
            registration.SoftwareId,
            entry?.DisplayName ?? registration.Name,
            projectedKind,
            registration.RequiresRootPathConfirmation ? "RootConfirmationRequired" : "portable",
            [new(SoftwareSourceKinds.Portable), new(SoftwareSourceKinds.IdentityCatalog, softwareIdentityCatalog.Version)],
            registration.RootPaths,
            NoUninstall(SoftwareUninstallConstraints.Portable),
            null,
            registration.RequiresRootPathConfirmation,
            registration.IdentityConfirmed,
            registration.SuggestedRootPaths,
            registration.ExecutablePaths,
            registration.CatalogEntryId,
            RegistrationFacts: new(SoftwareSourceKinds.Portable));
    }
}
