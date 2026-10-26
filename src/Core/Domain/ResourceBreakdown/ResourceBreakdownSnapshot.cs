using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Domain.ResourceBreakdown;

public static class ResourceBreakdownMetricIds
{
    public static bool IsSupported(string? metricId)
    {
        if (string.IsNullOrWhiteSpace(metricId)) return false;
        var id = metricId.Trim().ToLowerInvariant();
        if (id is CpuUsage or MemoryUsage or "virtualmemory.usage" or DiskIo or DiskRead or DiskWrite
            or NetworkTraffic or NetworkReceive or NetworkSend or NetworkRawTraffic or NetworkRawReceive or NetworkRawSend) return true;
        var parts = id.Split('.');
        return parts.Length == 3 && parts[0] == "gpu" && int.TryParse(parts[1], out var index)
            && index >= 0 && parts[2] is "usage" or "vram";
    }

    public const string CpuUsage = "cpu.usage";
    public const string MemoryUsage = "memory.usage";
    public const string VirtualMemoryUsage = "virtualMemory.usage";
    public const string DiskIo = "disk.io";
    public const string DiskRead = "disk.read";
    public const string DiskWrite = "disk.write";
    public const string NetworkTraffic = "network.traffic";
    public const string NetworkReceive = "network.receive";
    public const string NetworkSend = "network.send";
    public const string NetworkRawTraffic = "network.raw.traffic";
    public const string NetworkRawReceive = "network.raw.receive";
    public const string NetworkRawSend = "network.raw.send";
    public const string GpuPrefix = "gpu.";
}

public static class ResourceBreakdownScaleModes
{
    public const string Capacity = "capacity";
    public const string Active = "active";

    public static bool SupportsCapacity(string? metricId)
    {
        if (string.IsNullOrWhiteSpace(metricId))
        {
            return false;
        }

        var normalized = metricId.Trim();
        if (normalized.Equals(ResourceBreakdownMetricIds.CpuUsage, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.MemoryUsage, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(ResourceBreakdownMetricIds.VirtualMemoryUsage, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string gpuPrefix = "gpu.";
        if (!normalized.StartsWith(gpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var metricSeparator = normalized.IndexOf('.', gpuPrefix.Length);
        return metricSeparator > gpuPrefix.Length
            && int.TryParse(normalized[gpuPrefix.Length..metricSeparator], out _)
            && (normalized[(metricSeparator + 1)..].Equals("usage", StringComparison.OrdinalIgnoreCase)
                || normalized[(metricSeparator + 1)..].Equals("vram", StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string? metricId, string? scaleMode)
    {
        if (!SupportsCapacity(metricId))
        {
            return Active;
        }

        return scaleMode?.Equals(Active, StringComparison.OrdinalIgnoreCase) == true
            ? Active
            : Capacity;
    }
}

public static class ResourceBreakdownSamplingStatuses
{
    public const string Warming = "warming";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public sealed record ResourceBreakdownSamplingState(
    string Status,
    long StateRevision,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string? FailureCode,
    string? FailureMessage)
{
    public static ResourceBreakdownSamplingState Ready(DateTimeOffset capturedAt)
        => new(
            ResourceBreakdownSamplingStatuses.Ready,
            0,
            capturedAt,
            capturedAt,
            null,
            null);

    public static ResourceBreakdownSamplingState Aggregate(
        IReadOnlyList<ResourceBreakdownDatasetSamplingState> datasets,
        long stateRevision,
        DateTimeOffset? fallbackAttemptAt = null,
        string? emptyFailureCode = null,
        string? emptyFailureMessage = null)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        if (datasets.Count == 0)
        {
            return new ResourceBreakdownSamplingState(
                emptyFailureCode is null
                    ? ResourceBreakdownSamplingStatuses.Warming
                    : ResourceBreakdownSamplingStatuses.Failed,
                stateRevision,
                fallbackAttemptAt,
                null,
                emptyFailureCode,
                emptyFailureMessage);
        }

        var hasReady = datasets.Any(static item =>
            item.Status == ResourceBreakdownSamplingStatuses.Ready);
        var hasFailed = datasets.Any(static item =>
            item.Status == ResourceBreakdownSamplingStatuses.Failed);
        var hasWarming = datasets.Any(static item =>
            item.Status == ResourceBreakdownSamplingStatuses.Warming);
        var status = hasReady
            ? ResourceBreakdownSamplingStatuses.Ready
            : hasFailed
                ? ResourceBreakdownSamplingStatuses.Failed
                : hasWarming
                    ? ResourceBreakdownSamplingStatuses.Warming
                    : ResourceBreakdownSamplingStatuses.Ready;
        var failure = datasets.FirstOrDefault(static item =>
            item.FailureCode is not null);
        return new ResourceBreakdownSamplingState(
            status,
            stateRevision,
            datasets
                .Where(static item => item.LastAttemptAt is not null)
                .Select(static item => item.LastAttemptAt)
                .DefaultIfEmpty(fallbackAttemptAt)
                .Max(),
            datasets
                .Where(static item => item.LastSuccessAt is not null)
                .Select(static item => item.LastSuccessAt)
                .DefaultIfEmpty(null)
                .Max(),
            failure?.FailureCode,
            failure?.FailureMessage);
    }
}

public sealed record ResourceBreakdownDatasetSamplingState(
    string DatasetId,
    string Status,
    ulong Generation,
    long StateRevision,
    DateTimeOffset? CapturedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? ReadyUntil,
    string? FailureCode,
    string? FailureMessage);

public sealed record ResourceBreakdownSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<ResourceBreakdownBar> Bars)
{
    public PhysicalMemoryResidualFrame? PhysicalMemoryResidual { get; init; }
    public ProcessMetricFieldSnapshot? ProcessFields { get; init; }
    public IReadOnlyList<ProcessMemoryFact> ProcessMemoryFacts { get; init; } = [];

    public ResourceBreakdownSamplingState Sampling { get; init; }
        = ResourceBreakdownSamplingState.Ready(CapturedAt);

    public IReadOnlyList<ResourceBreakdownDatasetSamplingState> Datasets { get; init; }
        = [];
}

public sealed record ResourceBreakdownBar(
    string MetricId,
    string Unit,
    string ScaleMode,
    double? TotalValue,
    double? CapacityValue,
    double? TotalSystemPercent,
    IReadOnlyList<ResourceSoftwareSegment> Software,
    SamplingObservationStatus ObservationStatus = SamplingObservationStatus.Current,
    SamplingObservationStatus AttributionStatus = SamplingObservationStatus.Current)
{
    public double? SharedValue { get; init; }
    public string? ResidualSource { get; init; }
    public string? ProcessFieldName { get; init; }
    public string? Quantity { get; init; }
    public string? UnavailableReason { get; init; }
    public string? TotalMetricId { get; init; }
    public double? ProcessSumValue { get; init; }
    public double? ResidualValue { get; init; }
    public bool AdditiveAcrossProcesses { get; init; } = true;
    public DateTimeOffset? CapturedAt { get; init; }
    public long? WindowMilliseconds { get; init; }
    public ResourceManager.ReadWrite.ProcessFields.ProcessGpuEngineScope? GpuEngine { get; init; }
    public string? EngineClass { get; init; }
}

public sealed record ResourceSoftwareSegment(
    string SoftwareId,
    string Name,
    string Kind,
    string DisplayKind,
    double Value,
    double SystemPercent,
    int ProcessCount,
    IReadOnlyList<ResourceProcessSegment> Processes,
    double BaseScore = 0)
{
    public double? SharedValue { get; init; }
}

public sealed record ResourceProcessSegment(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    double Value,
    double SystemPercent,
    double SoftwarePercent,
    string? UserName = null,
    string? Architecture = null,
    string AttributionKind = ResourceProcessAttributionKinds.Process,
    double BaseScore = 0)
{
    public long? ProcessStartKey { get; init; }
    public double? SharedValue { get; init; }
}

public static class ResourceProcessAttributionKinds
{
    public const string Process = "process";
    public const string SystemResidual = "system-residual";
    public const string EtwResidualProcess = "etw-residual-process";
}
