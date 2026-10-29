using System.Text.Json;
using System.Security.Cryptography;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Settings;

partial class JsonAppSettingsStore
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(AppSettings))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(AppHardwareControlSettings))]
    private partial class JsonAppSettingsStoreJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
