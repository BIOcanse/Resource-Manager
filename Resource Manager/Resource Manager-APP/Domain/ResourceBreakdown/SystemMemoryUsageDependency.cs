using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Domain.ResourceBreakdown;

public sealed record SystemMemoryUsageDependency(
    string DatasetId,
    ulong DenominatorBytes,
    ulong SourceGeneration,
    long ObservedAtUtcTicks,
    ulong WorkspaceIdentity,
    ulong ConfigurationGeneration,
    ulong CatalogGeneration,
    ulong CommittedGeneration,
    long ReadyUntilUtcTicks)
{
    public bool IsCurrentAt(DateTimeOffset now)
    {
        _ = now;
        return IsWellFormed();
    }

    public bool IsWellFormed()
        => string.Equals(
                DatasetId,
                SamplingDatasetIds.SystemMemoryUsage,
                StringComparison.OrdinalIgnoreCase)
            && DenominatorBytes > 0
            && SourceGeneration > 0
            && ObservedAtUtcTicks > 0
            && ObservedAtUtcTicks <= DateTimeOffset.MaxValue.UtcTicks
            && WorkspaceIdentity > 0
            && ConfigurationGeneration > 0
            && CatalogGeneration > 0
            && CommittedGeneration > 0
            && (ReadyUntilUtcTicks == 0
                || ReadyUntilUtcTicks >= ObservedAtUtcTicks
                    && ReadyUntilUtcTicks <=
                        DateTimeOffset.MaxValue.UtcTicks);

    public static bool TryCreate(
        HardwareMetricSnapshot snapshot,
        DateTimeOffset now,
        out SystemMemoryUsageDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.TryGetCurrentDataset(
                SamplingDatasetIds.SystemMemoryUsage,
                now,
                out var observation)
            || !string.Equals(
                observation.DatasetId,
                SamplingDatasetIds.SystemMemoryUsage,
                StringComparison.OrdinalIgnoreCase)
            || observation.CatalogGeneration == 0
            || snapshot.Memory.ObservationStatus !=
                SamplingObservationStatus.Current
            || !snapshot.Memory.IsUsageAvailable
            || snapshot.Memory.TotalBytes == 0
            || snapshot.Memory.UsedBytes > snapshot.Memory.TotalBytes)
        {
            dependency = null!;
            return false;
        }

        dependency = new SystemMemoryUsageDependency(
            observation.DatasetId,
            snapshot.Memory.TotalBytes,
            observation.SourceGeneration,
            observation.ObservedAtUtcTicks,
            observation.WorkspaceIdentity,
            observation.ConfigurationGeneration,
            observation.CatalogGeneration,
            observation.CommittedGeneration,
            0);
        if (dependency.IsWellFormed())
        {
            return true;
        }

        dependency = null!;
        return false;
    }
}
