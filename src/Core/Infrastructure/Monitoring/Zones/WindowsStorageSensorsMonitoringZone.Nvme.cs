using ResourceManager.ReadWrite.Windows.Devices;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed partial class WindowsStorageSensorsMonitoringZone
{
    internal StorageSensorSnapshot ReadNvmeSensors(StorageSensorReadRequest request)
        => CanRead ? ReadNvmeSensorsCore(request) : StorageSensorsFrozen();
    internal StorageSensorSnapshot ReadNvmeSensorsCore(StorageSensorReadRequest request) => Adapt(owner.ReadNvmeSensorsCore(request));
    internal static int? FindNvmeDiskNumber(IReadOnlyDictionary<string, object?> physical,
        IReadOnlyList<Dictionary<string, object?>> osDisks) => WindowsStorageOwner.FindNvmeDiskNumber(physical, osDisks);
}
