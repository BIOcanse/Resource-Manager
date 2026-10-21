namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public enum DeviceTopologySampleCompletion : byte
{
    Sampled = 1,
    Failed = 2,
    SkippedBusy = 3,
    Canceled = 4
}
