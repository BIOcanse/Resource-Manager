using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

// App owns typed plan compilation. Declaration parsing and SDK validation are shared with build tools.
internal sealed class FreedomPointRegistry
{
    private readonly BackendFreedomPointRegistry contract;
    internal FreedomPointSdkAdapter Sdk => contract.Sdk;
    internal CompiledFreedomPointTree Declarations => contract.Declarations;

    private FreedomPointRegistry(BackendFreedomPointRegistry contract) => this.contract = contract;

    internal static JsonSerializerOptions CreateDeclarationSerializerOptions()
        => BackendFreedomPointRegistry.CreateDeclarationSerializerOptions();

    public static FreedomPointRegistry Parse(ReadOnlySpan<byte> bytes,
        Func<CompiledFreedomPoint, ImmutableArray<global::FreedomPoint.GenerationTarget>>? generationTargets = null)
        => new(BackendFreedomPointRegistry.Parse(bytes, generationTargets));

    internal const string ManifestResourceName = "ResourceManager.Configuration.FreedomPoints.backend.json";
    internal static JsonSerializerOptions CreateSerializerOptions() => HostManagerPlanCompiler.CreateFreedomPointSerializerOptions(new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    });

    public static FreedomPointRegistry LoadEmbedded()
    {
        using var stream = typeof(FreedomPointRegistry).Assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new InvalidDataException($"Freedom point declarations '{ManifestResourceName}' were not embedded.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Parse(bytes.ToArray());
    }

    public FreedomPointCompilation BeginCompilation(
        HostManagerConfigurationProfile profile,
        CompiledCpuScoringPlan? cpuScoring = null)
    {
        var options = CreateSerializerOptions();
        return new(Sdk, JsonSerializer.SerializeToElement(profile, options),
            cpuScoring is null ? default : JsonSerializer.SerializeToElement(
                new CpuScoringInput(cpuScoring.BaselineRatio, cpuScoring.CoreWeights), options), options);
    }

    internal sealed record CpuScoringInput(double baseline_ratio, CompiledCpuCoreWeights core_performance_weights);
}
