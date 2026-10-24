using System.Text.Json;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

partial class JsonDeviceTopologySnapshotStore
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(DeviceTopologyPersistedSnapshot))]
    private partial class JsonDeviceTopologySnapshotStoreJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
