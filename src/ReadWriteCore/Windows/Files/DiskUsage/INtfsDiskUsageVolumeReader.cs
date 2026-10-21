namespace ResourceManager.App.Infrastructure.DiskUsage;

/// <summary>The existing read-only volume seam. A recorded implementation supplies
/// byte reads/runs without opening a real volume; the default reader retains its original ownership.</summary>
public interface INtfsDiskUsageVolumeReader : IDisposable
{
    NtfsVolumeLayout Layout { get; }
    int Read(long offset, Span<byte> destination);
    IReadOnlyList<NtfsDataRun> ReadMasterFileTableRuns();
}
