using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Settings;
namespace ResourceManager.App.Application.Settings;
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppHardwareControlSettings))]
[JsonSerializable(typeof(AppReportRuleOverride[]))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
