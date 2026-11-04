using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

partial class JsonGpuPlacementProcessHistoryStore
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(GpuPlacementProcessHistoryDocument))]
    private partial class JsonGpuPlacementProcessHistoryStoreJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
