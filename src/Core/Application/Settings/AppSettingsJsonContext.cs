using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Settings;
namespace ResourceManager.App.Application.Settings;
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppHardwareControlSettings))]
[JsonSerializable(typeof(AppReportRuleOverride[]))]
[JsonSerializable(typeof(MemoryCpuTierSettings))]
[JsonSerializable(typeof(AppTargetedRecordingSettings))]
[JsonSerializable(typeof(TargetedRecordingProfile[]))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
