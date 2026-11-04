using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Windows.Metrics.Views;

public readonly record struct MetricSourceVersion(ulong Identity, ulong Sequence);

/// <summary>Identity, not time: ordered vector includes all projection dependencies.</summary>
public readonly ref struct MetricObjectVersion(ulong regionIncarnation, ulong configurationRevision,
    ulong catalogRevision, ulong historyRevision, ulong committedGeneration, uint privilege,
    ReadOnlySpan<MetricSourceVersion> sources)
{
    public ulong RegionIncarnation { get; } = regionIncarnation;
    public ulong ConfigurationRevision { get; } = configurationRevision;
    public ulong CatalogRevision { get; } = catalogRevision;
    public ulong HistoryRevision { get; } = historyRevision;
    public ulong CommittedGeneration { get; } = committedGeneration;
    public uint Privilege { get; } = privilege;
    public ReadOnlySpan<MetricSourceVersion> Sources { get; } = sources;
}

/// <summary>Native-decided aggregate health and availability, never inferred from candidate winners.</summary>
public readonly record struct MetricAggregateFacts(CpuMetricObservationStatus CpuStatus, bool CpuUsageAvailable,
    SamplingObservationStatus MemoryStatus, bool MemoryUsageAvailable,
    SamplingObservationStatus VirtualMemoryStatus, bool VirtualMemorySelectable,
    HardwareSensorProviderState CpuProvider);

/// <summary>
/// Demand-time projection input. All columns expire with the parent's read callback. Datasets, provider
/// states and inventory columns carry native decisions; this layer only owns their immutable object form.
/// </summary>
public readonly ref struct MetricSnapshotInput(MetricCurrentView current, MetricCatalogView catalog,
    DateTimeOffset capturedAt, MetricAggregateFacts aggregates,
    MetricDatasetColumns datasets, MetricHistoryColumns history,
    MetricInventoryHeader inventory, MetricInventoryColumns adapters,
    ReadOnlySpan<HardwareSensorProviderState> gpuProviders, ReadOnlySpan<byte> gpuUsageAvailable,
    ReadOnlySpan<double?> percents = default, ReadOnlySpan<double?> totals = default,
    MetricEngineVectorView engines = default, ReadOnlySpan<int> engineBaseMetricIndices = default,
    ReadOnlySpan<string> projectionSuppressedIds = default)
{
    public MetricSnapshotInput(MetricDataAreaCurrentView current, MetricCatalogView catalog,
        DateTimeOffset capturedAt, MetricAggregateFacts aggregates, MetricDatasetColumns datasets,
        MetricHistoryColumns history, MetricInventoryHeader inventory, MetricInventoryColumns adapters,
        ReadOnlySpan<HardwareSensorProviderState> gpuProviders, ReadOnlySpan<byte> gpuUsageAvailable,
        ReadOnlySpan<double?> percents = default, ReadOnlySpan<double?> totals = default,
        MetricEngineVectorView engines = default, ReadOnlySpan<int> engineBaseMetricIndices = default,
        ReadOnlySpan<string> projectionSuppressedIds = default)
        : this(default(MetricCurrentView), catalog, capturedAt, aggregates, datasets, history, inventory,
            adapters, gpuProviders, gpuUsageAvailable, percents, totals, engines, engineBaseMetricIndices, projectionSuppressedIds)
    { DataArea = current; UsesDataArea = true; }

    public MetricCurrentView Current { get; } = current;
    public MetricDataAreaCurrentView DataArea { get; }
    public bool UsesDataArea { get; }
    internal MetricSnapshotCurrentView Values => UsesDataArea ? new(DataArea) : new(Current);
    public MetricCatalogView Catalog { get; } = catalog;
    public DateTimeOffset CapturedAt { get; } = capturedAt;
    public MetricAggregateFacts Aggregates { get; } = aggregates;
    public MetricDatasetColumns Datasets { get; } = datasets;
    public MetricHistoryColumns History { get; } = history;
    public MetricInventoryHeader Inventory { get; } = inventory;
    public MetricInventoryColumns Adapters { get; } = adapters;
    public ReadOnlySpan<HardwareSensorProviderState> GpuProviders { get; } = gpuProviders;
    public ReadOnlySpan<byte> GpuUsageAvailable { get; } = gpuUsageAvailable;
    public ReadOnlySpan<double?> Percents { get; } = percents;
    public ReadOnlySpan<double?> Totals { get; } = totals;
    public MetricEngineVectorView Engines { get; } = engines;
    public ReadOnlySpan<int> EngineBaseMetricIndices { get; } = engineBaseMetricIndices;
    public ReadOnlySpan<string> ProjectionSuppressedIds { get; } = projectionSuppressedIds;
}

/// <summary>Stack-only input dispatch; direct bindings never flatten/normalize native source columns.</summary>
internal ref struct MetricSnapshotCurrentView
{
    private readonly MetricCurrentView selected;
    private MetricDataAreaCurrentView direct;
    private readonly bool useDirect;
    internal MetricSnapshotCurrentView(MetricCurrentView selected)
    { this.selected = selected; direct = default; useDirect = false; }
    internal MetricSnapshotCurrentView(MetricDataAreaCurrentView direct)
    { selected = default; this.direct = direct; useDirect = true; }
    internal readonly int Count => useDirect ? direct.Count : selected.Count;
    internal ulong Identity(int index) => useDirect ? direct.Identity(index) : selected.Identity(index);
    internal MetricViewReadStatus TryRead(int index, out MetricValueView value)
    {
        if (!useDirect) return selected.TryRead(index, out value);
        var status = direct.TryRead(index, out value, out var detail);
        if (status != MetricViewReadStatus.Available) throw new MetricDataAreaReadException(detail);
        return status;
    }
}
