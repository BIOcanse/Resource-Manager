using System.Text.Json.Serialization;
namespace ResourceManager.Shared.Runtime;
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(FrontendSessionCredentialReply))]
internal partial class SessionCredentialJsonContext : JsonSerializerContext;
