using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuResidencyAggregationBufferTests
{
    [Fact]
    public void SameSwitchCountsUseExecutionTimeForDistribution()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Long", 10);
        StartProcess(buffer, 0, 200, 2000, "Short", 20);

        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 900, 0, 100, 10, 200, 20);
        Switch(buffer, 1000, 0, 200, 20, 0, 0);

        var snapshot = buffer.Snapshot([0]);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(900, Assert.Single(snapshot.Records, row => row.ProcessId == 100).ExecutionTimeQpc);
        Assert.Equal(100, Assert.Single(snapshot.Records, row => row.ProcessId == 200).ExecutionTimeQpc);
        Assert.All(snapshot.Records, row => Assert.Equal(1, row.SwitchCount));
    }

    [Fact]
    public void IdleSwitchClosesThePreviousUserSlice()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);

        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var row = Assert.Single(buffer.Snapshot([0]).Records);
        Assert.Equal(1000, row.ExecutionTimeQpc);
        Assert.Equal(100, row.ProcessId);
    }

    [Fact]
    public void ParserUnknownProcessWithZeroThreadIsStillIdle()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);

        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 400, 0, 100, 10, -1, 0);
        Switch(buffer, 600, 0, -1, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, -1, 0);

        var snapshot = buffer.Snapshot([0]);
        Assert.True(snapshot.IsComplete);
        var row = Assert.Single(snapshot.Records);
        Assert.Equal(800, row.ExecutionTimeQpc);
        Assert.Equal(100, row.ProcessId);
    }

    [Fact]
    public void OpenSliceIsNotExtendedBySnapshotOrWallClock()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10, DateTimeOffset.UnixEpoch.AddDays(10));

        var first = buffer.Snapshot([0]);
        var second = buffer.Snapshot([0]);

        Assert.False(first.IsComplete);
        Assert.Empty(first.Records);
        Assert.Equal(first, second);
    }

    [Fact]
    public void SnapshotUsesTheLatestClosedBoundarySharedByEveryProcessor()
    {
        var buffer = CreateBuffer(windowMilliseconds: 500);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        StartProcess(buffer, 0, 200, 2000, "Worker", 20);

        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 0, 1, 0, 0, 200, 20);
        Switch(buffer, 500, 1, 200, 20, 0, 0);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var snapshot = buffer.Snapshot([0, 1]);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(500, snapshot.MeasuredThroughQpc);
        Assert.Equal(500, Assert.Single(snapshot.Records, row => row.ProcessId == 100).ExecutionTimeQpc);
        Assert.Equal(500, Assert.Single(snapshot.Records, row => row.ProcessId == 200).ExecutionTimeQpc);
    }

    [Fact]
    public void ThirtyTwoProcessorsProduceOneExactFiveSecondWindow()
    {
        var buffer = CreateBuffer(windowMilliseconds: 5000, maximumClosedSlices: 64);
        var logicalProcessors = Enumerable.Range(0, 32).ToArray();

        foreach (var logicalProcessorId in logicalProcessors)
        {
            StartProcess(
                buffer,
                0,
                1000 + logicalProcessorId,
                10_000 + logicalProcessorId,
                $"Process {logicalProcessorId}",
                20_000 + logicalProcessorId);
            Switch(
                buffer,
                0,
                logicalProcessorId,
                0,
                0,
                1000 + logicalProcessorId,
                20_000 + logicalProcessorId);
        }

        foreach (var logicalProcessorId in logicalProcessors)
        {
            Switch(
                buffer,
                5000,
                logicalProcessorId,
                1000 + logicalProcessorId,
                20_000 + logicalProcessorId,
                0,
                0);
        }

        var snapshot = buffer.Snapshot(logicalProcessors);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(0, snapshot.WindowStartQpc);
        Assert.Equal(5000, snapshot.MeasuredThroughQpc);
        Assert.Equal(32, snapshot.Records.Count);
        Assert.All(snapshot.Records, static row =>
        {
            Assert.Equal(5000, row.ExecutionTimeQpc);
            Assert.Equal(1, row.SwitchCount);
        });
    }

    [Fact]
    public void ClosedSliceIsClippedAtBothWindowEdges()
    {
        var buffer = CreateBuffer(windowMilliseconds: 5000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 10_000, 0, 100, 10, 0, 0);

        var first = buffer.Snapshot([0]);
        Assert.True(first.IsComplete);
        Assert.Equal(5000, Assert.Single(first.Records).ExecutionTimeQpc);

        Switch(buffer, 12_000, 0, 0, 0, 0, 0);
        var second = buffer.Snapshot([0]);
        Assert.Equal(3000, Assert.Single(second.Records).ExecutionTimeQpc);

        Switch(buffer, 15_000, 0, 0, 0, 0, 0);
        Assert.Empty(buffer.Snapshot([0]).Records);
    }

    [Fact]
    public void EventTimestampNotCallbackDelayDefinesDuration()
    {
        var baseline = CreateBuffer(windowMilliseconds: 1000);
        var delayed = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(baseline, 0, 100, 1000, "Game", 10);
        StartProcess(delayed, 0, 100, 1000, "Game", 10);

        Switch(baseline, 0, 0, 0, 0, 100, 10, DateTimeOffset.UnixEpoch);
        Switch(baseline, 1000, 0, 100, 10, 0, 0, DateTimeOffset.UnixEpoch.AddSeconds(1));
        Switch(delayed, 0, 0, 0, 0, 100, 10, DateTimeOffset.UnixEpoch.AddDays(1));
        Switch(delayed, 1000, 0, 100, 10, 0, 0, DateTimeOffset.UnixEpoch.AddDays(30));

        Assert.Equal(
            Assert.Single(baseline.Snapshot([0]).Records).ExecutionTimeQpc,
            Assert.Single(delayed.Snapshot([0]).Records).ExecutionTimeQpc);
    }

    [Fact]
    public void UnknownNewProcessIdUsesTheKnownThreadLifecycleIdentity()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);

        Switch(buffer, 0, 0, 0, 0, -1, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var row = Assert.Single(buffer.Snapshot([0]).Records);
        Assert.Equal(100, row.ProcessId);
        Assert.Equal(10, row.ThreadId);
        Assert.Equal(1000, row.ExecutionTimeQpc);
    }

    [Fact]
    public void UnknownOldProcessIdStillClosesTheKnownRunningThread()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);

        Switch(buffer, 0, 0, 0, 0, -1, 10);
        Switch(buffer, 1000, 0, -1, 10, 0, 0);

        var row = Assert.Single(buffer.Snapshot([0]).Records);
        Assert.Equal(100, row.ProcessId);
        Assert.Equal(10, row.ThreadId);
        Assert.Equal(1000, row.ExecutionTimeQpc);
    }

    [Fact]
    public void UnknownNonIdleThreadBreaksContinuityUntilOneCleanWindowCloses()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);

        Switch(buffer, 400, 0, 100, 10, -1, 999);
        Switch(buffer, 600, 0, -1, 999, 100, 10);

        var interrupted = buffer.Snapshot([0]);
        Assert.False(interrupted.IsComplete);
        Assert.Empty(interrupted.Records);

        Switch(buffer, 1600, 0, 100, 10, 0, 0);

        var recovered = buffer.Snapshot([0]);
        Assert.True(recovered.IsComplete);
        var row = Assert.Single(recovered.Records);
        Assert.Equal(100, row.ProcessId);
        Assert.Equal(1000, row.ExecutionTimeQpc);
    }

    [Fact]
    public void OldIdentityMismatchBreaksWindowContinuity()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        StartProcess(buffer, 0, 200, 2000, "Other", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);

        Switch(buffer, 1000, 0, 200, 20, 200, 20);
        var interrupted = buffer.Snapshot([0]);
        Assert.False(interrupted.IsComplete);
        Assert.Empty(interrupted.Records);

        Switch(buffer, 2000, 0, 200, 20, 0, 0);

        var recovered = buffer.Snapshot([0]);
        Assert.True(recovered.IsComplete);
        Assert.Equal(1000, Assert.Single(recovered.Records).ExecutionTimeQpc);
    }

    [Fact]
    public void ResetDropsClosedAndOpenState()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Old", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);
        Assert.NotEmpty(buffer.Snapshot([0]).Records);

        buffer.ResetContinuity();

        Assert.False(buffer.Snapshot([0]).IsComplete);
        Assert.Empty(buffer.Snapshot([0]).Records);
    }

    [Fact]
    public void PidReuseProducesDistinctProcessInstances()
    {
        var buffer = CreateBuffer(windowMilliseconds: 5000);
        Switch(buffer, 0, 0, 0, 0, 0, 0);
        StartProcess(buffer, 3000, 100, 1000, "Old", 10, uniqueProcessKey: 11);
        Switch(buffer, 3000, 0, 0, 0, 100, 10);
        Switch(buffer, 4000, 0, 100, 10, 0, 0);
        buffer.ObserveThreadEnd(4000, Time(4000), 100, 10);
        buffer.ObserveProcessEnd(4000, Time(4000), 100, 11);

        StartProcess(buffer, 4500, 100, 2000, "New", 20, uniqueProcessKey: 22);
        Switch(buffer, 5000, 0, 0, 0, 100, 20);
        Switch(buffer, 7000, 0, 100, 20, 0, 0);
        Switch(buffer, 8000, 0, 0, 0, 0, 0);

        var rows = buffer.Snapshot([0]).Records.OrderBy(row => row.ProcessStartKey).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal([1000L, 2000L], rows.Select(row => row.ProcessStartKey!.Value));
        Assert.NotEqual(rows[0].ProcessInstanceId, rows[1].ProcessInstanceId);
        Assert.Equal([1000L, 2000L], rows.Select(row => row.ExecutionTimeQpc));
    }

    [Fact]
    public void ConflictingUniqueProcessKeysNeverMergeEvenWhenStartKeysMatch()
    {
        var buffer = CreateBuffer(windowMilliseconds: 3000);
        StartProcess(buffer, 0, 100, 1000, "Old", 10, uniqueProcessKey: 11);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        StartProcess(buffer, 1000, 100, 1000, "New", 20, uniqueProcessKey: 22);
        Switch(buffer, 1000, 0, 0, 0, 100, 20);
        Switch(buffer, 2000, 0, 100, 20, 0, 0);
        Switch(buffer, 3000, 0, 0, 0, 0, 0);

        var rows = buffer.Snapshot([0]).Records
            .OrderBy(static row => row.ProcessInstanceId)
            .ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(["Old", "New"], rows.Select(static row => row.ProcessName));
        Assert.Equal([1000L, 1000L], rows.Select(static row => row.ExecutionTimeQpc));
        Assert.NotEqual(rows[0].ProcessInstanceId, rows[1].ProcessInstanceId);
    }

    [Fact]
    public void MatchingUniqueProcessKeyDoesNotRewriteConflictingStartMetadata()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10, uniqueProcessKey: 11);
        buffer.ObserveProcessStart(0, Time(0), 100, 11, 9999, "Game Renamed");
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var row = Assert.Single(buffer.Snapshot([0]).Records);
        Assert.Equal(1000, row.ProcessStartKey);
        Assert.Equal("Game Renamed", row.ProcessName);
    }

    [Fact]
    public void TidReuseWithinOneProcessProducesDistinctThreadInstances()
    {
        var buffer = CreateBuffer(windowMilliseconds: 3000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10, uniqueProcessKey: 11);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);
        buffer.ObserveThreadEnd(1000, Time(1000), 100, 10);

        buffer.ObserveThreadStart(1500, Time(1500), 100, 10);
        Switch(buffer, 2000, 0, 0, 0, 100, 10);
        Switch(buffer, 3000, 0, 100, 10, 0, 0);

        var rows = buffer.Snapshot([0]).Records
            .OrderBy(static row => row.ThreadInstanceId)
            .ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, static row =>
        {
            Assert.Equal(100, row.ProcessId);
            Assert.Equal(10, row.ThreadId);
            Assert.Equal(1000, row.ExecutionTimeQpc);
        });
        Assert.NotEqual(rows[0].ThreadInstanceId, rows[1].ThreadInstanceId);
    }

    [Fact]
    public void ThreadEndDoesNotEraseExecutionBeforeTheNextRealSwitchBoundary()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);

        buffer.ObserveThreadEnd(900, Time(900), 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var row = Assert.Single(buffer.Snapshot([0]).Records);
        Assert.Equal(1000, row.ExecutionTimeQpc);
        Assert.Equal(10, row.ThreadId);
    }

    [Fact]
    public void ProcessEndDoesNotEraseExecutionBeforeTheNextRealSwitchBoundary()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10, uniqueProcessKey: 11);
        Switch(buffer, 0, 0, 0, 0, 100, 10);

        buffer.ObserveProcessEnd(900, Time(900), 100, 11);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var row = Assert.Single(buffer.Snapshot([0]).Records);
        Assert.Equal(1000, row.ExecutionTimeQpc);
        Assert.Equal(1000, row.ProcessStartKey);
    }

    [Fact]
    public void CapacityOverflowFailsClosedAndRewarms()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumClosedSlices: 1);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 500, 0, 100, 10, 200, 20);
        Switch(buffer, 1000, 0, 200, 20, 100, 10);

        var snapshot = buffer.Snapshot([0]);
        Assert.False(snapshot.IsComplete);
        Assert.Empty(snapshot.Records);
    }

    [Fact]
    public void CapacityOverflowRecoversOnlyAfterOneCompleteCleanWindow()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumClosedSlices: 1);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 500, 0, 100, 10, 200, 20);
        Switch(buffer, 1000, 0, 200, 20, 100, 10);

        Assert.False(buffer.Snapshot([0]).IsComplete);
        Assert.Equal(1, buffer.GetDiagnostics().CapacityResetCount);

        Switch(buffer, 2000, 0, 100, 10, 0, 0);

        var recovered = buffer.Snapshot([0]);
        Assert.True(recovered.IsComplete);
        var row = Assert.Single(recovered.Records);
        Assert.Equal(100, row.ProcessId);
        Assert.Equal(1000, row.ExecutionTimeQpc);
        Assert.Equal(1, buffer.GetDiagnostics().CapacityResetCount);
    }

    [Fact]
    public void CapacityEvictsExpiredSlicesBeforeRejectingAnAppend()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumClosedSlices: 2);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 100, 0, 100, 10, 200, 20);
        Switch(buffer, 200, 0, 200, 20, 100, 10);
        Switch(buffer, 1200, 0, 100, 10, 0, 0);

        var snapshot = buffer.Snapshot([0]);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(200, snapshot.WindowStartQpc);
        var row = Assert.Single(snapshot.Records);
        Assert.Equal(100, row.ProcessId);
        Assert.Equal(1000, row.ExecutionTimeQpc);
        Assert.Equal(1, row.SwitchCount);
        Assert.Equal(0, buffer.GetDiagnostics().CapacityResetCount);
    }

    [Fact]
    public void RetiredIdentityStorageRemainsBoundedAcrossIncompleteSettlementAndOverflow()
    {
        const int capacity = 8;
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumClosedSlices: capacity);
        Switch(buffer, 0, 0, 0, 0, 0, 0);

        for (var index = 0; index < 1000; index++)
        {
            var processKey = checked((ulong)(index + 1));
            var threadId = 10_000 + index;
            var start = checked(index * 2L + 1);
            StartProcess(
                buffer,
                start,
                100,
                100_000 + index,
                $"Process {index}",
                threadId,
                processKey);
            Switch(buffer, start, 0, 0, 0, 100, threadId);
            Switch(buffer, start + 1, 0, 100, threadId, 0, 0);
            buffer.ObserveThreadEnd(start + 1, Time(start + 1), 100, threadId);
            buffer.ObserveProcessEnd(start + 1, Time(start + 1), 100, processKey);
        }

        var diagnostics = buffer.GetDiagnostics();
        Assert.InRange(diagnostics.ClosedSliceCount, 0, capacity);
        Assert.InRange(diagnostics.RetainedProcessIdentityCount, 0, capacity);
        Assert.InRange(diagnostics.RetainedThreadIdentityCount, 0, capacity);
        Assert.Equal(0, diagnostics.CurrentProcessCount);
        Assert.Equal(0, diagnostics.CurrentThreadCount);
        Assert.True(diagnostics.CapacityResetCount > 0);

        buffer.ResetContinuity();
        diagnostics = buffer.GetDiagnostics();
        Assert.Equal(0, diagnostics.ClosedSliceCount);
        Assert.Equal(0, diagnostics.RetainedProcessIdentityCount);
        Assert.Equal(0, diagnostics.RetainedThreadIdentityCount);
    }

    [Fact]
    public void MaximumQpcBoundaryIsNotConfusedWithAnUnsetBoundary()
    {
        var buffer = new CpuResidencyAggregationBuffer(1000, 1, 4);
        buffer.ObserveProcessStart(
            long.MaxValue - 1,
            DateTimeOffset.UnixEpoch,
            100,
            11,
            1000,
            "Game");
        buffer.ObserveThreadStart(
            long.MaxValue - 1,
            DateTimeOffset.UnixEpoch,
            100,
            10);
        buffer.ObserveContextSwitch(
            long.MaxValue - 1,
            DateTimeOffset.UnixEpoch,
            0,
            0,
            0,
            100,
            "Game",
            10);
        buffer.ObserveContextSwitch(
            long.MaxValue,
            DateTimeOffset.UnixEpoch.AddTicks(1),
            0,
            100,
            10,
            0,
            null,
            0);

        var snapshot = buffer.Snapshot([0]);
        Assert.True(snapshot.IsComplete);
        Assert.NotNull(snapshot.MeasuredThrough);
        Assert.Equal(long.MaxValue, snapshot.MeasuredThroughQpc);
        Assert.Equal(1, Assert.Single(snapshot.Records).ExecutionTimeQpc);
    }

    [Fact]
    public void BackwardEventTimeFailsClosed()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        Switch(buffer, 1000, 0, 0, 0, 100, 10);
        Switch(buffer, 900, 0, 100, 10, 0, 0);

        Assert.False(buffer.Snapshot([0]).IsComplete);
        Assert.Empty(buffer.Snapshot([0]).Records);
    }

    [Fact]
    public void ConcurrentSnapshotsDoNotMutateSettlement()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var snapshots = new CpuResidencyAggregationSnapshot[64];
        Parallel.For(0, snapshots.Length, index => snapshots[index] = buffer.Snapshot([0]));

        Assert.All(snapshots, snapshot =>
        {
            Assert.True(snapshot.IsComplete);
            Assert.Equal(1000, Assert.Single(snapshot.Records).ExecutionTimeQpc);
        });
    }

    [Fact]
    public async Task ConcurrentIngestionAndSettlementKeepMakingProgress()
    {
        const int switchCount = 50_000;
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumClosedSlices: 4096);
        StartProcess(buffer, 0, 100, 1000, "A", 10, uniqueProcessKey: 11);
        StartProcess(buffer, 0, 200, 2000, "B", 20, uniqueProcessKey: 22);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        var start = new ManualResetEventSlim();
        var completeSnapshotCount = 0;

        var writer = Task.Run(() =>
        {
            start.Wait();
            var oldProcessId = 100;
            var oldThreadId = 10;
            for (var qpc = 1; qpc <= switchCount; qpc++)
            {
                var newProcessId = oldProcessId == 100 ? 200 : 100;
                var newThreadId = oldThreadId == 10 ? 20 : 10;
                Switch(
                    buffer,
                    qpc,
                    0,
                    oldProcessId,
                    oldThreadId,
                    newProcessId,
                    newThreadId);
                oldProcessId = newProcessId;
                oldThreadId = newThreadId;
            }
        });
        var settler = Task.Run(() =>
        {
            start.Wait();
            while (!writer.IsCompleted)
            {
                var snapshot = buffer.Snapshot([0]);
                if (!snapshot.IsComplete)
                {
                    continue;
                }

                Assert.InRange(
                    snapshot.Records.Sum(static row => row.ExecutionTimeQpc),
                    0,
                    1000);
                Interlocked.Increment(ref completeSnapshotCount);
            }
        });

        start.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        await settler.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(Volatile.Read(ref completeSnapshotCount) > 0);
        var final = buffer.Snapshot([0]);
        Assert.True(final.IsComplete);
        Assert.Equal(1000, final.Records.Sum(static row => row.ExecutionTimeQpc));
        Assert.Equal(0, buffer.GetDiagnostics().CapacityResetCount);
    }

    private static CpuResidencyAggregationBuffer CreateBuffer(
        int windowMilliseconds,
        int maximumClosedSlices = 1024)
        => new(1000, windowMilliseconds, maximumClosedSlices);

    private static void StartProcess(
        CpuResidencyAggregationBuffer buffer,
        long qpc,
        int processId,
        long processStartKey,
        string processName,
        int threadId,
        ulong uniqueProcessKey = 1)
    {
        buffer.ObserveProcessStart(
            qpc,
            Time(qpc),
            processId,
            uniqueProcessKey,
            processStartKey,
            processName);
        buffer.ObserveThreadStart(qpc, Time(qpc), processId, threadId);
    }

    private static void Switch(
        CpuResidencyAggregationBuffer buffer,
        long qpc,
        int logicalProcessorId,
        int oldProcessId,
        int oldThreadId,
        int newProcessId,
        int newThreadId,
        DateTimeOffset? eventTime = null)
        => buffer.ObserveContextSwitch(
            qpc,
            eventTime ?? Time(qpc),
            logicalProcessorId,
            oldProcessId,
            oldThreadId,
            newProcessId,
            null,
            newThreadId);

    private static DateTimeOffset Time(long qpc)
        => DateTimeOffset.UnixEpoch.AddMilliseconds(qpc);
}
