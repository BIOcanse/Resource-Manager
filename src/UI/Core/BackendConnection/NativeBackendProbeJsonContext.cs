using System.Text.Json.Serialization;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BackendStartupCapabilitiesResponse))]
internal partial class NativeBackendProbeJsonContext : JsonSerializerContext;
