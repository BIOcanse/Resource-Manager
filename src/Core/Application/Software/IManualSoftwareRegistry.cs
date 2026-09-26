using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Application.Software;

public interface IManualSoftwareRegistry
{
    Task<IReadOnlyList<ManualSoftwareRecord>> GetAllAsync(CancellationToken cancellationToken);

    Task<ManualSoftwareRecord> AddOrUpdateAsync(
        ManualSoftwareRequest request,
        CancellationToken cancellationToken);

    Task<bool> RemoveAsync(
        string id,
        CancellationToken cancellationToken);
}
