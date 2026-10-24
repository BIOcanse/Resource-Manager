using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.Adapter;
using ResourceManager.Adapter.LocalResources;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

internal sealed class EmptyDiskUsageTreeStore : IDiskUsageTreeStore
{
    public DiskUsageScanSummary? CurrentSummary => null;
    public DiskUsageTreeReadLease? TryAcquire() => null;
    public int ClearCount { get; private set; }
    public void Replace(DiskUsageScanResult result, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Clear() => ClearCount++;
}

public sealed class ResourceManagerSelfTypedResourceActionTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(LocalResourceSoftwareMemoryMode.Unrestricted, LedgerTableMode.Unrestricted)]
    [InlineData(LocalResourceSoftwareMemoryMode.Normal, LedgerTableMode.Normal)]
    [InlineData(LocalResourceSoftwareMemoryMode.Optimize, LedgerTableMode.Optimize)]
    [InlineData(LocalResourceSoftwareMemoryMode.PagedFrozen, LedgerTableMode.Normal)]
    public void ProductModesHaveExplicitLedgerMappings(LocalResourceSoftwareMemoryMode mode, LedgerTableMode expected)
        => Assert.Equal(expected, ResourceManagerSelfLocalResourceManager.LedgerMode(mode));

    [Fact]
    public void UnknownModeIsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            ResourceManagerSelfLocalResourceManager.LedgerMode((LocalResourceSoftwareMemoryMode)255));

    [Theory]
    [InlineData(LocalResourceSoftwareMemoryMode.Normal)]
    [InlineData(LocalResourceSoftwareMemoryMode.Optimize)]
    [InlineData(LocalResourceSoftwareMemoryMode.Unrestricted)]
    public async Task ModesWithoutWorkingSetConsumerSkipOsCensus(LocalResourceSoftwareMemoryMode mode)
    {
        using var owner = new ResourceManagerLedgerOwner();
        var writer = CreateWriter();
        using var manager = new ResourceManagerSelfLocalResourceManager(writer, new EmptyDiskUsageTreeStore(), owner,
            () => throw new InvalidOperationException("No working-set consumer is enabled."), (_, _) => 500);
        manager.SetDesiredMemoryMode(mode);
        var result = await manager.TickAsync();
        Assert.False(manager.IsBackgroundWorkerRunning);
        Assert.Null(result.BackendWorkingSetBytes);
        Assert.Null(result.NativeUiWorkingSetBytes);
        Assert.Equal(0UL, result.Memory.RegisteredBytes);
        Assert.Equal(0UL, result.Ledger.Released);
        Assert.Empty(result.TrimResults);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public async Task OptimizeRepeatedTicksReclaimOnlyLedgerCacheWithoutTrimmingProcessWorkingSets()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var writer = CreateWriter();
        var store = new DiskUsageTreeStore(owner);
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", true, 0, 0);
        for (var index = 0; index < 65; index++)
            builder.Add(root, $"owned-cache-entry-{index:00}", false, 0, 0);
        store.Replace(new(builder.Build(), new DiskUsageScanSummary(
            DiskUsageScanScopes.Volume, DiskUsageScanModes.Fast, "C:", ["C:\\"],
            DiskUsageScanKinds.MasterFileTable, [], StartedAt, 0.1, 0, 0, 65, 1, 0)));
        Assert.True(owner.ReadTable().RegisteredBytes > 0);
        using var manager = new ResourceManagerSelfLocalResourceManager(
            writer, store, owner, Sample, (_, _) => 500);
        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.Optimize);
        var reading = store.TryAcquire();
        Assert.NotNull(reading);
        try
        {
            var inUse = await manager.TickAsync();
            Assert.Equal(0UL, inUse.Ledger.Released);
            Assert.NotNull(store.CurrentSummary);
            Assert.Empty(inUse.TrimResults);
        }
        finally { reading.Dispose(); }
        ulong released = 0;
        for (var round = 0; round < 4; round++)
        {
            var result = await manager.TickAsync();
            released += result.Ledger.Released;
            Assert.Null(result.BackendWorkingSetBytes);
            Assert.Null(result.NativeUiWorkingSetBytes);
            Assert.Empty(result.TrimResults);
            Assert.Empty(result.RecheckedTrimObservations);
        }
        Assert.True(released > 0);
        Assert.Null(store.CurrentSummary);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public async Task FailedBatchesKeepTheirActualOsOutcome()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var writer = new RecordingPolicyWriter(succeed: false, startedAtOverride: _ => StartedAt);
        using var manager = CreateManager(writer, owner);
        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.PagedFrozen);
        var failed = await manager.TickAsync();
        Assert.Equal(1000UL, failed.BackendWorkingSetBytes);
        Assert.Equal(2000UL, failed.NativeUiWorkingSetBytes);
        Assert.All(failed.TrimResults, trim => Assert.Equal(AdapterResourceActionStatus.Failed, trim.Status));
        Assert.Equal(0UL, failed.Ledger.Released);
    }

    [Fact]
    public async Task UnreadableMalformedBatchIsRecheckedWithoutClaimingThatTrimSucceeded()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var writer = new RecordingPolicyWriter(malformedResults: true, startedAtOverride: _ => StartedAt);
        ulong? refreshed = null;
        using var manager = CreateManager(writer, owner, read: (_, _) => refreshed);
        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.PagedFrozen);
        var unknown = await manager.TickAsync();
        Assert.Equal(2, unknown.PendingTrimObservationCount);
        Assert.All(unknown.TrimResults, trim =>
        {
            Assert.Equal(AdapterResourceActionStatus.ResourceBusy, trim.Status);
            Assert.Empty(trim.Results);
            Assert.True(trim.ObservationPending);
        });
        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.Optimize);
        refreshed = 300;
        var observed = await manager.TickAsync();
        Assert.Empty(observed.TrimResults);
        Assert.Equal(0, observed.PendingTrimObservationCount);
        Assert.Equal(2, observed.RecheckedTrimObservations.Count);
        Assert.All(observed.RecheckedTrimObservations, trim =>
        {
            Assert.Equal(AdapterResourceActionStatus.ResourceBusy, trim.Status);
            Assert.False(trim.ObservationPending);
            Assert.All(trim.Observations, observation => Assert.Equal(300UL, observation.BytesAfter));
        });
        Assert.Equal(2, writer.Requests.Count);
        Assert.Equal(0UL, observed.Ledger.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedRecheckSurvivesLaterSamplingFailureOrCancellation(bool cancel)
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var cancellation = new CancellationTokenSource();
        var writer = new RecordingPolicyWriter(malformedResults: true, startedAtOverride: _ => StartedAt);
        ulong? refreshed = null;
        var captureCount = 0;
        using var manager = new ResourceManagerSelfLocalResourceManager(writer, new EmptyDiskUsageTreeStore(), owner,
            () =>
            {
                if (++captureCount == 2)
                {
                    if (cancel)
                    {
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                    }
                    throw new IOException("injected capture failure after completed recheck");
                }
                return Sample();
            }, (_, _) => refreshed);
        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.PagedFrozen);
        var first = await manager.TickAsync();
        Assert.Equal(2, first.PendingTrimObservationCount);
        var previous = manager.LastTrimResults;
        // Keep the census consumer enabled to exercise a failure after readback.
        refreshed = 300;

        var error = await Record.ExceptionAsync(() => manager.TickAsync(cancellation.Token));

        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<IOException>(error);
        Assert.NotSame(previous, manager.LastTrimResults);
        Assert.Equal(2, manager.LastTrimResults.Count);
        foreach (var observation in manager.LastTrimResults)
        {
            var original = Assert.Single(first.TrimResults, trim => trim.Group == observation.Group);
            Assert.Equal(AdapterResourceActionStatus.ResourceBusy, observation.Status);
            Assert.Same(original.Requests, observation.Requests);
            Assert.Same(original.Results, observation.Results);
            Assert.False(observation.ObservationPending);
            Assert.All(observation.Observations, row => Assert.Equal(300UL, row.BytesAfter));
        }
        // The normal and failure branches call this same OS-only logger. A
        // completed observation must retain ResourceBusy, never Ledger release data.
        var logger = new WorkingSetLogger();
        HostManagerSmartCoordinator.LogSelfWorkingSetResults(logger, manager.LastTrimResults);
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Equal(AdapterResourceActionStatus.ResourceBusy, entry.Properties["Status"]);
            Assert.Equal(false, entry.Properties["ObservationPending"]);
            using var evidence = System.Text.Json.JsonDocument.Parse((string)entry.Properties["BatchEvidence"]!);
            Assert.Equal(300UL, evidence.RootElement.GetProperty("Observations")[0].GetProperty("BytesAfter").GetUInt64());
            Assert.NotEmpty(evidence.RootElement.GetProperty("Requests").EnumerateArray());
            Assert.Equal(0, evidence.RootElement.GetProperty("Results").GetArrayLength());
            Assert.False(entry.Properties.ContainsKey("ReleasedCount"));
            Assert.False(entry.Properties.ContainsKey("ReleasedBytes"));
        });
        Assert.Equal(2, writer.Requests.Count);
        var later = await manager.TickAsync();
        Assert.Empty(later.RecheckedTrimObservations);
        Assert.Equal(0, later.PendingTrimObservationCount);
    }

    [Fact]
    public async Task SuccessfulTickSnapshotIncludesRechecksAndNewTrimsExactlyOnce()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var writer = new RecordingPolicyWriter(malformedResults: true, startedAtOverride: _ => StartedAt);
        ulong? refreshed = null;
        using var manager = CreateManager(writer, owner, read: (_, _) => refreshed);
        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.PagedFrozen);
        _ = await manager.TickAsync();
        refreshed = 300;

        var result = await manager.TickAsync();

        Assert.Equal(2, result.RecheckedTrimObservations.Count);
        Assert.Equal(2, result.TrimResults.Count);
        Assert.Equal(4, manager.LastTrimResults.Count);
        Assert.Equal(result.RecheckedTrimObservations.Concat(result.TrimResults), manager.LastTrimResults);
        var logger = new WorkingSetLogger();
        HostManagerSmartCoordinator.LogSelfWorkingSetResults(logger,
            result.TrimResults.Concat(result.RecheckedTrimObservations));
        Assert.Equal(4, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.Equal(0UL, result.Ledger.Released);
    }
    [Fact]
    public async Task WriterExceptionRetainsSubmittedIdentitiesAndFailureForLaterObservation()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var writer = new RecordingPolicyWriter(throwOnApply: true, startedAtOverride: _ => StartedAt);
        using var manager = CreateManager(writer, owner, read: (_, _) => null);
        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.PagedFrozen);
        var result = await manager.TickAsync();
        Assert.Equal(2, result.PendingTrimObservationCount);
        Assert.All(result.TrimResults, trim =>
        {
            Assert.Equal(AdapterResourceActionStatus.ResourceBusy, trim.Status);
            Assert.Contains("IOException", trim.Failure!);
            Assert.Single(trim.Requests);
            Assert.True(trim.ObservationPending);
        });
        Assert.Equal(result.TrimResults, manager.LastTrimResults);
    }

    [Fact]
    public async Task ANewModeWithdrawsTheFormerModesActionsBeforeSubmission()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var writer = CreateWriter();
        ResourceManagerSelfLocalResourceManager? manager = null;
        using (manager = new(writer, new EmptyDiskUsageTreeStore(), owner, () =>
        {
            manager!.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.Normal);
            return Sample();
        }, (_, _) => 500))
        {
            manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.PagedFrozen);
            var result = await manager.TickAsync();
            Assert.Empty(result.TrimResults);
            Assert.Empty(writer.Requests);
        }
    }

    [Fact]
    public async Task ReusedPidIsNeverSubmittedToTrim()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var writer = new RecordingPolicyWriter(startedAtOverride: _ => StartedAt.AddSeconds(1));
        using var manager = CreateManager(writer, owner);
        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.PagedFrozen);
        var result = await manager.TickAsync();
        Assert.Empty(result.TrimResults);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public async Task PagedFrozenExplicitlyWithdrawsTheStoreAndUsesSafeLedgerMaintenance()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var store = new EmptyDiskUsageTreeStore();
        using var manager = CreateManager(CreateWriter(), owner, store);
        manager.SetDesiredMemoryMode(LocalResourceSoftwareMemoryMode.PagedFrozen);
        _ = await manager.TickAsync();
        Assert.Equal(1, store.ClearCount);
        Assert.Equal(LedgerStatus.Ok, manager.TryClose());
        Assert.Equal(LedgerStatus.Ok, manager.TryClose());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.TickAsync());
    }

    [Fact]
    public void PartialTypedTrimIsUncertainAndOnlyExactNoEffectIsFailed()
    {
        var requests = new[] { new ProcessResourcePolicyBatchRequest(101, StartedAt, TrimWorkingSet: true),
            new ProcessResourcePolicyBatchRequest(102, StartedAt, TrimWorkingSet: true) };
        ProcessResourcePolicyBatchWriteResult Result(int pid, bool succeeded) => new(pid,
            [new(ProcessResourcePolicyBatchFields.TrimWorkingSet, succeeded, "observed")]);
        Assert.Equal(AdapterResourceActionStatus.ResourceBusy,
            ResourceManagerSelfLocalResourceManager.ClassifyTrimBatchResults(requests, [Result(101, true), Result(102, false)]));
        Assert.Equal(AdapterResourceActionStatus.Failed,
            ResourceManagerSelfLocalResourceManager.ClassifyTrimBatchResults(requests, [Result(101, false), Result(102, false)]));
        Assert.Equal(AdapterResourceActionStatus.Completed,
            ResourceManagerSelfLocalResourceManager.ClassifyTrimBatchResults(requests, [Result(101, true), Result(102, true)]));
        Assert.Equal(AdapterResourceActionStatus.ResourceBusy,
            ResourceManagerSelfLocalResourceManager.ClassifyTrimBatchResults(requests, [Result(101, true), Result(101, true)]));
        Assert.Equal(AdapterResourceActionStatus.ResourceBusy,
            ResourceManagerSelfLocalResourceManager.ClassifyTrimBatchResults(requests, [Result(101, true)]));
    }

    private static RecordingPolicyWriter CreateWriter() => new(startedAtOverride: _ => StartedAt);
    private static ResourceManagerSelfProcessResource[] Sample() =>
        [new(101, true, false, false, 1000, StartedAt), new(102, false, true, false, 2000, StartedAt)];
    private static ResourceManagerSelfLocalResourceManager CreateManager(RecordingPolicyWriter writer,
        ResourceManagerLedgerOwner owner, EmptyDiskUsageTreeStore? store = null,
        Func<int, DateTimeOffset, ulong?>? read = null)
        => new(writer, store ?? new EmptyDiskUsageTreeStore(), owner, Sample, read ?? ((_, _) => 500));
    private sealed class WorkingSetLogger : ILogger
    {
        internal sealed record Entry(LogLevel Level, IReadOnlyDictionary<string, object?> Properties);
        public List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            Entries.Add(new(logLevel, fields.ToDictionary(static pair => pair.Key, static pair => pair.Value)));
        }
    }
    internal sealed class RecordingPolicyWriter(
        bool succeed = true,
        bool throwOnApply = false,
        bool malformedResults = false,
        ManualResetEventSlim? applyEntered = null,
        ManualResetEventSlim? applyContinue = null,
        Action? onApply = null,
        Func<int, DateTimeOffset?>? startedAtOverride = null)
        : IProcessResourcePolicyWriter
    {
        public List<ProcessResourcePolicyBatchRequest> Requests { get; } = [];

        public DateTimeOffset? TryReadProcessStartedAt(int processId)
        {
            if (startedAtOverride is not null) return startedAtOverride(processId);
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                return new DateTimeOffset(process.StartTime.ToUniversalTime());
            }
            catch (ArgumentException) { return null; }
        }

        public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
            IReadOnlyList<ProcessResourcePolicyBatchRequest> requests)
        {
            if (throwOnApply)
            {
                throw new IOException("injected typed resource effect failure");
            }
            Requests.AddRange(requests);
            onApply?.Invoke();
            applyEntered?.Set();
            if (applyContinue is not null
                && !applyContinue.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("injected typed resource effect did not resume");
            }
            if (malformedResults)
            {
                return [];
            }
            return requests.Select(request =>
                new ProcessResourcePolicyBatchWriteResult(
                    request.ProcessId,
                    [
                        new ProcessResourcePolicyBatchFieldResult(
                            ProcessResourcePolicyBatchFields.TrimWorkingSet,
                            succeed,
                            succeed ? "ok" : "unchanged")
                    ])).ToArray();
        }

        public ProcessResourcePolicySnapshot? TryReadProcess(int processId)
            => throw Unexpected();

        public RecoveryReadResult<ProcessInstanceRecoverySnapshot>
            ReadProcessInstanceForRecovery(int processId)
            => throw Unexpected();

        public RecoveryReadResult<ProcessPlacementRecoverySnapshot>
            ReadPlacementStateForRecovery(
                int processId,
                ProcessPlacementReadFields requiredFields)
            => throw Unexpected();

        public RecoveryReadResult<ThreadCpuSetPolicySnapshot>
            ReadThreadPlacementStateForRecovery(
                int processId,
                int threadId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetPriorityClass(
            int processId,
            string priorityClass)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(
            int processId,
            long affinityMask)
            => throw Unexpected();

        public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int processId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(
            int processId,
            IReadOnlyList<uint> cpuSetIds)
            => throw Unexpected();

        public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(
            int processId,
            int threadId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
            int processId,
            int threadId,
            DateTimeOffset expectedCreatedAt,
            IReadOnlyList<uint> cpuSetIds)
            => throw Unexpected();

        public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(
            int processId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
            int processId,
            DateTimeOffset expectedStartedAt,
            uint expectedCurrentMemoryPriority,
            uint memoryPriority)
            => throw Unexpected();

        public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(
            int processId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetPowerThrottling(
            int processId,
            uint controlMask,
            uint stateMask)
            => throw Unexpected();

        private static InvalidOperationException Unexpected()
            => new("The typed self action used a non-batch writer path.");
    }
}
