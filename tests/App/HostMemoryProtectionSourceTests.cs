using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.MemoryProtection;
using ResourceManager.ReadWrite.ProcessFields;

namespace Resource_Manager_APP.Tests;

public sealed class HostMemoryProtectionSourceTests
{
    private const string Holder = "memory-protection-test";
    private const string PrivateCommit = "process_all_private_commit_actual";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(137);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnsBothSubscriptionsAtConfiguredFrequencyUntilDisposedOrCancelled(bool cancel)
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await using var dashboardMetrics = fixture.Metrics.SubscribeAsync("dashboard", MetricSampleRequest.All,
            TimeSpan.FromSeconds(10), CancellationToken.None).GetAsyncEnumerator();
        var dashboardMetricRead = dashboardMetrics.MoveNextAsync().AsTask();
        await fixture.Metrics.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        fixture.Metrics.Publish("dashboard", CommitSnapshot());
        Assert.True(await dashboardMetricRead.WaitAsync(Timeout));
        await using var dashboardResources = fixture.Resources.SubscribeAsync("dashboard",
            new(["memory.usage"], new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableFull),
            TimeSpan.FromSeconds(10), CancellationToken.None).GetAsyncEnumerator();
        var dashboardResourceRead = dashboardResources.MoveNextAsync().AsTask();
        await fixture.Resources.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        fixture.Resources.Publish("dashboard", ResourceSnapshot());
        Assert.True(await dashboardResourceRead.WaitAsync(Timeout));

        await using var stream = await fixture.OpenAsync(cancellation.Token);
        var machine = fixture.Metrics.Active[Holder + ":commit"];
        var processes = fixture.Resources.Active[Holder + ":processes"];
        Assert.Equal(Interval, machine.Interval);
        Assert.Equal(Interval, processes.Interval);
        Assert.False(machine.Request.IsAll);
        Assert.Equal(new[] { "virtualMemory.percent", "virtualMemory.total", "virtualMemory.usage" },
            machine.Request.Ids.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { PrivateCommit }, processes.Request.MetricIds);
        Assert.Equal(ProcessSampleDetailLevel.ResourceTableBasic, processes.Request.ProcessDetailLevel);
        Assert.Equal(SchedulingProcessMetricMask.None, processes.Request.SchedulingMetricMask);
        Assert.Equal(new[] { SamplingDatasetIds.ProcessInventory, SamplingDatasetIds.ProcessAttribution },
            processes.Request.PublicationDatasetIds);

        await dashboardMetrics.DisposeAsync();
        await dashboardResources.DisposeAsync();
        Assert.Single(fixture.Metrics.Active);
        Assert.Single(fixture.Resources.Active);
        fixture.Foundations.Current = Foundation(Process(42, 100));
        var reading = await fixture.DeliverProcessesAsync(stream, ResourceSnapshot(Row(42, "100", new(17))));
        Assert.Equal(17UL, Assert.Single(reading.Processes!).PrivateCommitBytes);

        if (cancel)
        {
            var pending = stream.MoveNextAsync().AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        }
        else await stream.DisposeAsync();
        Assert.Empty(fixture.Metrics.Active);
        Assert.Empty(fixture.Resources.Active);
        Assert.Equal(0, fixture.Metrics.AcquireCount);
        Assert.Equal(0, fixture.Foundations.AcquireCount);
    }

    [Fact]
    public async Task CurrentCommitWaitsForOwnFreshSubscriptionAndPreservesUnsignedPrecision()
    {
        var fixture = new Fixture();
        const ulong used = 9007199254740993;
        fixture.Metrics.Current = CommitSnapshot(capturedAt: DateTimeOffset.UnixEpoch);
        var pending = fixture.Source.ReadCurrentCommitAsync(CancellationToken.None);
        var holder = await fixture.Metrics.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        Assert.Equal("host-memory-protection:status", holder);
        Assert.False(pending.IsCompleted);
        Assert.Equal(3, fixture.Metrics.Active[holder].Request.Ids.Count);
        fixture.Metrics.Publish(holder, fixture.Metrics.Current!);
        Assert.False(pending.IsCompleted);
        fixture.Metrics.Publish(holder, CommitSnapshot(new(used), new(ulong.MaxValue), 97.25));
        Assert.Equal(new HostMemoryCommit(used, ulong.MaxValue, 97.25), await pending.WaitAsync(Timeout));
        Assert.Equal(0, fixture.Metrics.ReadCount);
        Assert.Equal(0, fixture.Metrics.AcquireCount);
        Assert.Empty(fixture.Metrics.Active);
        Assert.Empty(fixture.Resources.Active);
        Assert.Equal(0, fixture.Foundations.ReadCount);
    }

    [Fact]
    public async Task CancellingCurrentCommitReleasesItsSubscription()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Source.ReadCurrentCommitAsync(cancellation.Token);
        await fixture.Metrics.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        Assert.Empty(fixture.Metrics.Active);
    }

    [Theory]
    [InlineData("used-unavailable")]
    [InlineData("limit-unavailable")]
    [InlineData("percent-unavailable")]
    [InlineData("missing-used")]
    [InlineData("numeric-used-only")]
    [InlineData("negative-used")]
    [InlineData("overflow-used")]
    [InlineData("zero-limit")]
    [InlineData("negative-limit")]
    [InlineData("overflow-limit")]
    [InlineData("missing-percent")]
    [InlineData("nan-percent")]
    [InlineData("infinite-percent")]
    public async Task UnavailableCommitReplacesPreviouslyDeliveredCommitWithTypedNull(string fault)
    {
        var fixture = new Fixture();
        await using var stream = await fixture.OpenAsync();
        Assert.NotNull(stream.Current.Commit);
        var snapshot = CommitSnapshot();
        var items = snapshot.Items.ToDictionary(pair => pair.Key, pair => pair.Value);
        switch (fault)
        {
            case "used-unavailable": items["used"] = items["used"] with { Status = SamplingObservationStatus.Unavailable }; break;
            case "limit-unavailable": items["limit"] = items["limit"] with { Status = SamplingObservationStatus.Warming }; break;
            case "percent-unavailable": items["percent"] = items["percent"] with { Status = SamplingObservationStatus.NotRequested }; break;
            case "missing-used": items.Remove("used"); break;
            case "numeric-used-only": items["used"] = items["used"] with { IntegerValue = null, NumericValue = 9007199254740992 }; break;
            case "negative-used": items["used"] = items["used"] with { IntegerValue = MetricInteger.FromSigned64(-1) }; break;
            case "overflow-used": items["used"] = items["used"] with { IntegerValue = new(0, 1) }; break;
            case "zero-limit": items["limit"] = items["limit"] with { IntegerValue = new(0) }; break;
            case "negative-limit": items["limit"] = items["limit"] with { IntegerValue = MetricInteger.FromSigned64(-1) }; break;
            case "overflow-limit": items["limit"] = items["limit"] with { IntegerValue = new(0, 1) }; break;
            case "missing-percent": items["percent"] = items["percent"] with { NumericValue = null }; break;
            case "nan-percent": items["percent"] = items["percent"] with { NumericValue = double.NaN }; break;
            case "infinite-percent": items["percent"] = items["percent"] with { NumericValue = double.PositiveInfinity }; break;
        }
        var pending = stream.MoveNextAsync().AsTask();
        fixture.Metrics.Publish(Holder + ":commit", snapshot with { Items = items });
        Assert.True(await pending.WaitAsync(Timeout));
        Assert.Null(stream.Current.Commit);
        var currentCommit = fixture.Source.ReadCurrentCommitAsync(CancellationToken.None);
        var holder = await fixture.Metrics.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        fixture.Metrics.Publish(holder, snapshot with { Items = items,
            Datasets = snapshot.Datasets.ToDictionary(pair => pair.Key,
                pair => pair.Value with { LastAttemptAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks }) });
        Assert.Null(await currentCommit.WaitAsync(Timeout));
        Assert.False(fixture.Metrics.Active.ContainsKey(holder));
    }

    [Fact]
    public async Task PrivateCommitUsesExactProcessIdentityAndIntegerBytesOnly()
    {
        var fixture = new Fixture();
        fixture.Foundations.Current = Foundation(Enumerable.Range(42, 8).Select(pid => Process(pid, 100)).ToArray());
        await using var stream = await fixture.OpenAsync();
        var reading = await fixture.DeliverProcessesAsync(stream, ResourceSnapshot(
            Row(42, "100", new(9007199254740993)),
            Row(43, "100", new(ulong.MaxValue)),
            Row(44, "99", new(123)), // Same PID from an earlier process creation cannot supply bytes.
            Row(999, "100", new(456)), // Same start key on another PID cannot supply bytes.
            Row(45, "100", null, 123),
            Row(46, "100", MetricInteger.FromSigned64(-1)),
            Row(47, "100", new(0, 1)),
            Row(48, "100", new(0))));
        var rows = reading.Processes!.ToDictionary(process => process.ProcessId);
        Assert.Equal(8, rows.Count);
        Assert.Equal(9007199254740993UL, rows[42].PrivateCommitBytes);
        Assert.Equal(ulong.MaxValue, rows[43].PrivateCommitBytes);
        Assert.Null(rows[44].PrivateCommitBytes);
        Assert.Null(rows[45].PrivateCommitBytes);
        Assert.Null(rows[46].PrivateCommitBytes);
        Assert.Null(rows[47].PrivateCommitBytes);
        Assert.Equal(0UL, rows[48].PrivateCommitBytes);
        Assert.Null(rows[49].PrivateCommitBytes);
        Assert.All(rows.Values, row => Assert.Equal("100", row.ProcessStartKey));
        Assert.Equal(SchedulingProcessMetricMask.None, fixture.Foundations.LastRequest!.RequestedMetricMask);
    }

    [Fact]
    public async Task UnavailableProcessSourceClearsBytesAndUnavailableInventoryClearsProcessList()
    {
        var fixture = new Fixture();
        fixture.Foundations.Current = Foundation(Process(42, 100));
        await using var stream = await fixture.OpenAsync();
        var good = ResourceSnapshot(Row(42, "100", new(17)));
        Assert.Equal(17UL, Assert.Single((await fixture.DeliverProcessesAsync(stream, good)).Processes!).PrivateCommitBytes);
        var unavailable = good with { ProcessFields = good.ProcessFields! with
            { Sources = [new(ProcessMetricFieldRegistry.Find(PrivateCommit)!.SourceId, null, null)] } };
        Assert.Null(Assert.Single((await fixture.DeliverProcessesAsync(stream, unavailable)).Processes!).PrivateCommitBytes);
        Assert.Null(Assert.Single((await fixture.DeliverProcessesAsync(stream, good with { ProcessFields = null })).Processes!).PrivateCommitBytes);
        fixture.Foundations.Current = fixture.Foundations.Current with
        {
            FoundationDatasetObservations = new Dictionary<string, SchedulingProcessFoundationDatasetObservation>()
        };
        Assert.Null((await fixture.DeliverProcessesAsync(stream, good)).Processes);
        fixture.Foundations.Current = null;
        Assert.Null((await fixture.DeliverProcessesAsync(stream, good)).Processes);
        fixture.Foundations.Current = Foundation();
        Assert.Empty((await fixture.DeliverProcessesAsync(stream, good)).Processes!);
    }

    [Fact]
    public async Task ResourceManagerAttributionRequiresMatchingPidAndCreation()
    {
        var fixture = new Fixture();
        var ordinary = Process(42, 100);
        var attributed = Process(43, 100);
        var wrongCreation = Process(44, 100);
        fixture.Foundations.Current = Foundation(ordinary, attributed, wrongCreation,
            Process(Environment.ProcessId, 100), Process(45, 100) with { ProcessName = ResourceManagerSelfDescriptor.ProcessNames.First() }) with
        {
            AttributionProcesses = [attributed with { SoftwareId = RuntimeAttributionIds.ResourceManagerSelf },
                wrongCreation with { ProcessStartKey = 99, SoftwareId = RuntimeAttributionIds.ResourceManagerSelf },
                ordinary with { ProcessId = 999, SoftwareId = RuntimeAttributionIds.ResourceManagerSelf }]
        };
        await using var stream = await fixture.OpenAsync();
        var rows = (await fixture.DeliverProcessesAsync(stream, ResourceSnapshot())).Processes!.ToDictionary(row => row.ProcessId);
        Assert.False(rows[42].IsResourceManager);
        Assert.Null(rows[42].SoftwareId);
        Assert.True(rows[43].IsResourceManager);
        Assert.Equal(RuntimeAttributionIds.ResourceManagerSelf, rows[43].SoftwareId);
        Assert.False(rows[44].IsResourceManager);
        Assert.Null(rows[44].SoftwareId);
        Assert.True(rows[Environment.ProcessId].IsResourceManager);
        Assert.True(rows[45].IsResourceManager);
    }

    private static HardwareMetricSnapshot CommitSnapshot(MetricInteger? used = null, MetricInteger? limit = null,
        double percent = 50, DateTimeOffset? capturedAt = null)
    {
        var provider = new HardwareSensorProviderState("fixture", "current", null);
        var at = capturedAt ?? DateTimeOffset.UtcNow;
        return new(at,
            new("", 0, false, CpuMetricObservationStatus.Unavailable, 0, 0, 0, 0, 0, "", new(provider, null, null, null, null)),
            new(0, 0, 0, false), new(0, 0, 0, false), [],
            new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []),
            new Dictionary<string, MetricReadingValue>
            {
                ["used"] = new("used", null, "B") { FieldCode = MetricFieldCode.CommitUsed, IntegerValue = used ?? new(50) },
                ["limit"] = new("limit", null, "B") { FieldCode = MetricFieldCode.CommitLimit, IntegerValue = limit ?? new(100) },
                ["percent"] = new("percent", percent, "%") { FieldCode = MetricFieldCode.CommitPercent }
            })
        { Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            { [SamplingDatasetIds.ForSystemMetric("virtualMemory.usage")] = new(
                SamplingDatasetIds.ForSystemMetric("virtualMemory.usage"), SamplingObservationStatus.Current,
                1, at.UtcTicks, 1, 1, 1, 1) { LastAttemptAtUtcTicks = at.UtcTicks } } };
    }

    private static SchedulingProcessFact Process(int pid, ulong start) =>
        new(pid, start, "fixture-application", null, "fixture", "Fixture", "other", "other", 0,
            SchedulingProcessMetricMask.None, 0, 0, 1, []);

    private static SchedulingProcessFactSnapshot Foundation(params SchedulingProcessFact[] processes) =>
        new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks,
            (uint)processes.Length, (uint)processes.Length, 0, 0, SchedulingProcessMetricMask.None,
            SchedulingProcessMetricMask.None, [])
        {
            HasSeparateFoundationPayloads = true,
            InventoryProcesses = processes,
            AttributionProcesses = processes,
            FoundationDatasetObservations = new[] { SamplingDatasetIds.ProcessInventory, SamplingDatasetIds.ProcessAttribution }
                .ToDictionary(id => id, id => SchedulingProcessFoundationDatasetObservation.CreateCurrent(id, 1, DateTimeOffset.UtcNow.UtcTicks))
        };

    private static ProcessMetricFieldValues Row(int pid, string start, MetricInteger? value, double? numeric = 999) =>
        new(pid, start, [new(PrivateCommit, value, numeric)]);

    private static ResourceBreakdownSnapshot ResourceSnapshot(params ProcessMetricFieldValues[] rows)
    {
        var field = ProcessMetricFieldRegistry.Find(PrivateCommit)!;
        return new(DateTimeOffset.UtcNow, [])
        {
            ProcessFields = new(DateTimeOffset.UtcNow, null, [field], rows)
                { Sources = [new(field.SourceId, DateTimeOffset.UtcNow, null)] }
        };
    }

    private sealed class Fixture
    {
        public MetricSource Metrics { get; } = new();
        public ResourceSource Resources { get; } = new();
        public FoundationSource Foundations { get; } = new();
        public HostMemoryProtectionSource Source => new(Metrics, Resources, Foundations);

        public async Task<IAsyncEnumerator<HostMemoryReading>> OpenAsync(CancellationToken cancellation = default)
        {
            var stream = Source.SubscribeAsync(Holder, Interval, cancellation).GetAsyncEnumerator();
            try
            {
                var pending = stream.MoveNextAsync().AsTask();
                await Metrics.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
                await Resources.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
                Metrics.Publish(Holder + ":commit", CommitSnapshot());
                Assert.True(await pending.WaitAsync(Timeout));
                Assert.NotNull(stream.Current.Commit);
                Assert.Null(stream.Current.Processes);
                return stream;
            }
            catch { await stream.DisposeAsync(); throw; }
        }

        public async Task<HostMemoryReading> DeliverProcessesAsync(IAsyncEnumerator<HostMemoryReading> stream, ResourceBreakdownSnapshot snapshot)
        {
            var pending = stream.MoveNextAsync().AsTask();
            Resources.Publish(Holder + ":processes", snapshot);
            Assert.True(await pending.WaitAsync(Timeout));
            return stream.Current;
        }
    }

    private sealed record Subscription<TValue, TRequest>(TRequest Request, TimeSpan Interval, Channel<TValue> Values);

    // These fakes implement the production push contracts and hold a separate channel for every holder.
    // They never run a sampler or use a real process inventory.
    private abstract class PushSource<TValue, TRequest>
    {
        public ConcurrentDictionary<string, Subscription<TValue, TRequest>> Active { get; } = new();
        public Channel<string> Started { get; } = Channel.CreateUnbounded<string>();
        public void Publish(string holder, TValue value) => Assert.True(Active[holder].Values.Writer.TryWrite(value));

        public async IAsyncEnumerable<TValue> SubscribeAsync(string subscriptionId, TRequest request, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var subscription = new Subscription<TValue, TRequest>(request, interval, Channel.CreateUnbounded<TValue>());
            Assert.True(Active.TryAdd(subscriptionId, subscription));
            Started.Writer.TryWrite(subscriptionId);
            try
            {
                await foreach (var value in subscription.Values.Reader.ReadAllAsync(cancellationToken)) yield return value;
            }
            finally { Assert.True(Active.TryRemove(subscriptionId, out _)); }
        }
    }

    private sealed class MetricSource : PushSource<HardwareMetricSnapshot, MetricSampleRequest>, IMetricSnapshotPushSource, IMetricSnapshotObservationSource
    {
        public HardwareMetricSnapshot? Current { get; set; }
        public int ReadCount { get; private set; }
        public int AcquireCount { get; private set; }
        public MetricSampleRequest? LastReadRequest { get; private set; }
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request)
        {
            ReadCount++;
            LastReadRequest = request;
            return Current;
        }
        public IDisposable AcquireSubscription(string subscriptionId, MetricSampleRequest request, TimeSpan refreshInterval)
        {
            AcquireCount++;
            throw new InvalidOperationException("Use the independently owned push subscription.");
        }
    }

    private sealed class ResourceSource : PushSource<ResourceBreakdownSnapshot, ResourceBreakdownSampleRequest>, IResourceBreakdownPushSource { }

    private sealed class FoundationSource : ISchedulingProcessFactObservationSource
    {
        public SchedulingProcessFactSnapshot? Current { get; set; }
        public SchedulingProcessFactRequest? LastRequest { get; private set; }
        public int ReadCount { get; private set; }
        public int AcquireCount { get; private set; }
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request)
        {
            ReadCount++;
            LastRequest = request;
            return Current;
        }
        public IDisposable AcquireSubscription(string subscriptionId, SchedulingProcessMetricMask metricMask, TimeSpan refreshInterval)
        {
            AcquireCount++;
            throw new InvalidOperationException("Foundation publications belong to the resource push subscription.");
        }
    }
}
