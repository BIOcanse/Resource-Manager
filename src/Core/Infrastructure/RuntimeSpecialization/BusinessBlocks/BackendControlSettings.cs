using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

internal static class BackendControlSettings
{
    private const string Consumer = "BackendControlSettings.Compile";

    // Native control is created before the first runtime plan publication. These values come from
    // the same embedded freedom-point declarations that the plan compiler later consumes.
    internal static CompiledBackendControlSettings ProductDefaults { get; } = ReadEmbeddedDefaults();

    internal static CompiledBackendControlSettings Compile(FreedomPointCompilation freedom)
    {
        ArgumentNullException.ThrowIfNull(freedom);
        var settings = new CompiledBackendControlSettings(
            freedom.Consume<int>(BackendFreedomPointPaths.BusinessBlockWorkerCount, Consumer),
            freedom.Consume<int>(BackendFreedomPointPaths.BusinessBlockHistoryCapacity, Consumer),
            freedom.Consume<int>(BackendFreedomPointPaths.BusinessBlockCompletionCapacity, Consumer),
            freedom.Consume<int>(BackendFreedomPointPaths.BusinessBlockReaderCapacity, Consumer),
            freedom.Consume<int>(BackendFreedomPointPaths.BusinessBlockReaderBytes, Consumer),
            freedom.Consume<int>(BackendFreedomPointPaths.BusinessBlockWorkerArenaBytes, Consumer),
            freedom.Consume<int>(BackendFreedomPointPaths.BusinessBlockReasonTextCapacity, Consumer));
        if (!settings.IsPublished)
            throw new InvalidDataException("Backend business-block capacities must be positive integers; history capacity may be zero.");
        return settings;
    }

    private static CompiledBackendControlSettings ReadEmbeddedDefaults()
    {
        var registry = FreedomPointRegistry.LoadEmbedded();
        var profile = StrictHostManagerProfileLoader.LoadBytes(
            StrictHostManagerProfileLoader.ReadExpandedDefaultBytes(),
            "embedded/HostManager.default.json").Profile;
        var cpuScoring = new CompiledCpuScoringPlan(1d, CompiledCpuCoreWeights.Empty);
        return Compile(registry.BeginCompilation(profile, cpuScoring));
    }
}
