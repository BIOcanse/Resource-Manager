using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
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

public sealed class EtwCpuCoreResidencyEtlReplayTests
{
    private const int ReplayWindowMilliseconds = 1000;

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETL_REPLAY_PATH")]
    public Task RealKernelEtlActivatesTheExactNonDefaultProductionReaderPlan()
        => VerifyProductionReaderPlanAsync(mixedPhysicalCores: false);

    [EnvironmentVariableFact("RESOURCE_MANAGER_ETL_REPLAY_PATH")]
    public Task RealKernelEtlReaderPublishesTheCompiledMixedPhysicalCoreMapping()
        => VerifyProductionReaderPlanAsync(mixedPhysicalCores: true);

    private static async Task VerifyProductionReaderPlanAsync(bool mixedPhysicalCores)
    {
        const int configuredWindowMilliseconds = 1250;
        const int configuredMaximumClosedSlices = 262144;
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETL_REPLAY_PATH");
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path), $"Kernel ETL does not exist: {path}");
        using var broker = new ReplayKernelEtwBroker(path);
        var topology = mixedPhysicalCores
            ? CreateMixedReplayTopology(broker.ProcessorCount)
            : new ReplayTopologySampler(broker.ProcessorCount).CaptureTopology();
        var hostPlan = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyObservationWindow)
                ["value"] = configuredWindowMilliseconds;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyExecutionTimeSource)
                ["value"]!["maximum_closed_slices"] = configuredMaximumClosedSlices;
        }, cpuTopology: topology);
        using var sampling = new HostManagerSamplingSubscriptionTestFixture(hostPlan);
        Assert.Equal(Stopwatch.Frequency, broker.QpcFrequency);
        using var reader = new EtwCpuCoreResidencyReader(
            broker,
            sampling.Provider,
            sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance,
            static (_, _) => { });
        await reader.StartAsync(CancellationToken.None);
        try
        {
            using var subscription = reader.AcquireSubscription(
                "test.real-etl-reader-plan",
                TimeSpan.FromMilliseconds(100));
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            CpuCoreResidencySnapshot? complete;
            while ((complete = reader.Read()) is null
                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            if (complete is null)
            {
                var diagnostics = reader.GetDiagnostics();
                throw new InvalidOperationException(
                    "Production reader did not publish a complete window. "
                    + $"readerState={diagnostics.State}; readerMessage={diagnostics.Message}; "
                    + $"bindingEpoch={diagnostics.BindingEpoch}; session={diagnostics.SessionGeneration}; "
                    + $"planWindow={diagnostics.SessionPlan?.ObservationWindowMilliseconds}; "
                    + $"aggregation={diagnostics.Aggregation}; {broker.Describe()}");
            }

            Assert.NotNull(complete);
            Assert.Equal(
                TimeSpan.FromMilliseconds(configuredWindowMilliseconds),
                complete.Window);
            Assert.Equal(complete.Window, complete.MeasuredThrough - complete.MeasuredFrom);
            Assert.NotEmpty(complete.Processes);
            Assert.Same(hostPlan.CpuCoreResidency, reader.GetDiagnostics().SessionPlan);
            AssertPublishedCoreMapping(complete, topology);
            var physicalCores = complete.Processes.SelectMany(static process => process.PhysicalCores).ToArray();
            Assert.Contains(physicalCores, static core => core.UsagePercent > 0);
            Assert.All(physicalCores, static core => Assert.InRange(core.UsagePercent, 0, 100));
            Assert.All(physicalCores.GroupBy(static core => core.PhysicalCoreId), static group =>
                Assert.InRange(Math.Round(group.Sum(static core => core.UsagePercent), 9), 0, 100));
            Assert.Equal(1, broker.SubscribeCount);
            Assert.True(broker.BoundKeywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch));
            Assert.True(broker.BoundKeywords.HasFlag(KernelTraceEventParser.Keywords.Process));
            Assert.True(broker.BoundKeywords.HasFlag(KernelTraceEventParser.Keywords.Thread));
            broker.ThrowIfProcessingFailed();

            CpuCoreResidencySnapshot? next = null;
            await WaitUntilAsync(() => (next = reader.Read()) is not null && !ReferenceEquals(complete, next));
            AssertPublishedCoreMapping(next!, topology);
            subscription.Dispose();
            await WaitUntilAsync(() => !broker.IsRunning);
            var retained = reader.Read();
            Assert.NotNull(retained);
            Assert.NotSame(complete, retained);
            AssertPublishedCoreMapping(retained, topology);

            reader.ApplyMode(ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Freeze);
            Assert.Same(retained, reader.Read());

            await reader.StopAsync(CancellationToken.None);
            Assert.Same(retained, reader.Read());
        }
        finally
        {
            await reader.StopAsync(CancellationToken.None);
        }
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
    public void RealKernelEtlUsesTheProductionCallbacksToBuildExecutionDurations()
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
        var buffer = new CpuResidencyAggregationBuffer(
            qpcFrequency,
            observationWindowMilliseconds: ReplayWindowMilliseconds,
            maximumClosedSlices: compiledPlan.ExecutionTimeSource.MaximumClosedSlices);
        EtwCpuCoreResidencyReader.BindKernelEventCallbacks(
            kernel,
            buffer,
            static () => true,
            static _ => null);

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

            var snapshot = buffer.Snapshot(expectedProcessors);
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
    public void RealKernelEtlSliceDemandFitsTheCompiledCapacity()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_ETL_REPLAY_PATH");
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path), $"Kernel ETL does not exist: {path}");
        var plan = HostManagerTestPlanFactory.CreatePlan().CpuCoreResidency.RequirePublished();
        var closedSliceBoundaries = new Queue<long>();
        var maximumDemand = 0;

        using var source = new ETWTraceEventSource(path);
        var qpcFrequency = ReadQpcFrequency(source);
        var windowQpcNumerator = checked(
            qpcFrequency * plan.ObservationWindowMilliseconds);
        var windowQpc = checked((windowQpcNumerator + 999) / 1000);
        var kernel = new KernelTraceEventParser(source);
#pragma warning disable CS0618 // Capacity is measured over the same exact QPC domain as production slices.
        kernel.ThreadCSwitch += data =>
        {
            if (data.OldThreadID != 0)
            {
                closedSliceBoundaries.Enqueue(data.TimeStampQPC);
            }
            var windowStart = data.TimeStampQPC - windowQpc;
            while (closedSliceBoundaries.TryPeek(out var boundary)
                && boundary <= windowStart)
            {
                closedSliceBoundaries.Dequeue();
            }
            maximumDemand = Math.Max(maximumDemand, closedSliceBoundaries.Count);
        };
#pragma warning restore CS0618

        source.Process();

        Assert.True(maximumDemand > 0);
        Assert.True(
            maximumDemand <= plan.ExecutionTimeSource.MaximumClosedSlices,
            $"The real {plan.ObservationWindowMilliseconds} ms ETL window needs {maximumDemand} closed slices, but the compiled freedom point allows only {plan.ExecutionTimeSource.MaximumClosedSlices}.");
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

    private sealed class ReplayKernelEtwBroker : IKernelEtwSessionBroker, IDisposable
    {
        private readonly object sync = new();
        private readonly ETWTraceEventSource source;
        private Task? processing;
        private Exception? processingFailure;
        private bool running;
        private bool disposed;

        public ReplayKernelEtwBroker(string path)
        {
            source = new ETWTraceEventSource(path);
            ProcessorCount = source.NumberOfProcessors;
            QpcFrequency = ReadQpcFrequency(source);
        }

        public int ProcessorCount { get; }
        public long QpcFrequency { get; }
        public int SubscribeCount { get; private set; }
        public KernelTraceEventParser.Keywords BoundKeywords { get; private set; }
        public long ContextSwitchCount => Interlocked.Read(ref contextSwitchCount);
        public bool IsRunning
        {
            get
            {
                lock (sync)
                {
                    return running;
                }
            }
        }
        private long contextSwitchCount;

        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (processing is not null)
                {
                    throw new InvalidOperationException("The ETL replay broker accepts one production reader binding.");
                }

                SubscribeCount++;
                BoundKeywords = request.Keywords;
                running = true;
                var parser = new KernelTraceEventParser(source);
                parser.ThreadCSwitch += _ =>
                {
                    var count = Interlocked.Increment(ref contextSwitchCount);
                    if ((count & 4095) == 0)
                    {
                        Thread.Sleep(1);
                    }
                };
                request.Bind(
                    parser,
                    new KernelEtwSessionContext(73, DateTimeOffset.UnixEpoch));
                processing = Task.Run(() =>
                {
                    try
                    {
                        source.Process();
                    }
                    catch (Exception ex)
                    {
                        lock (sync)
                        {
                            processingFailure = ex;
                        }
                    }
                });
                return new ReplayLease(this);
            }
        }

        public KernelEtwSessionSnapshot GetSnapshot()
        {
            lock (sync)
            {
                return new KernelEtwSessionSnapshot(
                    running ? "Running" : "Idle",
                    running ? "ETL replay is bound." : "ETL replay is idle.",
                    running ? 73 : 0,
                    running ? DateTimeOffset.UnixEpoch : DateTimeOffset.MinValue,
                    running ? BoundKeywords : KernelTraceEventParser.Keywords.None,
                    running ? 1 : 0,
                    0);
            }
        }

        public void ThrowIfProcessingFailed()
        {
            lock (sync)
            {
                if (processingFailure is not null)
                {
                    throw new InvalidOperationException(
                        "The real ETL replay failed while driving the production reader.",
                        processingFailure);
                }
            }
        }

        public string Describe()
        {
            lock (sync)
            {
                return $"subscribes={SubscribeCount}; switches={contextSwitchCount}; "
                    + $"processingCreated={processing is not null}; processingCompleted={processing?.IsCompleted}; "
                    + $"running={running}; failure={processingFailure?.GetType().Name}: {processingFailure?.Message}";
            }
        }

        public void Dispose()
        {
            Task? task;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                running = false;
                task = processing;
            }

            source.StopProcessing();
            task?.Wait(TimeSpan.FromSeconds(5));
            source.Dispose();
        }

        private void Release()
        {
            lock (sync)
            {
                running = false;
            }
            source.StopProcessing();
        }

        private sealed class ReplayLease(ReplayKernelEtwBroker owner) : IKernelEtwSubscription
        {
            private int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    owner.Release();
                }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ReplayTopologySampler(int logicalProcessorCount) : ICpuTopologySampler
    {
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
                "ETL processors",
                null,
                logicalIds,
                logicalIds,
                "real-etl-reader-plan");
            var cores = logicalIds.Select(index => new CpuPhysicalCoreModel(
                    $"core:{index}",
                    index,
                    $"Core {index}",
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
