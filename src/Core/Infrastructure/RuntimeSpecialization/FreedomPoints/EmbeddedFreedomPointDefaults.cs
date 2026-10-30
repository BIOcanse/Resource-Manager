using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

/// <summary>
/// Owners constructed before the first runtime plan publication read their build-time values here.
/// Each call starts a separate compilation over the same embedded declarations and default profile
/// that the plan compiler later consumes, so the values are the published values of this build.
/// The declarations and profile are parsed once per process.
/// </summary>
internal static class EmbeddedFreedomPointDefaults
{
    private static readonly Lazy<(FreedomPointRegistry Registry, HostManagerConfigurationProfile Profile)> Source =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static FreedomPointCompilation Begin()
    {
        var (registry, profile) = Source.Value;
        return registry.BeginCompilation(profile, new CompiledCpuScoringPlan(1d, CompiledCpuCoreWeights.Empty));
    }

    private static (FreedomPointRegistry, HostManagerConfigurationProfile) Load()
        => (FreedomPointRegistry.LoadEmbedded(), StrictHostManagerProfileLoader.LoadBytes(
            StrictHostManagerProfileLoader.ReadExpandedDefaultBytes(), "embedded/HostManager.default.json").Profile);
}
