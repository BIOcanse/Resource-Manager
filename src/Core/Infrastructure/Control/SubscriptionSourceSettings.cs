using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>One shared build-time choice for subscription sources. This reader
/// is the first consumer; other sources can use the same compiled setting.</summary>
internal static class SubscriptionSourceSettings
{
    internal static TimeSpan ProductStopBuffer { get; } = ReadEmbeddedDefault();

    internal static TimeSpan CompileStopBuffer(FreedomPointCompilation freedom)
        => TimeSpan.FromMilliseconds(freedom.Consume<int>(BackendFreedomPointPaths.SingleRequestStopBuffer,
            "SubscriptionSourceSettings.CompileStopBuffer"));

    private static TimeSpan ReadEmbeddedDefault()
    {
        var profile = StrictHostManagerProfileLoader.LoadBytes(
            StrictHostManagerProfileLoader.ReadExpandedDefaultBytes(), "embedded/HostManager.default.json").Profile;
        return CompileStopBuffer(FreedomPointRegistry.LoadEmbedded().BeginCompilation(profile,
            new CompiledCpuScoringPlan(1d, CompiledCpuCoreWeights.Empty)));
    }
}
