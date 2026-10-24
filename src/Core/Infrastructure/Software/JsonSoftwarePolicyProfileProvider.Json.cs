using System.Text.Json;
using ResourceManager.App.Application.Software;

namespace ResourceManager.App.Infrastructure.Software;

partial class JsonSoftwarePolicyProfileProvider
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(SoftwarePolicyProfileDocument))]
    private partial class JsonSoftwarePolicyProfileProviderJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
