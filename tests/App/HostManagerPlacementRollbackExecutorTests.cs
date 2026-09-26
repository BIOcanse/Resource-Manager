using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerPlacementRollbackExecutorTests
{
    [Fact]
    public void GpuUnavailableRetainsRecordWithoutWrite()
    {
        var graphics = new FakeGraphicsPreferenceStore(
            RecoveryReadResult<string>.Unavailable(5, "access denied"));
        var executor = CreateExecutor(graphics: graphics);

        var settlement = executor.RestoreRecord(GpuRecord("app.exe", hadValue: true, "old", "new"));

        Assert.Equal(HostManagerPlacementSettlementKind.RetryableFailure, settlement.Kind);
        Assert.False(settlement.CanRemoveReceipt);
        Assert.Equal(0, graphics.WriteCount);
    }

    [Fact]
    public void GpuPreviousAndThirdPartyValuesAreDistinguished()
    {
        var previousGraphics = new FakeGraphicsPreferenceStore(RecoveryReadResult<string>.Found("old"));
        var thirdPartyGraphics = new FakeGraphicsPreferenceStore(RecoveryReadResult<string>.Found("other"));

        var already = CreateExecutor(graphics: previousGraphics)
            .RestoreRecord(GpuRecord("a.exe", hadValue: true, "old", "new"));
        var ownershipLost = CreateExecutor(graphics: thirdPartyGraphics)
            .RestoreRecord(GpuRecord("b.exe", hadValue: true, "old", "new"));

        Assert.Equal(HostManagerPlacementSettlementKind.AlreadyRestored, already.Kind);
        Assert.Equal(HostManagerPlacementSettlementKind.OwnershipLost, ownershipLost.Kind);
        Assert.Equal(0, previousGraphics.WriteCount + thirdPartyGraphics.WriteCount);
    }

    [Fact]
    public void GpuAppliedValueRestoresPreviousAndRequiresReadBack()
    {
        var graphics = new FakeGraphicsPreferenceStore(
            RecoveryReadResult<string>.Found("new"),
            RecoveryReadResult<string>.Found("old"));
        var settlement = CreateExecutor(graphics: graphics)
            .RestoreRecord(GpuRecord("app.exe", hadValue: true, "old", "new"));

        Assert.Equal(HostManagerPlacementSettlementKind.Restored, settlement.Kind);
        Assert.Equal(1, graphics.WriteCount);
    }

    [Fact]
    public void ProcessUnavailableExitedAndStartMismatchHaveDifferentSettlements()
    {
        var record = AffinityRecord();
        var unavailable = new FakeProcessPolicyWriter(
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Unavailable(5, "access denied"));
        var exited = new FakeProcessPolicyWriter(
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.NotFoundOrExited(87, "exited"));
        var reused = new FakeProcessPolicyWriter(
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(ProcessSnapshot(2, 2)));

        Assert.Equal(
            HostManagerPlacementSettlementKind.RetryableFailure,
            CreateExecutor(unavailable).RestoreRecord(record).Kind);
        Assert.Equal(
            HostManagerPlacementSettlementKind.OwnershipLost,
            CreateExecutor(exited).RestoreRecord(record).Kind);
        Assert.Equal(
            HostManagerPlacementSettlementKind.OwnershipLost,
            CreateExecutor(reused).RestoreRecord(record).Kind);
        Assert.Equal(0, unavailable.AffinityWriteCount + exited.AffinityWriteCount + reused.AffinityWriteCount);
    }

    [Fact]
    public void AffinityAppliedValueRestoresPreviousAndMissingStartIsInvalid()
    {
        var writer = new FakeProcessPolicyWriter(
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(ProcessSnapshot(1, 2)),
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(ProcessSnapshot(1, 3)));
        var executor = CreateExecutor(writer);

        var restored = executor.RestoreRecord(AffinityRecord());
        var invalid = executor.RestoreRecord(AffinityRecord() with
        {
            Metadata = AffinityRecord().Metadata!
                .Where(static pair => pair.Key != "processStartedAt")
                .ToDictionary(static pair => pair.Key, static pair => pair.Value)
        });

        Assert.Equal(HostManagerPlacementSettlementKind.Restored, restored.Kind);
        Assert.Equal(1, writer.AffinityWriteCount);
        Assert.Equal(HostManagerPlacementSettlementKind.InvalidReceipt, invalid.Kind);
    }

    [Fact]
    public void ExactProcessStartKeyRejectsSameMillisecondPidReuse()
    {
        var expectedStartedAt = DateTimeOffset.FromUnixTimeMilliseconds(1000).AddTicks(1);
        var reusedStartedAt = expectedStartedAt.AddTicks(1);
        var record = AffinityRecord() with
        {
            Metadata = new Dictionary<string, string>(AffinityRecord().Metadata!)
            {
                ["processStartKey"] = expectedStartedAt.ToFileTime().ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        var writer = new FakeProcessPolicyWriter(
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(
                ProcessSnapshot(reusedStartedAt, affinity: 2)));

        var settlement = CreateExecutor(writer).RestoreRecord(record);

        Assert.Equal(HostManagerPlacementSettlementKind.OwnershipLost, settlement.Kind);
        Assert.Equal(0, writer.AffinityWriteCount);
    }

    [Fact]
    public void PerRecordSettlementDistinguishesTerminalAndRetainedReceipts()
    {
        var graphics = new FakeGraphicsPreferenceStore(
            RecoveryReadResult<string>.Found("old"),
            RecoveryReadResult<string>.Unavailable(5, "busy"));
        var first = GpuRecord("first.exe", hadValue: true, "old", "new");
        var second = GpuRecord("second.exe", hadValue: true, "old", "new");
        var executor = CreateExecutor(graphics: graphics);
        var terminal = executor.RestoreRecord(first);
        var retained = executor.RestoreRecord(second);

        Assert.True(terminal.CanRemoveReceipt);
        Assert.False(retained.CanRemoveReceipt);
        Assert.Equal(HostManagerPlacementSettlementKind.RetryableFailure, retained.Kind);
    }

    private static HostManagerPlacementRollbackExecutor CreateExecutor(
        FakeProcessPolicyWriter? process = null,
        FakeGraphicsPreferenceStore? graphics = null)
        => new(
            process ?? new FakeProcessPolicyWriter(),
            graphics ?? new FakeGraphicsPreferenceStore(),
            gpuShimRuntime: null!,
            NullLogger.Instance);

    private static HostManagerAppliedRecord GpuRecord(
        string path,
        bool hadValue,
        string previous,
        string applied)
        => new(
            HostManagerAppliedRecordKinds.GpuPreference,
            path,
            new Dictionary<string, string>
            {
                ["path"] = path,
                ["hadValue"] = hadValue ? "true" : "false",
                ["previousValue"] = previous,
                ["appliedValue"] = applied
            });

    private static HostManagerAppliedRecord AffinityRecord()
        => new(
            HostManagerAppliedRecordKinds.CpuAffinity,
            "affinity",
            new Dictionary<string, string>
            {
                ["processId"] = "42",
                ["processName"] = "process",
                ["executablePath"] = string.Empty,
                ["processStartedAt"] = "1000",
                ["previousAffinityMask"] = "3",
                ["appliedAffinityMask"] = "2"
            });

    private static ProcessPlacementRecoverySnapshot ProcessSnapshot(long startSeconds, long affinity)
        => new(
            42,
            DateTimeOffset.FromUnixTimeMilliseconds(startSeconds * 1000),
            "process",
            null,
            affinity,
            null,
            null,
            null);

    private static ProcessPlacementRecoverySnapshot ProcessSnapshot(
        DateTimeOffset startedAt,
        long affinity)
        => new(
            42,
            startedAt,
            "process",
            null,
            affinity,
            null,
            null,
            null);

    private sealed class FakeGraphicsPreferenceStore(params RecoveryReadResult<string>[] reads)
        : IWindowsGraphicsPreferenceStore
    {
        private readonly Queue<RecoveryReadResult<string>> reads = new(reads);

        public int WriteCount { get; private set; }

        public RecoveryReadResult<string> ReadValueForRecovery(string executablePath)
            => reads.Count == 0
                ? RecoveryReadResult<string>.Unavailable(0, "no scripted read")
                : reads.Dequeue();

        public void WriteValue(string executablePath, string value) => WriteCount++;

        public void DeleteValue(string executablePath) => WriteCount++;

        public string BuildPreferIntegratedGpuValue(string? currentValue) => "integrated";

        public string BuildPreferHighPerformanceGpuValue(string? currentValue) => "dedicated";

        public string DescribePreference(string? value) => value ?? "missing";
    }

    private sealed class FakeProcessPolicyWriter(
        params RecoveryReadResult<ProcessPlacementRecoverySnapshot>[] reads)
        : IProcessResourcePolicyWriter
    {
        private readonly Queue<RecoveryReadResult<ProcessPlacementRecoverySnapshot>> reads = new(reads);

        public int AffinityWriteCount { get; private set; }

        public RecoveryReadResult<ProcessPlacementRecoverySnapshot> ReadPlacementStateForRecovery(
            int processId,
            ProcessPlacementReadFields requiredFields)
            => reads.Count == 0
                ? RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Unavailable(0, "no scripted read")
                : reads.Dequeue();

        public RecoveryReadResult<ThreadCpuSetPolicySnapshot> ReadThreadPlacementStateForRecovery(
            int processId,
            int threadId)
            => RecoveryReadResult<ThreadCpuSetPolicySnapshot>.Unavailable(0, "not scripted");

        public ProcessResourcePolicySnapshot? TryReadProcess(int processId) => null;

        public DateTimeOffset? TryReadProcessStartedAt(int processId) => null;

        public RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadProcessInstanceForRecovery(
            int processId)
            => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(0, "not scripted");

        public ProcessResourcePolicyWriteResult TrySetPriorityClass(int processId, string priorityClass)
            => new(true, "ok");

        public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(int processId, long affinityMask)
        {
            AffinityWriteCount++;
            return new ProcessResourcePolicyWriteResult(true, "ok");
        }

        public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int processId) => null;

        public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(
            int processId,
            IReadOnlyList<uint> cpuSetIds) => new(true, "ok");

        public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(int processId, int threadId) => null;

        public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
            int processId,
            int threadId,
            DateTimeOffset expectedCreatedAt,
            IReadOnlyList<uint> cpuSetIds) => new(true, "ok");

        public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(int processId) => null;

        public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
            int processId,
            DateTimeOffset expectedStartedAt,
            uint expectedCurrentMemoryPriority,
            uint memoryPriority)
            => new(true, "ok");

        public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(int processId) => null;

        public ProcessResourcePolicyWriteResult TrySetPowerThrottling(
            int processId,
            uint controlMask,
            uint stateMask) => new(true, "ok");

        public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
            IReadOnlyList<ProcessResourcePolicyBatchRequest> requests) => [];
    }
}
