using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Fields;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class MetricBatchCachedReadTests
{
    [Fact]
    public void HundredFieldsProjectOneSelectedReadWithoutReconstructingASnapshot()
    {
        var catalog = Catalog(100);
        var observedAt = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var items = catalog.FieldBindings.Values.ToDictionary(binding => binding.RequestAddress,
            binding => Item(binding, observedAt));
        var selectedReads = 0;
        var backend = Backend(catalog, _ => throw new InvalidOperationException("Whole snapshot must not be read."), ids =>
        {
            selectedReads++;
            Assert.Equal(100, ids.Length);
            return items;
        });
        var fields = backend.Catalog.Fields.Select(static capability => capability.Field).Reverse().ToImmutableArray();

        var readings = backend.ReadCachedBatch(fields);

        Assert.Equal(1, selectedReads);
        Assert.Equal(fields, readings.Select(static reading => reading.Field));
        Assert.All(readings, reading =>
        {
            Assert.Equal(FieldObservationStatus.Current, reading.Status);
            Assert.Equal(7d, reading.Value?.Number);
            Assert.Equal(observedAt, reading.ObservedAt);
            Assert.Equal(42UL, reading.SampleDurationMilliseconds);
        });
    }

    [Fact]
    public async Task BindingRemovedBetweenDeliveryCatalogAndBatchKeepsTheOtherSelectedField()
    {
        var original = Catalog(2);
        var bindings = original.FieldBindings.Values.ToArray();
        var removed = new FieldReference(bindings[0].DeviceIdentity, bindings[0].Field.Name);
        var retained = new FieldReference(bindings[1].DeviceIdentity, bindings[1].Field.Name);
        var replacement = original with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { [bindings[1].RequestAddress] = bindings[1] } };
        var catalogReads = 0;
        var retireDuringBatch = false;
        var selectedReads = 0;
        var backend = new MetricReadWriteBackend(() => retireDuringBatch && catalogReads++ > 0 ? replacement : original,
            _ => throw new InvalidOperationException("Whole snapshot must not be read."),
            (_, _) => throw new NotSupportedException(), _ => { }, (_, _) => new(true, null, []),
            _ => OperationSupport.Unsupported("read-only"), (_, _, _, _) => throw new NotSupportedException(), readItems: ids =>
            {
                selectedReads++;
                Assert.Equal(bindings[1].RequestAddress, Assert.Single(ids));
                return new Dictionary<string, MetricReadingValue>
                    { [bindings[1].RequestAddress] = Item(bindings[1], DateTimeOffset.UnixEpoch) };
            });
        var delivery = new NativeFieldSubscriptionDelivery();
        using var registration = delivery.Register("race", backend, [removed, retained],
            [new(removed, ["item"], ["publication"], FieldCollectionSourceKind.SourceQuery),
             new(retained, ["item"], ["publication"], FieldCollectionSourceKind.SourceQuery)]);
        retireDuringBatch = true;

        delivery.Deliver<string>([new("race", "item", 100, 1, "request", false)], null);

        await using var reader = registration.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(FieldObservationStatus.Unavailable, reader.Current[0].Status);
        Assert.Equal("recorded-source", reader.Current[0].SourceId);
        Assert.Null(reader.Current[0].Value);
        Assert.Equal(FieldObservationStatus.Current, reader.Current[1].Status);
        Assert.Equal(7d, reader.Current[1].Value?.Number);
        Assert.Equal(1, selectedReads);
        Assert.Throws<ArgumentException>(() => backend.ReadCached(removed));
    }

    [Fact]
    public void SelectedReadPreservesFailureAndRejectsReusedAddressMetadata()
    {
        var catalog = Catalog(1);
        var binding = Assert.Single(catalog.FieldBindings).Value;
        var item = Item(binding, DateTimeOffset.UnixEpoch);
        var backend = Backend(catalog, _ => throw new InvalidOperationException("Whole snapshot must not be read."),
            _ => new Dictionary<string, MetricReadingValue> { [binding.RequestAddress] = item });
        var field = Assert.Single(backend.Catalog.Fields).Field;

        item = item with { Status = SamplingObservationStatus.Unavailable };
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(field).Status);
        Assert.Null(backend.ReadCached(field).Value);
        Assert.Null(backend.ReadCached(field).ObservedAt);

        item = Item(binding, DateTimeOffset.UnixEpoch) with { DeviceIdentity = "replacement-device" };
        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(field).Status);
        Assert.Null(backend.ReadCached(field).Value);
    }

    [Fact]
    public void SnapshotBackedOwnerCombinesSelectedRequestsIntoOneRead()
    {
        var catalog = Catalog(100);
        var snapshot = catalog.CreateSnapshot();
        var reads = 0;
        var backend = Backend(catalog, request =>
        {
            reads++;
            Assert.Equal(100, request.Ids.Count);
            return snapshot;
        });

        Assert.Equal(100, backend.ReadCachedBatch([.. backend.Catalog.Fields.Select(static capability => capability.Field)]).Length);
        Assert.Equal(1, reads);
    }

    private static HardwareMetricCatalog Catalog(int count)
    {
        var definition = MetricFieldRegistry.Definitions.First(static field => field.ValueKind == "float64");
        var bindings = Enumerable.Range(0, count).ToDictionary(index => $"recorded-{index}",
            index => new MetricDeviceFieldBinding($"device-{index}", definition, $"recorded-{index}", "recorded-source"));
        return new(new("recorded", 0, "recorded"), [], new Dictionary<string, HardwareMetricItemDescriptor>(), bindings, 1, 1, 1);
    }

    private static MetricReadingValue Item(MetricDeviceFieldBinding binding, DateTimeOffset observedAt)
        => new(binding.RequestAddress, 7, binding.Field.Unit)
        {
            FieldCode = binding.Field.Code, FieldName = binding.Field.Name, DeviceIdentity = binding.DeviceIdentity,
            ValueKind = binding.Field.ValueKind, SourceId = binding.SourceId, ObservedAt = observedAt,
            SampleDurationMilliseconds = 42
        };

    private static MetricReadWriteBackend Backend(HardwareMetricCatalog catalog,
        Func<MetricSampleRequest, HardwareMetricSnapshot?> read,
        Func<ImmutableArray<string>, IReadOnlyDictionary<string, MetricReadingValue>>? readItems = null)
        => new(() => catalog, read, (_, _) => throw new NotSupportedException(), _ => { },
            (_, _) => new(true, null, []), _ => OperationSupport.Unsupported("read-only"),
            (_, _, _, _) => throw new NotSupportedException(), readItems: readItems);
}
