using ResourceManager.App.Domain.Operations;

namespace ResourceManager.App.Application.Operations;

public interface IFileChangeTracker
{
    Task<FileInventorySnapshot> CaptureAsync(
        FileChangeTrackingScope scope,
        CancellationToken cancellationToken);

    FileChangeReport Compare(
        FileInventorySnapshot before,
        FileInventorySnapshot after);
}
