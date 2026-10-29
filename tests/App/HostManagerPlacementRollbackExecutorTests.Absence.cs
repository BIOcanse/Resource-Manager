using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerPlacementRollbackExecutorTests
{
    [Theory]
    [InlineData(RecoveryReadAbsenceReason.TargetExited, HostManagerSmartRecordRestoreResults.TargetExited)]
    [InlineData(RecoveryReadAbsenceReason.IdentityChanged, HostManagerSmartRecordRestoreResults.OwnershipLost)]
    [InlineData(RecoveryReadAbsenceReason.Unspecified, HostManagerSmartRecordRestoreResults.Unknown)]
    public void ProcessAbsencePreservesRetirementSemanticsAndTypedHistoryReason(
        RecoveryReadAbsenceReason reason, string expected)
    {
        var writer = new FakeProcessPolicyWriter(AbsentRead<ProcessPlacementRecoverySnapshot>(reason));
        var settlement = CreateExecutor(writer).RestoreRecord(AffinityRecord());
        AssertAbsentSettlement(settlement, expected);
        Assert.Equal(0, writer.AffinityWriteCount);
    }

    [Theory]
    [InlineData(RecoveryReadAbsenceReason.TargetExited, HostManagerSmartRecordRestoreResults.TargetExited)]
    [InlineData(RecoveryReadAbsenceReason.IdentityChanged, HostManagerSmartRecordRestoreResults.OwnershipLost)]
    [InlineData(RecoveryReadAbsenceReason.Unspecified, HostManagerSmartRecordRestoreResults.Unknown)]
    public void ThreadCpuSetAbsencePreservesTypedHistoryReasonBeforeWrite(
        RecoveryReadAbsenceReason reason, string expected)
    {
        var writer = new FakeProcessPolicyWriter();
        writer.ThreadReads.Enqueue(AbsentRead<ThreadCpuSetPolicySnapshot>(reason));
        AssertAbsentSettlement(CreateExecutor(writer).RestoreRecord(AbsenceThreadCpuSetRecord()), expected);
    }

    [Theory]
    [InlineData(RecoveryReadAbsenceReason.TargetExited, HostManagerSmartRecordRestoreResults.TargetExited)]
    [InlineData(RecoveryReadAbsenceReason.IdentityChanged, HostManagerSmartRecordRestoreResults.OwnershipLost)]
    [InlineData(RecoveryReadAbsenceReason.Unspecified, HostManagerSmartRecordRestoreResults.Unknown)]
    public void ThreadCpuSetAbsencePreservesTypedHistoryReasonAfterWrite(
        RecoveryReadAbsenceReason reason, string expected)
    {
        var writer = new FakeProcessPolicyWriter();
        writer.ThreadReads.Enqueue(RecoveryReadResult<ThreadCpuSetPolicySnapshot>.Found(
            new(42, 43, DateTimeOffset.FromUnixTimeMilliseconds(1000), [2])));
        writer.ThreadReads.Enqueue(AbsentRead<ThreadCpuSetPolicySnapshot>(reason));
        AssertAbsentSettlement(CreateExecutor(writer).RestoreRecord(AbsenceThreadCpuSetRecord()), expected);
        Assert.Empty(writer.ThreadReads);
    }

    [Theory]
    [InlineData(RecoveryReadAbsenceReason.TargetExited, HostManagerSmartRecordRestoreResults.TargetExited)]
    [InlineData(RecoveryReadAbsenceReason.IdentityChanged, HostManagerSmartRecordRestoreResults.OwnershipLost)]
    [InlineData(RecoveryReadAbsenceReason.Unspecified, HostManagerSmartRecordRestoreResults.Unknown)]
    public void IdealProcessorAbsencePreservesTypedHistoryReasonBeforeAndAfterWrite(
        RecoveryReadAbsenceReason reason, string expected)
    {
        var initialMissing = new FakeProcessPolicyWriter { IdealRead = AbsentRead<ThreadIdealProcessorPolicySnapshot>(reason) };
        AssertAbsentSettlement(CreateExecutor(initialMissing).RestoreRecord(IdealRecord()), expected);
        Assert.Equal(0, initialMissing.IdealWrites);
        var disappearsAfterWrite = new FakeProcessPolicyWriter
        {
            IdealRead = RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Found(
                new(123, 456, 789, 890, new(0, 2), 0, 15, [])),
            IdealAfterWrite = AbsentRead<ThreadIdealProcessorPolicySnapshot>(reason)
        };
        AssertAbsentSettlement(CreateExecutor(disappearsAfterWrite).RestoreRecord(IdealRecord()), expected);
        Assert.Equal(1, disappearsAfterWrite.IdealWrites);
    }

    [Fact]
    public void FoundProcessAndThreadIdentityChangesRemainOwnershipLost()
    {
        var process = CreateExecutor(new FakeProcessPolicyWriter(
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(ProcessSnapshot(2, 2))))
            .RestoreRecord(AffinityRecord());
        var thread = CreateExecutor(new FakeProcessPolicyWriter
        {
            IdealRead = RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Found(
                new(123, 456, 789, 891, new(0, 2), 0, 15, []))
        }).RestoreRecord(IdealRecord());
        Assert.Equal(HostManagerPlacementSettlementKind.OwnershipLost, process.Kind);
        Assert.Equal(HostManagerPlacementSettlementKind.OwnershipLost, thread.Kind);
        Assert.True(process.CanRemoveReceipt);
        Assert.True(thread.CanRemoveReceipt);
        Assert.Null(process.HistoryRestoreResult);
        Assert.Null(thread.HistoryRestoreResult);
    }

    [Theory]
    [InlineData(true, HostManagerPlacementSettlementKind.OwnershipLost)]
    [InlineData(false, HostManagerPlacementSettlementKind.AlreadyRestored)]
    public void MissingGraphicsPreferenceIsNeverTreatedAsTargetExit(bool hadValue, HostManagerPlacementSettlementKind expected)
    {
        var graphics = new FakeGraphicsPreferenceStore(RecoveryReadResult<string>.NotFoundOrExited(0, "missing value"));
        var settlement = CreateExecutor(graphics: graphics).RestoreRecord(GpuRecord("app.exe", hadValue, "old", "new"));
        Assert.Equal(expected, settlement.Kind);
        Assert.True(settlement.CanRemoveReceipt);
        Assert.Null(settlement.HistoryRestoreResult);
    }

    [Fact]
    public void AbsenceReasonAndHistoryOverrideStayOutOfJson()
    {
        var read = RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Exited(87, "gone");
        using var readJson = JsonDocument.Parse(JsonSerializer.Serialize(read));
        Assert.False(readJson.RootElement.TryGetProperty(nameof(read.AbsenceReason), out _));
        var settlement = CreateExecutor(new FakeProcessPolicyWriter(read)).RestoreRecord(AffinityRecord());
        using var settlementJson = JsonDocument.Parse(JsonSerializer.Serialize(settlement));
        Assert.False(settlementJson.RootElement.TryGetProperty(nameof(settlement.HistoryRestoreResult), out _));
    }

    [Theory]
    [InlineData("owner", RecoveryReadAbsenceReason.IdentityChanged)]
    [InlineData("creation", RecoveryReadAbsenceReason.IdentityChanged)]
    [InlineData("exit", RecoveryReadAbsenceReason.TargetExited)]
    public void IdealNativeReadDistinguishesChangedIdentityFromConfirmedExit(string scenario, RecoveryReadAbsenceReason expected)
    {
        var api = new AbsenceNativeApi
        {
            Owner = scenario == "owner" ? 101U : 100U,
            Creation = scenario == "creation" ? 999UL : 1001UL,
            Exit = scenario == "exit" ? 1002UL : 0UL
        };
        var read = WindowsProcessResourcePolicyWriter.ReadIdealOnVerifiedHandle(api, new IntPtr(1), 100, 1000, 123);
        Assert.Equal(RecoveryReadStatus.NotFoundOrExited, read.Status);
        Assert.Equal(expected, read.AbsenceReason);
    }

    [Theory]
    [InlineData("parent-identity", RecoveryReadAbsenceReason.IdentityChanged)]
    [InlineData("parent-exit", RecoveryReadAbsenceReason.TargetExited)]
    [InlineData("thread-absent", RecoveryReadAbsenceReason.TargetExited)]
    public void IdealReadCyclePreservesTypedParentAndMissingThreadReasons(string scenario, RecoveryReadAbsenceReason expected)
    {
        var parent = scenario == "parent-exit"
            ? RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Exited(0, "gone")
            : RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(100, DateTimeOffset.FromFileTime(scenario == "parent-identity" ? 1001 : 1000)));
        var api = new AbsenceNativeApi { Missing = scenario == "thread-absent" };
        var cycle = new ThreadIdealProcessorReadCycle(
            () => RecoveryReadResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>.Found(
                new Dictionary<int, IReadOnlyList<int>> { [100] = [123] }),
            _ => parent, api);
        var read = cycle.ReadThread(100, 1000, 123);
        Assert.Equal(RecoveryReadStatus.NotFoundOrExited, read.Status);
        Assert.Equal(expected, read.AbsenceReason);
        if (scenario != "thread-absent")
            Assert.Equal(expected, cycle.ReadProcessThreads(100, 1000, 10).AbsenceReason);
    }

    private static RecoveryReadResult<T> AbsentRead<T>(RecoveryReadAbsenceReason reason)
        => RecoveryReadResult<T>.NotFoundOrExited(1168, "This message intentionally does not describe the absence reason.", reason);

    private static void AssertAbsentSettlement(HostManagerPlacementRecordSettlement settlement, string expected)
    {
        Assert.Equal(HostManagerPlacementSettlementKind.OwnershipLost, settlement.Kind);
        Assert.True(settlement.CanRemoveReceipt);
        Assert.Equal(expected, settlement.HistoryRestoreResult);
        Assert.Equal(1168, settlement.NativeErrorCode);
    }

    private static HostManagerAppliedRecord AbsenceThreadCpuSetRecord()
        => new(HostManagerAppliedRecordKinds.CpuThreadCpuSets, "thread-cpu-sets", new Dictionary<string, string>
        {
            ["processId"] = "42", ["threadId"] = "43", ["threadCreatedAt"] = "1000",
            ["previousCpuSetIds"] = "1", ["appliedCpuSetIds"] = "2"
        });

    private sealed class AbsenceNativeApi : IThreadIdealProcessorNativeApi
    {
        public uint Owner { get; init; } = 100;
        public ulong Creation { get; init; } = 1001;
        public ulong Exit { get; init; }
        public bool Missing { get; init; }
        public IntPtr OpenThread(int threadId, bool write) => Missing ? IntPtr.Zero : new IntPtr(1);
        public void CloseThread(IntPtr thread) { }
        public int LastError => 87;
        public uint ReadProcessId(IntPtr thread) => Owner;
        public bool TryReadTimes(IntPtr thread, out ulong creation, out ulong exit)
        {
            creation = Creation;
            exit = Exit;
            return true;
        }
        public bool TryReadIdeal(IntPtr thread, out ThreadIdealProcessor ideal) { ideal = new(0, 0); return true; }
        public bool TryReadAffinity(IntPtr thread, out ushort group, out ulong mask) { group = 0; mask = 15; return true; }
        public bool UsesCpuSets => false;
        public IReadOnlyList<uint>? ReadSelectedCpuSets(IntPtr thread) => [];
        public bool TrySetIdeal(IntPtr thread, ThreadIdealProcessor desired) => true;
    }
}
