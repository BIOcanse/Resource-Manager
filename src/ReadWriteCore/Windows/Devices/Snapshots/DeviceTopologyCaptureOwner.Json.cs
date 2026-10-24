using System.Text.Json.Serialization;
using ResourceManager.ReadWrite.Windows.Devices.Models;
namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;
public sealed partial class DeviceTopologyCaptureOwner
{
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(DeviceTopologyDriverInformation[]))]
    [JsonSerializable(typeof(DeviceTopologyNpuInformation[]))]
    private partial class DeviceFingerprintJsonContext : JsonSerializerContext;
}
