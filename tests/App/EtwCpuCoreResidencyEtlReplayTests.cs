using System.Collections.Frozen;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Domain.RuntimeSpecialization;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Reflection;
using Xunit.Abstractions;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwCpuCoreResidencyEtlReplayTests(ITestOutputHelper output)
{
    private const int ReplayWindowMilliseconds = 1000;

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETL_REPLAY_PATH")]
    public Task RealKernelEtlActivatesTheExactNonDefaultProductionReaderPlan()
        => VerifyProductionReaderPlanAsync(mixedPhysicalCores: false);

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETL_REPLAY_PATH")]
    public Task RealKernelEtlReaderPublishesTheCompiledMixedPhysicalCoreMapping()
        => VerifyProductionReaderPlanAsync(mixedPhysicalCores: true);

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETL_REPLAY_PATH")]
    public async Task ConcurrentRealKernelEtlReadersKeepTheirFirstCompletePublications()
    {
        for (var round = 0; round < 4; round++)
            await Task.WhenAll(VerifyProductionReaderPlanAsync(mixedPhysicalCores: true),
                VerifyProductionReaderPlanAsync(mixedPhysicalCores: false));
    }

    [Fact]
    public async Task CompleteReplayBankCannotStopTheFlowBeforeTheReadersPendingSampleIsAcknowledged()
    {
        using var broker = new CpuResidencyHeldTestBroker();
        var plan = new CpuResidencyCaptureConfiguration(1000, 32,
            new Dictionary<int, CpuResidencyCoreAccounting>
            {
                [0] = new("core:0", "", 2, 0.5), [1] = new("core:0", "", 2, 0.5)
            }.ToFrozenDictionary(), "controlled SMT replay barrier");
        using var owner = new EtwCpuResidencyCaptureOwner(broker, () => plan, new(), null, static (_, _) => { });
        owner.EnsureStarted();
        owner.BeginWindow(TimeSpan.FromSeconds(1));
        var end = NativeCpuReaderTestFeed.ClosedWindow(owner, [0, 1], 1000, startQpc: 0);
        Assert.True(owner.Collect());
        Assert.NotNull(owner.Read());
        Assert.True(RecordedPublicationGate.CanHoldBank(broker.Resource.Session, () => TryReadRequiredTicket(owner)));

        // Working state has advanced beyond the still-complete immutable bank.
        // The real collection therefore submits a new tracked CPU sample.
        broker.Resource.CSwitch(end + 1, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0);
        using var cancellation = new CancellationTokenSource();
        var collection = owner.CollectAsync(cancellationToken: cancellation.Token);
        try
        {
            Assert.False(collection.IsCompleted);
            ulong sequence = 0;
            var bank = KernelEtwNativeReaders.ReadResidency(broker.Resource.Session, ref sequence, 0, 0, null,
                static view => new(view.Frequency, view.StartQpc, view.ThroughQpc, null, (view.Flags & 1) != 0, []));
            Assert.True(bank!.IsComplete); // The old policy stopped the flow here.
            Assert.False(RecordedPublicationGate.CanHoldBank(broker.Resource.Session, () => TryReadRequiredTicket(owner)));
            Assert.False(RecordedPublicationGate.CanHoldBank(broker.Resource.Session, static () => null));

            // Let the original consumer boundary acknowledge the sample first.
            broker.Resource.Publish(broker.Resource.Tick);
            Assert.True(await collection);
            Assert.True(RecordedPublicationGate.CanHoldBank(broker.Resource.Session, () => TryReadRequiredTicket(owner)));
            Assert.NotNull(owner.Read());
        }
        finally
        {
            cancellation.Cancel();
            try { await collection; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private async Task VerifyProductionReaderPlanAsync(bool mixedPhysicalCores)
    {
        var elapsed = Stopwatch.StartNew();
        const int configuredWindowMilliseconds = 1250;
        const int configuredMaximumActivePairs = 262144;
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETL_REPLAY_PATH");
        Assert.False(string.IsNullOrWhiteSpace(path)); Assert.True(File.Exists(path));
        CompiledHostManagerPlan? preparedPlan = null;
        CpuTopologySnapshot? preparedTopology = null;
        using var broker = new NativeKernelEtwRecordedTestResource(path!, metadata =>
        {
            preparedTopology = mixedPhysicalCores ? CreateMixedReplayTopology(metadata.ProcessorCount)
                : new ReplayTopologySampler(metadata.ProcessorCount).CaptureTopology();
            preparedPlan = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            {
                FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyObservationWindow)["value"] = configuredWindowMilliseconds;
                FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyExecutionTimeSource)["value"]!["maximum_active_pairs"] = configuredMaximumActivePairs;
            }, cpuTopology: preparedTopology);
            var config = KernelEtwNativeSettings.CreateConfiguration(preparedPlan.KernelEtwLimits
                ?? throw new InvalidOperationException("Native ETW limits are unpublished."), preparedPlan.CpuCoreResidency,
                checked((uint)metadata.ProcessorCount));
            config.Residency.QpcFrequency = metadata.QpcFrequency;
            return config;
        });
        var hostPlan = preparedPlan ?? throw new InvalidOperationException("The fixture did not prepare its plan.");
        var topology = preparedTopology ?? throw new InvalidOperationException("The fixture did not prepare its topology.");
        Assert.Equal(Stopwatch.Frequency, broker.QpcFrequency);
        using var sampling = new HostManagerSamplingSubscriptionTestFixture(hostPlan);
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        RecordedPublicationGate? preparedBanks = null;
        KernelEtwEventSource? preparedSource = null;
        using var preparation = broker.Acquire(new("recorded-test-publication-observer",
            KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread,
            (source, _) =>
            {
                preparedSource = source;
                preparedBanks = new(broker, () => TryReadRequiredTicket(NativeCpuReaderTestFeed.Owner(reader)));
            })
            { NativeOutputs = KernelEtwNativeOutputs.Residency });
        var banks = preparedBanks ?? throw new InvalidOperationException("The fixture did not bind its native publication observer.");
        var boundSource = preparedSource ?? throw new InvalidOperationException("The fixture did not bind its native source.");
        var nativeSession = boundSource.NativeSession;
        var generation = preparation.GetSnapshot().Generation;
        using var firstWindowDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstWindowStarted = elapsed.Elapsed;
        try
        {
            await reader.StartAsync(CancellationToken.None);
            Assert.Same(boundSource, preparedSource);
            Assert.Same(nativeSession, broker.Session);
            Assert.Equal(generation, broker.GetSnapshot().Generation);
            using var subscription = reader.AcquireSubscription("test.real-etl-reader-plan", TimeSpan.FromMilliseconds(100));
            Assert.Same(boundSource, preparedSource);
            Assert.Same(nativeSession, broker.Session);
            Assert.Equal(generation, broker.GetSnapshot().Generation);
            await banks.Ready(0).WaitAsync(firstWindowDeadline.Token);
            output.WriteLine($"mixed={mixedPhysicalCores}; first bank ready at {elapsed.Elapsed.TotalMilliseconds:F3}ms; {banks.Describe()}");
            CpuCoreResidencySnapshot? complete = null;
            await WaitUntilAsync(() => (complete = reader.Read()) is not null, firstWindowDeadline.Token);
            output.WriteLine($"mixed={mixedPhysicalCores}; first reader current at {elapsed.Elapsed.TotalMilliseconds:F3}ms; {banks.Describe()}");
            Assert.Equal(TimeSpan.FromMilliseconds(configuredWindowMilliseconds), complete!.Window);
            Assert.Equal(complete.Window, complete.MeasuredThrough - complete.MeasuredFrom);
            Assert.NotEmpty(complete.Processes);
            Assert.Same(hostPlan.CpuCoreResidency, reader.GetDiagnostics().SessionPlan);
            AssertPublishedCoreMapping(complete, topology);
            var physical = complete.Processes.SelectMany(process => process.PhysicalCores).ToArray();
            Assert.Contains(physical, core => core.UsagePercent > 0);
            Assert.All(physical, core => Assert.InRange(core.UsagePercent, 0, 100));
            Assert.All(physical.GroupBy(core => core.PhysicalCoreId), group =>
                Assert.InRange(Math.Round(group.Sum(core => core.UsagePercent), 9), 0, 100));
            Assert.Equal(2, broker.SubscribeCount); // Fixture observer and original product metadata hold.
            Assert.Equal(1, broker.DurationCount);
            Assert.True(broker.BoundKeywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch));
            Assert.True(broker.BoundKeywords.HasFlag(KernelTraceEventParser.Keywords.Process));
            Assert.True(broker.BoundKeywords.HasFlag(KernelTraceEventParser.Keywords.Thread));
            broker.ThrowIfProcessingFailed();
            using var nextWindowDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            banks.Release(0);
            await banks.Ready(1).WaitAsync(nextWindowDeadline.Token);
            CpuCoreResidencySnapshot? next = null;
            await WaitUntilAsync(() => (next = reader.Read()) is not null && !ReferenceEquals(complete, next), nextWindowDeadline.Token);
            Assert.True(next!.MeasuredThrough > complete.MeasuredThrough);
            AssertPublishedCoreMapping(next!, topology);
            subscription.Dispose();
            await reader.StopAsync(CancellationToken.None);
            Assert.Same(boundSource, preparedSource);
            Assert.Same(nativeSession, broker.Session);
            Assert.Equal(generation, broker.GetSnapshot().Generation);
            Assert.True(boundSource.IsActive);
            Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
            Assert.Equal(0, broker.DurationCount);
            Assert.False(broker.BoundKeywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch));
            Assert.False(broker.ProcessingCompletion.IsCompleted);
            var retained = Assert.IsType<CpuCoreResidencySnapshot>(reader.Read());
            AssertPublishedCoreMapping(retained, topology);
            await reader.StopAsync(CancellationToken.None); Assert.Same(retained, reader.Read());
            banks.ReleaseAll(); preparation.Dispose();
            await WaitUntilAsync(() => !broker.IsRunning);
            broker.ThrowIfProcessingFailed(); Assert.True(broker.ContextSwitchCount > 0);
            Assert.False(boundSource.IsActive);
            Assert.Equal(0UL, broker.RegionUsage.CurrentBytes);
            Assert.Same(retained, reader.Read());
        }
        catch (OperationCanceledException) when (firstWindowDeadline.IsCancellationRequested)
        {
            output.WriteLine($"REPLAY TIMEOUT mixed={mixedPhysicalCores}; elapsedMs={elapsed.Elapsed.TotalMilliseconds:F3}; "
                + $"firstWindowMs={(elapsed.Elapsed - firstWindowStarted).TotalMilliseconds:F3}; "
                + $"worker={reader.ExecuteTask?.Status}; {banks.Describe()}; {broker.Describe()}");
            var diagnostics = reader.GetDiagnostics();
            output.WriteLine($"reader={diagnostics.State}; message={diagnostics.Message}; "
                + $"binding={diagnostics.BindingEpoch}; generation={diagnostics.SessionGeneration}; aggregation={diagnostics.Aggregation}");
            output.WriteLine(DescribeNativeReader(NativeCpuReaderTestFeed.Owner(reader)));
            broker.ThrowIfProcessingFailed();
            throw;
        }
        finally
        {
            banks.ReleaseAll();
            await reader.StopAsync(CancellationToken.None);
            preparation.Dispose(); banks.Dispose();
        }
    }

    private static string DescribeNativeReader(EtwCpuResidencyCaptureOwner owner)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var state = typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession", flags)!.GetValue(owner);
        if (state is null) return "CPU binding absent";
        var native = state.GetType().GetProperty("Native")!.GetValue(state)!;
        var type = native.GetType();
        var gate = type.GetField("gate", flags)!.GetValue(native)!;
        var windowActive = owner.IsWindowActive;
        if (!Monitor.TryEnter(gate, TimeSpan.FromMilliseconds(50))) return "CPU reader gate held at timeout";
        try
        {
            var ticket = (ulong)type.GetField("requiredTicket", flags)!.GetValue(native)!;
            return $"CPU windowActive={windowActive}; revision={owner.Revision}; "
                + $"requiredTicket={ticket}; applied={KernelEtwNativeReaders.IsResidencyApplied((NativeKernelEtwSession)type.GetProperty("Session", flags)!.GetValue(native)!, ticket)}; "
                + $"floor={type.GetField("minimumWindowQpc", flags)!.GetValue(native)}; "
                + $"sequence={type.GetField("sequence", flags)!.GetValue(native)}; aggregate={type.GetField("aggregate", flags)!.GetValue(native)}";
        }
        finally { Monitor.Exit(gate); }
    }

    private static ulong? TryReadRequiredTicket(EtwCpuResidencyCaptureOwner owner)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var state = typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession", flags)!.GetValue(owner);
        if (state is null) return null;
        var native = state.GetType().GetProperty("Native")!.GetValue(state)!;
        var gate = native.GetType().GetField("gate", flags)!.GetValue(native)!;
        // A worker may be between submitting a ticket and storing it. Do not
        // stop its consumer using a stale ticket, or block it on this test hook.
        if (!Monitor.TryEnter(gate)) return null;
        try { return (ulong)native.GetType().GetField("requiredTicket", flags)!.GetValue(native)!; }
        finally { Monitor.Exit(gate); }
    }

    // Two bounded barriers AFTER the native signal retain advancing CPU banks
    // only once they acknowledge the ORIGINAL reader's current sample ticket.
    // Passive shared banks may retain an unchanged CPU component and do not count.
    // There is no per-event callback, QPC rewrite, sleep, or fake Running state.
    private sealed class RecordedPublicationGate : IDisposable
    {
        private readonly TaskCompletionSource<bool>[] ready = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        private readonly ManualResetEventSlim[] releases = [new(false), new(false)];
        private readonly IDisposable observation;
        private CpuResidencyAggregationSnapshot current = new(1, 0, 0, null, false, []);
        private ulong sequence;
        private int index;
        private long? previousThrough;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private string lastObserved = "No publication observed";
        private string firstReady = "No complete publication observed";
        private int deferredForReader;
        internal string Describe() => $"firstReady=[{Volatile.Read(ref firstReady)}]; latest=[{Volatile.Read(ref lastObserved)}]; slots={Volatile.Read(ref index)}; deferredForReader={Volatile.Read(ref deferredForReader)}";
        internal static bool CanHoldBank(NativeKernelEtwSession session, Func<ulong?> readRequiredTicket)
            => readRequiredTicket() is { } ticket && KernelEtwNativeReaders.IsResidencyApplied(session, ticket);
        internal RecordedPublicationGate(NativeKernelEtwRecordedTestResource resource, Func<ulong?> readRequiredTicket)
        {
            var session = resource.Session;
            observation = resource.ObservePublicationsAfterSignals(() =>
            {
                try
                {
                    current = KernelEtwNativeReaders.ReadResidency(session, ref sequence, session.EventsLost, 0, current,
                        static view => new(view.Frequency, view.StartQpc, view.ThroughQpc,
                            (view.Flags & 2) != 0 ? new DateTimeOffset(view.TimeTicks, TimeSpan.Zero) : null,
                            (view.Flags & 1) != 0, []) { HasBoundaries = (view.Flags & 5) != 0 }) ?? current;
                    Volatile.Write(ref lastObserved, $"elapsedMs={elapsed.Elapsed.TotalMilliseconds:F3}; sequence={sequence}; "
                        + $"complete={current.IsComplete}; boundaries={current.HasBoundaries}; start={current.WindowStartQpc}; through={current.MeasuredThroughQpc}");
                    if (!current.IsComplete || index >= releases.Length) return;
                    if (!CanHoldBank(session, readRequiredTicket))
                    {
                        Interlocked.Increment(ref deferredForReader);
                        return; // The queued sample must reach the next consumer boundary.
                    }
                    if (previousThrough is { } previous && current.MeasuredThroughQpc <= previous) return;
                    previousThrough = current.MeasuredThroughQpc;
                    var slot = index++;
                    if (slot == 0) Volatile.Write(ref firstReady, Volatile.Read(ref lastObserved));
                    ready[slot].TrySetResult(true);
                    if (!releases[slot].Wait(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("The test did not release its recorded publication barrier.");
                }
                catch (Exception error)
                {
                    foreach (var signal in ready) signal.TrySetException(error);
                    ReleaseAll();
                }
            });
        }
        internal Task Ready(int slot) => ready[slot].Task;
        internal void Release(int slot) => releases[slot].Set();
        internal void ReleaseAll() { foreach (var release in releases) release.Set(); }
        public void Dispose() { ReleaseAll(); observation.Dispose(); foreach (var release in releases) release.Dispose(); }
    }
    private static CpuTopologySnapshot CreateMixedReplayTopology(int logicalProcessorCount)
    {
        Assert.True(logicalProcessorCount >= 7, "The mixed-core replay needs at least seven recorded logical CPUs.");
        var siblingsPerCore = new List<int> { 1, 2, 4 };
        siblingsPerCore.AddRange(Enumerable.Repeat(1, logicalProcessorCount - 7));
        return HostManagerTestPlanFactory.CreateCpuTopology(siblingsPerCore.ToArray());
    }

    private static void AssertPublishedCoreMapping(CpuCoreResidencySnapshot snapshot, CpuTopologySnapshot topology)
    {
        var expectedCores = topology.PhysicalCores.ToDictionary(static core => core.Id);
        var expectedLogical = topology.LogicalProcessors.ToDictionary(static logical => logical.Id);
        foreach (var process in snapshot.Processes)
        {
            Assert.Equal(
                process.LogicalProcessors.Select(logical => expectedLogical[logical.LogicalProcessorId].PhysicalCoreId).Distinct().Order(),
                process.PhysicalCores.Select(static core => core.PhysicalCoreId).Order());
            foreach (var logical in process.LogicalProcessors)
            {
                var expected = expectedLogical[logical.LogicalProcessorId];
                Assert.Equal(expected.PhysicalCoreId, logical.PhysicalCoreId);
                Assert.Equal(expected.CcdId, logical.CcdId);
            }
            foreach (var physical in process.PhysicalCores)
            {
                var expected = expectedCores[physical.PhysicalCoreId];
                Assert.Equal(expected.CcdId, physical.CcdId);
                var expectedUsage = 100 * physical.ExecutionTimeMilliseconds
                    / snapshot.Window.TotalMilliseconds / expected.LogicalProcessorIds.Count;
                // Display durations round to 0.001 ms; the production ratio retains raw QPC precision.
                var roundingBound = 100 * 0.0005 / snapshot.Window.TotalMilliseconds
                    / expected.LogicalProcessorIds.Count + 1e-10;
                Assert.InRange(Math.Abs(expectedUsage - physical.UsagePercent), 0, roundingBound);
            }
        }
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETL_REPLAY_PATH")]
    public void RealKernelEtlUsesTheRetainedManagedOracleCallbacksToBuildExecutionDurations()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETL_REPLAY_PATH");
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path), $"Kernel ETL does not exist: {path}");
        using var source = new ETWTraceEventSource(path);
        var topology = new ReplayTopologySampler(source.NumberOfProcessors).CaptureTopology();
        var compiledPlan = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology)
            .CpuCoreResidency.RequirePublished();
        var qpcFrequency = ReadQpcFrequency(source);
        var kernel = new KernelTraceEventParser(source);
        var buffer = new OracleCpuResidencyAggregationBuffer(
            qpcFrequency,
            observationWindowMilliseconds: ReplayWindowMilliseconds,
            maximumActivePairs: compiledPlan.ExecutionTimeSource.MaximumActivePairs,
            logicalProcessorIds: Enumerable.Range(0, source.NumberOfProcessors).ToArray());
        var dispatcher = new OracleKernelEtwSubscriptionDispatcher();
        dispatcher.AttachParser(kernel);
        dispatcher.InitializeSubscriptions(
            [new(1, "etl-production-residency",
                KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
                    | KernelTraceEventParser.Keywords.ContextSwitch,
                (callbacks, _) => OracleCpuResidencyEventAdapter.BindKernelEventCallbacks(
                    callbacks, buffer, static () => true, static _ => null))],
            new(1, DateTimeOffset.UnixEpoch));

        var expectedProcessors = Enumerable.Range(0, source.NumberOfProcessors).ToArray();
        var observedProcessors = new HashSet<int>();
        CpuResidencyAggregationSnapshot? complete = null;
        var contextSwitchCount = 0;
        var backwardQpcCount = 0;
        var lastEventQpc = long.MinValue;

#pragma warning disable CS0618 // C1 measures exact event QPC intervals, not relative floating-point display time.
        kernel.ThreadCSwitch += data =>
        {
            if (data.TimeStampQPC < lastEventQpc)
            {
                backwardQpcCount++;
            }
            lastEventQpc = data.TimeStampQPC;
            contextSwitchCount++;
            observedProcessors.Add(data.ProcessorNumber);
            if (contextSwitchCount % 4096 != 0)
            {
                return;
            }

            var snapshot = buffer.Snapshot();
            if (snapshot.IsComplete && snapshot.Records.Count > 0)
            {
                complete = snapshot;
                source.StopProcessing();
            }
        };
#pragma warning restore CS0618

        source.Process();

        Assert.True(
            complete is not null,
            $"No complete exact window: processors={source.NumberOfProcessors}; observed=[{string.Join(',', observedProcessors.Order())}]; contextSwitch={contextSwitchCount}; backwardQpc={backwardQpcCount}.");
        var snapshot = complete;
        Assert.Equal(expectedProcessors, observedProcessors.Order());
        Assert.Equal(0, backwardQpcCount);
        Assert.True(contextSwitchCount >= 4096);
        Assert.Equal(qpcFrequency, snapshot.QpcFrequency);
        Assert.Equal(
            qpcFrequency * ReplayWindowMilliseconds / 1000,
            snapshot.MeasuredThroughQpc - snapshot.WindowStartQpc);
        Assert.NotEmpty(snapshot.Records);
        Assert.True(snapshot.Records.Sum(static record => record.ExecutionTimeQpc) > 0);
        Assert.True(
            snapshot.Records.Sum(static record => record.ExecutionTimeQpc)
                <= checked((long)expectedProcessors.Length
                    * qpcFrequency
                    * ReplayWindowMilliseconds
                    / 1000));
        Assert.Contains(snapshot.Records, static record => record.SwitchCount > 0);
        Assert.All(snapshot.Records, static record =>
        {
            Assert.True(record.ProcessInstanceId > 0);
            Assert.True(record.ProcessId > 0);
            Assert.True(record.ThreadInstanceId > 0);
            Assert.True(record.ThreadId > 0);
            Assert.True(record.ExecutionTimeQpc > 0);
        });

        var projected = EtwCpuCoreResidencyReader.BuildProcessSnapshot(compiledPlan, 73, snapshot);
        foreach (var core in topology.PhysicalCores)
        {
            var rawDurationQpc = snapshot.Records
                .Where(record => core.LogicalProcessorIds.Contains(record.LogicalProcessorId))
                .Sum(static record => record.ExecutionTimeQpc);
            var expectedUsage = 100.0 * rawDurationQpc
                / (snapshot.MeasuredThroughQpc - snapshot.WindowStartQpc)
                / core.LogicalProcessorIds.Count;
            var publishedUsage = projected.SelectMany(static process => process.PhysicalCores)
                .Where(item => item.PhysicalCoreId == core.Id)
                .Sum(static item => item.UsagePercent);
            Assert.Equal(expectedUsage, publishedUsage, 10);
            Assert.InRange(Math.Round(publishedUsage, 9), 0, 100);
        }

        // Test weights are intentionally unequal; expected W is recomputed from raw ETL durations.
        var cpu = CpuScoringPlanCompiler.Compile(topology,
            topology.PhysicalCores.ToDictionary(core => core.Index, core => core.Index + 1d), 0.5);
        var host = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology, cpuScoring: cpu);
        var configuration = HostManagerComputeScoringConfigurationFactory.Create(host.SmartCoordinator, host.CpuScoring!);
        var weights = cpu.CoreWeights.Cores.Select(core => core.ReferenceWeight).ToArray();
        var coreIndices = cpu.CoreWeights.Cores.Select((core, index) => (core.PhysicalCoreId, Index: (uint)index))
            .ToDictionary(core => core.PhysicalCoreId, core => core.Index);
        using var native = new NativeComputeScoringSession(in configuration, weights);
        double[] result = [0];
        var actualTotal = 0d;
        foreach (var process in projected)
        {
            var rows = process.PhysicalCores.Select(core => new NativeComputeScoringCpuCoreInput
            {
                ProcessIndex = 0,
                CoreIndex = coreIndices[core.PhysicalCoreId],
                UsagePercent = core.UsagePercent
            }).OrderBy(core => core.CoreIndex).ToArray();
            Assert.Equal(NativeComputeScoringStatus.Ok, native.CalculateWeightedCpuUse(rows, result));
            actualTotal += result[0];
        }
        var expectedTotal = topology.PhysicalCores.Sum(core =>
            (core.Index + 1d) * 100 * snapshot.Records
                .Where(record => core.LogicalProcessorIds.Contains(record.LogicalProcessorId))
                .Sum(record => record.ExecutionTimeQpc)
            / (snapshot.MeasuredThroughQpc - snapshot.WindowStartQpc) / core.LogicalProcessorIds.Count) / weights.Sum();
        Assert.True(actualTotal > 0);
        Assert.Equal(expectedTotal, actualTotal, 10);
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETL_REPLAY_PATH")]
    public void RealKernelEtlActivePairDemandFitsTheCompiledCapacity()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETL_REPLAY_PATH");
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path), $"Kernel ETL does not exist: {path}");
        using var source = new ETWTraceEventSource(path);
        var topology = new ReplayTopologySampler(source.NumberOfProcessors).CaptureTopology();
        var plan = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology).CpuCoreResidency.RequirePublished();
        var buffer = new OracleCpuResidencyAggregationBuffer(ReadQpcFrequency(source), plan.ObservationWindowMilliseconds, plan.ExecutionTimeSource.MaximumActivePairs,
            Enumerable.Range(0, source.NumberOfProcessors).ToArray());
        var dispatcher = new OracleKernelEtwSubscriptionDispatcher();
        dispatcher.AttachParser(new KernelTraceEventParser(source));
        dispatcher.InitializeSubscriptions(
            [new(1, "etl-pair-capacity", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
                | KernelTraceEventParser.Keywords.ContextSwitch,
                (callbacks, _) => OracleCpuResidencyEventAdapter.BindKernelEventCallbacks(callbacks, buffer, static () => true, static _ => null))],
            new(1, DateTimeOffset.UnixEpoch));
        source.Process();
        var diagnostics = buffer.GetDiagnostics();
        Assert.InRange(diagnostics.ActivePairCount, 0, plan.ExecutionTimeSource.MaximumActivePairs);
        Assert.Equal(0, diagnostics.CapacityResetCount);
    }

    private static long ReadQpcFrequency(TraceEventSource source)
    {
        var property = typeof(TraceEventSource).GetProperty(
            "QPCFreq",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return property?.GetValue(source) is long frequency && frequency > 0
            ? frequency
            : throw new InvalidDataException("The ETL source did not expose a positive QPC frequency.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate()) await Task.Delay(10, cancellationToken);
    }

    private sealed class ReplayTopologySampler(int logicalProcessorCount) : ICpuTopologySampler
    {
        public void ResetUsageBaseline() { }
        private readonly CpuTopologySnapshot topology = CreateTopology(logicalProcessorCount);

        public CpuTopologySnapshot CaptureTopology() => topology;

        public CpuTopologySnapshot CaptureSnapshot()
            => throw new InvalidOperationException("CPU residency must not capture CPU-usage snapshots.");

        private static CpuTopologySnapshot CreateTopology(int logicalProcessorCount)
        {
            Assert.True(logicalProcessorCount > 0);
            var logicalIds = Enumerable.Range(0, logicalProcessorCount).ToArray();
            var ccd = new CpuCcdModel(
                "ccd:etl",
                0,
                null,
                logicalIds,
                logicalIds,
                "real-etl-reader-plan");
            var cores = logicalIds.Select(index => new CpuPhysicalCoreModel(
                    $"core:{index}",
                    index,
                    ccd.Id,
                    0,
                    1,
                    null,
                    [index],
                    []))
                .ToArray();
            var logical = logicalIds.Select(index => new CpuLogicalProcessorModel(
                    index,
                    0,
                    index,
                    cores[index].Id,
                    ccd.Id,
                    1,
                    null,
                    true))
                .ToArray();
            return new CpuTopologySnapshot(
                DateTimeOffset.UnixEpoch,
                "Real ETL",
                new CpuSpecificationModel(
                    "Real ETL",
                    "Recorded",
                    "Recorded",
                    logicalProcessorCount,
                    logicalProcessorCount,
                    null,
                    null,
                    null,
                    null,
                    "real-etl-reader-plan"),
                "real-etl-reader-plan",
                "none",
                CpuTopologyAffinityTargetKinds.LogicalProcessorMask,
                CpuTopologyVisualLayoutKinds.Grid,
                "real-etl-reader-plan",
                logicalProcessorCount,
                logicalProcessorCount,
                1,
                false,
                [ccd],
                cores,
                logical,
                []);
        }
    }
}
