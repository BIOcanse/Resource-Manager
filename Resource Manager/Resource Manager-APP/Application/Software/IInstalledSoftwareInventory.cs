using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public interface IInstalledSoftwareInventory
{
    Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstalledSoftwareEntry>> RefreshInstalledSoftwareAsync(
        CancellationToken cancellationToken)
    {
        return GetInstalledSoftwareAsync(cancellationToken);
    }
}
