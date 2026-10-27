using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    internal static System.Text.Json.JsonSerializerOptions CreateFreedomPointSerializerOptions(System.Text.Json.JsonSerializerOptions options)
        => new FreedomPointJsonContext(options).Options;

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(HostManagerConfigurationProfile))]
    [JsonSerializable(typeof(FreedomPointRegistry.CpuScoringInput))]
    [JsonSerializable(typeof(CompiledCpuCoreWeights))]
    [JsonSerializable(typeof(CpuExecutionTimeSourceDeclaration))]
    [JsonSerializable(typeof(CpuSmtAccountingDeclaration))]
    [JsonSerializable(typeof(CompiledCpuAutomaticExclusivityPolicy))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(double[]))]
    [JsonSerializable(typeof(GpuWindowExecutionLimitsDeclaration))]
    [JsonSerializable(typeof(HostManagerBaseScoreTierProfile))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(SoftwareWelfareDeclaration))]
    [JsonSerializable(typeof(Application.KernelLeak.KernelLeakOptions))]
    private partial class FreedomPointJsonContext : JsonSerializerContext;
}
