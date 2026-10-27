using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.MemoryProtection;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Platform;

namespace Resource_Manager_APP.Tests;

public sealed class SubscriptionHolderFanCompositionTests
{
    [Fact]
    public async Task SoftwareFanCurveSamplesAndDrivesWithoutSmartOrUiSubscriptions()
    {
        await using var fixture = new TemperatureFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await fixture.StartAsync(deadline.Token);
        var backend = new ProbeFanBackend();
        await using var client = new FanControlCoreClient(new FanTestEnvironment(),
            (_, _) => new LibraryFanSession(new FanControlCore.Discovery.FanCore(backend)),
            NullLogger<FanControlCoreClient>.Instance);
        using var feed = new FanCurveTemperatureFeed(fixture.Sampler, client, fixture.Owner.Catalog);
        await feed.StartAsync(deadline.Token);
        try
        {
            Assert.True(Assert.Single(client.Describe()).SupportsSoftwareCurve);
            Assert.Equal(0u, fixture.ReadDemand().ActiveSourceCount);
            Assert.Equal(0, fixture.Reads);
            var writer = new FanControlWriter(client, sampler: fixture.Sampler, catalog: fixture.Owner.Catalog);
            var target = new ControlObject("fan:oem0", ControlObjectKinds.Fan, "Fixture",
                new(ControlOperatingSystems.Windows, ControlVendors.Oem), []);
            var capability = new ControlCapability("fan.curve", ControlValueKinds.Curve, true);
            var setting = new ControlSetting("fan.curve", Curve: [new(60, 40), new(100, 100)],
                CurveExecution: ControlFanCurveExecutions.Software);
            Assert.Equal(ControlApplyStatuses.Applied,
                (await writer.WriteAsync(target, capability, setting, deadline.Token)).Status);

            await WaitUntilAsync(() => backend.Read()[0].DutyPercent == 40, deadline.Token);
            Assert.True(fixture.HardwareMonitorProbes > 0);
            var demand = fixture.ReadDemand();
            Assert.Equal(1u, demand.ActiveSourceCount);
            Assert.Equal(1u, demand.PersistentSourceCount);
            Assert.Equal(1u, demand.MembershipCount);
            Assert.Equal(100ul, demand.IntervalMilliseconds);
            var firstReads = fixture.Reads;

            fixture.Temperature = 100;
            await WaitUntilAsync(() => backend.Read()[0].DutyPercent == 100, deadline.Token);
            Assert.True(fixture.Reads > firstReads, "The production sampler did not collect the changed temperature.");
            Assert.Equal(100d, fixture.Sampler.ReadLatest(TemperatureFixture.Request)!.Items["cpu.temperature"].NumericValue);
            Assert.False(backend.Read()[0].AutomaticControl);

            Assert.True(await writer.RestoreAsync(target, capability, new HashSet<string>(), deadline.Token, setting));
            Assert.True(backend.Read()[0].AutomaticControl);
            Assert.False(client.NeedsTemperatures());
            Assert.Equal(0u, fixture.ReadDemand().ActiveSourceCount);
            Assert.Equal(0u, fixture.ReadDemand().MembershipCount);
            // Allow a capture already accepted before withdrawal to finish, then
            // prove the running sampler has no remaining temperature consumer.
            await Task.Delay(200, deadline.Token);
            var stoppedReads = fixture.Reads;
            var stoppedProbes = fixture.HardwareMonitorProbes;
            await Task.Delay(250, deadline.Token);
            Assert.Equal(stoppedReads, fixture.Reads);
            Assert.Equal(stoppedProbes, fixture.HardwareMonitorProbes);
            Assert.Empty(fixture.UnexpectedReads);
        }
        finally { await feed.StopAsync(CancellationToken.None); }
        Assert.Equal(0u, fixture.ReadDemand().ActiveSourceCount);
    }

    [Fact]
    public async Task HardwareSingleFetchSamplesNewTemperatureAndReleasesEveryDemandWithoutOtherSubscribers()
    {
        await using var fixture = new TemperatureFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.StartAsync(deadline.Token);
        await fixture.Sampler.CaptureSnapshotAsync(TemperatureFixture.Request, deadline.Token);
        Assert.Equal(60d, fixture.Sampler.ReadLatest(TemperatureFixture.Request)!.Items["cpu.temperature"].NumericValue);
        Assert.True(fixture.HardwareMonitorProbes > 0);
        Assert.Equal(0u, fixture.ReadDemand().ActiveSourceCount);

        foreach (var temperature in new[] { 75d, 100d })
        {
            var previousReads = fixture.Reads;
            fixture.Temperature = temperature;
            var sampled = await fixture.Sampler.GetSnapshotAsync(TemperatureFixture.Request, deadline.Token);
            Assert.Equal(temperature, sampled.Items["cpu.temperature"].NumericValue);
            Assert.True(fixture.Reads > previousReads);
            Assert.True(sampled.TryGetCurrentDataset("cpu.temperature", out _));
            Assert.Equal(0u, fixture.ReadDemand().ActiveSourceCount);
            Assert.Equal(0u, fixture.ReadDemand().MembershipCount);
        }
        Assert.Empty(fixture.UnexpectedReads);
    }

    [Fact]
    public async Task HardwareSingleFetchCompletionDoesNotCancelOrSlowContinuousTemperatureSubscription()
    {
        await using var fixture = new TemperatureFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.StartAsync(deadline.Token);
        await using (var stream = fixture.Sampler.SubscribeAsync("continuous-temperature",
            TemperatureFixture.Request, TimeSpan.FromMilliseconds(100), deadline.Token).GetAsyncEnumerator(deadline.Token))
        {
            Assert.True(await stream.MoveNextAsync());
            var demand = fixture.ReadDemand();
            Assert.Equal(1u, demand.ActiveSourceCount);
            Assert.Equal(100ul, demand.IntervalMilliseconds);
            fixture.Temperature = 80;
            var pending = fixture.Sampler.GetSnapshotAsync(TemperatureFixture.Request, deadline.Token);
            using (var whileFetching = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                whileFetching.CancelAfter(TimeSpan.FromMilliseconds(750));
                do { Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(whileFetching.Token)); }
                while (stream.Current.Items["cpu.temperature"].NumericValue != 80d);
                // A slower single fetch must not suspend deliveries from the
                // existing fast holder while waiting for its own delivery.
                Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(whileFetching.Token));
                Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(whileFetching.Token));
            }
            var fetched = await pending;
            Assert.Equal(80d, fetched.Items["cpu.temperature"].NumericValue);
            Assert.Equal(1u, fixture.ReadDemand().ActiveSourceCount);
            Assert.Equal(100ul, fixture.ReadDemand().IntervalMilliseconds);

            // The same enumerator and original 100 ms demand must continue
            // after the temporary 1 s holder has been released.
            fixture.Temperature = 95;
            using var cadence = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            cadence.CancelAfter(TimeSpan.FromMilliseconds(750));
            do { Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(cadence.Token)); }
            while (stream.Current.Items["cpu.temperature"].NumericValue != 95d);
            Assert.Equal(1u, fixture.ReadDemand().ActiveSourceCount);
            Assert.Equal(100ul, fixture.ReadDemand().IntervalMilliseconds);
        }
        Assert.Equal(0u, fixture.ReadDemand().ActiveSourceCount);
        Assert.Empty(fixture.UnexpectedReads);
    }

    [Fact]
    public async Task CancellingSingleFetchDoesNotCancelContinuousTemperatureSubscriptionOrSharedCollection()
    {
        await using var fixture = new TemperatureFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await fixture.StartAsync(deadline.Token);
        await using (var stream = fixture.Sampler.SubscribeAsync("continuous-temperature",
            TemperatureFixture.Request, TimeSpan.FromMilliseconds(100), deadline.Token).GetAsyncEnumerator(deadline.Token))
        {
            Assert.True(await stream.MoveNextAsync());
            fixture.BeforeRead = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("The shared read was not released.");
            };
            var pending = fixture.Sampler.GetSnapshotAsync(TemperatureFixture.Request, cancellation.Token);
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(pending.IsCompleted);
                Assert.Equal(2u, fixture.ReadDemand().ActiveSourceCount);
                Assert.Equal(100ul, fixture.ReadDemand().IntervalMilliseconds);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(deadline.Token));
                Assert.Equal(1u, fixture.ReadDemand().ActiveSourceCount);
                Assert.Equal(100ul, fixture.ReadDemand().IntervalMilliseconds);
                fixture.Temperature = 90;
            }
            finally { fixture.BeforeRead = null; release.Set(); }

            using var cadence = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            cadence.CancelAfter(TimeSpan.FromMilliseconds(750));
            do { Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(cadence.Token)); }
            while (stream.Current.Items["cpu.temperature"].NumericValue != 90d);
            fixture.Temperature = 100;
            do { Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(cadence.Token)); }
            while (stream.Current.Items["cpu.temperature"].NumericValue != 100d);
            Assert.Equal(1u, fixture.ReadDemand().ActiveSourceCount);
        }
        Assert.Equal(0u, fixture.ReadDemand().ActiveSourceCount);
        Assert.Empty(fixture.UnexpectedReads);
    }

    [Fact]
    public async Task CancelledHardwareSingleFetchReleasesItsDemandWhileTemperatureReadIsBlocked()
    {
        await using var fixture = new TemperatureFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.BeforeRead = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("The fake temperature read was not released.");
        };
        await fixture.StartAsync(deadline.Token);
        var pending = fixture.Sampler.GetSnapshotAsync(TemperatureFixture.Request, cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1u, fixture.ReadDemand().ActiveSourceCount);
            Assert.Equal(1u, fixture.ReadDemand().MembershipCount);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(deadline.Token));
            Assert.Equal(0u, fixture.ReadDemand().ActiveSourceCount);
            Assert.Equal(0u, fixture.ReadDemand().MembershipCount);
        }
        finally
        {
            release.Set();
            await fixture.Sampler.StopAsync(CancellationToken.None).WaitAsync(deadline.Token);
        }
        Assert.Empty(fixture.UnexpectedReads);
    }

    [Fact]
    public async Task MemoryProtectionStatusFetchesNewUnsignedCommitWithoutSmartOrUiSubscriptions()
    {
        await using var fixture = new TemperatureFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const ulong limit = 18014398509481987;
        ulong used = 1024;
        var reads = 0;
        fixture.ReadCommit = () =>
        {
            Interlocked.Increment(ref reads);
            return new(used, limit, 50, true);
        };
        await fixture.StartAsync(deadline.Token);
        var request = MetricSampleRequest.ForIds(["virtualMemory.usage", "virtualMemory.total", "virtualMemory.percent"]);
        await fixture.Sampler.CaptureSnapshotAsync(request, deadline.Token);
        var seeded = fixture.Sampler.ReadLatest(request)!;
        var usedItem = seeded.Items["virtualMemory.usage"];
        Assert.True(usedItem.IntegerValue?.ToBigInteger() == 1024,
            System.Text.Json.JsonSerializer.Serialize(new { usedItem, seeded.Datasets }));
        var unused = new UnusedProcessSources();
        var source = new HostMemoryProtectionSource(fixture.Sampler, unused, unused);

        foreach (var next in new[] { 9007199254740993ul, 9007199254740995ul })
        {
            used = next;
            var previousReads = Volatile.Read(ref reads);
            var current = await source.ReadCurrentCommitAsync(deadline.Token);
            var sampled = fixture.Sampler.ReadLatest(request)!;
            Assert.True(current == new HostMemoryCommit(next, limit, 50),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Expected = new HostMemoryCommit(next, limit, 50),
                    Actual = current,
                    Items = sampled.Items,
                    sampled.Datasets,
                    CompleteCommitItems = fixture.Sampler.ReadLatest(MetricSampleRequest.All)?.Items.Values
                        .Where(item => item.FieldCode is MetricFieldCode.CommitUsed or MetricFieldCode.CommitLimit or MetricFieldCode.CommitPercent)
                }));
            Assert.True(Volatile.Read(ref reads) > previousReads);
            Assert.Equal(0u, fixture.ReadDemand().ActiveSourceCount);
            Assert.Equal(0u, fixture.ReadDemand().MembershipCount);
        }
        // Inspect the retained projection explicitly: fixing an explicitly
        // requested capacity alias must not expose it to percent-only readers.
        var percentOnly = fixture.Sampler.ReadLatest(MetricSampleRequest.ForIds(["virtualMemory.percent"]))!;
        Assert.DoesNotContain(percentOnly.Items.Values, item => item.FieldCode == MetricFieldCode.CommitLimit);
        Assert.Equal(0, fixture.Reads);
        Assert.Equal(0, fixture.HardwareMonitorProbes);
        Assert.Empty(fixture.UnexpectedReads);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(20, token);
    }

    internal sealed class TemperatureFixture : IAsyncDisposable
    {
        internal static readonly MetricSampleRequest Request = MetricSampleRequest.ForIds(["cpu.temperature"]);
        internal readonly MetricSnapshotOwnerTestFixture.Fixture Owner = new(MetricSnapshotOwnerTestFixture.Topology([]));
        internal readonly ConcurrentQueue<string> UnexpectedReads = new();
        private double temperature = 60;
        private int reads;
        private int hardwareMonitorProbes;
        internal Action? BeforeRead;
        internal Func<VirtualMemoryMetrics>? ReadCommit;
        internal int Reads => Volatile.Read(ref reads);
        internal int HardwareMonitorProbes => Volatile.Read(ref hardwareMonitorProbes);
        internal double Temperature { set => Volatile.Write(ref temperature, value); }
        internal WindowsHardwareMetricSampler Sampler { get; }

        internal TemperatureFixture()
        {
            Sampler = new(cpuZone: null!, memoryZone: null!, virtualMemoryZone: null!,
                gpuAdapterOrderZone: null!, cpuFrequencyZone: null!, gpuEngineZone: null!, systemIoZone: null!,
                nvidiaZone: null!, nvidiaNvapiZone: null!, amdAdlxZone: null!, amdSmuZone: null!,
                integratedGpuSensorRoute: new AmdIntegratedGpuSensorRoute(), platformSensorReader: null!,
                metricSnapshotOwner: Owner.Owner, metricCatalog: Owner.Catalog,
                monitoringSourceZoneRegistry: new MonitoringSourceZoneRegistry([]),
                samplingSubscriptionOwner: Owner.Slots.Owner, runtimePlanProvider: Owner.Slots.Provider,
                logger: NullLogger<WindowsHardwareMetricSampler>.Instance, borrowedResources: Resources());
        }

        internal async Task StartAsync(CancellationToken token)
        {
            // This empty-topology catalog needs no measurement reads. The first
            // temperature request later probes its unverified source candidates.
            await Sampler.CaptureSnapshotAsync(MetricSampleRequest.CatalogProbe, token);
            Assert.Equal(0, Reads);
            Assert.Equal(0, HardwareMonitorProbes);
            Assert.Empty(UnexpectedReads);
            await Sampler.StartAsync(token);
        }

        internal (uint ActiveSourceCount, uint PersistentSourceCount, uint MembershipCount, ulong IntervalMilliseconds) ReadDemand()
            => Owner.Slots.Owner.Execute(1, session =>
            {
                var header = new NativeSamplingSubscriptionSnapshotHeader
                {
                    AbiVersion = NativeSamplingSubscriptionAbi.Version,
                    StructSize = checked((uint)Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
                };
                var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
                var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
                Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
                var activeItems = items.Take(checked((int)header.ItemOutputCount)).Where(item => item.SourceCount > 0).ToArray();
                if (header.ActiveSourceCount > 0) Assert.Single(activeItems);
                return (header.ActiveSourceCount, header.PersistentSourceCount, header.MembershipCount,
                    activeItems.Length == 0 ? 0ul : activeItems[0].EffectiveIntervalMilliseconds);
            });

        private Exception Unexpected(string source)
        {
            UnexpectedReads.Enqueue(source);
            return new InvalidOperationException("Unexpected source read: " + source);
        }

        private CpuSensorMetrics ReadTemperature(AmdSmuCpuSensorReadRequest request)
        {
            Assert.Equal(new AmdSmuCpuSensorReadRequest(false, false, false, true, false, false, false, false, false, false, false, false, false, false, false), request);
            Interlocked.Increment(ref reads);
            BeforeRead?.Invoke();
            return new(new("recorded-temperature", HardwareSensorProviderStates.Active, null),
                null, null, null, Volatile.Read(ref temperature));
        }

        private HardwareMonitorSensorSnapshot ProbeUnavailableHardwareMonitor()
        {
            // ExpandLegacyRequest probes unverified sources for cpu.temperature
            // through the original capture owner. This alternative supplies no
            // measurement; only the fake SMU producer can drive these tests.
            Interlocked.Increment(ref hardwareMonitorProbes);
            return new(new("recorded-unavailable-hardware-monitor", HardwareSensorProviderStates.Unavailable, null),
                null, null, null, null, null, null, null, [], []) { CatalogComplete = false };
        }

        private WindowsHardwareMetricResources Resources() => new()
        {
            Controls = new WindowsMetricControlResources
            {
                ControlReadCpuIdentity = () => throw Unexpected("control CPU identity"),
                ControlReadCpuTopology = () => throw Unexpected("control CPU topology"),
                ControlIsFixedChassis = () => throw Unexpected("control chassis")
            },
            ReadAmdSmu = ReadTemperature,
            ReadCpuCounters = () => throw Unexpected("CPU counters"), ReadMemory = () => throw Unexpected("memory"),
            ReadVirtualMemory = () => ReadCommit?.Invoke() ?? throw Unexpected("virtual memory"),
            ReadNumaAvailable = (_, _) => throw Unexpected("NUMA available"), ReadNumaCapacityMiB = (_, _) => throw Unexpected("NUMA capacity"),
            ReadNumaIdentities = () => throw Unexpected("NUMA identities"), ReadGpuInventory = () => throw Unexpected("GPU inventory"),
            ReadCpuFrequency = () => throw Unexpected("CPU frequency"), ReadGpuUsage = _ => throw Unexpected("GPU usage"),
            ReadNvmlAsync = (_, _, _) => throw Unexpected("NVML"), ReadNvmlFanIdentities = _ => throw Unexpected("NVML fan identities"),
            ReadNvmlFansAsync = (_, _, _) => throw Unexpected("NVML fans"),
            ReadNvapiAsync = (_, _, _) => throw Unexpected("NVAPI"), ReadNvapiDrs = (_, _, _) => throw Unexpected("DRS"),
            DiscoverNvapiDrs = (_, _) => throw Unexpected("DRS discovery"), ReadAdlxAsync = (_, _, _) => throw Unexpected("ADLX"),
            ReadAmdCcdIdentities = (_, _) => throw Unexpected("CCD identities"), ReadAmdCcd = (_, _) => throw Unexpected("CCD"),
            ReadIntelGpu = (_, _, _) => throw Unexpected("Intel GPU"), ReadIntelMsr = (_, _) => throw Unexpected("Intel MSR"),
            OpenIgclFanDirectory = (_, _, _) => throw Unexpected("IGCL directory"), ReadIgclFans = (_, _, _, _) => throw Unexpected("IGCL fans"),
            ReadNpuMemoryIdentities = _ => throw Unexpected("NPU memory identities"), ReadNpuMemory = (_, _, _, _) => throw Unexpected("NPU memory"),
            OpenNpuEngineDirectory = (_, _) => throw Unexpected("NPU directory"), ReadNpuEngines = (_, _, _, _) => throw Unexpected("NPU engines"),
            ReadBatteryIdentities = () => throw Unexpected("battery identities"), ReadBattery = (_, _) => throw Unexpected("battery"),
            ReadNetworkIdentities = () => throw Unexpected("network identities"), ReadNetworkInterfaces = (_, _) => throw Unexpected("network"),
            ReadSystemIo = _ => throw Unexpected("system IO"), ReadStorageIdentities = _ => throw Unexpected("storage identities"),
            ReadDiskPerformanceAsync = (_, _) => throw Unexpected("disk performance"), ReadFanIdentities = _ => throw Unexpected("fan identities"),
            ReadStorageSensors = () => throw Unexpected("storage sensors"), ReadNvmeSensors = _ => throw Unexpected("NVMe"),
            ReadAtaSensors = _ => throw Unexpected("ATA"), ReadHardwareMonitor = ProbeUnavailableHardwareMonitor,
            ReadNotebookFans = () => throw Unexpected("notebook fans"),
            SynchronizeNotebookDemand = demanded => { if (demanded) throw Unexpected("notebook fan demand"); }
        };

        public async ValueTask DisposeAsync()
        {
            try { await Sampler.StopAsync(CancellationToken.None); }
            finally { Sampler.Dispose(); Owner.Dispose(); }
        }
    }

    private sealed class UnusedProcessSources : IResourceBreakdownPushSource, ISchedulingProcessFactObservationSource
    {
        public IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(string subscriptionId,
            ResourceBreakdownSampleRequest request, TimeSpan interval, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A commit status read must not subscribe to process metrics.");
        public IDisposable AcquireSubscription(string subscriptionId, SchedulingProcessMetricMask metricMask, TimeSpan refreshInterval)
            => throw new InvalidOperationException("A commit status read must not acquire process facts.");
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request)
            => throw new InvalidOperationException("A commit status read must not read process facts.");
    }
}
