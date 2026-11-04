using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

partial class DeviceTopologySemanticComparer
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(DeviceTopologySnapshot))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(DeviceTopologyPort))]
    private partial class DeviceTopologySemanticComparerJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
