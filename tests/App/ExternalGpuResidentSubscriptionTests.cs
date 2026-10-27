using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class ExternalGpuResidentSubscriptionTests
{
    [Fact]
    public void ProductionSamplersOwnGpuDemandWithoutSmartOrUiAndReleaseBothLeases()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        using var hardware = CreateHardwareSampler(slots);
        using var processes = new WindowsResourceBreakdownSampler(hardware, null!, null!, null!, null!,
            slots.Provider, slots.Owner, new PdhProcessGpuReader());
        var observations = new ExternalGpuResidentObservation(hardware, processes, hardware, processes);

        using (observations.AcquireInventorySubscription("external-gpu-test:prepare"))
        {
            Assert.NotEmpty(Sources(slots.Owner, 1));
            Assert.Empty(Sources(slots.Owner, 3));
        }
        Assert.Empty(Sources(slots.Owner, 1));

        var lease = observations.AcquireSubscription("external-gpu-test:execute");
        Assert.NotEmpty(Sources(slots.Owner, 1));
        var processDemand = Assert.Single(Sources(slots.Owner, 3));
        Assert.Equal(1000UL, processDemand.ExplicitIntervalMilliseconds);
        Assert.All(Sources(slots.Owner, 1), source => Assert.Equal(1000UL, source.ExplicitIntervalMilliseconds));
        lease.Dispose();
        lease.Dispose();
        Assert.Empty(Sources(slots.Owner, 1));
        Assert.Empty(Sources(slots.Owner, 3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationWaitsForNewInventoryAndReleasesDemandOnDeliveryOrCancellation(bool cancel)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        using var state = new JsonHostManagerRollbackStateStore(Path.Combine(files.Root, "state.json"),
            TimeProvider.System, TimeSpan.FromMinutes(1));
        var source = new RecordedSources();
        var runtime = new WindowsExternalGpuPlacementRuntime(files, new(source, source, source, source),
            NullLogger<WindowsExternalGpuPlacementRuntime>.Instance, state);
        using var stop = new CancellationTokenSource();
        var preparation = runtime.PrepareAsync([new(123, 456, "fixture", @"C:\fixture.exe")], stop.Token);

        Assert.False(preparation.IsCompleted);
        Assert.Equal(1, source.InventoryLeases);
        Assert.Equal(1, source.StreamLeases);
        Assert.Equal(0, source.ProcessLeases);
        Assert.Equal(0, source.Reads);
        if (cancel)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        }
        else
        {
            source.Inventory.Writer.TryWrite(new(DateTimeOffset.UtcNow, HardwareMetricTestData.EmptyCpu,
                HardwareMetricTestData.EmptyMemory, HardwareMetricTestData.EmptyVirtualMemory, [],
                HardwareMetricTestData.EmptyGpuInventory, new Dictionary<string, MetricReadingValue>())
            {
                Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
                {
                    ["system.gpu.inventory"] = new("system.gpu.inventory", SamplingObservationStatus.Unavailable,
                        1, DateTimeOffset.UtcNow.UtcTicks, 1, 1, 1, 1)
                    { LastAttemptAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks }
                }
            });
            Assert.Null(await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(0, source.InventoryLeases);
        Assert.Equal(0, source.StreamLeases);
        Assert.Equal(0, source.ProcessLeases);
    }

    [Fact]
    public void FailedProcessDemandReleasesTheAlreadyAcquiredInventoryLease()
    {
        var source = new RecordedSources { RejectProcessDemand = true };
        var observations = new ExternalGpuResidentObservation(source, source, source, source);
        Assert.Throws<InvalidOperationException>(() => observations.AcquireSubscription("external-gpu-test"));
        Assert.Equal(0, source.InventoryLeases);
    }

    [Fact]
    public async Task ExecutionOwnsBothDemandsWhileWaitingAndCancellationReleasesThem()
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        using var state = new JsonHostManagerRollbackStateStore(Path.Combine(files.Root, "state.json"),
            TimeProvider.System, TimeSpan.FromMinutes(1));
        var source = new RecordedSources();
        var runtime = new WindowsExternalGpuPlacementRuntime(files, new(source, source, source, source),
            NullLogger<WindowsExternalGpuPlacementRuntime>.Instance, state);
        var process = new GpuPlacementProcessInstance(123, 456, "fixture", @"C:\fixture.exe");
        var request = new RunningGpuPlacementActionRequest("target", "software", "Software", [process],
            "automatic-placement", 23, GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover);
        var plan = new RunningGpuPlacementActionPlan(request, [], new Dictionary<int, GpuGraphicsApi>())
            { External = new(process, process) };
        var deadline = checked((ulong)Environment.TickCount64 + 10000);
        var execution = new RunningGpuPlacementExecution(1,
            _ => throw new InvalidOperationException("No device action should run."),
            (_, _) => throw new InvalidOperationException("No injection should run."),
            (_, _, _) => throw new InvalidOperationException("No callback should run."))
            { RecreationDeadlineMilliseconds = deadline, CleanupDeadlineMilliseconds = deadline + 1000 };
        using var stop = new CancellationTokenSource();
        var attempt = runtime.ExecuteAsync(plan, execution, stop.Token);

        Assert.False(attempt.IsCompleted);
        Assert.Equal(1, source.InventoryLeases);
        Assert.Equal(1, source.ProcessLeases);
        Assert.Equal(1, source.StreamLeases);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Equal(0, source.InventoryLeases);
        Assert.Equal(0, source.ProcessLeases);
        Assert.Equal(0, source.StreamLeases);
        Assert.False(runtime.HasUnreleasedControl);
    }

    private static WindowsHardwareMetricSampler CreateHardwareSampler(HostManagerSamplingSubscriptionTestFixture slots)
        => new(cpuZone: null!, memoryZone: null!, virtualMemoryZone: null!, gpuAdapterOrderZone: null!,
            cpuFrequencyZone: null!, gpuEngineZone: null!, systemIoZone: null!, nvidiaZone: null!,
            nvidiaNvapiZone: null!, amdAdlxZone: null!, amdSmuZone: null!,
            integratedGpuSensorRoute: new AmdIntegratedGpuSensorRoute(), platformSensorReader: null!,
            metricSnapshotOwner: null!, metricCatalog: new HardwareMetricCatalogState(),
            monitoringSourceZoneRegistry: new MonitoringSourceZoneRegistry([]),
            samplingSubscriptionOwner: slots.Owner, runtimePlanProvider: slots.Provider,
            logger: NullLogger<WindowsHardwareMetricSampler>.Instance);

    private static NativeSamplingSubscriptionSourceView[] Sources(HostManagerSamplingSubscriptionOwner owner, int role)
        => owner.Execute(role, session =>
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
            };
            var sources = new NativeSamplingSubscriptionSourceView[checked((int)session.Capacity.SnapshotSourceCapacity)];
            var items = new NativeSamplingSubscriptionItemState[checked((int)session.Capacity.SnapshotItemCapacity)];
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return sources.Take(checked((int)header.ActiveSourceCount)).ToArray();
        });

    private sealed class RecordedSources : IMetricSnapshotObservationSource, ISchedulingProcessFactObservationSource,
        IMetricSnapshotPushSource, IResourceBreakdownPushSource
    {
        internal readonly Channel<HardwareMetricSnapshot> Inventory = Channel.CreateUnbounded<HardwareMetricSnapshot>();
        internal int InventoryLeases, StreamLeases, ProcessLeases, Reads;
        internal bool RejectProcessDemand;
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) { Reads++; return null; }
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request) => throw new InvalidOperationException();
        public IDisposable AcquireSubscription(string holder, MetricSampleRequest request, TimeSpan interval)
        {
            Assert.StartsWith("external-gpu", holder);
            Assert.Equal(["system.gpu.inventory"], request.Ids);
            Assert.Equal(TimeSpan.FromSeconds(1), interval);
            InventoryLeases++;
            return new Lease(() => InventoryLeases--);
        }
        public IDisposable AcquireSubscription(string holder, SchedulingProcessMetricMask mask, TimeSpan interval)
        {
            Assert.Equal(SchedulingProcessMetricMask.GpuUsage | SchedulingProcessMetricMask.GpuDedicatedMemory, mask);
            if (RejectProcessDemand) throw new InvalidOperationException("Recorded process admission failure.");
            ProcessLeases++;
            return new Lease(() => ProcessLeases--);
        }
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string holder, MetricSampleRequest request,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken token)
        {
            StreamLeases++;
            try { yield return await Inventory.Reader.ReadAsync(token); }
            finally { StreamLeases--; }
        }
        public IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(string holder, ResourceBreakdownSampleRequest request,
            TimeSpan interval, CancellationToken token) => throw new InvalidOperationException("Preparation only needs inventory.");
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) release(); }
    }
}
