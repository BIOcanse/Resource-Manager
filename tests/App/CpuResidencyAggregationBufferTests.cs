using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuResidencyAggregationBufferTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void CaptureOpenedAtWindowStartCannotPublishTheSameCompleteWindow(int firstSwitchDelayMilliseconds)
    {
        var continuous = CreateBuffer(windowMilliseconds: 5000);
        var windowed = CreateBuffer(windowMilliseconds: 5000);
        StartProcess(continuous, 0, 100, 1000, "Busy", 10);
        StartProcess(windowed, 0, 100, 1000, "Busy", 10);
        Switch(continuous, 0, 0, 0, 0, 100, 10);
        const int windowStart = 25000;
        Switch(continuous, windowStart - 10, 0, 100, 10, 100, 10);
        var firstBoundary = windowStart + firstSwitchDelayMilliseconds;
        Switch(continuous, firstBoundary, 0, 100, 10, 100, 10);
        Switch(windowed, firstBoundary, 0, 100, 10, 100, 10);
        Switch(continuous, 30000, 0, 100, 10, 100, 10);
        Switch(windowed, 30000, 0, 100, 10, 100, 10);

        var expected = continuous.Snapshot();
        var actual = windowed.Snapshot();
        Assert.True(expected.IsComplete);
        Assert.Equal(5000, Assert.Single(expected.Records).ExecutionTimeQpc);
        Assert.False(actual.IsComplete);
        Assert.Empty(actual.Records);

        // The first observed switch establishes continuity, rather than closing
        // the interval that was already running when capture opened.
        Switch(windowed, firstBoundary + 5000, 0, 100, 10, 0, 0);
        var later = windowed.Snapshot();
        Assert.True(later.IsComplete);
        Assert.NotEmpty(later.Records);
        Assert.Equal(firstBoundary, later.WindowStartQpc);
    }

    [Fact]
    public void UnattributedThreadDoesNotDiscardKnownProcessExecutionWindow()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Known", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 400, 0, 100, 10, -1, 999);
        Switch(buffer, 600, 0, -1, 999, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var snapshot = buffer.Snapshot();
        Assert.True(snapshot.IsComplete);
        var known = Assert.Single(snapshot.Records);
        Assert.Equal(100, known.ProcessId);
        Assert.Equal(800, known.ExecutionTimeQpc);
    }

    [Fact]
    public void SameSwitchCountsUseExecutionTimeForDistribution()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Long", 10);
        StartProcess(buffer, 0, 200, 2000, "Short", 20);

        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 900, 0, 100, 10, 200, 20);
        Switch(buffer, 1000, 0, 200, 20, 0, 0);

        var snapshot = buffer.Snapshot();
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

        var row = Assert.Single(buffer.Snapshot().Records);
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

        var snapshot = buffer.Snapshot();
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

        var first = buffer.Snapshot();
        var second = buffer.Snapshot();

        Assert.False(first.IsComplete);
        Assert.Empty(first.Records);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ContinuousWindowReusesRingCellsAcrossWrapAndBeginWhileSnapshotsStayIndependent()
    {
        const int maximumActivePairs = 64;
        const int firstWarmThroughQpc = 256;
        const int measuredIterations = 2048;
        var buffer = CreateBuffer(windowMilliseconds: 8, maximumActivePairs: maximumActivePairs);
        // n = 8 slots of 1 QPC each; the ring of 18 cells wraps many times.
        buffer.BeginWindow(0, 8);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        CpuResidencyAggregationSnapshot? first = null;
        CpuResidencyAggregationSnapshot? last = null;

        for (var qpc = 1; qpc <= firstWarmThroughQpc; qpc++)
        {
            var oldProcessId = qpc % 2 == 1 ? 100 : 200;
            var oldThreadId = qpc % 2 == 1 ? 10 : 20;
            var newProcessId = qpc % 2 == 1 ? 200 : 100;
            var newThreadId = qpc % 2 == 1 ? 20 : 10;
            Switch(buffer, qpc, 0, oldProcessId, oldThreadId, newProcessId, newThreadId);
            if (qpc >= 8)
            {
                last = buffer.Snapshot();
                if (qpc == 8)
                {
                    first = last;
                }
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 1; index <= measuredIterations; index++)
        {
            var qpc = firstWarmThroughQpc + index;
            var oldProcessId = qpc % 2 == 1 ? 100 : 200;
            var oldThreadId = qpc % 2 == 1 ? 10 : 20;
            var newProcessId = qpc % 2 == 1 ? 200 : 100;
            var newThreadId = qpc % 2 == 1 ? 20 : 10;
            Switch(buffer, qpc, 0, oldProcessId, oldThreadId, newProcessId, newThreadId);
            last = buffer.Snapshot();
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocatedBytes, 0, 2 * 1024 * 1024);
        Assert.NotNull(first);
        Assert.NotNull(last);
        Assert.True(last.IsComplete);
        Assert.Equal(2, last.Records.Count);
        Assert.Equal(new[] { 100, 200 }, last.Records.Select(static record => record.ProcessId));
        Assert.Equal(8, last.Records.Sum(static record => record.ExecutionTimeQpc));
        Assert.Equal(4, Assert.Single(last.Records, static record => record.ProcessId == 100).ExecutionTimeQpc);
        Assert.Equal(4, Assert.Single(last.Records, static record => record.ProcessId == 200).ExecutionTimeQpc);
        Assert.InRange(buffer.GetDiagnostics().ActivePairCount, 0, maximumActivePairs);
        Assert.NotSame(first.Records, last.Records);
        Assert.Equal(8, first.Records.Sum(static record => record.ExecutionTimeQpc));

        Assert.Equal(firstWarmThroughQpc + measuredIterations - 8, last.WindowStartQpc);
        Assert.Equal(firstWarmThroughQpc + measuredIterations, last.MeasuredThroughQpc);

        // A re-issued begin (same n) restarts above the latest event and releases every ring.
        buffer.BeginWindow(0, 8);
        Assert.Equal(0, buffer.GetDiagnostics().ActivePairCount);
        Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
        Switch(buffer, firstWarmThroughQpc + measuredIterations, 0, 0, 0, 100, 10);
        for (var qpc = firstWarmThroughQpc + measuredIterations + 1;
            qpc <= firstWarmThroughQpc + measuredIterations + 8;
            qpc++)
        {
            var oldProcessId = qpc % 2 == 1 ? 100 : 200;
            var oldThreadId = qpc % 2 == 1 ? 10 : 20;
            var newProcessId = qpc % 2 == 1 ? 200 : 100;
            var newThreadId = qpc % 2 == 1 ? 20 : 10;
            Switch(buffer, qpc, 0, oldProcessId, oldThreadId, newProcessId, newThreadId);
        }

        var afterReset = buffer.Snapshot();
        Assert.True(afterReset.IsComplete);
        Assert.Equal(8, afterReset.Records.Sum(static record => record.ExecutionTimeQpc));
        Assert.Equal(8, first.Records.Sum(static record => record.ExecutionTimeQpc));
    }

    [Fact]
    public void RepeatedSameInstanceProcessCallbacksDoNotAllocateIdentityRecords()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumActivePairs: 8);
        StartProcess(buffer, 0, 100, 1000, "Game", 10, uniqueProcessKey: 11);
        for (var qpc = 1; qpc <= 32; qpc++)
        {
            buffer.ObserveProcessStart(qpc, DateTimeOffset.UnixEpoch, 100, 11, 1000, " Game ");
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var qpc = 33; qpc <= 10_032; qpc++)
        {
            buffer.ObserveProcessStart(qpc, DateTimeOffset.UnixEpoch, 100, 11, 1000, " Game ");
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocatedBytes, 0, 32 * 1024);
    }

    [Fact]
    public void SnapshotUsesTheLatestClosedBoundarySharedByEveryProcessor()
    {
        var buffer = CreateBuffer(windowMilliseconds: 500, processors: [0, 1]);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        StartProcess(buffer, 0, 200, 2000, "Worker", 20);

        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 0, 1, 0, 0, 200, 20);
        Switch(buffer, 500, 1, 200, 20, 0, 0);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var snapshot = buffer.Snapshot();
        Assert.True(snapshot.IsComplete);
        Assert.Equal(500, snapshot.MeasuredThroughQpc);
        Assert.Equal(500, Assert.Single(snapshot.Records, row => row.ProcessId == 100).ExecutionTimeQpc);
        Assert.Equal(500, Assert.Single(snapshot.Records, row => row.ProcessId == 200).ExecutionTimeQpc);
    }

    [Fact]
    public void ThirtyTwoProcessorsProduceOneExactFiveSecondWindow()
    {
        var logicalProcessors = Enumerable.Range(0, 32).ToArray();
        var buffer = CreateBuffer(windowMilliseconds: 5000, maximumActivePairs: 64, processors: logicalProcessors);

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

        var snapshot = buffer.Snapshot();
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
        // Continuous: five 1,000-QPC slots, read as the last five complete slots.
        buffer.BeginWindow(0, 5);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 10_000, 0, 100, 10, 0, 0);

        var first = buffer.Snapshot();
        Assert.True(first.IsComplete);
        Assert.Equal(5000, Assert.Single(first.Records).ExecutionTimeQpc);

        Switch(buffer, 12_000, 0, 0, 0, 0, 0);
        var second = buffer.Snapshot();
        Assert.Equal(3000, Assert.Single(second.Records).ExecutionTimeQpc);

        Switch(buffer, 15_000, 0, 0, 0, 0, 0);
        Assert.Empty(buffer.Snapshot().Records);
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
            Assert.Single(baseline.Snapshot().Records).ExecutionTimeQpc,
            Assert.Single(delayed.Snapshot().Records).ExecutionTimeQpc);
    }

    [Fact]
    public void UnknownNewProcessIdUsesTheKnownThreadLifecycleIdentity()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);

        Switch(buffer, 0, 0, 0, 0, -1, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var row = Assert.Single(buffer.Snapshot().Records);
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

        var row = Assert.Single(buffer.Snapshot().Records);
        Assert.Equal(100, row.ProcessId);
        Assert.Equal(10, row.ThreadId);
        Assert.Equal(1000, row.ExecutionTimeQpc);
    }

    [Fact]
    public void UnattributedTimeIsNotBackfilledWhenACompleteWindowLaterCloses()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);

        Switch(buffer, 400, 0, 100, 10, -1, 999);
        Switch(buffer, 600, 0, -1, 999, 100, 10);

        var interrupted = buffer.Snapshot();
        Assert.False(interrupted.IsComplete);
        Assert.Empty(interrupted.Records);

        Switch(buffer, 1600, 0, 100, 10, 0, 0);

        var recovered = buffer.Snapshot();
        Assert.True(recovered.IsComplete);
        var row = Assert.Single(recovered.Records);
        Assert.Equal(100, row.ProcessId);
        // Finite [0, 1000): 400 before and 400 after the unattributed 200.
        Assert.Equal(800, row.ExecutionTimeQpc);
    }

    [Fact]
    public void OldIdentityMismatchBreaksWindowContinuity()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        StartProcess(buffer, 0, 200, 2000, "Other", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);

        Switch(buffer, 1000, 0, 200, 20, 200, 20);
        var interrupted = buffer.Snapshot();
        Assert.False(interrupted.IsComplete);
        Assert.Empty(interrupted.Records);

        Switch(buffer, 2000, 0, 200, 20, 0, 0);

        var recovered = buffer.Snapshot();
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
        Assert.NotEmpty(buffer.Snapshot().Records);

        buffer.BeginWindow(0, 0);

        Assert.False(buffer.Snapshot().IsComplete);
        Assert.Empty(buffer.Snapshot().Records);
    }

    [Fact]
    public void EventLossDiscardsIdentityAndRejectsOldInventoryWithoutReusingInstanceIds()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Old", 10, uniqueProcessKey: 11);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);
        var old = Assert.Single(buffer.Snapshot().Records);
        var inventory = buffer.CaptureInventorySeed();
        var process = Assert.IsType<OracleCpuResidencyAggregationBuffer.SeededProcess>(inventory.SeedProcess(100, 1000, "Old"));

        buffer.ResetAfterEventLoss();

        Assert.False(buffer.Snapshot().IsComplete);
        Assert.Null(inventory.SeedProcess(999, 9999, "Stale"));
        process.SeedThread(77);
        Assert.Equal(0, buffer.GetDiagnostics().CurrentProcessCount);
        Assert.Equal(0, buffer.GetDiagnostics().CurrentThreadCount);

        var replacement = Assert.IsType<OracleCpuResidencyAggregationBuffer.SeededProcess>(
            buffer.CaptureInventorySeed().SeedProcess(100, 2000, "New"));
        replacement.SeedThread(10);
        Switch(buffer, 1000, 0, 0, 0, 100, 10);
        Switch(buffer, 2000, 0, 100, 10, 0, 0);
        var recovered = buffer.Snapshot();
        Assert.True(recovered.IsComplete);
        var current = Assert.Single(recovered.Records);
        Assert.Equal(2000L, current.ProcessStartKey);
        Assert.Equal(1000, current.ExecutionTimeQpc);
        Assert.NotEqual(old.ProcessInstanceId, current.ProcessInstanceId);
        Assert.NotEqual(old.ThreadInstanceId, current.ThreadInstanceId);
    }

    [Fact]
    public void InventoryThreadCannotAttachToAReusedPid()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        var inventory = buffer.CaptureInventorySeed();
        var old = Assert.IsType<OracleCpuResidencyAggregationBuffer.SeededProcess>(inventory.SeedProcess(100, 1000, "Old"));
        old.SeedThread(10);
        StartProcess(buffer, 0, 100, 2000, "New", 20, uniqueProcessKey: 22);

        old.SeedThread(30);
        Assert.Null(inventory.SeedProcess(100, 1000, "Stale"));
        Switch(buffer, 0, 0, 0, 0, 100, 30);
        Switch(buffer, 1000, 0, 100, 30, 0, 0);
        Assert.Empty(buffer.Snapshot().Records);
        Assert.Equal(1, buffer.GetDiagnostics().CurrentThreadCount);

        // The finite window [0, 1000) is complete; the reused PID's thread runs in the next window.
        buffer.BeginWindow(0, 0);
        Switch(buffer, 1000, 0, 0, 0, 100, 20);
        Switch(buffer, 2000, 0, 100, 20, 0, 0);
        var row = Assert.Single(buffer.Snapshot().Records);
        Assert.Equal(20, row.ThreadId);
        Assert.Equal(2000L, row.ProcessStartKey);
        Assert.Equal("New", row.ProcessName);
    }

    [Fact]
    public void ContinuityResetPreservesAValidInventorySeed()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        var process = Assert.IsType<OracleCpuResidencyAggregationBuffer.SeededProcess>(
            buffer.CaptureInventorySeed().SeedProcess(100, 1000, "Current"));
        buffer.BeginWindow(0, 0);
        process.SeedThread(10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        Assert.Equal(1000, Assert.Single(buffer.Snapshot().Records).ExecutionTimeQpc);
    }

    [Fact]
    public void ClearingTheBufferRevokesAllCapturedInventoryWriters()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        var inventory = buffer.CaptureInventorySeed();
        var process = Assert.IsType<OracleCpuResidencyAggregationBuffer.SeededProcess>(inventory.SeedProcess(100, 1000, "Old"));
        buffer.Clear();
        Assert.Null(inventory.SeedProcess(100, 2000, "Stale"));
        process.SeedThread(10);
        Assert.Equal(0, buffer.GetDiagnostics().CurrentProcessCount);
        Assert.Equal(0, buffer.GetDiagnostics().CurrentThreadCount);
    }

    [Fact]
    public void EventLossKeepsTheQpcFloorAgainstDelayedOldLifecycleEvents()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 1000, 100, 1000, "Old", 10, uniqueProcessKey: 11);
        buffer.ResetAfterEventLoss();
        StartProcess(buffer, 999, 100, 1000, "Delayed", 10, uniqueProcessKey: 11);

        Assert.Equal(0, buffer.GetDiagnostics().CurrentProcessCount);
        Assert.Equal(0, buffer.GetDiagnostics().CurrentThreadCount);
        Assert.False(buffer.Snapshot().IsComplete);
    }

    [Fact]
    public void PidReuseProducesDistinctProcessInstances()
    {
        // The finite window [0, 8000) covers both instances' runs.
        var buffer = CreateBuffer(windowMilliseconds: 8000);
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

        var rows = buffer.Snapshot().Records.OrderBy(row => row.ProcessStartKey).ToArray();
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

        var rows = buffer.Snapshot().Records
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

        var row = Assert.Single(buffer.Snapshot().Records);
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

        var rows = buffer.Snapshot().Records
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

        var row = Assert.Single(buffer.Snapshot().Records);
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

        var row = Assert.Single(buffer.Snapshot().Records);
        Assert.Equal(1000, row.ExecutionTimeQpc);
        Assert.Equal(1000, row.ProcessStartKey);
    }

    [Fact]
    public void CapacityOverflowFailsClosedAndRewarms()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumActivePairs: 1);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 500, 0, 100, 10, 200, 20);
        Switch(buffer, 1000, 0, 200, 20, 100, 10);

        var snapshot = buffer.Snapshot();
        Assert.False(snapshot.IsComplete);
        Assert.Empty(snapshot.Records);
    }

    [Fact]
    public void CapacityOverflowRecoversOnlyAfterOneCompleteCleanWindow()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumActivePairs: 1);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 500, 0, 100, 10, 200, 20);
        Switch(buffer, 1000, 0, 200, 20, 100, 10);

        Assert.False(buffer.Snapshot().IsComplete);
        Assert.Equal(1, buffer.GetDiagnostics().CapacityResetCount);

        Switch(buffer, 2000, 0, 100, 10, 0, 0);

        var recovered = buffer.Snapshot();
        Assert.True(recovered.IsComplete);
        var row = Assert.Single(recovered.Records);
        Assert.Equal(100, row.ProcessId);
        Assert.Equal(1000, row.ExecutionTimeQpc);
        Assert.Equal(1, buffer.GetDiagnostics().CapacityResetCount);
    }

    [Fact]
    public void DefaultCapacityPublishesACompleteWindowForRecordedThreadCountAcrossTwoProcessors()
    {
        // Copy 006 retained 13,165 thread identities. Each recorded thread runs
        // once on each CPU, serially, so no thread executes concurrently.
        const int threadCount = 13165;
        var plan = HostManagerTestPlanFactory.CreatePlan(cpuTopology: HostManagerTestPlanFactory.CreateCpuTopology(1, 1))
            .CpuCoreResidency;
        var buffer = ReplayRecordedThreadMigration(plan.ExecutionTimeSource.MaximumActivePairs,
            plan.ObservationWindowMilliseconds);

        var diagnostics = buffer.GetDiagnostics();
        Assert.Equal(0, diagnostics.CapacityResetCount);
        var aggregate = buffer.Snapshot();
        Assert.True(aggregate.IsComplete);
        Assert.Equal(threadCount * 2, aggregate.Records.Count);
        var published = EtwCpuCoreResidencyReader.BuildProcessSnapshot(plan, 7, aggregate);
        var process = Assert.Single(published);
        Assert.Equal(threadCount, process.ThreadCount);
        Assert.Equal(2, process.LogicalProcessors.Count);
    }

    [Fact]
    public void FormerDefaultNamesThePairLimitForRecordedThreadMigration()
    {
        var buffer = ReplayRecordedThreadMigration(16384, 5000);
        var diagnostics = buffer.GetDiagnostics();
        Assert.Equal(1, diagnostics.CapacityResetCount);
        Assert.False(buffer.Snapshot().IsComplete);
        var failure = Assert.IsType<CpuResidencyCapacityResetDiagnostics>(diagnostics.LastCapacityReset);
        Assert.Equal("active-pair-limit", failure.Reason);
        Assert.Equal(16384, failure.ActivePairCount);
        Assert.Equal(16384, failure.MaximumActivePairs);
        Assert.Equal(0L, failure.WindowStartQpc);
        // A finite window has no ring and the pair limit needs no slot span.
        Assert.Equal(0, failure.RingSlots);
        Assert.Equal(13165, failure.CurrentThreadCount);
        Assert.Equal(0, failure.RequiredSlotSpan);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1500, false)]
    public void ContinuousWindowResetsOnRingSpanOnlyWhenTheRecomputedFloorStillCannotHoldTheWrite(
        int lowerProcessorBoundary, bool resets)
    {
        // n = 4 slots of 250 QPC, R = 10 cells. CPU 0's run [0, 2501) ends in slot 10.
        var buffer = CreateBuffer(windowMilliseconds: 1000, processors: [0, 1]);
        buffer.BeginWindow(0, 4);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 0, 1, 0, 0, 0, 0);
        if (lowerProcessorBoundary > 0) Switch(buffer, lowerProcessorBoundary, 1, 0, 0, 0, 0);
        Switch(buffer, 2501, 0, 100, 10, 0, 0);

        var diagnostics = buffer.GetDiagnostics();
        Assert.Equal(10, diagnostics.RingSlots);
        if (!resets)
        {
            // F = slot(1500) - 4 = 2, so slot 10 < F + R.
            Assert.Equal(0, diagnostics.CapacityResetCount);
            Assert.Equal(1, diagnostics.ActivePairCount);
            return;
        }

        Assert.Equal(1, diagnostics.CapacityResetCount);
        Assert.Equal(0, diagnostics.ActivePairCount);
        var failure = Assert.IsType<CpuResidencyCapacityResetDiagnostics>(diagnostics.LastCapacityReset);
        Assert.Equal("ring-span", failure.Reason);
        Assert.Equal(2501, failure.EventQpc);
        Assert.Equal(0L, failure.WindowStartQpc);
        Assert.Equal(0, failure.ActivePairCount);
        // CPU 1 still holds the lowest boundary 0: F stays 0 and slots 0..10 need 11 cells.
        Assert.Equal(11, failure.RequiredSlotSpan);
        Assert.Equal(10, failure.RingSlots);
        Assert.Equal(1, failure.CurrentThreadCount);
        Assert.False(buffer.Snapshot().IsComplete);
    }

    [Fact]
    public void CapacityFailureRetainsItsCauseAndPreResetCountsAfterRewarming()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumActivePairs: 1);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 500, 0, 100, 10, 200, 20);
        Switch(buffer, 1000, 0, 200, 20, 100, 10);

        var afterReset = buffer.GetDiagnostics();
        Assert.Equal(1, afterReset.CapacityResetCount);
        Assert.Equal(0, afterReset.ActivePairCount);
        // Read the diagnostic boundary reflectively so the red test compiles
        // against the former snapshot that erased all pre-reset evidence.
        var property = afterReset.GetType().GetProperty("LastCapacityReset");
        Assert.NotNull(property);
        var failure = property.GetValue(afterReset);
        Assert.NotNull(failure);
        Assert.Equal("active-pair-limit", failure.GetType().GetProperty("Reason")!.GetValue(failure));
        Assert.Equal(1, failure.GetType().GetProperty("ActivePairCount")!.GetValue(failure));
        Assert.Equal(1000L, failure.GetType().GetProperty("EventQpc")!.GetValue(failure));

        Switch(buffer, 2000, 0, 100, 10, 0, 0);
        Assert.True(buffer.Snapshot().IsComplete);
        Assert.Equal(failure, property.GetValue(buffer.GetDiagnostics()));
        buffer.ReleaseWindowStorage();
        Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
        Assert.Equal(0, buffer.GetDiagnostics().ActivePairCount);
        Assert.Equal(failure, property.GetValue(buffer.GetDiagnostics()));
    }

    [Fact]
    public void CapacityEvictsExpiredPairsBeforeRejectingAnAppend()
    {
        // One pair fits. Continuous n = 10 (100-QPC slots): when F reaches slot 2 at 1200, B's pair
        // (last written slot 1) is dropped before A's run needs the only pair.
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumActivePairs: 1);
        buffer.BeginWindow(0, 10);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        Switch(buffer, 0, 0, 0, 0, 200, 20);
        Switch(buffer, 200, 0, 200, 20, 100, 10);
        Switch(buffer, 1200, 0, 100, 10, 0, 0);

        var snapshot = buffer.Snapshot();
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
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumActivePairs: capacity);
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
        Assert.InRange(diagnostics.ActivePairCount, 0, capacity);
        Assert.InRange(diagnostics.RetainedProcessIdentityCount, 0, capacity);
        Assert.InRange(diagnostics.RetainedThreadIdentityCount, 0, capacity);
        Assert.Equal(0, diagnostics.CurrentProcessCount);
        Assert.Equal(0, diagnostics.CurrentThreadCount);
        Assert.True(diagnostics.CapacityResetCount > 0);

        buffer.ReleaseWindowStorage();
        diagnostics = buffer.GetDiagnostics();
        Assert.Equal(0, diagnostics.ActivePairCount);
        Assert.Equal(0, diagnostics.RetainedProcessIdentityCount);
        Assert.Equal(0, diagnostics.RetainedThreadIdentityCount);
    }

    [Fact]
    public void MaximumQpcBoundaryIsNotConfusedWithAnUnsetBoundary()
    {
        var buffer = new OracleCpuResidencyAggregationBuffer(1000, 1, 4, [0]);
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

        var snapshot = buffer.Snapshot();
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

        Assert.False(buffer.Snapshot().IsComplete);
        Assert.Empty(buffer.Snapshot().Records);
    }

    [Fact]
    public void ExpectedProcessorSetIsValidatedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new OracleCpuResidencyAggregationBuffer(1000, 1000, 8, []));
        Assert.Throws<ArgumentException>(() => new OracleCpuResidencyAggregationBuffer(1000, 1000, 8, [0, 0]));
        Assert.Throws<ArgumentException>(() => new OracleCpuResidencyAggregationBuffer(1000, 1000, 8, [-1]));
        Assert.Throws<InvalidOperationException>(
            () => new OracleCpuResidencyAggregationBuffer(1000, 1000, 8, [0, 1, 2], maximumProcessors: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateBuffer(1000).BeginWindow(0, 2048));
    }

    [Fact]
    public void FiniteWindowTotalsEqualHandComputedRunsAcrossStartEndAndALateProcessor()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000, processors: [0, 1]);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        buffer.ObserveThreadStart(0, Time(0), 200, 21);
        Switch(buffer, 100, 0, 0, 0, 100, 10);
        // A processor outside the expected set is ignored.
        Switch(buffer, 150, 5, 0, 0, 200, 21);
        Assert.False(buffer.Snapshot().HasBoundaries);
        // CPU 1's first switch makes every processor known: S = 300, E = 1300.
        Switch(buffer, 300, 1, 0, 0, 200, 20);
        // [100, 700) spans S: 400 QPC and no switch (it started before S).
        Switch(buffer, 700, 0, 100, 10, 200, 21);
        // [700, 1500) spans E: 600 QPC and one switch.
        Switch(buffer, 1500, 0, 200, 21, 0, 0);

        var waiting = buffer.Snapshot();
        Assert.False(waiting.IsComplete);
        Assert.True(waiting.HasBoundaries);
        Assert.Empty(waiting.Records);
        Assert.Equal(300, waiting.WindowStartQpc);
        Assert.Equal(300, waiting.MeasuredThroughQpc);
        Assert.Equal(Time(300), waiting.MeasuredThrough);

        // CPU 1 covers E late: [300, 2000) gives 1000 QPC and one switch (it started at S).
        Switch(buffer, 2000, 1, 200, 20, 100, 10);
        // A run that starts after E adds nothing and creates no pair.
        Switch(buffer, 2100, 1, 100, 10, 0, 0);

        var snapshot = buffer.Snapshot();
        Assert.True(snapshot.IsComplete);
        Assert.Equal(300, snapshot.WindowStartQpc);
        Assert.Equal(1300, snapshot.MeasuredThroughQpc);
        Assert.Equal(Time(1300), snapshot.MeasuredThrough);
        Assert.Equal(
            [(10, 0, 400L, 0), (20, 1, 1000L, 1), (21, 0, 600L, 1)],
            snapshot.Records.Select(static row => (row.ThreadId, row.LogicalProcessorId, row.ExecutionTimeQpc, row.SwitchCount)));
        Assert.Equal(3, buffer.GetDiagnostics().ActivePairCount);

        // A complete finite window stops growing.
        Switch(buffer, 3000, 0, 0, 0, 100, 10);
        Switch(buffer, 4000, 0, 100, 10, 0, 0);
        Assert.Equal(snapshot.Records, buffer.Snapshot().Records);
    }

    [Fact]
    public void FiniteWindowKeepsItsTotalsWhenAProcessorThatAlreadyCoveredEDiscontinues()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000, processors: [0, 1]);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 0, 1, 0, 0, 0, 0);
        Switch(buffer, 1200, 0, 100, 10, 0, 0);
        var epoch = buffer.GetDiagnostics().ContinuityEpoch;

        // CPU 0 already covered E = 1000, so its broken run cannot change [0, 1000).
        Switch(buffer, 1300, 0, 200, 20, 200, 20);
        Switch(buffer, 1400, 1, 0, 0, 0, 0);

        var snapshot = buffer.Snapshot();
        Assert.True(snapshot.IsComplete);
        Assert.Equal(0, snapshot.WindowStartQpc);
        Assert.Equal(1000, Assert.Single(snapshot.Records).ExecutionTimeQpc);
        Assert.Equal(epoch, buffer.GetDiagnostics().ContinuityEpoch);
    }

    [Fact]
    public void DiscontinuityRestartsAContinuousWindowAtTheSwitchWithoutAContinuityReset()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        buffer.BeginWindow(0, 4);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        StartProcess(buffer, 0, 200, 2000, "B", 20);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1200, 0, 100, 10, 100, 10);
        Assert.True(buffer.Snapshot().IsComplete);
        var epoch = buffer.GetDiagnostics().ContinuityEpoch;

        // The old thread does not match the running one: pairs are dropped and S moves to 1300.
        Switch(buffer, 1300, 0, 200, 20, 200, 20);
        var restarted = buffer.Snapshot();
        Assert.False(restarted.IsComplete);
        Assert.Equal(1300, restarted.WindowStartQpc);
        Assert.Equal(0, buffer.GetDiagnostics().ActivePairCount);
        Assert.Equal(epoch, buffer.GetDiagnostics().ContinuityEpoch);

        Switch(buffer, 2300, 0, 200, 20, 0, 0);
        var recovered = buffer.Snapshot();
        Assert.True(recovered.IsComplete);
        Assert.Equal(1300, recovered.WindowStartQpc);
        var row = Assert.Single(recovered.Records);
        Assert.Equal(20, row.ThreadId);
        Assert.Equal(1000, row.ExecutionTimeQpc);
    }

    [Fact]
    public void ConsecutiveContinuousSnapshotsSlideByOneSlot()
    {
        // n = 3: b(k) = floor(k * 1000 / 3) = 0, 333, 666, 1000, 1333, 1666, 2000.
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        buffer.BeginWindow(0, 3);
        Assert.Equal(3, buffer.SlotsPerWindow);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        long[] boundaries = [0, 333, 666, 1000, 1333, 1666, 2000];
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        for (var slot = 1; slot < boundaries.Length; slot++)
        {
            Switch(buffer, boundaries[slot], 0, 100, 10, 100, 10);
            var snapshot = buffer.Snapshot();
            if (slot < 3)
            {
                Assert.False(snapshot.IsComplete);
                continue;
            }

            Assert.True(snapshot.IsComplete);
            Assert.Equal(boundaries[slot - 3], snapshot.WindowStartQpc);
            Assert.Equal(boundaries[slot], snapshot.MeasuredThroughQpc);
            Assert.Equal(Time(boundaries[slot]), snapshot.MeasuredThrough);
            var row = Assert.Single(snapshot.Records);
            Assert.Equal(1000, row.ExecutionTimeQpc);
            Assert.Equal(3, row.SwitchCount);
        }
    }

    [Fact]
    public void ContinuousWindowStorageStaysBoundedOverALongRunWithThreadChurn()
    {
        const int threads = 10_000;
        // W = 100 QPC, n = 4 (25-QPC slots), R = 10. Each thread runs once, for 10 QPC.
        var buffer = CreateBuffer(windowMilliseconds: 100, maximumActivePairs: 64);
        buffer.BeginWindow(0, 4);
        buffer.ObserveProcessStart(0, Time(0), 100, 11, 1000, "Churn");
        buffer.ObserveThreadStart(0, Time(0), 100, 1);
        Switch(buffer, 0, 0, 0, 0, 100, 1);
        var peakActivePairs = 0;
        for (var thread = 2; thread <= threads; thread++)
        {
            var qpc = (thread - 1) * 10L;
            buffer.ObserveThreadStart(qpc, Time(qpc), 100, thread);
            Switch(buffer, qpc, 0, 100, thread - 1, 100, thread);
            buffer.ObserveThreadEnd(qpc, Time(qpc), 100, thread - 1);
            peakActivePairs = Math.Max(peakActivePairs, buffer.GetDiagnostics().ActivePairCount);
        }

        // Pairs whose last written slot fell below F are dropped: about W + one slot of threads stay live.
        Assert.InRange(peakActivePairs, 1, 16);
        var diagnostics = buffer.GetDiagnostics();
        Assert.Equal(0, diagnostics.CapacityResetCount);
        Assert.Equal(1, diagnostics.CurrentThreadCount);
        Assert.InRange(diagnostics.RetainedThreadIdentityCount, 1, 17);
        // One record chunk (2,730 x 24 B) and one ring chunk (409 rings x 160 B).
        Assert.Equal(65_520 + 409 * 160, diagnostics.WindowStorageBytes);
        Assert.Equal(diagnostics.MaximumWindowStorageBytes, diagnostics.WindowStorageBytes);

        // The last switch at 99,990 is in slot 3999: the window is [99,875, 99,975), fully attributed.
        var snapshot = buffer.Snapshot();
        Assert.True(snapshot.IsComplete);
        Assert.Equal(99_875, snapshot.WindowStartQpc);
        Assert.Equal(99_975, snapshot.MeasuredThroughQpc);
        Assert.Equal(100, snapshot.Records.Sum(static row => row.ExecutionTimeQpc));
        Assert.Equal(10, snapshot.Records.Sum(static row => row.SwitchCount));
    }

    [Fact]
    public void WindowStorageIsZeroAfterRelease()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumActivePairs: 8);
        StartProcess(buffer, 0, 100, 1000, "A", 10);
        Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);
        var finite = buffer.GetDiagnostics();
        // One 2,730-record chunk of 24 B records; a finite window has no ring.
        Assert.Equal(0, finite.SlotsPerWindow);
        Assert.Equal(0, finite.RingSlots);
        Assert.Equal(65_520, finite.WindowStorageBytes);
        Assert.Equal(65_520, finite.MaximumWindowStorageBytes);

        buffer.ReleaseWindowStorage();
        Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
        Assert.Equal(0, buffer.GetDiagnostics().ActivePairCount);
        Assert.False(buffer.Snapshot().IsComplete);

        buffer.BeginWindow(0, 4);
        Switch(buffer, 1000, 0, 0, 0, 100, 10);
        Switch(buffer, 2000, 0, 100, 10, 0, 0);
        var continuous = buffer.GetDiagnostics();
        Assert.Equal(4, continuous.SlotsPerWindow);
        Assert.Equal(10, continuous.RingSlots);
        // Plus one chunk of floor(65,536 / 160) = 409 rings of ten 16 B cells.
        Assert.Equal(65_520 + 409 * 160, continuous.WindowStorageBytes);
        Assert.True(buffer.Snapshot().IsComplete);

        buffer.ReleaseWindowStorage();
        Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
        buffer.Clear();
        Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
        Assert.Equal(0, buffer.GetDiagnostics().SlotsPerWindow);
    }

    [Fact]
    public void ConcurrentSnapshotsDoNotMutateSettlement()
    {
        var buffer = CreateBuffer(windowMilliseconds: 1000);
        StartProcess(buffer, 0, 100, 1000, "Game", 10);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        Switch(buffer, 1000, 0, 100, 10, 0, 0);

        var snapshots = new CpuResidencyAggregationSnapshot[64];
        Parallel.For(0, snapshots.Length, index => snapshots[index] = buffer.Snapshot());

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
        var buffer = CreateBuffer(windowMilliseconds: 1000, maximumActivePairs: 4096);
        // Continuous n = 4: every complete snapshot is the last 1,000 QPC.
        buffer.BeginWindow(0, 4);
        StartProcess(buffer, 0, 100, 1000, "A", 10, uniqueProcessKey: 11);
        StartProcess(buffer, 0, 200, 2000, "B", 20, uniqueProcessKey: 22);
        Switch(buffer, 0, 0, 0, 0, 100, 10);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var windowReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeSnapshotCount = 0;

        var writer = Task.Run(async () =>
        {
            try
            {
                var oldProcessId = 100;
                var oldThreadId = 10;
                for (var qpc = 1; qpc <= switchCount; qpc++)
                {
                    stop.Token.ThrowIfCancellationRequested();
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
                    if (qpc == 1000)
                    {
                        windowReady.SetResult();
                        await snapshotReady.Task.WaitAsync(stop.Token);
                    }
                }
            }
            catch
            {
                stop.Cancel();
                throw;
            }
        });
        var settler = Task.Run(async () =>
        {
            try
            {
                await windowReady.Task.WaitAsync(stop.Token);
                while (!writer.IsCompleted)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var snapshot = buffer.Snapshot();
                    if (!snapshot.IsComplete)
                    {
                        continue;
                    }

                    Assert.InRange(
                        snapshot.Records.Sum(static row => row.ExecutionTimeQpc),
                        0,
                        1000);
                    Interlocked.Increment(ref completeSnapshotCount);
                    snapshotReady.TrySetResult();
                }
            }
            catch
            {
                stop.Cancel();
                throw;
            }
        });

        try
        {
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
            await settler.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(writer, settler).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(Volatile.Read(ref completeSnapshotCount) > 0);
        var final = buffer.Snapshot();
        Assert.True(final.IsComplete);
        Assert.Equal(1000, final.Records.Sum(static row => row.ExecutionTimeQpc));
        Assert.Equal(0, buffer.GetDiagnostics().CapacityResetCount);
    }

    private static OracleCpuResidencyAggregationBuffer ReplayRecordedThreadMigration(
        int maximumPairs, int windowMilliseconds)
    {
        const int threadCount = 13165;
        const long frequency = 10000;
        var buffer = new OracleCpuResidencyAggregationBuffer(frequency, windowMilliseconds, maximumPairs, [0, 1]);
        buffer.ObserveProcessStart(0, DateTimeOffset.UnixEpoch, 100, 11, 1000, "recorded-two-cpu-migration");
        for (var thread = 1; thread <= threadCount; thread++)
            buffer.ObserveThreadStart(0, DateTimeOffset.UnixEpoch, 100, thread);
        void RecordedSwitch(long qpc, int cpu, int oldThread, int newThread)
            => buffer.ObserveContextSwitch(qpc, DateTimeOffset.UnixEpoch.AddTicks(qpc * TimeSpan.TicksPerSecond / frequency),
                cpu, oldThread == 0 ? 0 : 100, oldThread, newThread == 0 ? 0 : 100, null, newThread);

        RecordedSwitch(0, 0, 0, 1);
        RecordedSwitch(0, 1, 0, 0);
        long at = 0;
        for (var thread = 1; thread <= threadCount; thread++)
            RecordedSwitch(++at, 0, thread, thread == threadCount ? 0 : thread + 1);
        RecordedSwitch(at, 1, 0, 1);
        for (var thread = 1; thread <= threadCount; thread++)
            RecordedSwitch(++at, 1, thread, thread == threadCount ? 0 : thread + 1);
        var through = frequency * windowMilliseconds / 1000;
        Assert.True(at < through); // Every pair fits inside the same five-second window.
        RecordedSwitch(through, 0, 0, 0);
        RecordedSwitch(through, 1, 0, 0);
        return buffer;
    }

    // A fresh buffer begins a finite window at floor 0; the expected processor set defaults to [0].
    private static OracleCpuResidencyAggregationBuffer CreateBuffer(
        int windowMilliseconds,
        int maximumActivePairs = 1024,
        int[]? processors = null)
        => new(1000, windowMilliseconds, maximumActivePairs, processors ?? [0]);

    private static void StartProcess(
        OracleCpuResidencyAggregationBuffer buffer,
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
        OracleCpuResidencyAggregationBuffer buffer,
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
