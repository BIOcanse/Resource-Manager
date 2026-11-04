using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Windows.Metrics.Views;

/// <summary>Native dataset decisions, in selected order; identifiers/reason strings are cold metadata.</summary>
public readonly ref struct MetricDatasetColumns
{
    public MetricDatasetColumns(ReadOnlySpan<string> ids, ReadOnlySpan<SamplingObservationStatus> statuses,
        ReadOnlySpan<ulong> sourceGenerations, ReadOnlySpan<long> observedAtUtcTicks,
        ReadOnlySpan<long> attemptAtUtcTicks, ReadOnlySpan<long> successAtUtcTicks,
        ReadOnlySpan<long> readyUntilUtcTicks, ReadOnlySpan<string?> failureCodes,
        ReadOnlySpan<ulong> workspaceIdentities, ReadOnlySpan<ulong> configurationGenerations,
        ReadOnlySpan<ulong> catalogGenerations, ReadOnlySpan<ulong> committedGenerations,
        MetricTimestampUnit timestampUnit = MetricTimestampUnit.UtcTicks)
    {
        var count = ids.Length;
        if (statuses.Length != count || sourceGenerations.Length != count || observedAtUtcTicks.Length != count
            || attemptAtUtcTicks.Length != count || successAtUtcTicks.Length != count || readyUntilUtcTicks.Length != count
            || failureCodes.Length != count || workspaceIdentities.Length != count || configurationGenerations.Length != count
            || catalogGenerations.Length != count || committedGenerations.Length != count)
            throw new ArgumentException("Selected dataset column lengths differ.");
        Ids = ids; Statuses = statuses; SourceGenerations = sourceGenerations; ObservedAtUtcTicks = observedAtUtcTicks;
        AttemptAtUtcTicks = attemptAtUtcTicks; SuccessAtUtcTicks = successAtUtcTicks; ReadyUntilUtcTicks = readyUntilUtcTicks;
        FailureCodes = failureCodes; WorkspaceIdentities = workspaceIdentities; ConfigurationGenerations = configurationGenerations;
        CatalogGenerations = catalogGenerations; CommittedGenerations = committedGenerations;
        TimestampUnit = timestampUnit;
    }
    public int Count => Ids.Length;
    public ReadOnlySpan<string> Ids { get; }
    public ReadOnlySpan<SamplingObservationStatus> Statuses { get; }
    public ReadOnlySpan<ulong> SourceGenerations { get; }
    public ReadOnlySpan<long> ObservedAtUtcTicks { get; }
    public ReadOnlySpan<long> AttemptAtUtcTicks { get; }
    public ReadOnlySpan<long> SuccessAtUtcTicks { get; }
    public ReadOnlySpan<long> ReadyUntilUtcTicks { get; }
    public ReadOnlySpan<string?> FailureCodes { get; }
    public ReadOnlySpan<ulong> WorkspaceIdentities { get; }
    public ReadOnlySpan<ulong> ConfigurationGenerations { get; }
    public ReadOnlySpan<ulong> CatalogGenerations { get; }
    public ReadOnlySpan<ulong> CommittedGenerations { get; }
    public MetricTimestampUnit TimestampUnit { get; }
}

/// <summary>Already ordered native history; no ring/retention logic exists in this view.</summary>
public readonly ref struct MetricHistoryColumns
{
    public MetricHistoryColumns(ReadOnlySpan<int> metricIndices, ReadOnlySpan<ulong> metricIdentities,
        ReadOnlySpan<long> observedAtUtcTicks, ReadOnlySpan<byte> present, ReadOnlySpan<double> values,
        MetricTimestampUnit timestampUnit = MetricTimestampUnit.UtcTicks)
    {
        var count = metricIndices.Length;
        if (metricIdentities.Length != count || observedAtUtcTicks.Length != count || present.Length != count || values.Length != count)
            throw new ArgumentException("Selected history column lengths differ.");
        MetricIndices = metricIndices; MetricIdentities = metricIdentities; ObservedAtUtcTicks = observedAtUtcTicks;
        Present = present; Values = values;
        TimestampUnit = timestampUnit;
    }
    public int Count => MetricIndices.Length;
    public ReadOnlySpan<int> MetricIndices { get; }
    public ReadOnlySpan<ulong> MetricIdentities { get; }
    public ReadOnlySpan<long> ObservedAtUtcTicks { get; }
    public ReadOnlySpan<byte> Present { get; }
    public ReadOnlySpan<double> Values { get; }
    public MetricTimestampUnit TimestampUnit { get; }
}

public readonly record struct MetricInventoryHeader(SamplingObservationStatus Status, ulong Generation,
    long ObservedAtUtcTicks, uint ObservedCount, uint SkippedCount, uint OverflowCount, ulong TopologyFingerprint,
    MetricTimestampUnit TimestampUnit = MetricTimestampUnit.UtcTicks);

/// <summary>Native adapter inventory, with native capability/validity/health decisions unchanged.</summary>
public readonly ref struct MetricInventoryColumns
{
    public MetricInventoryColumns(ReadOnlySpan<int> indices, ReadOnlySpan<ulong> adapterKeys,
        ReadOnlySpan<SchedulingGpuCapabilityMask> capabilities, ReadOnlySpan<SchedulingGpuMetricMask> validMetrics,
        ReadOnlySpan<SamplingObservationStatus> usageStatuses, ReadOnlySpan<SamplingObservationStatus> capacityStatuses,
        ReadOnlySpan<double> usagePercent, ReadOnlySpan<ulong> usedBytes, ReadOnlySpan<ulong> totalBytes,
        ReadOnlySpan<ulong> generations, ReadOnlySpan<long> observedAtUtcTicks,
        MetricTimestampUnit timestampUnit = MetricTimestampUnit.UtcTicks)
    {
        var count = indices.Length;
        if (adapterKeys.Length != count || capabilities.Length != count || validMetrics.Length != count
            || usageStatuses.Length != count || capacityStatuses.Length != count || usagePercent.Length != count
            || usedBytes.Length != count || totalBytes.Length != count || generations.Length != count || observedAtUtcTicks.Length != count)
            throw new ArgumentException("Selected inventory column lengths differ.");
        Indices = indices; AdapterKeys = adapterKeys; Capabilities = capabilities; ValidMetrics = validMetrics;
        UsageStatuses = usageStatuses; CapacityStatuses = capacityStatuses; UsagePercent = usagePercent;
        UsedBytes = usedBytes; TotalBytes = totalBytes; Generations = generations; ObservedAtUtcTicks = observedAtUtcTicks;
        TimestampUnit = timestampUnit;
    }
    public int Count => Indices.Length;
    public ReadOnlySpan<int> Indices { get; }
    public ReadOnlySpan<ulong> AdapterKeys { get; }
    public ReadOnlySpan<SchedulingGpuCapabilityMask> Capabilities { get; }
    public ReadOnlySpan<SchedulingGpuMetricMask> ValidMetrics { get; }
    public ReadOnlySpan<SamplingObservationStatus> UsageStatuses { get; }
    public ReadOnlySpan<SamplingObservationStatus> CapacityStatuses { get; }
    public ReadOnlySpan<double> UsagePercent { get; }
    public ReadOnlySpan<ulong> UsedBytes { get; }
    public ReadOnlySpan<ulong> TotalBytes { get; }
    public ReadOnlySpan<ulong> Generations { get; }
    public ReadOnlySpan<long> ObservedAtUtcTicks { get; }
    public MetricTimestampUnit TimestampUnit { get; }
}
