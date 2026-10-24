using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Infrastructure.Adaptation;

partial class ResourceManagerSelfSchedulingControl
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(CoordinatorSchedulingState))]
    private partial class ResourceManagerSelfSchedulingControlJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
