namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

/// <summary>Caller-selected operational bounds, distinct from the export formats.</summary>
public sealed record WindowsExplicitBenchmarkExportLimits(
    int MaximumCsvBytes = 256 * 1024,
    int MaximumCsvRecords = 256,
    int MaximumCsvFieldChars = 4096,
    int MaximumResultArchiveBytes = 4 * 1024 * 1024,
    int MaximumResultArchiveEntries = 64,
    int MaximumResultXmlBytes = 1024 * 1024,
    int MaximumSourceMetadataBytes = 16 * 1024)
{
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCsvBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCsvRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCsvFieldChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumResultArchiveBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumResultArchiveEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumResultXmlBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSourceMetadataBytes);
    }
}
