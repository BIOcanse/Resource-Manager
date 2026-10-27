using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.KernelLeak;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed partial class KernelLeakReportTests
{
    private static readonly KernelLeakOptions Fast = new(1000, 4, 8, 3, 2, 1000, 100, 25);
    [Theory]
    [InlineData(1000, 1100, 1200, true)]
    [InlineData(1200, 1400, 1200, false)]
    [InlineData(800, 900, 1000, false)]
    [InlineData(1200, 1200, 1200, false)]
    public async Task RecordedSequencesThroughProductionComposition(int first, int second, int third, bool suspected)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var subscription = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(KernelLeakFailure.NotObserved, subscription.Current.Failure);
        await fixture.Deliver(subscription, 0, first);
        await fixture.Deliver(subscription, 1, second);
        var report = await fixture.Deliver(subscription, 2, third);
        Assert.Equal(suspected ? KernelLeakReportStatus.Suspected : KernelLeakReportStatus.Empty, report.Status);
        if (!suspected) { Assert.Empty(report.Items); return; }
        var item = Assert.Single(report.Items);
        Assert.Equal(1200UL, item.CurrentValue);
        Assert.Equal(100, item.GrowthPerSecond);
        Assert.Equal(2, item.ObservationSeconds);
        Assert.Equal(3, item.SampleCount);
        Assert.Equal("recorded-service", Assert.Single(item.Holder.ServiceNames));
        Assert.Equal("42", item.Holder.StartKey);
        Assert.Equal("recorded-product", item.Holder.ProductName);
        var wire = JsonSerializer.Serialize(new { subscriptionId = "leaks", value = report }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"resourceKind\":\"ProcessHandles\"", wire);
        File.WriteAllText(EvidencePath("wire-example.json"), wire);
    }
    [Fact]
    public async Task ExitAndPidReuseReleaseSeriesAndLastSubscriberDrainsDemand()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(0, fixture.Processes.Starts);
        Assert.Equal(KernelLeakFailure.NotObserved, fixture.Reports.Read().Failure);
        Assert.Equal(0, fixture.Processes.Starts); // Reads and diagnostics cannot start capture.
        var subscription = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        await fixture.Deliver(subscription, 0, 1000);
        await fixture.Deliver(subscription, 1, 1100);
        await fixture.Deliver(subscription, 2, 1200);
        Assert.Equal(1, fixture.Reports.ReadDiagnostics().HolderCount);
        var ended = await fixture.Deliver(subscription, 3, null);
        Assert.Empty(ended.Items);
        Assert.Equal(0, fixture.Reports.ReadDiagnostics().HolderCount);
        await fixture.Deliver(subscription, 4, 1600, "43");
        Assert.Empty(subscription.Current.Items);
        Assert.Equal(1, fixture.Reports.ReadDiagnostics().HolderCount);
        Assert.Equal("kernel-leak-observer", fixture.Processes.Holder);
        Assert.Equal(TimeSpan.FromSeconds(1), fixture.Processes.Interval);
        await subscription.DisposeAsync();
        Assert.Equal(0, fixture.Processes.Active);
        Assert.Equal(0, fixture.Reports.ReadDiagnostics().HolderCount);
        Assert.Equal(0, fixture.Reports.ReadDiagnostics().SeriesBytes);
        Assert.Equal(KernelLeakReportStatus.Empty, fixture.Reports.Read().Status);
    }
    [Fact]
    public async Task TwoReportSubscribersShareOneDemand()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var first = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        var second = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync()); Assert.True(await second.MoveNextAsync());
        await fixture.Deliver(first, 0, 1000);
        Assert.Equal(1, fixture.Processes.Starts);
        await first.DisposeAsync();
        Assert.Equal(1, fixture.Processes.Active);
        Assert.True(await second.MoveNextAsync());
        await second.DisposeAsync();
        Assert.Equal(0, fixture.Processes.Active);
    }
    [Fact]
    public async Task FailedSourceClearsEvidenceRatherThanKeepingLastSuspect()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var subscription = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        await fixture.Deliver(subscription, 0, 1000);
        await fixture.Deliver(subscription, 1, 1100);
        await fixture.Deliver(subscription, 2, 1200);
        fixture.Processes.Push(new(DateTimeOffset.UnixEpoch.AddSeconds(3), []));
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(KernelLeakReportStatus.Unavailable, subscription.Current.Status);
        Assert.Empty(subscription.Current.Items);
        Assert.Equal(0, fixture.Reports.ReadDiagnostics().HolderCount);
        Assert.Empty((await fixture.Deliver(subscription, 4, 1400)).Items);
    }
    [Fact]
    public async Task CacheLikeQuantitiesAreExcludedAndOverflowIsExplicit()
    {
        // This alternative recorded source passes through the same production observer composition.
        using var fixture = new Fixture(rawSamples: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var subscription = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        for (var index = 0; index < 3; index++)
        {
            fixture.Raw!.Push(new(DateTimeOffset.UnixEpoch.AddSeconds(index), true,
                [new(7, "42", KernelLeakResourceKind.FileCachePoolBytes, (ulong)(10000 + 1000 * index))]));
            Assert.True(await subscription.MoveNextAsync());
            Assert.Empty(subscription.Current.Items);
            Assert.Equal(0, fixture.Reports.ReadDiagnostics().HolderCount);
        }
        fixture.Raw!.Push(new(DateTimeOffset.UnixEpoch.AddSeconds(4), true,
            Enumerable.Range(1, 9).Select(pid => new KernelLeakSample(pid, "42", KernelLeakResourceKind.ProcessHandles, 1200)).ToArray()));
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(KernelLeakFailure.CapacityExceeded, subscription.Current.Failure);
        Assert.Empty(subscription.Current.Items);
    }
    [Fact]
    public void BoundedRingEvictsOldFallbackAndRequiresWholeWindow()
    {
        var series = new KernelLeakSeries(Fast);
        var values = new ulong[] { 1400, 1000, 1100, 1200, 1300 };
        for (var index = 0; index < values.Length; index++)
        {
            var result = series.Observe(new(DateTimeOffset.UnixEpoch.AddSeconds(index), true,
                [new(7, "42", KernelLeakResourceKind.ProcessHandles, values[index])]));
            Assert.Equal(index == 4 ? 1 : 0, result.Count);
        }
        Assert.Equal(64, series.Bytes);
    }
    [Fact]
    public async Task NativeHandleCaptureUsesOneSharedTableAndMeasuresCost()
    {
        using var owner = WindowsProcessFieldOwner.CreateDefault();
        var inventory = new CountingInventory(); owner.Inventory = inventory;
        var elapsed = new List<double>();
        for (var round = 0; round < 7; round++)
        {
            var timer = Stopwatch.StartNew();
            var result = await owner.CollectAsync([KernelLeakProcessSampleSource.HandleField]);
            elapsed.Add(timer.Elapsed.TotalMilliseconds);
            Assert.Contains(result.Processes, row => row.ProcessId == Environment.ProcessId && row.Values.Single().IntegerValue is not null);
        }
        Assert.Equal(0, inventory.Walks); Assert.Equal(7, inventory.Tables);
        var fullSamplerCosts = new List<double>();
        using (var subscriptions = new HostManagerSamplingSubscriptionTestFixture())
        using (var production = new WindowsResourceBreakdownSampler(null!, null!, new StaticResourceResidualBreakdownProvider(), null!,
            new ProcessNetworkFieldCollectorTests.NetworkReader(_ => throw new InvalidOperationException("No network demand.")),
            subscriptions.Provider, subscriptions.Owner, new PdhProcessGpuReader(), WindowsProcessInventoryReader.Instance))
        {
            var request = ResourceBreakdownSampleRequest.ForResourceTable([KernelLeakProcessSampleSource.HandleField], new Dictionary<string, string>());
            for (var round = 0; round < 7; round++)
            {
                var timer = Stopwatch.StartNew();
                var result = await production.CaptureSnapshotAsync(request, default);
                fullSamplerCosts.Add(timer.Elapsed.TotalMilliseconds);
                Assert.NotEmpty(result.ProcessFields!.Processes);
            }
        }
        var current = Process.GetCurrentProcess();
        using (current)
        {
            var birth = current.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var timer = Stopwatch.StartNew();
            var metadata = new WindowsKernelLeakAttributionReader().ReadBatch([new(Environment.ProcessId, birth, null, null, null, [],
                KernelLeakMetadataStatus.Unavailable, KernelLeakMetadataStatus.Unavailable)]);
            var attributionMs = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(KernelLeakMetadataStatus.Current, metadata[0].ImageStatus);
            var options = HostManagerTestPlanFactory.CreatePlan().KernelLeak!.Validate();
            var series = new KernelLeakSeries(options);
            var samples = Enumerable.Range(1, options.MaximumHolders).Select(pid =>
                new KernelLeakSample(pid, "42", KernelLeakResourceKind.ProcessHandles, 1500)).ToArray();
            timer.Restart();
            for (var round = 0; round < options.SeriesCapacity; round++)
                series.Observe(new(DateTimeOffset.UnixEpoch.AddMilliseconds((long)round * options.IntervalMilliseconds), true, samples));
            var seriesMs = timer.Elapsed.TotalMilliseconds / options.SeriesCapacity;
            File.WriteAllText(EvidencePath("sampling-cost.json"), JsonSerializer.Serialize(new
            {
                handleCaptureMilliseconds = elapsed, warmMedianMilliseconds = elapsed.Skip(1).Order().Skip(2).Take(2).Average(),
                fullSamplerCaptureMilliseconds = fullSamplerCosts, fullSamplerWarmMedianMilliseconds = fullSamplerCosts.Skip(1).Order().Skip(2).Take(2).Average(),
                attributionOneHolderIncludingScmMilliseconds = attributionMs, series4096HoldersMilliseconds = seriesMs,
                seriesBytes = series.Bytes, intervalMilliseconds = options.IntervalMilliseconds,
                nativeTableQueries = inventory.Tables, processInventories = inventory.Walks
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    [Fact]
    public void RecordedNativeTableKeepsHandleCountBesideStartIdentity()
    {
        var bytes = new byte[WindowsProcessSystemThreadParser.ProcessHeaderSize];
        var header = new WindowsProcessSystemThreadParser.SystemProcessInformationHeader
            { UniqueProcessId = new(7), CreationTime = 42, HandleCount = 1234 };
        MemoryMarshal.Write(bytes, in header);
        Assert.True(WindowsProcessSystemThreadParser.TryParseInventory(bytes, WindowsProcessDetailsOptions.Default, out var rows));
        var row = Assert.Single(rows);
        Assert.Equal(42, row.Creation.Creation); Assert.Equal(1234u, row.Creation.HandleCount);
    }
    [Fact]
    public void KernelLeakFreedomPointDefaultsAreCompiledAndInvalidBoundsAreRejected()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        Assert.Equal(new KernelLeakOptions(30000, 32, 4096, 8, 600, 1024, 128, 25), baseline.KernelLeak);
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, "resource-manager/backend/monitoring/build/complex/0")["value"]!["handle_floor"] = 2000);
        Assert.Equal(2000, changed.KernelLeak!.HandleFloor);
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
        Assert.Throws<InvalidDataException>(() => (Fast with { SeriesCapacity = 1 }).Validate());
        Assert.Throws<InvalidDataException>(() => (Fast with { MaximumHolders = 20000 }).Validate());
        Assert.Throws<InvalidDataException>(() => (Fast with { MinimumObservationSeconds = 5 }).Validate());
    }
    [Fact]
    public async Task MetadataFailureDoesNotEraseIndependentlyObservedGrowth()
    {
        using var fixture = new Fixture(failedAttribution: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var subscription = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        await fixture.Deliver(subscription, 0, 1000);
        await fixture.Deliver(subscription, 1, 1100);
        var report = await fixture.Deliver(subscription, 2, 1200);
        var suspect = Assert.Single(report.Items);
        Assert.Equal(KernelLeakReportStatus.Suspected, report.Status);
        Assert.Equal(KernelLeakFailure.None, report.Failure);
        Assert.Equal(KernelLeakMetadataStatus.Unavailable, suspect.Holder.ImageStatus);
        Assert.Equal(KernelLeakMetadataStatus.Unavailable, suspect.Holder.ServiceStatus);
        Assert.Equal(7, suspect.Holder.ProcessId); Assert.Equal("42", suspect.Holder.StartKey);
    }
    [Fact]
    public async Task MissingHolderReadIsExplicitAndDoesNotSuppressOtherHolders()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var subscription = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        for (var index = 0; index < 3; index++)
        {
            var at = DateTimeOffset.UnixEpoch.AddSeconds(index);
            fixture.Processes.Push(new(at, []) { ProcessFields = new(at, null, [],
                [new(7, "42", [new(KernelLeakProcessSampleSource.HandleField, new MetricInteger((ulong)(1000 + index * 100)), null)]),
                 new(8, "43", [new(KernelLeakProcessSampleSource.HandleField, null, null)])]) });
            Assert.True(await subscription.MoveNextAsync());
            Assert.Equal(1, subscription.Current.UnavailableHolderCount);
            Assert.Equal(index == 2 ? 1 : 0, subscription.Current.Items.Count);
            Assert.Equal(1, fixture.Reports.ReadDiagnostics().HolderCount);
        }
    }
    [Fact]
    public void DebugSnapshotUsesProductionJsonMetadataAndDoesNotStartSampling()
    {
        using var fixture = new Fixture();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManager.App.Endpoints.ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var json = JsonSerializer.Serialize(new KernelLeakDebugSnapshot(fixture.Reports.Read(), fixture.Reports.ReadDiagnostics()), options);
        Assert.Contains("\"NotObserved\"", json);
        Assert.Equal(0, fixture.Processes.Starts);
    }
    [Theory]
    [InlineData(true, new int[] { 1000, 1030, 1100, 1099, 1200, 1195, 1300, 1297 })]
    [InlineData(false, new int[] { 1000, 1030, 1200, 1250, 1245, 1200, 1005, 1000 })]
    [InlineData(false, new int[] { 1000, 1030, 1100, 1200, 1300, 1400, 1000, 1700 })]
    public async Task SustainedEdgesTolerateNoiseAndVetoReturnToEarlyLevel(bool suspected, int[] values)
    {
        using var fixture = new Fixture(options: Fast with { SeriesCapacity = 8, MinimumSamples = 8, MinimumObservationSeconds = 7 });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var subscription = fixture.Reports.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        for (var index = 0; index < values.Length; index++)
        {
            var report = await fixture.Deliver(subscription, index, values[index]);
            Assert.Equal(suspected && index == values.Length - 1 ? KernelLeakReportStatus.Suspected : KernelLeakReportStatus.Empty, report.Status);
        }
        if (suspected)
        {
            var item = Assert.Single(subscription.Current.Items);
            Assert.Equal(297UL, item.Growth);
            Assert.Equal(297d / 7, item.GrowthPerSecond);
            Assert.Equal(7, item.ObservationSeconds);
            File.WriteAllText(EvidencePath("noisy-wire-example.json"), JsonSerializer.Serialize(subscription.Current, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }
    [Fact]
    public void EdgePercentageIsValidatedAndChangesTheEdgeBand()
    {
        Assert.Throws<InvalidDataException>(() => (Fast with { EdgePercentage = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (Fast with { EdgePercentage = 51 }).Validate());
        foreach (var percentage in new[] { 25, 50 })
        {
            var series = new KernelLeakSeries((Fast with { SeriesCapacity = 8, MinimumSamples = 8, MinimumObservationSeconds = 7, EdgePercentage = percentage }).Validate());
            IReadOnlyList<KernelLeakSuspect> items = [];
            var values = new ulong[] { 1000, 1030, 1250, 1300, 1250, 1290, 1400, 1450 };
            for (var index = 0; index < values.Length; index++)
                items = series.Observe(new(DateTimeOffset.UnixEpoch.AddSeconds(index), true, [new(7, "42", KernelLeakResourceKind.ProcessHandles, values[index])]));
            Assert.Equal(percentage == 25 ? 1 : 0, items.Count);
        }
    }
    private static string EvidencePath(string name, [CallerFilePath] string source = "")
        => Path.Combine(Environment.GetEnvironmentVariable("RM_KERNEL_LEAK_EVIDENCE_ROOT")
            ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../.local/reports")), name);
    private sealed class CountingInventory : IWindowsProcessInventoryReader
    {
        internal int Walks, Tables;
        public Process[] GetProcesses() { Walks++; throw new InvalidOperationException("A second process walk is forbidden."); }
        public WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer buffer)
        { Tables++; return WindowsProcessInventoryReader.Instance.Capture(buffer); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider services;
        internal RecordedProcesses Processes { get; } = new();
        internal RawSamples? Raw { get; }
        internal IKernelLeakReportSource Reports => services.GetRequiredService<IKernelLeakReportSource>();
        internal Fixture(bool rawSamples = false, bool failedAttribution = false, KernelLeakOptions? options = null)
        {
            var collection = new ServiceCollection(); collection.AddLogging();
            var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
            var configured = options ?? Fast;
            var plan = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            {
                var value = FreedomPointTestFactory.Point(root, "resource-manager/backend/monitoring/build/complex/0")["value"]!;
                value["interval_milliseconds"] = configured.IntervalMilliseconds;
                value["series_capacity"] = configured.SeriesCapacity;
                value["maximum_holders"] = configured.MaximumHolders;
                value["minimum_samples"] = configured.MinimumSamples;
                value["minimum_observation_seconds"] = configured.MinimumObservationSeconds;
                value["handle_floor"] = configured.HandleFloor;
                value["minimum_growth"] = configured.MinimumGrowth;
                value["edge_percentage"] = configured.EdgePercentage;
            });
            plans.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = plan });
            collection.AddSingleton(plans);
            collection.AddSingleton<IResourceBreakdownPushSource>(Processes);
            collection.AddKernelLeakObservation();
            collection.AddSingleton<IKernelLeakAttributionReader>(new RecordedAttribution(failedAttribution));
            if (rawSamples) { Raw = new(); collection.AddSingleton<IKernelLeakSampleSource>(Raw); }
            services = collection.BuildServiceProvider();
        }
        internal async Task<KernelLeakReport> Deliver(IAsyncEnumerator<KernelLeakReport> subscription, int second, int? value, string birth = "42")
        {
            var at = DateTimeOffset.UnixEpoch.AddSeconds(second);
            Processes.Push(new(at, []) { ProcessFields = new(at, null, [ProcessMetricFieldRegistry.Find(KernelLeakProcessSampleSource.HandleField)!],
                value is null ? [] : [new(7, birth, [new(KernelLeakProcessSampleSource.HandleField, new MetricInteger((ulong)value.Value), null)])]) });
            do { Assert.True(await subscription.MoveNextAsync()); } while (subscription.Current.CapturedAt != at);
            return subscription.Current;
        }
        public void Dispose() => services.Dispose();
    }
    internal sealed class RecordedProcesses : IResourceBreakdownPushSource
    {
        private readonly Channel<ResourceBreakdownSnapshot> channel = Channel.CreateBounded<ResourceBreakdownSnapshot>(16);
        internal int Starts, Active;
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? Holder;
        internal TimeSpan Interval;
        internal void Push(ResourceBreakdownSnapshot snapshot) => Assert.True(channel.Writer.TryWrite(snapshot));
        public async IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(string subscriptionId, ResourceBreakdownSampleRequest request,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.Equal([KernelLeakProcessSampleSource.HandleField], request.MetricIds);
            Holder = subscriptionId; Interval = interval; Interlocked.Increment(ref Starts); Interlocked.Increment(ref Active);
            try { await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken)) yield return snapshot; }
            finally { Interlocked.Decrement(ref Active); Released.TrySetResult(); }
        }
    }
    private sealed class RawSamples : IKernelLeakSampleSource
    {
        private readonly Channel<KernelLeakSampleFrame> channel = Channel.CreateBounded<KernelLeakSampleFrame>(16);
        internal void Push(KernelLeakSampleFrame frame) => Assert.True(channel.Writer.TryWrite(frame));
        public IAsyncEnumerable<KernelLeakSampleFrame> SubscribeAsync(string holder, TimeSpan interval, CancellationToken cancellationToken)
            => channel.Reader.ReadAllAsync(cancellationToken);
    }
    private sealed class RecordedAttribution(bool fail) : IKernelLeakAttributionReader
    {
        public IReadOnlyList<KernelLeakHolder> ReadBatch(IReadOnlyList<KernelLeakHolder> holders, CancellationToken cancellationToken = default)
            => fail ? throw new IOException("Recorded metadata query failure.") : holders.Select(holder => holder with { Name = "recorded-holder", ImagePath = "C:\\recorded\\holder.exe", ProductName = "recorded-product",
                ServiceNames = ["recorded-service"], ImageStatus = KernelLeakMetadataStatus.Current, ServiceStatus = KernelLeakMetadataStatus.Current }).ToArray();
    }
}