using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Settings;

partial class JsonDashboardSettingsStore
{
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(DashboardSettings))]
    private partial class JsonDashboardSettingsStoreJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
