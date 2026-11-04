using System.Text.Json.Serialization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RunningGpuPlacementActionResult))]
[JsonSerializable(typeof(GpuPlacementProcessInstance))]
internal partial class ExternalGpuPlacementJsonContext : JsonSerializerContext;
