using ResourceManager.ReadWrite.Windows.Devices;
namespace ResourceManager.App.Infrastructure.Monitoring;

// Passive request contracts retain their original namespace for host callers.
public sealed record AtaSmartDeviceReadRequest(string DeviceIdentity, AtaSmartPages Pages);
public sealed record AtaSmartSensorReadRequest(IReadOnlyDictionary<int, AtaSmartDeviceReadRequest> Devices);
[Flags]
public enum AtaSmartAttributeInformationFields { None = 0, Normalized = 1, Raw = 2, Threshold = 4 }
public sealed record AtaSmartAttributeInformationRequest(
    IReadOnlyDictionary<string, AtaSmartAttributeInformationFields> Devices,
    int MaximumDeviceCount = 4096);
public sealed record StorageSensorReadRequest(IReadOnlyDictionary<int, string> Devices);
