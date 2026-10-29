using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Native.Pdh;
using System.Collections.Immutable;

namespace ResourceManager.App.Domain.Metrics;

public sealed record MetricDeviceFieldBinding(
    string DeviceIdentity,
    MetricFieldDefinition Field,
    string RequestAddress,
    string SourceId)
{
    private readonly IReadOnlyList<string> requestAliases = ImmutableArray<string>.Empty;
    public IReadOnlyList<string> RequestAliases { get => requestAliases; init => requestAliases = value.ToImmutableArray(); }
    private readonly IReadOnlyList<string> selectedRequestAliases = ImmutableArray<string>.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> SelectedRequestAliases { get => selectedRequestAliases; init => selectedRequestAliases = value.ToImmutableArray(); }
    // Some public fields are projections of a source-planner trigger. The
    // trigger is not a public value address and must remain out of payloads.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SampleRequestAddress { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public NativePdhGpuEngineIdentity? PdhGpuEngineIdentity { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveSampleRequestAddress => SampleRequestAddress ?? RequestAddress;
    // Execution identity comes from the bound native scope, never a field-name
    // parser or a potentially changed display ordinal.
    [System.Text.Json.Serialization.JsonIgnore]
    public ulong? AdapterKey { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public WindowsMetricControlTarget? ControlTarget { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public ResourceManager.ReadWrite.Windows.Devices.Npu.NpuEngineCatalogIdentity? NpuEngine { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public MetricDeviceDescriptor Device { get; init; } = MetricDeviceDescriptor.Unspecified;
}

public sealed record DeviceMetricFieldValue(
    string Name,
    string Unit,
    double? NumericValue,
    MetricInteger? IntegerValue,
    SamplingObservationStatus Status,
    DateTimeOffset? ObservedAt,
    string SourceId)
{
    public string ValueKind { get; init; } = "float64";
    public byte[]? BytesValue { get; init; }
    public bool? BooleanValue { get; init; }
    public ulong? SampleDurationMilliseconds { get; init; }
    public MetricReadingSource? Reading { get; init; }
}

public sealed record DeviceMetricFieldSnapshot(
    string DeviceIdentity,
    IReadOnlyList<DeviceMetricFieldValue> Fields)
{
    [System.Text.Json.Serialization.JsonPropertyName("device")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public MetricDeviceDescriptor Device { get; init; } = MetricDeviceDescriptor.Unspecified;
}

public sealed record MetricDeviceFieldCatalog(
    string DeviceIdentity,
    IReadOnlyList<MetricFieldDefinition> Fields)
{
    [System.Text.Json.Serialization.JsonPropertyName("device")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public MetricDeviceDescriptor Device { get; init; } = MetricDeviceDescriptor.Unspecified;
}

public static class MetricDeviceFields
{
    public static IReadOnlyList<MetricDeviceFieldCatalog> Catalog(HardwareMetricSnapshot? snapshot)
    {
        if (snapshot is null) return [];
        var descriptors = ResolveDeviceDescriptors(snapshot);
        return snapshot.FieldBindings.Values
            .GroupBy(static binding => binding.DeviceIdentity, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new MetricDeviceFieldCatalog(group.Key, group
                .Select(static binding => binding.Field)
                .OrderBy(static field => field.Name, StringComparer.Ordinal).ToArray())
            { Device = descriptors[group.Key] }).ToArray();
    }

    // Returns public field addresses only. A projected field (a discovered GPU engine) is mapped to its
    // internal planner trigger at the native planning boundary (WindowsHardwareMetricCapture.CreateCollectionRequest);
    // the host subscribes, publishes and projects by public dataset ids, which never contain that trigger.
    public static MetricSampleRequest CreateRequest(
        HardwareMetricSnapshot? catalog, string? deviceIdentity, IEnumerable<string> fields)
    {
        if (deviceIdentity is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentity);
        ArgumentNullException.ThrowIfNull(fields);
        var names = fields.Select(MetricFieldRegistry.NormalizePublicName).ToHashSet(StringComparer.Ordinal);
        if (catalog is null) throw new InvalidOperationException("metric-field-catalog-unavailable");

        if (deviceIdentity is null)
        {
            if (names.Count == 0 || names.Any(static name => MetricFieldRegistry.FindByName(name) is null))
                throw new ArgumentException("metric-field-name-unknown", nameof(fields));
            return SelectBound(catalog, null, names);
        }

        var available = catalog.FieldBindings.Values.Where(binding => binding.DeviceIdentity == deviceIdentity).ToArray();
        if (available.Length == 0) throw new ArgumentException("metric-field-device-unknown", nameof(deviceIdentity));
        if (names.Count == 0 || names.Except(available.Select(static binding => binding.Field.Name), StringComparer.Ordinal).Any())
            throw new ArgumentException("metric-field-name-unknown", nameof(fields));
        return SelectBound(catalog, deviceIdentity, names);
    }

    // The addresses bound to an already validated selection in this catalog. Empty while the device or
    // fields are absent, so a long-lived subscription can re-expand it after each catalog change.
    public static MetricSampleRequest SelectBound(
        HardwareMetricSnapshot catalog, string? deviceIdentity, IReadOnlySet<string> normalizedNames)
        => MetricSampleRequest.ForIds(catalog.FieldBindings.Values
            .Where(binding => (deviceIdentity is null || binding.DeviceIdentity == deviceIdentity)
                && normalizedNames.Contains(binding.Field.Name))
            .Select(static binding => binding.RequestAddress));

    public static IReadOnlyList<DeviceMetricFieldSnapshot> Read(
        HardwareMetricSnapshot snapshot, string? deviceIdentity = null, IReadOnlySet<string>? fields = null)
    {
        var descriptors = ResolveDeviceDescriptors(snapshot);
        var selectedFields = fields?.Select(MetricFieldRegistry.NormalizePublicName)
            .ToHashSet(StringComparer.Ordinal);
        return snapshot.FieldBindings.Values
            .Where(binding => deviceIdentity is null || binding.DeviceIdentity == deviceIdentity)
            .Where(binding => selectedFields is null || selectedFields.Contains(binding.Field.Name))
            .GroupBy(static binding => binding.DeviceIdentity, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var bindings = group.ToArray();
                return new DeviceMetricFieldSnapshot(group.Key, bindings
                    .OrderBy(static binding => binding.Field.Name, StringComparer.Ordinal)
                    .Select(binding =>
                {
                    var value = snapshot.Items.GetValueOrDefault(binding.RequestAddress);
                    var observation = snapshot.Datasets.GetValueOrDefault(binding.RequestAddress);
                    var status = observation?.Status ?? SamplingObservationStatus.NotRequested;
                    var current = status == SamplingObservationStatus.Current;
                    return new DeviceMetricFieldValue(binding.Field.Name, binding.Field.Unit,
                        current ? value?.NumericValue : null, current ? value?.IntegerValue : null,
                        status, current && observation!.ObservedAtUtcTicks > 0
                            ? new DateTimeOffset(observation.ObservedAtUtcTicks, TimeSpan.Zero) : null,
                        value?.SourceId ?? binding.SourceId)
                    {
                        ValueKind = binding.Field.ValueKind,
                        BytesValue = current ? value?.BytesValue : null,
                        BooleanValue = current ? value?.BooleanValue : null,
                        SampleDurationMilliseconds = current ? value?.SampleDurationMilliseconds : null,
                        Reading = binding.Field.Reading
                    };
                }).ToArray())
                { Device = descriptors[group.Key] };
            }).ToArray();
    }

    private static Dictionary<string, MetricDeviceDescriptor> ResolveDeviceDescriptors(HardwareMetricSnapshot snapshot)
    {
        var devices = snapshot.FieldBindings.Values
            .GroupBy(static binding => binding.DeviceIdentity, StringComparer.Ordinal)
            .Select(static group => (DeviceIdentity: group.Key, Device: group.First().Device))
            .GroupBy(static row => row.Device.Kind, StringComparer.Ordinal);
        var result = new Dictionary<string, MetricDeviceDescriptor>(StringComparer.Ordinal);
        foreach (var kind in devices)
        {
            foreach (var row in kind.OrderBy(static row => row.Device.Ordinal)
                         .ThenBy(static row => row.DeviceIdentity, StringComparer.Ordinal)
                         .Select((row, ordinal) => (row.DeviceIdentity, Device: row.Device with { Ordinal = ordinal })))
                result.Add(row.DeviceIdentity, row.Device);
        }
        return result;
    }
}
