using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Persistence;

/// <summary>
/// Compatibility reads for optimization values that have already been persisted.
/// Remove these mappings after the supported protection JSON and report target rows
/// have been rewritten or retired. They are not for request validation.
/// </summary>
internal static class StoredOptimizationWireValues
{
    internal static string ReadReportTargetType(string? value)
        => Read(value,
            [
                OptimizationReportTargetTypes.ProcessInstance,
                OptimizationReportTargetTypes.Software,
                OptimizationReportTargetTypes.Drive,
                OptimizationReportTargetTypes.System,
                OptimizationReportTargetTypes.Device,
                OptimizationReportTargetTypes.Display,
                OptimizationReportTargetTypes.PhysicalDisk,
                OptimizationReportTargetTypes.Volume,
                OptimizationReportTargetTypes.CpuPackage
            ],
            "optimization report target type");

    internal static string ReadProtectionState(string? value)
        => Read(value,
            [
                OptimizationProtectionStates.Active,
                OptimizationProtectionStates.Missing,
                OptimizationProtectionStates.Stale
            ],
            "optimization protection state");

    internal static string ReadSoftwareDisplayKind(string? value)
        => Read(value,
            [
                SoftwareDisplayKinds.Adapted,
                SoftwareDisplayKinds.Controlled,
                SoftwareDisplayKinds.Managed,
                SoftwareDisplayKinds.Game,
                SoftwareDisplayKinds.HighPerformance,
                SoftwareDisplayKinds.General,
                SoftwareDisplayKinds.WindowsSystem,
                SoftwareDisplayKinds.WindowsApp,
                SoftwareDisplayKinds.WindowsComponent,
                SoftwareDisplayKinds.WindowsService,
                SoftwareDisplayKinds.Unattributed,
                SoftwareDisplayKinds.SystemResidual
            ],
            "software display kind");

    private static string Read(
        string? value,
        ReadOnlySpan<string> canonicalValues,
        string valueSet)
    {
        foreach (var canonical in canonicalValues)
        {
            if (value is not null
                && string.Equals(
                    RemoveUnderscores(value),
                    RemoveUnderscores(canonical),
                    StringComparison.OrdinalIgnoreCase))
            {
                return canonical;
            }
        }

        throw new InvalidDataException($"Unsupported stored {valueSet} '{value}'.");
    }

    private static string RemoveUnderscores(string value)
        => value.Replace("_", string.Empty, StringComparison.Ordinal);
}
