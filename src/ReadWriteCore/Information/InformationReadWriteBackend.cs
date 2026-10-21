using System.Collections.Frozen;
using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Information;

public sealed record InformationFieldSelection(FieldReference Field, DeviceInformationDefinition Definition);

/// <summary>Adapts the original information publication without retaining a
/// second value cache. Capture is explicit; the supplied owner commits its result.</summary>
public sealed class InformationReadWriteBackend(Func<DeviceInformationSnapshot> current,
    Func<CancellationToken, ValueTask<DeviceInformationSnapshot>> capture,
    Func<DeviceInformationDefinition, OperationSupport> readSupport,
    Func<ImmutableArray<InformationFieldSelection>, CancellationToken, ValueTask<DeviceInformationSnapshot>>? selectedCapture = null) : IReadWriteBackend
{
    public InformationReadWriteBackend(Func<DeviceInformationSnapshot> current,
        Func<CancellationToken, ValueTask<DeviceInformationSnapshot>> capture,
        Func<DeviceInformationDefinition, OperationSupport> readSupport)
        : this(current, capture, readSupport, null) { }
    /// <summary>Fixed catalog policy over the original Current; omitted means every definition is included.</summary>
    public InformationReadWriteBackend(Func<DeviceInformationSnapshot> current,
        Func<CancellationToken, ValueTask<DeviceInformationSnapshot>> capture,
        Func<DeviceInformationDefinition, OperationSupport> readSupport,
        Func<ImmutableArray<InformationFieldSelection>, CancellationToken, ValueTask<DeviceInformationSnapshot>>? selectedCapture,
        Func<DeviceInformationDefinition, bool> definitionFilter)
        : this(current, capture, readSupport, selectedCapture)
        => this.definitionFilter = definitionFilter ?? throw new ArgumentNullException(nameof(definitionFilter));
    private readonly Func<DeviceInformationDefinition, bool>? definitionFilter;
    private readonly object gate = new();
    private DeviceInformationSnapshot? boundSnapshot;
    private FrozenDictionary<FieldReference, DeviceInformationDefinition> definitions = FrozenDictionary<FieldReference, DeviceInformationDefinition>.Empty;
    private ReadWriteCatalog catalog = new([], []);

    public ReadWriteCatalog Catalog { get { lock (gate) { Refresh(); return catalog; } } }

    public async ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
    {
        await capture(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Catalog;
    }

    public ImmutableArray<InformationFieldSelection> Select(ImmutableArray<FieldReference> fields)
    {
        lock (gate)
        {
            Refresh();
            return [.. fields.Select(field => new InformationFieldSelection(field,
                definitions.TryGetValue(field, out var definition) ? definition : throw new ArgumentException("Unknown information field.", nameof(fields))))];
        }
    }

    public FieldReading ReadCached(FieldReference field)
    {
        DeviceInformationDefinition definition;
        lock (gate) { Refresh(); definition = definitions.GetValueOrDefault(field)
            ?? throw new ArgumentException("Unknown information field.", nameof(field)); }
        return Read(new InformationFieldSelection(field, definition), current());
    }

    public async ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken cancellationToken)
    {
        var selection = Select(fields);
        var snapshot = selectedCapture is null ? await capture(cancellationToken)
            : await selectedCapture(selection, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Read(selection, snapshot);
    }

    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken)
        => ValueTask.FromResult(new FieldWriteResult(field, "unsupported", "field-write-not-bound"));

    public static ImmutableArray<FieldReading> Read(ImmutableArray<InformationFieldSelection> selection, DeviceInformationSnapshot snapshot)
        => [.. selection.Select(item => Read(item, snapshot))];

    private static FieldReading Read(InformationFieldSelection selection, DeviceInformationSnapshot snapshot)
    {
        var actual = snapshot.FindField(selection.Field);
        return actual is null || (actual.ObservedAt ?? snapshot.ObservedAt) is null || actual.Definition != selection.Definition
            ? new(selection.Field, FieldObservationStatus.Unavailable, null, null, selection.Definition.SourceId)
            : InformationFieldProjection.Read(selection.Field.DeviceIdentity, actual, snapshot.ObservedAt);
    }

    private void Refresh()
    {
        var snapshot = current();
        if (ReferenceEquals(snapshot, boundSnapshot)) return;
        var included = snapshot.Devices.Select(device => (Device: device,
            Fields: device.Fields.Where(field => definitionFilter?.Invoke(field.Definition) ?? true).ToArray()))
            .Where(item => item.Fields.Length != 0).ToArray();
        var nextDefinitions = included.SelectMany(item => item.Fields.Select(field =>
            KeyValuePair.Create(new FieldReference(item.Device.DeviceIdentity, field.Definition.Name), field.Definition))).ToFrozenDictionary();
        var devices = included.Select(item => new DeviceDescriptor(item.Device.DeviceIdentity, item.Fields[0].Definition.Resource,
            item.Fields.Select(static field => field.Definition.Vendor).FirstOrDefault(static vendor => vendor != "all"))).ToArray();
        if (nextDefinitions.Count != definitions.Count || nextDefinitions.Any(pair => !definitions.TryGetValue(pair.Key, out var old) || old != pair.Value)
            || !catalog.Devices.SequenceEqual(devices))
        {
            catalog = new(devices, nextDefinitions.Select(pair => new FieldCapability(pair.Key, InformationFieldProjection.Describe(pair.Value),
                readSupport(pair.Value), OperationSupport.Unsupported("field-write-not-bound"), pair.Value.SourceId)));
            definitions = nextDefinitions;
        }
        boundSnapshot = snapshot;
    }
}

