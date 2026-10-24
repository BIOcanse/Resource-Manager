using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class NumaMemoryInformationCollectorTests
{
    [Fact]
    public void NativeCommitAndWireKeepUInt64ExactAndFailuresClearOnlyDependentFields()
    {
        const ulong exact = 9_007_199_254_740_993UL;
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"numa-memory-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), numaNodes: [new(0), new(9)]);
        var now = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, now);
        var bindings = catalog.MetricBindings.Values.Where(static binding => binding.SourceId == NativeMetricSnapshotSourceCatalog.WindowsMemory).ToArray();
        Assert.Equal(8, bindings.Length);
        var requested = bindings.Select(static binding => binding.MetricHandle).ToArray();
        var modes = catalog.Sources.Select(static source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle, 1,
            NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var global = new MemoryMetrics(10, 20, 50, true, "fixture") { AvailableBytes = 10 };
        var first = Publish(1, global, new Dictionary<string, ulong> { ["windows-numa-node|0"] = exact, ["windows-numa-node|9"] = ulong.MaxValue },
            new Dictionary<string, ulong> { ["windows-numa-node|0"] = 0, ["windows-numa-node|9"] = uint.MaxValue });
        AssertNode(first, "windows-numa-node|0", "9007199254740993");
        AssertNode(first, "windows-numa-node|9", ulong.MaxValue.ToString());
        AssertCapacityNode(first, "windows-numa-node|0", "0");
        AssertCapacityNode(first, "windows-numa-node|9", uint.MaxValue.ToString());
        var text = JsonSerializer.Serialize(MetricDeviceFields.Read(first, "windows-numa-node|0"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"integerValue\":\"9007199254740993\"", text);
        Assert.True(first.Memory.IsUsageAvailable);
        var partial = Publish(2, null, new Dictionary<string, ulong> { ["windows-numa-node|9"] = 0 },
            new Dictionary<string, ulong> { ["windows-numa-node|9"] = 0 });
        AssertNode(partial, "windows-numa-node|0", null);
        AssertNode(partial, "windows-numa-node|9", "0");
        AssertCapacityNode(partial, "windows-numa-node|0", null);
        AssertCapacityNode(partial, "windows-numa-node|9", "0");
        Assert.Equal(SamplingObservationStatus.Unavailable, partial.Memory.ObservationStatus);
        Assert.All(Assert.Single(MetricDeviceFields.Read(partial, "static|memory")).Fields, field =>
        { Assert.NotEqual(SamplingObservationStatus.Current, field.Status); Assert.Null(field.NumericValue); Assert.Null(field.IntegerValue); });
        var recovered = Publish(3, global, new Dictionary<string, ulong> { ["windows-numa-node|0"] = exact },
            new Dictionary<string, ulong> { ["windows-numa-node|0"] = uint.MaxValue });
        AssertNode(recovered, "windows-numa-node|0", "9007199254740993");
        AssertNode(recovered, "windows-numa-node|9", null);
        AssertCapacityNode(recovered, "windows-numa-node|0", uint.MaxValue.ToString());
        AssertCapacityNode(recovered, "windows-numa-node|9", null);
        Assert.True(recovered.Memory.IsUsageAvailable);
        var cache = new LastSuccessfulHardwareMetricSnapshot();
        var request = MetricSampleRequest.ForIds(bindings.Where(static binding => binding.FieldCode is MetricFieldCode.NumaNodeAvailableBytes or MetricFieldCode.NumaNodeCapacityMiB)
            .Select(static binding => binding.MetricId));
        cache.Publish(recovered, request);
        cache.RecordFailure(request, now.AddSeconds(4), "node-read-failed");
        AssertNode(cache.Read()!, "windows-numa-node|0", null);
        AssertCapacityNode(cache.Read()!, "windows-numa-node|0", null);
        cache.Publish(recovered, request);
        AssertNode(cache.Read()!, "windows-numa-node|0", "9007199254740993");
        AssertCapacityNode(cache.Read()!, "windows-numa-node|0", uint.MaxValue.ToString());

        HardwareMetricSnapshot Publish(ulong sequence, MemoryMetrics? memory, IReadOnlyDictionary<string, ulong> values,
            IReadOnlyDictionary<string, ulong> capacityMiBValues)
        {
            var at = now.AddSeconds(sequence);
            var plan = workspace.PlanCollection(requested, modes, false, at);
            var source = Assert.Single(plan.Sources);
            var completion = WindowsHardwareMetricSampler.CreateMemoryCompletion(
                plan, source, new(source.SourceHandle, 1, sequence), at, memory, values, capacityMiBValues);
            Assert.Equal(NativeMetricSnapshotSourceStatus.Complete, completion.Status);
            workspace.CompleteSource(plan, completion, at);
            return NativeMetricSnapshotCommittedProjection.Create(workspace.ReadCommitted(), "CPU", "RAM", 1);
        }
    }

    private static void AssertNode(HardwareMetricSnapshot snapshot, string device, string? integer)
    {
        var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, device)).Fields, static field => field.Name == FieldName);
        Assert.Equal(FieldName, field.Name); Assert.Equal("B", field.Unit); Assert.Equal("unsigned64", field.ValueKind);
        Assert.Equal(integer is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, field.Status);
        Assert.Null(field.NumericValue); Assert.Equal(integer, field.IntegerValue);
    }

    private static void AssertCapacityNode(HardwareMetricSnapshot snapshot, string device, string? integer)
    {
        var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, device)).Fields, static field => field.Name == CapacityFieldName);
        Assert.Equal(CapacityFieldName, field.Name); Assert.Equal("MiB", field.Unit); Assert.Equal("unsigned64", field.ValueKind);
        Assert.Equal(integer is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, field.Status);
        Assert.Null(field.NumericValue); Assert.Equal(integer, field.IntegerValue);
    }
}
