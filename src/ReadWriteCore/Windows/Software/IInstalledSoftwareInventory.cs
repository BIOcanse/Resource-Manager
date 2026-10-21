using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public interface IInstalledSoftwareInventory
{
    Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(
        string? windowsSid,
        CancellationToken cancellationToken)
        => GetInstalledSoftwareAsync(cancellationToken);

    Task<IReadOnlyList<InstalledSoftwareEntry>> RefreshInstalledSoftwareAsync(
        CancellationToken cancellationToken)
    {
        return GetInstalledSoftwareAsync(cancellationToken);
    }

    Task<IReadOnlyList<InstalledSoftwareEntry>> RefreshInstalledSoftwareAsync(
        string? windowsSid,
        CancellationToken cancellationToken)
        => RefreshInstalledSoftwareAsync(cancellationToken);
}
