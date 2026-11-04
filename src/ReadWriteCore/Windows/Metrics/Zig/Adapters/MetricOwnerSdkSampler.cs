using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Adapters;

/// <summary>Inactive, borrowed original SDK bridge. Selection and calculation belong to native.</summary>
public sealed partial class MetricOwnerSdkSampler : IMetricOwnerDiscoverySampler
{
    private readonly WindowsHardwareMetricResources resources;
    // Original immutable SDK bindings stay caller-owned until topology replacement
    // or owner stop. These are references, not a duplicated catalog/value bank.
    private readonly IReadOnlyList<MetricOwnerSdkSourceBinding> sources;
    private readonly IReadOnlyList<MetricOwnerSdkScope> scopes;
    private readonly MetricOwnerSdkTopology topology;
    private readonly SemaphoreSlim sampling = new(1, 1);
    public int ActiveCalls => 1 - sampling.CurrentCount;
    public const uint SdkUnavailableReason = 0x53444b01;
    public const uint MissingOperandReason = 0x53444b02;
    private const uint Complete = 1, Unavailable = 3, Unsupported = 4;

    public MetricOwnerSdkSampler(WindowsHardwareMetricResources resources,
        IReadOnlyList<MetricOwnerSdkSourceBinding> sources, IReadOnlyList<MetricOwnerSdkScope> scopes,
        MetricOwnerSdkTopology topology)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.topology = topology ?? throw new ArgumentNullException(nameof(topology));
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        var i = 0;
        while (i < sources.Count)
        {
            var binding = sources[i] ?? throw Contract();
            if (binding.NativeSourceId == 0 || !Enum.IsDefined(binding.Resource)) throw Contract();
            var j = 0; while (j < i) { if (sources[j++].NativeSourceId == binding.NativeSourceId) throw Contract(); }
            i++;
        }
        i = 0; var staticScopes = 0;
        while (i < scopes.Count)
        {
            var scope = scopes[i] ?? throw Contract();
            if (scope.DeviceKey is null) throw Contract();
            ValidateUtf16(scope.DeviceKey);
            if (scope.NativeScopeIndex == uint.MaxValue && ++staticScopes > 4) throw Contract();
            if (i != 0)
            {
                var prior = scopes[i - 1];
                if (prior.NativeScopeIndex > scope.NativeScopeIndex || (prior.NativeScopeIndex == scope.NativeScopeIndex
                    && scope.NativeScopeIndex != uint.MaxValue)) throw Contract();
                if (scope.NativeScopeIndex == uint.MaxValue)
                {
                    var j = i - 1;
                    while (j >= 0 && scopes[j].NativeScopeIndex == uint.MaxValue)
                    { if (scopes[j--].DeviceKey == scope.DeviceKey) throw Contract(); }
                }
            }
            i++;
        }
    }

    public async ValueTask SampleAsync(MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raw); ArgumentNullException.ThrowIfNull(facts);
        if (!sampling.Wait(0)) throw new InvalidOperationException("A borrowed SDK sampler cannot overlap rounds.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SdkNative.Validate(raw, facts);
            ValidateRawInputs(facts);
            // Validate immutable dispatch before any external acquisition. No old catalog lookup.
            var checkSource = 0;
            while (checkSource < facts.SourceCount)
            {
                var id = facts.SourceId(checkSource);
                if (!facts.HasSdkGrouping || SdkNative.SourceHasRows(facts, checkSource) || HasRawInput(facts, id)) _ = SourceBinding(id);
                var prior = 0;
                while (prior < checkSource) { if (facts.SourceId(prior++) == id) throw Contract(); }
                checkSource++;
            }
            var row = 0;
            while (row < raw.Count)
            {
                if (facts.HasSdkGrouping && SdkNative.Excluded(facts, row)) { row++; continue; }
                var source = 0;
                while (source < facts.SourceCount && facts.SourceId(source) != raw.SourceId(row)) source++;
                if (source == facts.SourceCount) throw Contract();
                var scope = Scope(facts, row);
                if (facts.HasSdkGrouping) ValidateDisplay(facts, row, scope.DisplayIndex);
                row++;
            }
            var sourceRow = 0;
            while (sourceRow < facts.SourceCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceId = facts.SourceId(sourceRow);
                if (facts.HasSdkGrouping && !SdkNative.SourceHasRows(facts, sourceRow) && !HasRawInput(facts, sourceId))
                {
                    // Dataset-only sources are completed by their admitted
                    // inventory callback. Never overwrite that source's facts.
                    if (TrySourceBinding(sourceId, out _)) facts.CompleteSource(sourceRow, 5, false, Now());
                    sourceRow++; continue;
                }
                var binding = SourceBinding(sourceId);
                using var borrow = new SdkRequestScope(raw, facts);
                var rows = new SourceRows(borrow, this, sourceId);
                if (rows.Count == 0 && !HasRawInput(facts, sourceId))
                {
                    // No raw SDK work was issued, including a dataset-only native operation.
                    facts.CompleteSource(sourceRow++, 5, false, Now()); continue;
                }
                uint status;
                try { status = await ReadSourceAsync(binding.Resource, rows, raw, facts, sourceRow, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not (OperationCanceledException or MetricOwnerException))
                {
                    Each(rows, r => raw.WriteUnavailable(r, MetricOwnerObservationStatus.Unavailable, SdkUnavailableReason));
                    FailRawInput(facts, sourceId);
                    status = Unavailable;
                }
                if (status == Unsupported || (status == Unavailable && binding.Resource != MetricOwnerSdkSource.PdhGpu))
                    Each(rows, r => raw.WriteUnavailable(r, status == Unsupported ? MetricOwnerObservationStatus.Unsupported
                        : MetricOwnerObservationStatus.Unavailable, status == Unavailable ? SdkUnavailableReason : 0));
                facts.CompleteSource(sourceRow++, status, binding.ApplyUnavailableCooldown, Now());
            }
        }
        finally { facts.ReleaseSdkGrouping(); sampling.Release(); }
    }

    public async ValueTask DrainAsync()
    {
        await sampling.WaitAsync().ConfigureAwait(false);
        sampling.Release();
    }
    [RuntimeRead(ReadVolatilityKind.EventDriven, "SDK topology replaced or owner stopped")]
    private MetricOwnerSdkScope Scope(MetricDiscoveryRoundFacts facts, int row)
    {
        var target = facts.ScopeIndex(row); var low = 0; var high = scopes.Count;
        if (target < scopes.Count && scopes[(int)target].NativeScopeIndex == target
            && ExactUtf8(scopes[(int)target].DeviceKey, facts.DeviceKey(row))) return scopes[(int)target];
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (scopes[middle].NativeScopeIndex < target) low = middle + 1; else high = middle;
        }
        while (low < scopes.Count && scopes[low].NativeScopeIndex == target)
        { var scope = scopes[low++]; if (ExactUtf8(scope.DeviceKey, facts.DeviceKey(row))) return scope; }
        throw Contract();
    }
    [RuntimeRead(ReadVolatilityKind.EventDriven, "SDK topology replaced or owner stopped")]
    private MetricOwnerSdkSourceBinding SourceBinding(ulong id)
        => TrySourceBinding(id, out var binding) ? binding! : throw Contract();
    [RuntimeRead(ReadVolatilityKind.EventDriven, "SDK topology replaced or owner stopped")]
    private bool TrySourceBinding(ulong id, out MetricOwnerSdkSourceBinding? binding)
    { var i = 0; while (i < sources.Count) { var candidate = sources[i++]; if (candidate.NativeSourceId == id) { binding = candidate; return true; } } binding = null; return false; }
    private static MetricFieldCode Field(MetricDiscoveryRoundFacts facts, int row) => (MetricFieldCode)facts.FieldCode(row);
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static MetricOwnerException Contract() => new(new() { Code = MetricOwnerCode.InvalidContract, Family = MetricOwnerFamily.Binding });
    private static void Missing(MetricOwnerRawRound raw, int row, uint reason = 0)
        => raw.WriteUnavailable(row, MetricOwnerObservationStatus.Unavailable, reason);
    private static void Number(MetricOwnerRawRound raw, int row, double? value, long at)
    {
        if (value is { } v) raw.WriteScalar(row, MetricOwnerValueKind.Float64, BitConverter.DoubleToUInt64Bits(v), 0, at);
        else Missing(raw, row);
    }
    private static void Integer(MetricOwnerRawRound raw, int row, ulong? value, long at)
    {
        if (value is { } v) raw.WriteScalar(row, MetricOwnerValueKind.Unsigned64, v, 0, at);
        else Missing(raw, row);
    }
    private static void Boolean(MetricOwnerRawRound raw, int row, bool? value, long at)
    {
        if (value is { } v) raw.WriteScalar(row, MetricOwnerValueKind.Boolean, v ? 1UL : 0UL, 0, at);
        else Missing(raw, row);
    }
    private static bool Has(SourceRows rows, MetricDiscoveryRoundFacts facts, params ReadOnlySpan<MetricFieldCode> codes)
    {
        using var e = rows.GetEnumerator();
        while (e.MoveNext())
        {
            var field = Field(facts, e.Current); var i = 0;
            while (i < codes.Length) if (codes[i++] == field) return true;
        }
        return false;
    }
    private static IReadOnlySet<MetricOwnerSdkScope> SelectedScopes(SourceRows rows, MetricDiscoveryRoundFacts facts)
        => SdkSetView(rows, rows.Select(row => rows.Scope(row)).Where(static x => x.CanSample));
    private static IReadOnlyDictionary<string, IReadOnlySet<MetricFieldCode>> ByKey(SourceRows rows, MetricDiscoveryRoundFacts facts)
        => SdkMapView(rows, SdkDeviceKeys(rows), key => SdkCodes(rows.ForKey(key)));
    private static IReadOnlyDictionary<int, IReadOnlySet<MetricFieldCode>> ByIndex(SourceRows rows, MetricDiscoveryRoundFacts facts)
        => SdkMapView(rows, SdkIndexes(rows), index => SdkCodes(rows.ForIndex(index)));
    private static void Each(SourceRows rows, Action<int> write)
    { using var e = rows.GetEnumerator(); while (e.MoveNext()) write(e.Current); }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    private async ValueTask<uint> ReadSourceAsync(MetricOwnerSdkSource source, SourceRows rows,
        MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts, int sourceRow, CancellationToken token)
    {
        // Unavailable directories are acquisition facts, never permission to reuse stale handles.
        Each(rows, row => { if (!Scope(facts, row).CanSample) Missing(raw, row); });
        rows = rows.Filter(static (r, row) => r.Scope(row).CanSample);
        if (rows.Count == 0 && !HasRawInput(facts, rows.source)) return Unavailable;
        switch (source)
        {
            case MetricOwnerSdkSource.Cpu: return ReadCpu(rows, raw, facts);
            case MetricOwnerSdkSource.Memory: return ReadMemory(rows, raw, facts);
            case MetricOwnerSdkSource.VirtualMemory: return ReadVirtualMemory(rows, raw, facts);
            case MetricOwnerSdkSource.CpuFrequency: return ReadFrequency(rows, raw, facts);
            case MetricOwnerSdkSource.PdhGpu: return ReadPdhGpu(rows, raw, facts);
            case MetricOwnerSdkSource.Nvml: return await ReadNvmlAsync(rows, raw, facts, token).ConfigureAwait(false);
            case MetricOwnerSdkSource.Nvapi: return await ReadNvapiAsync(rows, raw, facts, token).ConfigureAwait(false);
            case MetricOwnerSdkSource.Adlx: return await ReadAdlxAsync(rows, raw, facts, token).ConfigureAwait(false);
            case MetricOwnerSdkSource.Igcl: return ReadIgcl(rows, raw, facts, token);
            case MetricOwnerSdkSource.IntelMsr: return ReadIntelMsr(rows, raw, facts);
            case MetricOwnerSdkSource.AmdSmu: return ReadSmu(rows, raw, facts);
            case MetricOwnerSdkSource.AmdCcd: return ReadCcd(rows, raw, facts);
            case MetricOwnerSdkSource.Network: return ReadNetwork(rows, raw, facts);
            case MetricOwnerSdkSource.Battery: return ReadBattery(rows, raw, facts);
            case MetricOwnerSdkSource.NpuEngine: return ReadNpuEngine(rows, raw, facts, sourceRow, token);
            case MetricOwnerSdkSource.NpuMemory: return ReadNpuMemory(rows, raw, facts, token);
            case MetricOwnerSdkSource.Storage:
            case MetricOwnerSdkSource.Nvme:
            case MetricOwnerSdkSource.Ata: return ReadStorage(source, rows, raw, facts);
            case MetricOwnerSdkSource.DiskPerformance: return await ReadDiskAsync(rows, raw, facts, token).ConfigureAwait(false);
            case MetricOwnerSdkSource.HardwareMonitor: return ReadHardwareMonitor(rows, raw, facts);
            case MetricOwnerSdkSource.NotebookFan: return ReadNotebook(rows, raw, facts);
            case MetricOwnerSdkSource.SystemIo: return ReadSystemIo(rows, raw, facts);
            case MetricOwnerSdkSource.GpuInventory: return ReadGpuInventory(facts, sourceRow);
            default: throw Contract();
        }
    }
}
