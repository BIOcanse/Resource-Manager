using ResourceManager.ReadWrite.Windows.Devices;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed partial class WindowsStorageSensorsMonitoringZone
{
    internal StorageSensorSnapshot ReadNvmeSensors(StorageSensorReadRequest request)
        => ReadNvmeSensorsCore(request);
    internal StorageSensorSnapshot ReadNvmeSensorsCore(StorageSensorReadRequest request) => Adapt(owner.ReadNvmeSensorsCore(request));
    internal static int? FindNvmeDiskNumber(IReadOnlyDictionary<string, object?> physical,
        IReadOnlyList<Dictionary<string, object?>> osDisks) => WindowsStorageOwner.FindNvmeDiskNumber(physical, osDisks);
}
