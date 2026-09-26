using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;

namespace ResourceManager.App.Application.SoftwareIdentity;

public interface ISoftwareIdentityCatalog
{
    string Version { get; }

    IReadOnlyList<SoftwareIdentityCatalogEntry> Entries { get; }

    SoftwareIdentityCatalogEntry? MatchInstalledSoftware(InstalledSoftwareEntry software);

    SoftwareIdentityCatalogEntry? MatchProcess(RuntimeProcessIdentity process);

    PortableSoftwareIdentityMatch? MatchPortableProcess(RuntimeProcessIdentity process);
}
