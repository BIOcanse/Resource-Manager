using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.ReadWrite.Information;
namespace ResourceManager.ReadWrite.Windows.Files.DiskUsage;

/// <summary>An explicit, fixed scan scope using the original scanner and result
/// owner. Construction and Query are passive. Full traversal requires the caller's
/// fallback. Subscription borrows observe committed scans without starting scans.</summary>
public sealed class WindowsMftDiskUsageResource
{
    private readonly IDiskUsageScanner scanner;
    private readonly DiskUsageScanRequest request;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    public DiskUsageTreeStore Store { get; }
    public InformationReadWriteBackend Backend { get; }
    public WindowsMftDiskUsageResource(IDiskUsageScanner scanner, DiskUsageScanRequest request, DiskUsageTreeStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(scanner); ArgumentNullException.ThrowIfNull(request);
        this.scanner = scanner; this.request = request; Store = store ?? new();
        Backend = new(Store.ReadInformation, async token => { await ScanAsync(null, token); return Store.ReadInformation(); },
            _ => new(true, null, [DeviceInformationFieldRegistry.MftSourceId]));
    }
    public static WindowsMftDiskUsageResource CreateDefault(DiskUsageScanRequest request, IDiskUsageScanner? explicitFullFallback = null)
        => new(new WindowsMftDiskUsageScanner(new WindowsDiskUsageVolumeCatalog(), explicitFullFallback), request);
    public IReadWriteCollectionBorrow BorrowSubscriptionCapture() => Store.BorrowSubscriptionCapture();
    public MftDiskUsageInformationView CreateInformationView(IEnumerable<int> nodes) => Store.CreateInformationView(nodes);
    public async Task<DiskUsageScanResult> ScanAsync(IProgress<DiskUsageScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await scanGate.WaitAsync(cancellationToken);
        try
        {
            DiskUsageScanResult result;
            try
            {
                result = await scanner.ScanAsync(request, progress, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { Store.ClearCurrentValues(); throw; }
            // Import failures do not discard a valid previously published tree.
            cancellationToken.ThrowIfCancellationRequested();
            Store.Replace(result, cancellationToken);
            return result;
        }
        finally { scanGate.Release(); }
    }
}
