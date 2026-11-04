using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

partial class NativeDisplayCoordinatorPayloadCatalog
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(DeviceTopologyDisplayPath))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(DeviceTopologyOemDisplayConnectorProfile))]
    private partial class DisplayPayloadJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
