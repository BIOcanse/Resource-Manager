using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

/// <summary>
/// Build-time GPU placement choices (freedom-point table scheduling/placement). The history store and observer, the
/// external GPU runtime, the injector and the launch report store are constructed before the first runtime plan
/// publication, so they read <see cref="ProductDefaults"/>; the plan compiler consumes the same declarations through
/// <see cref="Compile"/>.
/// </summary>
internal static class GpuPlacementSettings
{
    private const string Consumer = "GpuPlacementSettings.Compile";

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static CompiledGpuPlacementSettings ProductDefaults { get; } = Compile(EmbeddedFreedomPointDefaults.Begin());

    internal static CompiledGpuPlacementSettings Compile(FreedomPointCompilation freedom)
    {
        ArgumentNullException.ThrowIfNull(freedom);
        return new(
            freedom.Consume<GpuPlacementProcessHistoryOptions>(BackendFreedomPointPaths.GpuPlacementProcessHistory, Consumer).Validate(),
            freedom.Consume<ExternalGpuRuntimeOptions>(BackendFreedomPointPaths.ExternalGpuRuntime, Consumer).Validate(),
            freedom.Consume<GpuInjectionOptions>(BackendFreedomPointPaths.GpuInjection, Consumer).Validate(),
            freedom.Consume<GpuLaunchReportOptions>(BackendFreedomPointPaths.GpuLaunchReports, Consumer).Validate());
    }
}
