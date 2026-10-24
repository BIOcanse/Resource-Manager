namespace ResourceManager.ReadWrite.Windows.Processes;

/// <summary>Time resolution and hard limit for retained process/birth/disk/direction pairs.</summary>
public sealed record PhysicalDiskIoCaptureConfiguration(int BucketWidthMilliseconds, int MaximumActivePairs)
{
    public static PhysicalDiskIoCaptureConfiguration Default { get; } = new(100, 8192);
}
