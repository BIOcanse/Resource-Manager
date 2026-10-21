using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using System.Collections.Frozen;
using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.ReadWrite.Fields;

public sealed record MetricFieldSelection(MetricDeviceFieldBinding Binding, MetricSampleRequest CachedRequest);

/// <summary>Original canonical numeric catalog and exact field projection over an explicit owner.
/// It retains catalog metadata only; reads and capture share the supplied owner's current values.
/// Discover is explicit, including the supplied catalog publication and write-support refresh.</summary>
public sealed class MetricReadWriteBackend(Func<HardwareMetricSnapshot?> catalogSnapshot,
    Func<MetricSampleRequest, HardwareMetricSnapshot?> readCurrent,
    Func<MetricSampleRequest, CancellationToken, ValueTask<HardwareMetricSnapshot>> capture,
    Action<HardwareMetricSnapshot> publishCatalog,
    Func<HardwareMetricSnapshot, MetricDeviceFieldBinding, OperationSupport> readSupport,
    Func<MetricDeviceFieldBinding, OperationSupport> writeSupport,
    Func<FieldReference, MetricDeviceFieldBinding, FieldValue, CancellationToken, ValueTask<FieldWriteResult>> write,
    Func<MetricDeviceFieldBinding, FieldWriteContract?>? writeContract = null)
    : IReadWriteBackend
{
    private readonly object catalogGate = new();
    private HardwareMetricSnapshot? boundSnapshot;
    private MetricCatalog current = new(new([], []), FrozenDictionary<FieldReference, MetricFieldSelection>.Empty);

    public ReadWriteCatalog Catalog => CaptureCatalog().Public;

    public async ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
    {
        var snapshot = await capture(MetricSampleRequest.CatalogProbe, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (catalogGate)
        {
            publishCatalog(snapshot);
            boundSnapshot = null;
        }
        return Catalog;
    }

    public FieldReading ReadCached(FieldReference field)
    {
        var binding = Require(CaptureCatalog(), field);
        return Project(field, binding, readCurrent(binding.CachedRequest));
    }

    public async ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields,
        CancellationToken cancellationToken)
    {
        var catalog = CaptureCatalog();
        var bindings = fields.Select(field => Require(catalog, field)).ToArray();
        var request = MetricSampleRequest.ForIds(bindings.Select(static item => item.Binding.EffectiveSampleRequestAddress));
        var snapshot = await capture(request, cancellationToken);
        return Read(fields, bindings, snapshot);
    }

    public MetricFieldSelection[] Select(ImmutableArray<FieldReference> fields)
    {
        var catalog = CaptureCatalog();
        return fields.Select(field => Require(catalog, field)).ToArray();
    }

    public static ImmutableArray<FieldReading> Read(ImmutableArray<FieldReference> fields,
        MetricFieldSelection[] bindings, HardwareMetricSnapshot snapshot)
    {
        return [.. fields.Select((field, index) => Project(field, bindings[index], snapshot))];
    }

    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken)
    {
        var canonicalField = NormalizeField(field);
        return write(canonicalField, Require(CaptureCatalog(), canonicalField).Binding, value, cancellationToken);
    }

    private MetricCatalog CaptureCatalog()
    {
        var snapshot = catalogSnapshot();
        lock (catalogGate)
        {
            if (ReferenceEquals(snapshot, boundSnapshot)) return current;
            var bindings = new Dictionary<FieldReference, MetricFieldSelection>();
            var capabilities = new List<FieldCapability>();
            if (snapshot is not null)
            {
                foreach (var binding in snapshot.FieldBindings.Values)
                {
                    var bound = new MetricFieldSelection(binding,
                        MetricSampleRequest.ForIds([binding.EffectiveSampleRequestAddress]));
                    var read = readSupport(snapshot, binding);
                    var writable = writeSupport(binding);
                    var contract = writeContract?.Invoke(binding) ?? WindowsMetricControlBindings.TryFind(binding)?.DeclaredRange;
                    foreach (var name in MetricFieldRegistry.PublicNamesOf(binding.Field.Name))
                    {
                        var reference = new FieldReference(binding.DeviceIdentity, name);
                        bindings.Add(reference, bound);
                        capabilities.Add(new(reference, new(name, binding.Field.Unit,
                            binding.Field.ValueKind, binding.Field.Meaning), read, writable, binding.SourceId)
                        { WriteContract = contract });
                    }
                }
            }
            var devices = bindings.Values.GroupBy(static item => item.Binding.DeviceIdentity, StringComparer.Ordinal)
                .Select(group => new DeviceDescriptor(group.Key,
                    group.Any(static item => item.Binding.NpuEngine is not null) ? "npu_engine" :
                    group.Any(static item => item.Binding.PdhGpuEngineIdentity is not null)
                        ? "gpu_engine" : group.Any(static item => item.Binding.Field.Code is
                            MetricFieldCode.NvmlFanSpeedPercentOfMax or MetricFieldCode.NvmlFanSpeedRpm or MetricFieldCode.IgclFanSpeedRpm)
                            ? "gpu_fan" : group.First().Binding.Field.Resource.ToString().ToLowerInvariant(),
                    group.Select(static item => item.Binding.Field.Vendor).Where(static vendor => vendor != MetricVendor.All)
                        .Select(static vendor => vendor.ToString().ToLowerInvariant()).FirstOrDefault()));
            current = new(new(devices, capabilities), bindings.ToFrozenDictionary());
            boundSnapshot = snapshot;
            return current;
        }
    }

    public static ImmutableArray<string> DependenciesOf(string source) => source switch
    {
        NativeMetricSnapshotSources.WindowsCpu or NativeMetricSnapshotSources.WindowsMemory
            or NativeMetricSnapshotSources.WindowsVirtualMemory or NativeMetricSnapshotSources.WindowsGpuAdapterOrder
            or NativeMetricSnapshotSources.WindowsStorageSensors or NativeMetricSnapshotSources.WindowsDiskPerformance
            or NativeMetricSnapshotSources.WindowsNvmeHealth
            or NativeMetricSnapshotSources.WindowsAtaSmart or NativeMetricSnapshotSources.WindowsBattery
            or NativeMetricSnapshotSources.WindowsNetworkInterface or NativeMetricSnapshotSources.PdhCpuFrequency
            or NativeMetricSnapshotSources.PdhGpuEngine or NativeMetricSnapshotSources.PdhSystemIo
            or NativeMetricSnapshotSources.WindowsNpuMemory or NativeMetricSnapshotSources.WindowsNpuEngine => [],
        _ => [source]
    };

    private static MetricFieldSelection Require(MetricCatalog catalog, FieldReference reference)
    {
        var canonicalReference = NormalizeField(reference);
        return catalog.Bindings.TryGetValue(canonicalReference, out var binding) ? binding
            : throw new ArgumentException("Unknown device field.", nameof(reference));
    }

    private static FieldReference NormalizeField(FieldReference field)
        => field with { FieldName = MetricFieldRegistry.NormalizePublicName(field.FieldName) };

    private static FieldReading Project(FieldReference field, MetricFieldSelection binding, HardwareMetricSnapshot? snapshot)
    {
        // A canonical request address can be reused by a later workspace. Keep
        // the caller's exact device and value contract rather than redirecting it.
        if (snapshot is not null && (!snapshot.FieldBindings.TryGetValue(binding.Binding.RequestAddress, out var actual)
            || actual.DeviceIdentity != field.DeviceIdentity
            || actual.Field.Name != MetricFieldRegistry.NormalizePublicName(field.FieldName)
            || actual.Field.Unit != binding.Binding.Field.Unit || actual.Field.ValueKind != binding.Binding.Field.ValueKind))
            return new(field, FieldObservationStatus.Unavailable, null, null, binding.Binding.SourceId);
        var observation = snapshot?.Datasets.GetValueOrDefault(binding.Binding.RequestAddress);
        var value = snapshot?.Items.GetValueOrDefault(binding.Binding.RequestAddress);
        var status = observation?.Status ?? SamplingObservationStatus.Warming;
        var current = status == SamplingObservationStatus.Current;
        return new(field, MapStatus(status), current && value is not null
            ? new(binding.Binding.Field.ValueKind == "float64" ? value.NumericValue : null, value.IntegerValue, value.BooleanValue,
                value.BytesValue is { } bytes ? [.. bytes] : null) : null,
            current && observation!.ObservedAtUtcTicks > 0 ? new DateTimeOffset(observation.ObservedAtUtcTicks, TimeSpan.Zero) : null,
            value?.SourceId ?? binding.Binding.SourceId, current ? value?.SampleDurationMilliseconds : null);
    }

    private static FieldObservationStatus MapStatus(SamplingObservationStatus status) => status switch
    {
        SamplingObservationStatus.Current => FieldObservationStatus.Current,
        SamplingObservationStatus.Invalid => FieldObservationStatus.Invalid,
        SamplingObservationStatus.Unavailable => FieldObservationStatus.Unavailable,
        SamplingObservationStatus.NotRequested => FieldObservationStatus.NotRequested,
        SamplingObservationStatus.Unsupported => FieldObservationStatus.Unsupported,
        SamplingObservationStatus.Partial => FieldObservationStatus.Partial,
        SamplingObservationStatus.Warming => FieldObservationStatus.Warming,
        _ => FieldObservationStatus.Unavailable
    };

    private sealed record MetricCatalog(ReadWriteCatalog Public, FrozenDictionary<FieldReference, MetricFieldSelection> Bindings);
}
