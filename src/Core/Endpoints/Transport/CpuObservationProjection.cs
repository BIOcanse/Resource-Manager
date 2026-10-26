using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Endpoints.Transport;

internal sealed record CpuObservationUnavailable(string State, string ReasonCode);
internal sealed record CpuCoreLoad(string PhysicalCoreId, string? CcdId, double UsagePercent,
    double ExecutionTimeMilliseconds, int ProcessCount);
internal sealed record CpuCoreLoadSnapshot(string State, string? ReasonCode, DateTimeOffset CapturedAt,
    DateTimeOffset MeasuredFrom, DateTimeOffset MeasuredThrough, long SessionGeneration, IReadOnlyList<CpuCoreLoad> Cores);

internal static class CpuObservationProjection
{
    internal static CpuCoreLoadSnapshot Summarize(CpuCoreResidencySnapshot snapshot)
    {
        var measured = snapshot.Processes.SelectMany(process => process.PhysicalCores)
            .GroupBy(core => core.PhysicalCoreId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new CpuCoreLoad(group.Key, group.First().CcdId,
                group.Sum(core => core.UsagePercent), group.Sum(core => core.ExecutionTimeMilliseconds), group.Count()),
                StringComparer.OrdinalIgnoreCase);
        // Scopes enumerate observed processors, so an idle core can be included without guessing a missing core is idle.
        foreach (var scope in snapshot.LogicalProcessorScopes)
            measured.TryAdd(scope.PhysicalCoreId, new(scope.PhysicalCoreId, scope.CcdId, 0, 0, 0));
        return new("available", null, snapshot.CapturedAt, snapshot.MeasuredFrom, snapshot.MeasuredThrough,
            snapshot.SessionGeneration, measured.Values.OrderBy(core => core.PhysicalCoreId, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
