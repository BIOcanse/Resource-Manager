using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.Subscription;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class CpuResidencyOwnerLibraryIntegrationTests
{
    private const string Name = "process_all_cpu_residency_attributed";
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddSeconds(9);
    private static CpuResidencyCaptureConfiguration Configuration() => new(1000, 1024,
        new Dictionary<int, CpuResidencyCoreAccounting>
        {
            [0] = new("core:0", "ccd:0", 1, 1), [1] = new("core:1", "ccd:1", 1, 1)
        }.ToFrozenDictionary(), "recorded explicit physical core/CCD map");
    private static CpuLogicalProcessorResidencyScope[] Scopes() =>
        [new(0, "core:0", "ccd:0", Configuration().MappingProvenance), new(1, "core:1", "ccd:1", Configuration().MappingProvenance)];
    private static ProcessMetricFieldSnapshot Inventory(int pid = 100, string? birth = "1000")
        => new(At, null, [], [new(pid, birth, [])]);
    [Fact]
    public void IntegratedCpuGpuAndDiskScopesShareCurrentWithoutExpandingUnscopedDirectory()
    {
        Assert.Equal(23, (int)ProcessMetricFieldCode.CpuResidency);
        Assert.Equal(24, (int)ProcessMetricFieldCode.GpuResidency);
        Assert.Equal(34, ProcessMetricFieldRegistry.Definitions.Count);
        var basic = ProcessMetricFieldRegistry.Definitions.Where(field => field.DatasetId == ProcessMetricDatasetIds.BasicMetrics).ToArray();
        var current = new ProcessFieldCurrentState();
        current.Apply(basic.Select(field => field.Name), new(At, null, basic,
            [new(100, "1000", basic.Select(field => new ProcessMetricFieldValue(field.Name, null, null)).ToArray())]));
        current.Apply([Name], WindowsCpuResidencyProcessFields.Project([Name], Inventory(), Scopes(), Window()));
        var gpu = new ProcessGpuEngineScope(23, 0, 1);
        var interval = new ProcessGpuEngineInterval(At.AddMilliseconds(-100).ToFileTime(), At.ToFileTime(),
            10, 20, 100, ResourceManager.ReadWrite.Native.Pdh.NativePdhEngineClass.Compute);
        var gpuField = ProcessMetricFieldRegistry.Find(WindowsPdhProcessGpuFields.FieldName)!;
        current.Apply([gpuField.Name], new(At, null, [gpuField],
            [new(100, "1000", [new(gpuField.Name, null, 37)]) { GpuEngine = gpu, GpuInterval = interval }]));
        var disk = new PhysicalDiskIoScope(9, 0);
        var diskFields = ProcessMetricFieldRegistry.Definitions.Where(field => field.DatasetId == ProcessMetricDatasetIds.ObservedPhysicalDiskIo).ToArray();
        current.Apply(diskFields.Select(field => field.Name), new(At, 2000, diskFields,
            [new(100, "1000", diskFields.Select(field => new ProcessMetricFieldValue(field.Name, MetricInteger.Parse("7"), null)).ToArray()) { PhysicalDisk = disk }]));
        using var owner = new WindowsProcessFieldOwner(current);
        var catalog = owner.Backend.Catalog;
        Assert.Equal(30, catalog.Fields.Count(field => field.Field.DeviceIdentity == "windows-process|100:1000"));
        Assert.Equal(2, catalog.Fields.Count(field => field.Field.FieldName == Name));
        var gpuReference = catalog.Fields.Single(field => field.Field.FieldName == gpuField.Name).Field;
        Assert.Equal(37, owner.Backend.ReadCached(gpuReference).Value!.Number);
        var snapshot = owner.ReadCachedSnapshot()!;
        Assert.Equal(interval, snapshot.FindProcess(100, "1000", gpu: gpu)!.GpuInterval);
        Assert.NotNull(snapshot.FindProcess(100, "1000", logicalProcessor: Scopes()[0]));
        Assert.Equal(MetricInteger.Parse("7"), snapshot.FindProcess(100, "1000", disk)!.Values[0].IntegerValue);
        using var borrow = owner.BorrowSubscriptionCapture();
        var bindings = borrow.Bind([gpuReference, catalog.Fields.First(field => field.Field.FieldName == Name).Field]);
        Assert.Equal(new[] { ProcessMetricDatasetIds.GpuResidency, ProcessMetricDatasetIds.ObservedCpuResidency },
            bindings.Select(binding => binding.CollectionItemIds.Single()));
        Assert.Same(owner.Backend, borrow.Backend); Assert.Same(owner.Publications, borrow.Publications);
    }
    private static CpuCoreResidencySnapshot Window()
    {
        var plan = Configuration();
        var config = NativeKernelEtwTestResource.SmallConfiguration(qpcFrequency: 1000, maximumProcessors: 2);
        config.Residency.MaximumPairs = checked((uint)plan.MaximumActivePairs);
        using var resource = new NativeKernelEtwTestResource(config, At.AddSeconds(-1));
        using var owner = new EtwCpuResidencyCaptureOwner(resource, () => plan);
        owner.EnsureStarted(); owner.BeginWindow(TimeSpan.FromSeconds(1));
        resource.Publish(0);
        resource.ProcessStart(0, At.AddSeconds(-1), 100, 11, 1000, "recorded");
        resource.ThreadStart(0, At.AddSeconds(-1), 100, 10);
        // Both processors first switch at 250, so the window is [S, S + W) = [250, 1250).
        resource.CSwitch(250, At.AddMilliseconds(-750), 0, 100, 10, 0, 0);
        resource.CSwitch(250, At.AddMilliseconds(-750), 1, 0, 0, 100, 10);
        resource.CSwitch(1250, At.AddMilliseconds(250), 0, 0, 0, 0, 0);
        resource.CSwitch(1250, At.AddMilliseconds(250), 1, 100, 10, 0, 0);
        resource.Publish(1250);
        Assert.True(owner.Collect());
        var window = Assert.IsType<CpuCoreResidencySnapshot>(owner.Read());
        Assert.Equal(TimeSpan.FromSeconds(1), window.Window);
        Assert.Equal(At.AddMilliseconds(250), window.MeasuredThrough);
        Assert.InRange(resource.RegionUsage.CurrentBytes, 1UL, config.ByteCap);
        Assert.Empty(resource.CallbackFailures);
        return window;
    }

    [Fact]
    public void OriginalAggregationAndRecordsNowHaveTheActualSdkAssembly()
    {
        Assert.Equal(typeof(WindowsProcessFieldOwner).Assembly, typeof(CpuResidencyAggregationSnapshot).Assembly);
        Assert.Equal(typeof(WindowsProcessFieldOwner).Assembly, typeof(CpuCoreResidencySnapshot).Assembly);
        Assert.Equal(23, (int)ProcessMetricFieldCode.CpuResidency);
        var field = ProcessMetricFieldRegistry.Find(Name)!;
        Assert.Equal("float64", field.ValueKind); Assert.Equal("%", field.Unit);
        Assert.Equal(ProcessMetricReadMask.None, field.ReadMask);
        Assert.Equal(ProcessMetricDatasetIds.ObservedCpuResidency, field.DatasetId);
    }

    [Fact]
    public void WarmProcessProjectionDoesNotMaterializeAggregateRecordCopies()
    {
        const int threadCount = 128;
        const int processorCount = 8;
        var topology = Enumerable.Range(0, processorCount)
            .ToDictionary(
                static processorId => processorId,
                static processorId => new CpuResidencyCoreAccounting(
                    $"core:{processorId}", $"ccd:{processorId}", 1, 1));
        var plan = new CpuResidencyCaptureConfiguration(
            1000,
            1024,
            topology.ToFrozenDictionary(),
            "recorded projection allocation map");
        var records = new CpuExecutionTimeAggregate[threadCount * processorCount];
        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            for (var processorId = 0; processorId < processorCount; processorId++)
            {
                records[threadIndex * processorCount + processorId] = new(
                    1,
                    100,
                    1000,
                    "projection-allocation",
                    10_000 + threadIndex,
                    100 + threadIndex,
                    processorId,
                    10 + threadIndex + processorId,
                    1 + processorId);
            }
        }
        var aggregate = new CpuResidencyAggregationSnapshot(
            1000,
            0,
            1000,
            At,
            true,
            records);
        var expected = EtwCpuResidencyCaptureOwner.BuildProcessSnapshot(plan, 7, aggregate);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            expected = EtwCpuResidencyCaptureOwner.BuildProcessSnapshot(plan, 7, aggregate);
        }

        IReadOnlyList<CpuProcessCoreResidency>? last = null;
        const int measuredIterations = 32;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < measuredIterations; iteration++)
        {
            last = EtwCpuResidencyCaptureOwner.BuildProcessSnapshot(plan, 7, aggregate);
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(last);

        Assert.InRange(allocatedBytes, 0, 8 * 1024 * 1024);
        Assert.NotSame(expected, last);
        var process = Assert.Single(last!);
        Assert.Equal(threadCount, process.ThreadCount);
        Assert.Equal(78_848, process.ExecutionTimeMilliseconds);
        Assert.Equal(4608, process.SwitchCount);
        Assert.Equal(processorCount, process.LogicalProcessors.Count);
        Assert.Equal(processorCount, process.PhysicalCores.Count);
        Assert.Equal(processorCount, process.Ccds.Count);
        Assert.Equal(7, process.LogicalProcessors[0].LogicalProcessorId);
        Assert.Equal("core:7", process.PhysicalCores[0].PhysicalCoreId);
        Assert.Equal("ccd:7", process.Ccds[0].CcdId);
        Assert.Equal(227, process.Threads[0].ThreadId);
        Assert.Equal(7, process.Threads[0].PrimaryLogicalProcessorId);
        Assert.Equal(8, process.Threads[0].PhysicalCoreIds!.Count);
        Assert.NotSame(expected[0].Threads, process.Threads);
    }

    [Fact]
    public void OriginalClosedWindowProjectionUsesMeasuredBoundaryAndAttributedDenominator()
    {
        var window = Window();
        var projected = WindowsCpuResidencyProcessFields.Project([Name], Inventory(), Scopes(), window);
        Assert.Equal(window.MeasuredThrough, projected.CapturedAt);
        Assert.NotEqual(window.CapturedAt, projected.CapturedAt);
        Assert.Equal(1000, projected.Sources.Single().WindowMilliseconds);
        Assert.Equal(0, projected.FindProcess(100, "1000", logicalProcessor: Scopes()[0])!.Values.Single().NumericValue);
        Assert.Equal(100, projected.FindProcess(100, "1000", logicalProcessor: Scopes()[1])!.Values.Single().NumericValue);
        Assert.Equal("core:1", projected.Processes[1].LogicalProcessor!.PhysicalCoreId);
        Assert.Equal("ccd:1", projected.Processes[1].LogicalProcessor!.CcdId);
    }

    [Fact]
    public void MissingBirthWrongBirthAndNoPositiveDenominatorNeverBecomeInferredZero()
    {
        var window = Window();
        Assert.Empty(WindowsCpuResidencyProcessFields.Project([Name], Inventory(birth: null), Scopes(), window).Processes);
        var reused = WindowsCpuResidencyProcessFields.Project([Name], Inventory(birth: "2000"), Scopes(), window);
        Assert.All(reused.Processes, row => Assert.Null(row.Values.Single().NumericValue));
        var empty = WindowsCpuResidencyProcessFields.Project([Name], Inventory(), Scopes(), window with { Processes = [] });
        Assert.All(empty.Processes, row => Assert.Null(row.Values.Single().NumericValue));
        var incomplete = WindowsCpuResidencyProcessFields.Project([Name], Inventory(), Scopes(), null);
        Assert.Null(incomplete.CapturedAt); Assert.All(incomplete.Processes, row => Assert.Null(row.Values.Single().NumericValue));
        var wrongInterval = WindowsCpuResidencyProcessFields.Project([Name], Inventory(), Scopes(), window with { Window = TimeSpan.FromMilliseconds(1) });
        Assert.Null(wrongInterval.CapturedAt); Assert.All(wrongInterval.Processes, row => Assert.Null(row.Values.Single().NumericValue));
    }

    [Fact]
    public void PidOnlyStartCannotMergeAnInstanceOrGainBirthFromALaterInventory()
    {
        var buffer = new OracleCpuResidencyAggregationBuffer(1000, 1000, 1024, [0]);
        buffer.ObserveProcessStart(0, At, 100, 11, null, "unverified");
        Assert.Null(buffer.CaptureInventorySeed().SeedProcess(100, 1000, "later PID scan"));
        buffer.ObserveProcessStart(1, At, 200, 0, null, "first unknown");
        buffer.ObserveThreadStart(1, At, 200, 20);
        var before = buffer.GetDiagnostics().RetainedProcessIdentityCount;
        buffer.ObserveProcessStart(2, At, 200, 0, null, "second unknown");
        Assert.Equal(before, buffer.GetDiagnostics().RetainedProcessIdentityCount); // retired unreferenced identities may prune
        Assert.Equal(0, buffer.GetDiagnostics().CurrentThreadCount);
    }

    [Fact]
    public void EventBirthRequiresTheSameLiveHandleAndRejectsPermissionFailureChangedBirthAndDelayedReuse()
    {
        var eventTime = new DateTimeOffset(DateTime.FromFileTimeUtc(1000));
        using var identity = new IdentityLease(100, [1000, 1000]);
        Assert.Equal(1000, OracleCpuResidencyCaptureOwner.ResolveEventCreation(100, eventTime, identity));
        using var denied = new IdentityLease(100, []);
        Assert.Null(OracleCpuResidencyCaptureOwner.ResolveEventCreation(100, eventTime, denied));
        using var laterBirth = new IdentityLease(100, [2000]);
        Assert.Null(OracleCpuResidencyCaptureOwner.ResolveEventCreation(100, eventTime, laterBirth));
        using var changed = new IdentityLease(100, [1000, 2000]);
        Assert.Null(OracleCpuResidencyCaptureOwner.ResolveEventCreation(100, eventTime, changed));
        using var wrongPid = new IdentityLease(200, [1000, 1000]);
        Assert.Null(OracleCpuResidencyCaptureOwner.ResolveEventCreation(100, eventTime, wrongPid));
    }

    [Fact]
    public void RawOwnerStartsOnlyOnDemandAndStopPreservesBorrowedBrokerLifetime()
    {
        using var broker = new Broker();
        using var capture = Capture(broker);
        Assert.Null(capture.Read()); Assert.False(capture.HasSessionLease); Assert.Equal(0, broker.Acquires);
        capture.EnsureStarted();
        Assert.Equal(1, broker.Acquires);
        Assert.Equal(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread, broker.Keywords);
        capture.BeginWindow(TimeSpan.FromSeconds(1)); capture.Collect();
        Assert.Equal(2, broker.Acquires);
        Assert.Equal(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread |
            KernelTraceEventParser.Keywords.ContextSwitch, broker.Keywords);
        Assert.Null(capture.Read()); // no closed interval is manufactured by elapsed wall clock
        capture.Stop(); Assert.Equal(2, broker.Releases); Assert.False(capture.HasSessionLease);
        capture.EnsureStarted(); Assert.Equal(3, broker.Acquires); Assert.Null(capture.Read());
        capture.Dispose(); Assert.Equal(3, broker.Releases);
    }

    [Fact]
    public async Task OriginalLossRecoveryInvalidatesTheWindowAndReseedsTheSameSdkOwner()
    {
        using var broker = new Broker();
        var first = new TaskCompletionSource<EtwCpuResidencyCaptureOwner.InventorySeed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reseeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        using var capture = new EtwCpuResidencyCaptureOwner(broker, Configuration, new(), null, (inventory, current) =>
        {
            Assert.True(current());
            var scan = Interlocked.Increment(ref scans);
            Assert.True(inventory.SeedProcess(100, scan * 1000L, "inventory", [10, 11]));
            if (scan == 1) first.TrySetResult(inventory); else reseeded.TrySetResult();
        });
        capture.EnsureStarted(); capture.BeginWindow(TimeSpan.FromSeconds(1));
        var oldInventory = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var generation = broker.Resource.Generation;
        var end = NativeCpuReaderTestFeed.ClosedWindow(capture, [0, 1], 1000, firstTid: 10, seedIdentities: false);
        Assert.True(capture.Collect()); Assert.NotNull(capture.Read());
        broker.Resource.Loss(broker.Resource.Tick + 1, 1);
        broker.Resource.Publish(broker.Resource.Tick);
        Assert.True(capture.Collect()); Assert.Null(capture.Read());
        Assert.Equal("Warming", capture.State);
        await reseeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(oldInventory.SeedProcess(999, 9999, "stale", []));
        NativeCpuReaderTestFeed.ClosedWindow(capture, [0, 1], 1000, birth: 2000, firstTid: 10,
            previousEnd: end, seedIdentities: false);
        Assert.True(capture.Collect());
        var process = Assert.Single(capture.Read()!.Processes);
        Assert.Equal(100, process.ProcessId); Assert.Equal("2000", process.ProcessStartKey);
        Assert.Equal(2, scans); Assert.Equal(1, broker.Resource.Creations);
        Assert.Equal(generation, broker.Resource.Generation); Assert.Equal(2, broker.Acquires);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopOrPausePreservesPublishedWindowAndRejectsAnAlreadyCollectedWindow(bool pause)
    {
        using var broker = new Broker(); using var capture = Capture(broker);
        capture.EnsureStarted(); capture.BeginWindow(TimeSpan.FromSeconds(1));
        NativeCpuReaderTestFeed.ClosedWindow(capture, [0, 1], 1000, firstTid: 10);
        Assert.True(capture.Collect());
        Assert.NotNull(capture.Read());
        var published = capture.Read();
        var revision = capture.Revision;
        var committed = false;
        var permit = new BeforeCommitPermit(() =>
        {
            if (pause) capture.PauseWindow(); else capture.Stop();
            Assert.Same(published, capture.Read());
            Assert.Equal(revision, capture.Revision);
        });
        Assert.False(capture.Collect(permit, (_, _) => committed = true));
        Assert.False(committed);
        Assert.Same(published, capture.Read());
        Assert.Equal(pause, capture.HasSessionLease);
        Assert.Equal(pause ? 1 : 2, broker.Releases);
        if (pause) capture.BeginWindow(TimeSpan.FromSeconds(1));
        capture.EnsureStarted();
        capture.BeginWindow(TimeSpan.FromSeconds(1));
        Assert.Equal(pause ? 3 : 4, broker.Acquires);
        Assert.Same(published, capture.Read());
        Assert.True(capture.Collect()); // restarted capture is warming, with no old closed window
        Assert.Null(capture.Read());
    }

    [Fact]
    public async Task CanonicalDefaultUsesSameCurrentAndSubscriptionWithdrawalKeepsHostCpuHolder()
    {
        using var broker = new Broker(); using var owner = Owner(broker);
        owner.StartCpuResidencyCapture();
        var warming = await owner.CollectAsync([Name]);
        Assert.Equal(2, warming.Processes.Count); Assert.All(warming.Processes, row => Assert.Null(row.Values.Single().NumericValue));
        Assert.All(owner.Backend.Catalog.Fields, field => Assert.Equal(Name, field.Definition.Name));
        var references = owner.Backend.Catalog.Fields.Select(field => field.Field).ToImmutableArray();
        using var a = owner.BorrowSubscriptionCapture(); using var b = owner.BorrowSubscriptionCapture();
        a.Bind(references); b.Bind(references); a.SynchronizeDemand(references); b.SynchronizeDemand(references);
        a.Dispose(); b.SynchronizeDemand([]);
        Assert.Equal(0, broker.Releases); // the host run owns its lease independently
        Assert.All(owner.ReadCachedSnapshot()!.Processes, row => Assert.Null(row.Values.Single().NumericValue));
        owner.Dispose(); Assert.Equal(2, broker.Releases);
    }

    [Fact]
    public async Task OnlyExplicitCpuRunEntryAndExitAcquireAndReleaseTheHolder()
    {
        using var broker = new Broker(); using var owner = Owner(broker);
        var initial = WindowsCpuResidencyProcessFields.Project([Name], CurrentInventory(), Scopes(), CurrentWindow());
        owner.Current.SetPart(ProcessMetricDatasetIds.ObservedCpuResidency, initial);
        var refs = owner.Backend.Catalog.Fields.Select(field => field.Field).ToImmutableArray();
        using var a = owner.BorrowSubscriptionCapture(); using var b = owner.BorrowSubscriptionCapture();
        a.Bind(refs); b.Bind(refs); a.SynchronizeDemand(refs); b.SynchronizeDemand(refs);
        await a.CollectAsync(refs, new Permit());
        await owner.CollectAsync([Name]);
        Assert.Equal(0, broker.Acquires);
        owner.StartCpuResidencyCapture();
        owner.StartCpuResidencyCapture();
        await a.CollectAsync(refs, new Permit());
        Assert.Equal(2, broker.Acquires);
        a.Dispose(); Assert.Equal(0, broker.Releases);
        b.SynchronizeDemand([]); Assert.Equal(0, broker.Releases);
        b.SynchronizeDemand(refs); await b.CollectAsync(refs, new Permit());
        Assert.Equal(2, broker.Acquires);
        owner.StopCpuResidencyCapture();
        owner.StopCpuResidencyCapture();
        Assert.Equal(2, broker.Releases);
        await b.CollectAsync(refs, new Permit());
        await owner.CollectAsync([Name]);
        Assert.Equal(2, broker.Acquires);
        owner.StartCpuResidencyCapture();
        var restarted = await b.CollectAsync(refs, new Permit());
        Assert.Equal(4, broker.Acquires); Assert.All(restarted.Readings, reading => Assert.Null(reading.Value));
        b.Dispose(); Assert.Equal(2, broker.Releases);
        owner.StopCpuResidencyCapture(); Assert.Equal(4, broker.Releases);
    }

    [Fact]
    public void HostedCurrentOnlyOwnerRejectsStandaloneCpuCaptureAndDisposedOwnerRejectsEntry()
    {
        using var hosted = new WindowsProcessFieldOwner(new ProcessFieldCurrentState());
        Assert.Throws<InvalidOperationException>(() => hosted.StartCpuResidencyCapture());
        Assert.Throws<InvalidOperationException>(hosted.StopCpuResidencyCapture);
        using var broker = new Broker();
        var owner = Owner(broker);
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.StartCpuResidencyCapture());
        Assert.Throws<ObjectDisposedException>(owner.StopCpuResidencyCapture);
        Assert.Equal(0, broker.Acquires);
    }

    [Fact]
    public async Task RejectedCanceledAndRetiredRequestsPreserveOrClearTheOriginalCurrentPrecisely()
    {
        using var broker = new Broker(); using var owner = Owner(broker);
        var initial = WindowsCpuResidencyProcessFields.Project([Name], CurrentInventory(), Scopes(), CurrentWindow());
        owner.Current.SetPart(ProcessMetricDatasetIds.ObservedCpuResidency, initial);
        var refs = owner.Backend.Catalog.Fields.Select(field => field.Field).ToImmutableArray();
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.Bind(refs); borrow.SynchronizeDemand(refs);
        using var signal = owner.Publications.Subscribe([ProcessMetricDatasetIds.ObservedCpuResidency]);
        var rejected = await borrow.CollectAsync(refs, new Permit(false));
        Assert.False(rejected.Committed); Assert.Same(initial, owner.Current.ReadPart(ProcessMetricDatasetIds.ObservedCpuResidency));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow.CollectAsync(refs, new Permit(), canceled.Token).AsTask());
        Assert.Same(initial, owner.Current.ReadPart(ProcessMetricDatasetIds.ObservedCpuResidency));
        owner.Inventory = new RecordedInventory(static () => []);
        var retired = await borrow.CollectAsync(refs, new Permit());
        Assert.True(retired.Committed); Assert.All(retired.Readings, reading => Assert.Equal(FieldObservationStatus.Unavailable, reading.Status));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await signal.WaitAsync(timeout.Token);
        Assert.Empty(owner.Backend.Catalog.Fields);
        borrow.SynchronizeDemand(refs); // original PID/birth binding survives directory retirement
    }

    [Fact]
    public async Task HostCanonicalBackendKeepsTheOriginalRole6DemandAndSdkCurrent()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture(); using var broker = new Broker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        var backend = new HostCpuResidencyFieldBackend(reader);
        Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, broker.Acquires);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await reader.StartAsync(timeout.Token);
        try
        {
            var discovery = backend.DiscoverAsync(timeout.Token).AsTask();
            await broker.Bound.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await EtwTestRunRecorder.UntilAsync(() => Volatile.Read(ref broker.Acquires) == 2);
            Assert.False(discovery.IsCompleted); // warming cannot end the only demand before its window forms
            await timeout.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
            Assert.Equal(2, broker.Acquires); Assert.Null(reader.Read()); Assert.Empty(backend.Catalog.Fields);
        }
        finally { await reader.StopAsync(CancellationToken.None); }
        Assert.Equal(2, broker.Releases);
    }

    [Fact]
    public async Task ExternalNativeCompositionPublishesFromTheSameCurrentWhileHostOwnsCpuLease()
    {
        using var broker = new Broker(); using var owner = Owner(broker);
        owner.Current.SetPart(ProcessMetricDatasetIds.ObservedCpuResidency,
            WindowsCpuResidencyProcessFields.Project([Name], CurrentInventory(), Scopes(), CurrentWindow()));
        var fields = owner.Backend.Catalog.Fields.Select(field => field.Field).ToImmutableArray();
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        await using var subscriptions = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(),
            DefaultFieldSubscriptionCompositionTests.Settings(), in configuration,
            failure => throw new InvalidOperationException("Unexpected recorded worker failure.", failure.Error));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        owner.StartCpuResidencyCapture();
        await subscriptions.StartAsync(timeout.Token);
        try
        {
            await using var stream = subscriptions.SubscribeAsync("recorded-cpu", fields, TimeSpan.FromMilliseconds(20), timeout.Token).GetAsyncEnumerator();
            Assert.True(await stream.MoveNextAsync());
            Assert.All(stream.Current, reading => FieldReadingAssert.CurrentOrNewer(reading, owner.Backend.ReadCached(reading.Field)));
            Assert.All(stream.Current, reading => Assert.Null(reading.Value)); // real owner has no fabricated closed interval
            Assert.Equal(2, broker.Acquires);
        }
        finally { await subscriptions.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, broker.Releases);
        owner.StopCpuResidencyCapture();
        Assert.Equal(2, broker.Releases);
        Assert.NotNull(owner.ReadCachedSnapshot());
    }

    private static EtwCpuResidencyCaptureOwner Capture(Broker broker) => new(broker, Configuration, new());
    private static ProcessMetricFieldSnapshot CurrentInventory()
    {
        using var process = Process.GetCurrentProcess();
        return Inventory(process.Id, process.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private static CpuCoreResidencySnapshot CurrentWindow()
    {
        var identity = CurrentInventory().Processes.Single(); var recorded = Window();
        return recorded with { Processes = recorded.Processes.Select(row => row with
            { ProcessId = identity.ProcessId, ProcessStartKey = identity.CreationWindowsFileTime100Ns }).ToArray() };
    }
    private static WindowsProcessFieldOwner Owner(Broker broker)
    {
        var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState(), Capture(broker));
        owner.Inventory = new RecordedInventory(static () => [Process.GetCurrentProcess()]);
        owner.BasicReader = new(new ProcessBasicFieldCollectorTests.Api());
        return owner;
    }
    private sealed class RecordedInventory(Func<Process[]> read) : IWindowsProcessInventoryReader { public Process[] GetProcesses() => read(); }
    private sealed class IdentityLease(uint pid, ulong[] births) : IWindowsNetworkEndpointProcessLease
    {
        private int index;
        public uint ProcessId => pid;
        public bool TryReadCreationFileTime(out ulong created)
        { created = index < births.Length ? births[index++] : 0; return created != 0; }
        public void Dispose() { }
    }
    private sealed class Permit(bool active = true) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => active;
        public bool TryPublish(Action commit) { if (!active) return false; commit(); return true; }
    }
    private sealed class BeforeCommitPermit(Action beforeCommit) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => true;
        public bool TryPublish(Action commit) { beforeCommit(); commit(); return true; }
    }
    private sealed class Broker : CpuResidencyHeldTestBroker { internal Broker() : base(7, At) { } }
}
