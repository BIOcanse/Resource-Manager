using System.Collections.Immutable;
using System.Diagnostics;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.StateItems;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class HostProcessPeriodicSubscriptionTests
{
    private const string PrivateCommitField = "process_all_private_commit_actual";

    [Fact]
    public async Task ProcessFieldAndSnapshotStreamsSurviveAProducerFaultAndControllerRestart()
    {
        using var fixture = await Fixture.CreateAsync();
        var backend = new ProducerSubscriptionCurrentBackend(fixture.Backend);
        var selection = new ProcessFieldReadWriteBackend.Selection(fixture.Field, fixture.Api.ProcessId,
            fixture.Api.CreationFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ProcessMetricFieldRegistry.Find(PrivateCommitField)!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = fixture.Sampler.SubscribeFieldAsync("held-through-fault", [fixture.Field],
            [selection], TimeSpan.FromMilliseconds(100), FieldSubscriptionOptions.Default, backend, timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        var pending = stream.MoveNextAsync().AsTask();
        await using var snapshots = fixture.Sampler.SubscribeAsync("snapshot-held-through-fault",
            ResourceBreakdownSampleRequest.ForResourceTable([PrivateCommitField], new Dictionary<string, string>()),
            TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token);
        var pendingSnapshot = snapshots.MoveNextAsync().AsTask();
        await using var controller = await ProducerSubscriptionRestartHost<WindowsResourceBreakdownSampler>
            .StartAsync(fixture.Sampler, static producer => producer.CaptureSamplingWorkerTask(),
                BlockUids.WindowsResourceBreakdownSampler, StateItemUids.SamplingResourceBreakdownInUse,
                () => fixture.SamplingOwner.Activity.HasInUseSubscriptions(3), backend);
        var firstRun = await controller.FirstRun;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstRun.WaitAsync(timeout.Token));
        Assert.Equal(ProducerSubscriptionCurrentBackend.FailureMessage, failure.Message);
        await controller.AssertControllerRestartedAsync();
        Assert.True(await pending.WaitAsync(timeout.Token));
        Assert.True(await pendingSnapshot.WaitAsync(timeout.Token));
        Assert.Equal(MetricInteger.Parse("11"), Assert.Single(stream.Current).Value?.Integer);
        Assert.True(fixture.Api.MemoryReads >= 1); // Discovery supplies original current; shared delivery does not require a new query in each run.
    }

    [Fact]
    public async Task ProcessFieldDemandAcceptedWhileStoppingNotifiesActivityAndSurvivesNextEntry()
    {
        using var fixture = await Fixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var activity = new List<bool>();
        fixture.SamplingOwner.Activity.RoleChanged += role =>
        {
            if (role == 3) activity.Add(fixture.SamplingOwner.Activity.HasInUseSubscriptions(3));
        };
        var first = fixture.Backend.Acquire("first-run", [fixture.Field], TimeSpan.FromMilliseconds(100));
        fixture.Api.BlockNextMemoryRead();
        await fixture.Sampler.StartAsync(CancellationToken.None);
        await fixture.Api.MemoryReadBlocked.WaitAsync(timeout.Token);
        var stop = fixture.Sampler.StopAsync(CancellationToken.None);
        try
        {
            Assert.False(stop.IsCompleted);
            first.Dispose();
            Assert.False(fixture.SamplingOwner.Activity.HasInUseSubscriptions(3));
            await using var late = fixture.Backend.SubscribeAsync("accepted-while-stopping", [fixture.Field],
                TimeSpan.FromMilliseconds(100), new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
                .GetAsyncEnumerator(timeout.Token);
            var pending = late.MoveNextAsync().AsTask();
            Assert.False(pending.IsCompleted);
            Assert.True(fixture.SamplingOwner.Activity.HasInUseSubscriptions(3));
            Assert.Equal(new[] { true, false, true }, activity);
            fixture.Api.PrivateCommit = 37;
            fixture.Api.ReleaseBlockedMemoryRead();
            await stop.WaitAsync(timeout.Token);
            Assert.False(pending.IsCompleted);
            await fixture.Sampler.StartAsync(CancellationToken.None);
            Assert.True(await pending.WaitAsync(timeout.Token));
            Assert.Equal(MetricInteger.Parse("37"), Assert.Single(late.Current).Value?.Integer);
            await late.DisposeAsync();
            Assert.False(fixture.SamplingOwner.Activity.HasInUseSubscriptions(3));
            Assert.Equal(new[] { true, false, true, false }, activity);
        }
        finally { first.Dispose(); fixture.Api.ReleaseBlockedMemoryRead(); await fixture.Sampler.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RoleSixCpuResidencyIsNotAdmittedByTheRoleThreeProcessBackend()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.DoesNotContain(fixture.Backend.Catalog.Fields,
            static capability => capability.Field.FieldName == "process_all_cpu_residency_attributed");

        var cpuField = new FieldReference(fixture.Field.DeviceIdentity,
            "process_all_cpu_residency_attributed");
        var memoryReads = fixture.Api.MemoryReads;
        Assert.Throws<ArgumentException>(() => fixture.Backend.Acquire("role-six-cpu",
            [cpuField], TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            _ = await fixture.Backend.CollectAsync([cpuField], CancellationToken.None);
        });
        Assert.Equal(memoryReads, fixture.Api.MemoryReads);
    }

    [Fact]
    public async Task HighestFrequencyKeepsIndependentDeliveryPeriodsOnTheOriginalProcessCurrent()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Sampler.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var fast = fixture.Backend.SubscribeAsync("fast", [fixture.Field],
            TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token);
        await using var slow = fixture.Backend.SubscribeAsync("slow", [fixture.Field],
            TimeSpan.FromMilliseconds(2000), timeout.Token).GetAsyncEnumerator(timeout.Token);

        var fastFirst = fast.MoveNextAsync().AsTask();
        var slowFirst = slow.MoveNextAsync().AsTask();
        Assert.True(await fastFirst.WaitAsync(TimeSpan.FromSeconds(4)));
        var firstFast = Assert.Single(fast.Current);
        Assert.Equal(fixture.Field, firstFast.Field);
        Assert.Equal($"windows-process|{fixture.Api.ProcessId}:{fixture.Api.CreationFileTime}",
            firstFast.Field.DeviceIdentity);
        Assert.Equal(MetricInteger.Parse("11"), firstFast.Value?.Integer);
        Assert.True(await slowFirst.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Equal(fixture.Field, Assert.Single(slow.Current).Field);

        var slowSecond = slow.MoveNextAsync().AsTask();
        var fastSecond = fast.MoveNextAsync().AsTask();
        Assert.True(await fastSecond.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(slowSecond.IsCompleted,
            "The slower subscriber must keep its own period while the shared process current continues updating.");
        fixture.Api.PrivateCommit = 29;
        // A delivery may still carry the shared current sampled before the change; the next sample carries 29.
        MetricInteger? fastValue;
        do
        {
            Assert.True(await fast.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            fastValue = Assert.Single(fast.Current).Value?.Integer;
        }
        while (fastValue != MetricInteger.Parse("29") && !timeout.IsCancellationRequested);
        Assert.Equal(MetricInteger.Parse("29"), fastValue);

        Assert.True(await slowSecond.WaitAsync(TimeSpan.FromSeconds(6)));
        var slowReading = Assert.Single(slow.Current);
        Assert.Equal(fixture.Field, slowReading.Field);
        Assert.Equal(MetricInteger.Parse("29"), slowReading.Value?.Integer);
    }

    [Fact]
    public async Task FreshOnTriggerRunsTheOriginalQueryBetweenSharedCaptureBoundaries()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Sampler.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var shared = fixture.Backend.SubscribeAsync("shared", [fixture.Field],
            TimeSpan.FromMilliseconds(300), timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await shared.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Equal(MetricInteger.Parse("11"), Assert.Single(shared.Current).Value?.Integer);
        var readsAtSharedBoundary = fixture.Api.MemoryReads;

        await using var fresh = fixture.Backend.SubscribeAsync("fresh", [fixture.Field],
            TimeSpan.FromMilliseconds(200), new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        var freshFirst = fresh.MoveNextAsync().AsTask();
        fixture.Api.PrivateCommit = 37;

        Assert.True(await freshFirst.WaitAsync(TimeSpan.FromSeconds(3)));
        var reading = Assert.Single(fresh.Current);
        Assert.Equal(fixture.Field, reading.Field);
        Assert.Equal(MetricInteger.Parse("37"), reading.Value?.Integer);
        Assert.True(fixture.Api.MemoryReads > readsAtSharedBoundary,
            "The fresh trigger occurs before the shared 300 ms source's next query and must invoke the original reader.");
    }

    [Fact]
    public async Task FailedOriginalProcessSourceProducesAnUnavailableFreshReading()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Sampler.StartAsync(CancellationToken.None);
        fixture.Api.FailMemoryRead = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await using var fresh = fixture.Backend.SubscribeAsync("failed-fresh", [fixture.Field],
            TimeSpan.FromMilliseconds(100), new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        Assert.True(await fresh.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));
        var reading = Assert.Single(fresh.Current);
        Assert.Equal(fixture.Field, reading.Field);
        Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
        Assert.Null(reading.Value);
    }

    [Fact]
    public async Task BusyFreshCaptureIsUnavailableAndStopPreservesStreamWithoutDisablingDirectCapture()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Sampler.StartAsync(CancellationToken.None);
        fixture.Api.BlockNextMemoryRead();
        using var directCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var directCapture = Task.Run(async () => await fixture.Backend
            .CollectAsync([fixture.Field], directCancellation.Token));
        await fixture.Api.MemoryReadBlocked.WaitAsync(TimeSpan.FromSeconds(3));

        using var streamCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var fresh = fixture.Backend.SubscribeAsync("busy-fresh", [fixture.Field],
            TimeSpan.FromMilliseconds(100), new(FieldSubscriptionSamplingMode.FreshOnTrigger),
            streamCancellation.Token).GetAsyncEnumerator(streamCancellation.Token);
        Assert.True(await fresh.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));
        var unavailable = Assert.Single(fresh.Current);
        Assert.Equal(fixture.Field, unavailable.Field);
        Assert.Equal(FieldObservationStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Value);

        using var canceledToken = new CancellationTokenSource();
        await using var canceled = fixture.Backend.SubscribeAsync("canceled-fresh", [fixture.Field],
            TimeSpan.FromMilliseconds(100), new(FieldSubscriptionSamplingMode.FreshOnTrigger),
            canceledToken.Token).GetAsyncEnumerator(canceledToken.Token);
        Assert.True(await canceled.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        canceledToken.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.MoveNextAsync().AsTask());

        await fresh.DisposeAsync();
        await using var stopped = fixture.Backend.SubscribeAsync("stopped", [fixture.Field],
            TimeSpan.FromSeconds(4), streamCancellation.Token).GetAsyncEnumerator(streamCancellation.Token);
        var stoppedRound = stopped.MoveNextAsync().AsTask();
        await fixture.Sampler.StopAsync(CancellationToken.None);
        Assert.False(stoppedRound.IsCompleted);

        fixture.Api.ReleaseBlockedMemoryRead();
        _ = await directCapture.WaitAsync(TimeSpan.FromSeconds(4));
        var readsBeforeDirectCapture = fixture.Api.MemoryReads;
        var direct = await fixture.Backend.CollectAsync([fixture.Field], CancellationToken.None);
        Assert.Equal(MetricInteger.Parse("11"), Assert.Single(direct).Value?.Integer);
        Assert.True(fixture.Api.MemoryReads > readsBeforeDirectCapture);
        await fixture.Sampler.StartAsync(CancellationToken.None);
        Assert.True(await stoppedRound.WaitAsync(TimeSpan.FromSeconds(6)));
        Assert.Equal(MetricInteger.Parse("11"), Assert.Single(stopped.Current).Value?.Integer);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HostManagerSamplingSubscriptionTestFixture subscriptions = new();

        private Fixture()
        {
            Api = new RecordedProcessApi();
            Sampler = new WindowsResourceBreakdownSampler(
                metricSampler: null!,
                processAttributionCatalogProvider: null!,
                residualBreakdownProvider: new StaticResourceResidualBreakdownProvider(),
                physicalDiskIoAttributionReader: null!,
                networkAttributionReader: null!,
                runtimePlanProvider: subscriptions.Provider,
                samplingSubscriptionOwner: subscriptions.Owner,
                processGpuReader: new PdhProcessGpuReader(),
                processInventoryReader: new RecordedProcessInventoryReader(),
                processBasicMetricReader: new WindowsProcessBasicMetricReader(Api));
            Backend = new HostProcessFieldBackend(Sampler);
        }

        internal RecordedProcessApi Api { get; }
        internal WindowsResourceBreakdownSampler Sampler { get; }
        internal HostManagerSamplingSubscriptionOwner SamplingOwner => subscriptions.Owner;
        internal HostProcessFieldBackend Backend { get; }
        internal FieldReference Field { get; private set; }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                await fixture.Backend.DiscoverAsync(CancellationToken.None);
                fixture.Field = Assert.Single(fixture.Backend.Catalog.Fields,
                    item => item.Field.FieldName == PrivateCommitField).Field;
                Assert.Equal($"windows-process|{fixture.Api.ProcessId}:{fixture.Api.CreationFileTime}",
                    fixture.Field.DeviceIdentity);
                Assert.Equal(MetricInteger.Parse("11"), fixture.Backend.ReadCached(fixture.Field).Value?.Integer);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Sampler.Dispose();
            subscriptions.Dispose();
            Api.Dispose();
        }
    }

    private sealed class RecordedProcessInventoryReader : IWindowsProcessInventoryReader
    {
        public Process[] GetProcesses() => [Process.GetCurrentProcess()];
    }

    private sealed class RecordedProcessApi : IWindowsProcessBasicMetricApi, IDisposable
    {
        private readonly ManualResetEventSlim releaseMemoryRead = new(initialState: true);
        private TaskCompletionSource memoryReadBlocked = NewSignal();
        private long privateCommit = 11;
        private int blockNextMemoryRead;
        private int failMemoryRead;
        private int memoryReads;

        internal RecordedProcessApi()
        {
            using var process = Process.GetCurrentProcess();
            ProcessId = process.Id;
            CreationFileTime = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        }

        internal int ProcessId { get; }
        internal long CreationFileTime { get; }
        internal int MemoryReads => Volatile.Read(ref memoryReads);
        internal long PrivateCommit
        {
            get => Interlocked.Read(ref privateCommit);
            set => Interlocked.Exchange(ref privateCommit, value);
        }
        internal Task MemoryReadBlocked => memoryReadBlocked.Task;
        internal bool FailMemoryRead
        {
            get => Volatile.Read(ref failMemoryRead) != 0;
            set => Volatile.Write(ref failMemoryRead, value ? 1 : 0);
        }

        public IntPtr Open(int processId)
            => processId == ProcessId ? new IntPtr(42) : IntPtr.Zero;

        public IntPtr Open(int processId, ProcessMetricReadMask mask)
            => Open(processId);

        public bool Times(IntPtr handle, out ulong creation, out ulong kernel, out ulong user)
        {
            creation = checked((ulong)CreationFileTime);
            kernel = 75_000_000;
            user = 22_500_000;
            return handle == new IntPtr(42);
        }

        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet)
        {
            Interlocked.Increment(ref memoryReads);
            if (Interlocked.Exchange(ref blockNextMemoryRead, 0) != 0)
            {
                memoryReadBlocked.TrySetResult();
                _ = releaseMemoryRead.Wait(TimeSpan.FromSeconds(7));
            }
            if (FailMemoryRead || handle != new IntPtr(42)) return null;
            return new(1_024, checked((ulong)PrivateCommit), 2_048, privateWorkingSet ? 512 : null);
        }

        public ProcessIoObservation? Io(IntPtr handle)
            => handle == new IntPtr(42) ? new(1, 2, 3, 4, 5, 6) : null;

        public uint? HandleCount(IntPtr handle) => handle == new IntPtr(42) ? 7u : null;
        public uint? ThreadCount(IntPtr handle, int processId) => handle == new IntPtr(42) ? 8u : null;
        public IReadOnlyDictionary<int, uint?> ThreadCounts(IReadOnlyDictionary<int, IntPtr> handles)
            => handles.Keys.ToDictionary(static processId => processId, static _ => (uint?)8);
        public string? ImagePath(IntPtr handle) => handle == new IntPtr(42) ? @"C:\Recorded\process.exe" : null;
        public string? CommandLine(IntPtr handle) => handle == new IntPtr(42) ? "recorded-process --sample" : null;
        public uint? ParentPid(IntPtr handle) => handle == new IntPtr(42) ? 1u : null;
        public string? UserSid(IntPtr handle) => handle == new IntPtr(42) ? "S-1-5-18" : null;
        public bool ThreadCountLive(IntPtr handle, int processId) => handle == new IntPtr(42) && processId == ProcessId;
        public bool Close(IntPtr handle) => handle == new IntPtr(42);

        internal void BlockNextMemoryRead()
        {
            memoryReadBlocked = NewSignal();
            releaseMemoryRead.Reset();
            Volatile.Write(ref blockNextMemoryRead, 1);
        }

        internal void ReleaseBlockedMemoryRead() => releaseMemoryRead.Set();

        public void Dispose() => releaseMemoryRead.Dispose();

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
