using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;

namespace ResourceManager.App.Infrastructure.Adaptation;

// Import is borrowed access to the product Ledger, not another ownership route.
// The interface registration must not make DI capture its IDisposable owner again.
internal sealed class LedgerDiskUsageTreePayloadAllocator(ResourceManagerLedgerOwner owner)
    : IDiskUsageTreePayloadAllocator
{
    public IDiskUsageTreePayload Import(DiskUsageTree source, CancellationToken cancellationToken = default)
        => owner.Import(source, cancellationToken);
}
