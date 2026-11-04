using ResourceManager.ReadWrite.Windows.Devices;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed partial class WindowsStorageSensorsMonitoringZone
{
    internal StorageSensorSnapshot ReadAtaSensors(AtaSmartSensorReadRequest request)
        => ReadAtaSensorsCore(request);
    internal StorageSensorSnapshot ReadAtaSensorsCore(AtaSmartSensorReadRequest request) => Adapt(owner.ReadAtaSensorsCore(request));
    internal static int? FindAtaDiskNumber(IReadOnlyDictionary<string, object?> physical,
        IReadOnlyList<Dictionary<string, object?>> osDisks) => WindowsStorageOwner.FindAtaDiskNumber(physical, osDisks);
}
