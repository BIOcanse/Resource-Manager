using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Settings;
namespace ResourceManager.App.Application.Settings;
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CpuMonopolyProtectionSettings))]
[JsonSerializable(typeof(AppHardwareControlSettings))]
[JsonSerializable(typeof(AppCrashDumpKeySettings))]
[JsonSerializable(typeof(AppDeviceRestartSettings))]
[JsonSerializable(typeof(CrashDumpKeySnapshot))]
[JsonSerializable(typeof(CrashDumpKeyDiagnostics))]
[JsonSerializable(typeof(AppReportRuleOverride[]))]
[JsonSerializable(typeof(MemoryCpuTierSettings))]
[JsonSerializable(typeof(AppTargetedRecordingSettings))]
[JsonSerializable(typeof(TargetedRecordingProfile[]))]
[JsonSerializable(typeof(AppPerformanceOverlaySettings))]
[JsonSerializable(typeof(AppPerformanceOverlaySoftwareSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
