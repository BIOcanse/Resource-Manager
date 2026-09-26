using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class DxgkrnlVidMmEtwResidualBreakdownProvider(
    DxgkrnlVidMmEtwTelemetryZone etwZone) : IResourceResidualBreakdownProvider, IResourceTableProviderStateSource
{
    private static readonly TimeSpan EvidenceWindow = TimeSpan.FromSeconds(45);

    private readonly DxgkrnlVidMmEtwTelemetryZone etwZone = etwZone;
    private readonly StaticResourceResidualBreakdownProvider fallback = new();

    public IReadOnlyList<ResourceTableProviderState> GetStates(IReadOnlyList<ResourceTableColumn> columns)
    {
        if (!columns.Any(static column => IsGpuVramMetric(column.Id)))
        {
            return [];
        }

        var currentState = etwZone.State;
        if (currentState.Equals("Running", StringComparison.OrdinalIgnoreCase)
            || currentState.Equals("Idle", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            new ResourceTableProviderState(
                DxgkrnlVidMmEtwTelemetryZone.ProviderStateId,
                currentState,
                etwZone.Message)
        ];
    }

    public IReadOnlyList<ResourceProcessSegment> CreateResidualSegments(ResourceResidualBreakdownRequest request)
    {
        if (!IsGpuVramMetric(request.MetricId) || request.Value <= 0)
        {
            return fallback.CreateResidualSegments(request);
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = etwZone.ReadEvidence(EvidenceWindow)
            .Where(evidence => evidence.ValueBytes > 0
                && now - evidence.ObservedAt <= EvidenceWindow
                && evidence.ProcessId > 4
                && !request.KnownProcessIds.Contains(evidence.ProcessId)
                && IsProcessAlive(evidence.ProcessId))
            .GroupBy(static evidence => evidence.ProcessId)
            .Select(static group => group.OrderByDescending(evidence => evidence.ObservedAt).First())
            .OrderByDescending(static evidence => evidence.ValueBytes)
            .ToArray();
        if (candidates.Length == 0)
        {
            return fallback.CreateResidualSegments(request);
        }

        var rawTotal = candidates.Sum(static evidence => evidence.ValueBytes);
        if (rawTotal <= 0)
        {
            return fallback.CreateResidualSegments(request);
        }

        var scale = rawTotal > request.Value ? request.Value / rawTotal : 1;
        var remaining = request.Value;
        var rows = new List<ResourceProcessSegment>();
        foreach (var candidate in candidates)
        {
            var value = Math.Min(remaining, candidate.ValueBytes * scale);
            if (value < 1024 * 1024)
            {
                continue;
            }

            remaining -= value;
            rows.Add(new ResourceProcessSegment(
                candidate.ProcessId,
                $"{candidate.ProcessName}（DXGKrnl/VidMm）",
                null,
                value,
                Percent(value, request.CapacityValue),
                Percent(value, request.Value),
                null,
                null,
                ResourceProcessAttributionKinds.EtwResidualProcess));
        }

        if (rows.Count == 0)
        {
            return fallback.CreateResidualSegments(request);
        }

        if (remaining > Math.Max(1024 * 1024, request.Value * 0.05))
        {
            var fallbackRequest = request with
            {
                Value = remaining,
                SystemPercent = Percent(remaining, request.CapacityValue)
            };
            rows.AddRange(fallback.CreateResidualSegments(fallbackRequest));
        }

        return rows;
    }
}
