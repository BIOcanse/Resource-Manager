using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.Persistence;

namespace ResourceManager.App.Infrastructure.Optimization;

partial class JsonOptimizationProtectionStore
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(List<ProtectedOptimizationTarget>))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(IReadOnlyList<ProtectedOptimizationTarget>))]
    private partial class JsonOptimizationProtectionStoreJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
