using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareMetricNativeCurrentRetentionTests
{
    private static readonly string[] DatasetIds = ["cpu.usage", "memory.usage", "gpu.0.usage", "gpu.0.vram"];

    [Fact]
    public void RealLedgerLoanRemainsAccountedAcrossRepeatedNativeCommitsAndClosesOnDispose()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var configuration = WindowsMetricConfiguration.Default.Configuration;
        using var session = NativeMetricSnapshotSession.CreateCurrent(in configuration,
            (length, alignment) => MetricSnapshotScratch.Create(ledger.CreateScratch, length, alignment));
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        ulong registered = 0;
        for (var round = 1; round <= 12; round++)
        {
            var cells = Enumerable.Range(1, 512).Select(index => new NativeMetricCurrentValue
            {
                DatasetHandle = 1, CellHandle = (ulong)index, ValueKind = (uint)NativeMetricSnapshotValueKind.Unsigned64,
                ValueBits = (ulong)(round + index), Flags = 1
            }).ToArray();
            session.CommitCurrent([1], [], cells, []);
            var bytes = ledger.ReadTable().RegisteredBytes;
            Assert.True(bytes > 0, "The resident current table must be a real ledger allocation.");
            if (round == 1) registered = bytes;
            Assert.Equal(registered, bytes);
        }
        Assert.Equal(524UL, session.ReadCurrent().Values[^1].ValueBits);
        session.Dispose();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void ManyCapturesWithoutAReaderKeepNoManagedCaptureOrValueObjects()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var run = owner.OpenHostedRun();
        Assert.True(owner.TryCaptureHostedTicket(run, new Permit(), 1, 1, out var ticket));
        var captures = new List<WeakReference>();
        var readings = new List<WeakReference>();
        for (var round = 1; round <= 80; round++) PublishRound(owner, ticket, round, captures, readings);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(captures, static value => Assert.False(value.IsAlive));
        Assert.All(readings, static value => Assert.False(value.IsAlive));

        // Count the actual retained graph, not allocation totals and not just
        // fields named "snapshot". Traversal is iterative and bounded.
        var graph = OwnedObjects(owner);
        Assert.DoesNotContain(graph, static value => value is HardwareMetricSnapshot or MetricReadingValue or MetricValue);
        Assert.Equal(1352, graph.OfType<MetricDeviceFieldBinding>().Count());
        Assert.Equal(4, owner.Read()!.Datasets.Count);
        Assert.Equal(4, owner.Read()!.Items.Count);
    }

    [Fact]
    public void TypedPublicationPreservesExactValuesTimesAndIndependentPartialRounds()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.Publish(Frame(1), MetricSampleRequest.ForIds(DatasetIds));
        var first = owner.Read()!;
        var changed = Frame(2);
        owner.Publish(changed, MetricSampleRequest.ForIds(["cpu.usage"]));
        owner.RecordFailure(MetricSampleRequest.ForIds(["memory.usage"]), changed.CapturedAt.AddTicks(7), "recorded-failure");
        var current = owner.Read(DateTimeOffset.MaxValue)!;
        Assert.Equal(2, current.Items["cpu.usage"].NumericValue);
        Assert.Equal(first.Items["gpu.0.vram"], current.Items["gpu.0.vram"]);
        Assert.Equal(new MetricInteger(ulong.MaxValue, ulong.MaxValue), current.Items["gpu.0.vram"].IntegerValue);
        Assert.Equal(first.Items["gpu.0.usage"].ObservedAt, current.Items["gpu.0.usage"].ObservedAt);
        Assert.DoesNotContain("memory.usage", current.Items.Keys);
        Assert.Equal(SamplingObservationStatus.Unavailable, current.Datasets["memory.usage"].Status);
        Assert.Equal(changed.CapturedAt.AddTicks(7).UtcTicks, current.Datasets["memory.usage"].LastAttemptAtUtcTicks);
        Assert.Equal(first.Datasets["gpu.0.vram"], current.Datasets["gpu.0.vram"]);
    }

    [Fact]
    public void InventoryAndGpuAndItemCellsRemainSeparateAtTheDeclared64AdapterLimit()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var frame = Frame(1);
        var adapters = Enumerable.Range(0, 64).Select(index => new SchedulingGpuAdapterObservation(index,
            (ulong)index + 1, SchedulingGpuCapabilityMask.Usage | SchedulingGpuCapabilityMask.DedicatedMemory,
            SchedulingGpuMetricMask.None, SamplingObservationStatus.NotRequested, SamplingObservationStatus.NotRequested,
            0, 0, 0, 1, frame.CapturedAt.UtcTicks)).ToArray();
        var direct = frame with
        {
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(),
            GpuInventory = new(SamplingObservationStatus.Current, 1, frame.CapturedAt.UtcTicks, 64, 0, 0, 1, adapters),
            Gpus = Enumerable.Range(0, 64).Select(index => frame.Gpus[0] with { Index = index }).ToArray()
        };
        owner.Publish(direct);
        var read = owner.Read()!;
        Assert.Equal(64, read.GpuInventory.Adapters.Count);
        Assert.Equal(64, read.Gpus.Count);
        Assert.Equal(direct.Items["gpu.0.vram"], read.Items["gpu.0.vram"]);
        Assert.Equal(adapters[^1], read.GpuInventory.Adapters[^1]);
    }

    [Fact]
    public void FailedTypedSlotKeepsItsUnsupportedFactAndExactAttemptTime()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var frame = Frame(1);
        var attempt = frame.CapturedAt.AddTicks(73);
        frame = frame with { Datasets = frame.Datasets.ToDictionary(static pair => pair.Key,
            pair => pair.Key == "cpu.usage" ? pair.Value with
            { Status = SamplingObservationStatus.Unsupported, LastAttemptAtUtcTicks = attempt.UtcTicks } : pair.Value) };
        owner.Publish(frame, MetricSampleRequest.ForIds(["cpu.usage"]));
        var current = owner.Read()!;
        Assert.Equal(SamplingObservationStatus.Unsupported, current.Datasets["cpu.usage"].Status);
        Assert.Equal(attempt.UtcTicks, current.Datasets["cpu.usage"].LastAttemptAtUtcTicks);
        Assert.DoesNotContain("cpu.usage", current.Items.Keys);
    }

    [Fact]
    public void ExplicitFailedFieldResponseKeepsItsCatalogTypeAndIdentityWithoutAnItem()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var frame = Frame(1);
        var field = frame.FieldBindings["recorded.0"];
        owner.RecordFailure(MetricSampleRequest.ForIds([field.RequestAddress]), frame.CapturedAt, "not-available");
        // Publish the real declaration together with the failed attempted round.
        frame = frame with { Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
        {
            [field.RequestAddress] = new(field.RequestAddress, SamplingObservationStatus.Unsupported, 0, 0, 1, 1, 1, 1)
        } };
        owner.Publish(frame, MetricSampleRequest.ForIds([field.RequestAddress]));
        var wire = MetricSnapshotWireSnapshot.From(owner.Read()!, MetricSampleRequest.ForIds([field.RequestAddress]));
        var value = wire.Items[field.RequestAddress];
        Assert.Null(value.NumericValue);
        Assert.Equal(field.Field.Unit, value.Unit);
        Assert.Equal(field.Field.ValueKind, value.ValueKind);
        Assert.Equal(field.Field.Code, value.FieldCode);
        Assert.Equal(field.DeviceIdentity, value.DeviceIdentity);
        Assert.Equal(field.SourceId, value.SourceId);
        Assert.Equal(SamplingObservationStatus.Unsupported, value.Status);
    }

    [Fact]
    public void RecordedFourDatasetRawWireAndSubscriptionStayByteIdenticalAcrossAuthorityReads()
    {
        using var owner = new LastSuccessfulHardwareMetricSnapshot();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(json);
        var first = Frame(1);
        owner.Publish(first, MetricSampleRequest.ForIds(DatasetIds));
        AssertWire(first);
        var second = Frame(2);
        owner.Publish(second, MetricSampleRequest.ForIds(["cpu.usage"]));
        var expected = first with
        {
            Cpu = second.Cpu,
            Items = first.Items.Where(pair => DatasetIds.Contains(pair.Key)).ToDictionary(static pair => pair.Key,
                pair => pair.Key == "cpu.usage" ? second.Items[pair.Key] : pair.Value),
            Datasets = first.Datasets.ToDictionary(static pair => pair.Key,
                pair => pair.Key == "cpu.usage" ? second.Datasets[pair.Key] : pair.Value)
        };
        AssertWire(expected);
        owner.RecordFailure(MetricSampleRequest.ForIds(["memory.usage"]), second.CapturedAt, "recorded-failure");
        expected = expected with
        {
            Items = expected.Items.Where(static pair => pair.Key != "memory.usage").ToDictionary(),
            Datasets = expected.Datasets.ToDictionary(static pair => pair.Key, pair => pair.Key != "memory.usage"
                ? pair.Value : pair.Value with { Status = SamplingObservationStatus.Unavailable, ObservedAtUtcTicks = 0 })
        };
        AssertWire(expected);

        void AssertWire(HardwareMetricSnapshot expectedSnapshot)
        {
            var expected = MetricSnapshotWireSnapshot.From(expectedSnapshot, MetricSampleRequest.ForIds(DatasetIds), "recorded");
            var actual = MetricSnapshotWireSnapshot.From(owner.Read()!, MetricSampleRequest.ForIds(DatasetIds), "recorded");
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected, json), JsonSerializer.SerializeToUtf8Bytes(actual, json));
            var expectedFrame = new FrontendSubscriptionChannelFrame<MetricSnapshotWireSnapshot>("recorded", expected);
            var actualFrame = new FrontendSubscriptionChannelFrame<MetricSnapshotWireSnapshot>("recorded", actual);
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expectedFrame, json), JsonSerializer.SerializeToUtf8Bytes(actualFrame, json));
            using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(actual, json));
            var value = document.RootElement.GetProperty("items").GetProperty("cpu.usage");
            Assert.False(value.TryGetProperty("displayValue", out _));
            Assert.False(value.TryGetProperty("label", out _));
            Assert.False(value.TryGetProperty("group", out _));
            Assert.False(value.TryGetProperty("detail", out _));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PublishRound(LastSuccessfulHardwareMetricSnapshot owner, HardwareMetricHostedPublicationTicket ticket, int round,
        List<WeakReference> captures, List<WeakReference> readings)
    {
        var frame = Frame(round);
        captures.Add(new(frame)); readings.Add(new(frame.Items["cpu.usage"]));
        var ids = new[] { DatasetIds[(round - 1) % 4] };
        Assert.True(owner.TryPublishHosted(ticket, frame, MetricSampleRequest.All, ids, null, out var result));
        Assert.True(result.IsSuccessful);
    }

    private static HardwareMetricSnapshot Frame(int round)
    {
        var at = DateTimeOffset.UnixEpoch.AddSeconds(round).AddTicks(1234);
        var field = MetricFieldRegistry.FindByName("cpu_all_usage_percent")!;
        var bindings = Enumerable.Range(0, 1352).ToDictionary(index => $"recorded.{index}",
            index => new MetricDeviceFieldBinding($"device-{index}", field, $"recorded.{index}", "recorded-source"));
        var items = bindings.Keys.ToDictionary(static id => id, id => new MetricReadingValue(id, round, "%"));
        foreach (var id in DatasetIds)
            items[id] = new(id, round, id.EndsWith("vram", StringComparison.Ordinal) || id == "memory.usage" ? "B" : "%")
            {
                ObservedAt = at, SampleDurationMilliseconds = 1000,
                ValueKind = id.EndsWith("vram", StringComparison.Ordinal) ? "unsigned128" : "float64",
                IntegerValue = id.EndsWith("vram", StringComparison.Ordinal) ? new(ulong.MaxValue, ulong.MaxValue) : null
            };
        var provider = new HardwareSensorProviderState("recorded-source", "current", null);
        var gpu = new GpuMetrics(0, "Recorded GPU", round, 100, 200, 50, 300, 400, 800, 50,
            new(provider, null, null, null, null, null, null, null, null, null, null), "recorded-source", true, "recorded-gpu");
        return new(at,
            new("Recorded CPU", round, true, CpuMetricObservationStatus.Complete, (ulong)round, 1000, 2000, 4000, 50,
                "recorded-source", new(provider, null, null, null, null)),
            new(400, 800, 50, true), new(500, 1000, 50, true), [gpu],
            new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 1, []), items)
        {
            FieldBindings = bindings, WorkspaceIdentity = 1, ConfigurationGeneration = 1, CatalogGeneration = 1,
            CommittedGeneration = (ulong)round,
            Datasets = DatasetIds.ToDictionary(static id => id,
                id => new HardwareMetricDatasetObservation(id, SamplingObservationStatus.Current, (ulong)round,
                    at.UtcTicks, 1, 1, 1, (ulong)round) { LastAttemptAtUtcTicks = at.UtcTicks, LastSuccessAtUtcTicks = at.UtcTicks })
        };
    }

    private static object[] OwnedObjects(object root)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var value))
        {
            if (!visited.Add(value)) continue;
            Assert.True(visited.Count < 100000, "The retained-graph count exceeded its test bound.");
            if (value is string or Type) continue;
            if (value is Delegate callback) { if (callback.Target is { } target) queue.Enqueue(target); continue; }
            if (value is IEnumerable items)
            {
                foreach (var item in items) if (item is not null) queue.Enqueue(item);
                continue;
            }
            for (var type = value.GetType(); type is not null && type != typeof(object); type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum && !field.FieldType.IsPointer
                        && field.GetValue(value) is { } child) queue.Enqueue(child);
        }
        return visited.ToArray();
    }

    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
}
