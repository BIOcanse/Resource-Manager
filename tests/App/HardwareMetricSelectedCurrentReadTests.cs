using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareMetricSelectedCurrentReadTests(ITestOutputHelper output)
{
    [Fact]
    public void OneHundredSelectedFieldsAllocateForTheirCellsAndNotUnselectedCurrent()
    {
        using var small = new LastSuccessfulHardwareMetricSnapshot();
        using var large = new LastSuccessfulHardwareMetricSnapshot();
        small.Publish(Frame(100));
        large.Publish(Frame(1352));
        var ids = Enumerable.Range(0, 100).Select(static index => $"recorded.{index}").ToImmutableArray();
        // Warm JIT and both read paths before measuring this thread's allocations.
        _ = small.ReadItems(ids); _ = large.ReadItems(ids); _ = large.Read();
        Assert.Equal(100, small.ReadItems(ids).Count);
        Assert.Equal(100, large.ReadItems(ids).Count);
        Assert.Equal(1352, large.Read()!.Items.Count);
        var smallBytes = Allocated(() => small.ReadItems(ids));
        var selectedBytes = Allocated(() => large.ReadItems(ids));
        var wholeBytes = Allocated(() => large.Read());
        output.WriteLine($"Cached reads: selected100/catalog1352={selectedBytes}; selected100/catalog100={smallBytes}; full1352={wholeBytes}.");
        Assert.Equal(0, smallBytes);
        Assert.Equal(0, selectedBytes);
        Assert.Equal(0, wholeBytes);
        Assert.Same(large.ReadItems(ids), large.ReadItems(ids));
        var values = large.ReadItems(ids);
        Assert.Equal(99, values["recorded.99"].NumericValue);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1).AddTicks(73), values["recorded.99"].ObservedAt);
        Assert.DoesNotContain("recorded.100", values.Keys);
    }

    [Fact]
    public void SelectedDatasetReadPreservesFailureAndDoesNotDeliverOtherDatasets()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var frame = Frame(3);
        var at = frame.CapturedAt;
        owner.Publish(frame with
        {
            Datasets = frame.Items.Keys.ToDictionary(static id => id, id => new HardwareMetricDatasetObservation(
                id, SamplingObservationStatus.Current, 1, at.UtcTicks, 1, 1, 1, 1))
        }, MetricSampleRequest.All);
        owner.RecordFailure(MetricSampleRequest.ForIds(["recorded.1"]), at.AddSeconds(1), "recorded-failure");
        var selected = owner.Read(MetricSampleRequest.ForIds(["recorded.0", "recorded.1"]))!;
        Assert.Equal(2, selected.Datasets.Count);
        Assert.Equal(SamplingObservationStatus.Unavailable, selected.Datasets["recorded.1"].Status);
        Assert.Equal(2, selected.Items.Count);
        Assert.Equal(1, selected.Items["recorded.1"].NumericValue);
        Assert.Equal(0, selected.Items["recorded.0"].NumericValue);
        Assert.Equal(2, owner.ReadItems(["recorded.0", "recorded.1"]).Count);
        Assert.Equal(SamplingObservationStatus.Unavailable, owner.ReadItems(["recorded.1"])["recorded.1"].Status);
        Assert.Equal(3, owner.Read()!.Items.Count);
    }

    private static long Allocated(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static HardwareMetricSnapshot Frame(int count)
    {
        var at = DateTimeOffset.UnixEpoch.AddSeconds(1).AddTicks(73);
        var field = MetricFieldRegistry.FindByName("cpu_all_usage_percent")!;
        var bindings = Enumerable.Range(0, count).ToDictionary(index => $"recorded.{index}",
            index => new MetricDeviceFieldBinding($"device-{index}", field, $"recorded.{index}", "recorded-source"));
        var provider = new HardwareSensorProviderState("recorded-source", "current", null);
        return new(at, new("", 0, false, CpuMetricObservationStatus.Unavailable, 0, 0, 0, 0, 0, "",
                new(provider, null, null, null, null)),
            new(0, 0, 0, false), new(0, 0, 0, false), [],
            new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []),
            bindings.ToDictionary(static pair => pair.Key, pair => new MetricReadingValue(pair.Key,
                int.Parse(pair.Key.AsSpan("recorded.".Length)), "%")
            {
                FieldCode = field.Code, FieldName = field.Name, DeviceIdentity = pair.Value.DeviceIdentity,
                SourceId = pair.Value.SourceId, ValueKind = "float64", ObservedAt = at, SampleDurationMilliseconds = 1000
            })) { FieldBindings = bindings };
    }
}
