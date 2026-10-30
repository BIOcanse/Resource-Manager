using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Settings;
namespace ResourceManager.App.Application.Settings;
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppHardwareControlSettings))]
[JsonSerializable(typeof(AppReportRuleOverride[]))]
[JsonSerializable(typeof(MemoryCpuTierSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
