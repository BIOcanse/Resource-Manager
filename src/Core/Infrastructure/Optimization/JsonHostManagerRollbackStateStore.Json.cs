using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Optimization;
namespace ResourceManager.App.Infrastructure.Optimization;
public sealed partial class JsonHostManagerRollbackStateStore
{
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(HostManagerRollbackStateDocument))]
    [JsonSerializable(typeof(HostManagerCoordinatorFacts))]
    private partial class HostManagerRollbackStateStoreJsonContext : JsonSerializerContext;
}
