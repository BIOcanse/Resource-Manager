using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IHostManagerRollbackStateStore
{
    // Only durable receipts authorize retirement. Null means authority is not available yet.
    IReadOnlySet<string>? ReadExternalGpuAttemptReferences() => null;

    Task<HostManagerRollbackStateDocument> LoadAsync(CancellationToken cancellationToken);

    Task<HostManagerRollbackStateDocument> ReserveNativeHostSessionIncarnationAsync(
        CancellationToken cancellationToken);

    Task SaveAsync(
        HostManagerRollbackStateDocument document,
        CancellationToken cancellationToken);

    async Task<HostManagerRollbackStateDocument> SaveAndReadAsync(
        HostManagerRollbackStateDocument document, CancellationToken cancellationToken)
    {
        await SaveAsync(document, cancellationToken);
        return document;
    }
}
