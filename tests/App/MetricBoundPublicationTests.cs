using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Fields;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerAdversarialCorrectnessTests
{
    private const string BoundUsed = "recorded-bound-memory-used";
    private const string BoundTotal = "recorded-bound-memory-total";

    [Fact]
    public void MetricBoundPublicationIsBidirectionalKeepsZeroAndDoesNotPublishOtherCurrent()
    {
        foreach (var requestId in new[] { "memory.usage", BoundUsed })
        {
            var owner = new LastSuccessfulHardwareMetricSnapshot();
            owner.Publish(BoundFrame(1, 512, 1024), MetricSampleRequest.ForIds([BoundTotal]));
            var backend = BoundBackend(owner);
            foreach (var value in new[] { 512d, 0d })
            {
                var result = owner.Publish(BoundFrame(2, value, 9999), MetricSampleRequest.ForIds([requestId]));
                Assert.True(result.IsSuccessful);
                Assert.Contains(BoundUsed, result.CommittedDatasetIds);
                Assert.Contains("memory.usage", result.CommittedDatasetIds);
                Assert.Equal(value, BoundRead(backend).Value!.Number);
                Assert.Equal(SamplingObservationStatus.Current, owner.Read()!.Datasets["memory.usage"].Status);
                Assert.Equal(value, owner.Read()!.Items["memory.usage"].NumericValue);
                Assert.Equal(1024d, owner.Read()!.Items[BoundTotal].NumericValue);
                Assert.DoesNotContain(BoundTotal, result.CommittedDatasetIds);
                Assert.DoesNotContain("memory.total", result.CommittedDatasetIds);
            }
            owner.RecordFailure(MetricSampleRequest.ForIds([requestId]), DateTimeOffset.UnixEpoch.AddSeconds(10), "source-failed");
            Assert.Null(BoundRead(backend).Value);
            Assert.False(owner.Read()!.Items.ContainsKey("memory.usage"));
            Assert.False(owner.Read()!.Items.ContainsKey(BoundUsed));
            Assert.Equal(SamplingObservationStatus.Unavailable, owner.Read()!.Datasets["memory.usage"].Status);
            Assert.Equal(SamplingObservationStatus.Unavailable, owner.Read()!.Datasets[BoundUsed].Status);
            Assert.Equal(SamplingObservationStatus.Current, owner.Read()!.Datasets[BoundTotal].Status);
            Assert.Equal(1024d, owner.Read()!.Items[BoundTotal].NumericValue);
        }
    }

    [Fact]
    public void MetricBoundHostedExplicitSelectionExpandsOnlySameFieldAndHonorsPermission()
    {
        foreach (var requestId in new[] { "memory.usage", BoundUsed })
        {
            var owner = new LastSuccessfulHardwareMetricSnapshot();
            owner.Publish(BoundFrame(1, 100, 1024), MetricSampleRequest.ForIds([BoundTotal]));
            var permit = new MetricSdkTestPermission(1, 1);
            var run = owner.OpenHostedRun();
            Assert.True(owner.TryCaptureHostedTicket(run, permit, 1, 1, out var ticket));
            Assert.True(owner.TryPublishHosted(ticket, BoundFrame(2, 512, 9999), MetricSampleRequest.All,
                [requestId], null, out var result));
            Assert.True(result.IsSuccessful);
            Assert.Equal(512d, BoundRead(BoundBackend(owner)).Value!.Number);
            Assert.Equal(1024d, owner.Read()!.Items[BoundTotal].NumericValue);
            Assert.True(owner.TryRecordHostedFailure(ticket, MetricSampleRequest.All,
                [requestId], DateTimeOffset.UnixEpoch.AddSeconds(10), "hosted-source-failed"));
            Assert.Null(BoundRead(BoundBackend(owner)).Value);
            Assert.False(owner.Read()!.Items.ContainsKey("memory.usage"));
            Assert.Equal(SamplingObservationStatus.Unavailable, owner.Read()!.Datasets[BoundUsed].Status);
            Assert.Equal(SamplingObservationStatus.Current, owner.Read()!.Datasets[BoundTotal].Status);
            Assert.Equal(1024d, owner.Read()!.Items[BoundTotal].NumericValue);
            var before = owner.Read();
            permit.Revoke();
            Assert.False(owner.TryPublishHosted(ticket, BoundFrame(3, 900, 9999), MetricSampleRequest.All,
                [requestId], null, out _));
            Assert.Same(before, owner.Read());
        }
    }

    [Fact]
    public void MetricBoundReplacementRetiresOldAliasWithoutPublishingReplacementOrOtherSource()
    {
        var owner = new LastSuccessfulHardwareMetricSnapshot();
        owner.Publish(BoundFrame(1, 512, 1024), MetricSampleRequest.All);
        var oldBackend = BoundBackend(owner);
        var oldReference = oldBackend.Catalog.Fields.Single(item => item.Field.FieldName == "memory_all_physical_used_bytes").Field;
        var replacement = BoundFrame(2, 900, 9999, "replacement-memory") with { WorkspaceIdentity = 2 };
        owner.Publish(replacement, MetricSampleRequest.ForIds(["independent-not-requested-field"]));
        Assert.Throws<ArgumentException>(() => oldBackend.ReadCached(oldReference));
        var replacementBackend = BoundBackend(owner);
        Assert.Null(BoundRead(replacementBackend).Value);
        Assert.False(owner.Read()!.Items.ContainsKey("memory.usage"));
        Assert.False(owner.Read()!.Items.ContainsKey(BoundUsed));
        Assert.Equal(1024d, owner.Read()!.Items[BoundTotal].NumericValue);
        owner.Publish(replacement, MetricSampleRequest.ForIds(["memory.usage"]));
        Assert.Equal(900d, BoundRead(replacementBackend).Value!.Number);
    }

    private static MetricReadWriteBackend BoundBackend(LastSuccessfulHardwareMetricSnapshot owner)
        => new(() => owner.Read(), _ => owner.Read(),
            (_, _) => ValueTask.FromResult(owner.Read()!), _ => { },
            (_, binding) => new(true, null, MetricReadWriteBackend.DependenciesOf(binding.SourceId)),
            _ => OperationSupport.Unsupported("recorded-read-only"),
            (field, _, _, _) => ValueTask.FromResult(new FieldWriteResult(field, "unsupported", "recorded-read-only")));

    private static FieldReading BoundRead(MetricReadWriteBackend backend)
    {
        var field = backend.Catalog.Fields.Single(item => item.Field.FieldName == "memory_all_physical_used_bytes").Field;
        return backend.ReadCached(field);
    }

    private static HardwareMetricSnapshot BoundFrame(ulong round, double used, double total, string device = "recorded-memory")
    {
        var source = HistoryHardwareSnapshot(round, 50);
        var usedDefinition = MetricFieldRegistry.FindByName("memory_all_physical_used_bytes")!;
        var totalDefinition = MetricFieldRegistry.FindByName("memory_all_physical_total_bytes")!;
        var bindings = new Dictionary<string, MetricDeviceFieldBinding>
        {
            [BoundUsed] = new(device, usedDefinition, BoundUsed, "os.memory") { RequestAliases = ["memory.usage"] },
            // Capacity belongs to an independent source slot to catch broad publication.
            [BoundTotal] = new("independent-capacity", totalDefinition, BoundTotal, "recorded.capacity") { RequestAliases = ["memory.total"] }
        };
        var values = new Dictionary<string, MetricValue>();
        foreach (var (address, value) in new[] { (BoundUsed, used), ("memory.usage", used), (BoundTotal, total), ("memory.total", total) })
            values[address] = new(address, address, "recorded", "", value, "B", null, null);
        var datasets = values.Keys.ToDictionary(id => id, id => new HardwareMetricDatasetObservation(id,
            SamplingObservationStatus.Current, round, source.CapturedAt.UtcTicks, 1, 1, 1, round));
        return source with { Items = values, Datasets = datasets, FieldBindings = bindings,
            WorkspaceIdentity = 1, ConfigurationGeneration = 1 };
    }
}
