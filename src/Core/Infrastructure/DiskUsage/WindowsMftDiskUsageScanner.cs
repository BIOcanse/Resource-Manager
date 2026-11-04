using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
namespace ResourceManager.App.Infrastructure.DiskUsage;
// Host adapter retains the original scanner entry and explicit fallback.
internal sealed class WindowsMftDiskUsageScanner : IDiskUsageScanner
{
    private readonly ResourceManager.ReadWrite.Windows.Files.DiskUsage.WindowsMftDiskUsageScanner owner;
    public WindowsMftDiskUsageScanner(IDiskUsageVolumeCatalog volumes, IDiskUsageScanner fallbackScanner)
        => owner = new(volumes, fallbackScanner);
    public Task<DiskUsageScanResult> ScanAsync(DiskUsageScanRequest request, IProgress<DiskUsageScanProgress>? progress, CancellationToken cancellationToken)
        => owner.ScanAsync(request, progress, cancellationToken);
    internal sealed class ScanTotals { public long Bytes; public long Files; public long Directories; public long Unreadable; }
    internal static bool BuildSubtree(string volumeId, string? folderPath, Dictionary<long, NtfsFileRecord> records,
        DiskUsageTreeBuilder builder, ScanTotals totals, CancellationToken cancellationToken)
    {
        var raw = new ResourceManager.ReadWrite.Windows.Files.DiskUsage.WindowsMftDiskUsageScanner.ScanTotals
            { Bytes = totals.Bytes, Files = totals.Files, Directories = totals.Directories, Unreadable = totals.Unreadable };
        var result = ResourceManager.ReadWrite.Windows.Files.DiskUsage.WindowsMftDiskUsageScanner.BuildSubtree(volumeId, folderPath, records, builder, raw, cancellationToken);
        totals.Bytes = raw.Bytes; totals.Files = raw.Files; totals.Directories = raw.Directories; totals.Unreadable = raw.Unreadable;
        return result;
    }
}
