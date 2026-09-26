using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IProcessResourcePolicyWriter
{
    ProcessResourcePolicySnapshot? TryReadProcess(int processId);

    DateTimeOffset? TryReadProcessStartedAt(int processId);

    RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadProcessInstanceForRecovery(
        int processId);

    RecoveryReadResult<ProcessPlacementRecoverySnapshot> ReadPlacementStateForRecovery(
        int processId,
        ProcessPlacementReadFields requiredFields);

    RecoveryReadResult<ThreadCpuSetPolicySnapshot> ReadThreadPlacementStateForRecovery(
        int processId,
        int threadId);

    ProcessResourcePolicyWriteResult TrySetPriorityClass(
        int processId,
        string priorityClass);

    ProcessResourcePolicyWriteResult TrySetProcessorAffinity(
        int processId,
        long affinityMask);

    IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int processId);

    ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(
        int processId,
        IReadOnlyList<uint> cpuSetIds);

    ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(
        int processId,
        int threadId);

    ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
        int processId,
        int threadId,
        DateTimeOffset expectedCreatedAt,
        IReadOnlyList<uint> cpuSetIds);

    ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(int processId);

    [Obsolete("Use the exact-process expected-current overload.")]
    ProcessResourcePolicyWriteResult TrySetMemoryPriority(
        int processId,
        uint memoryPriority) =>
        new(false, "MemoryPriority 写入缺少精确进程实例与 expected-current；已拒绝执行。");

    ProcessResourcePolicyWriteResult TrySetMemoryPriority(
        int processId,
        DateTimeOffset expectedStartedAt,
        uint expectedCurrentMemoryPriority,
        uint memoryPriority);

    ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(int processId);

    ProcessResourcePolicyWriteResult TrySetPowerThrottling(
        int processId,
        uint controlMask,
        uint stateMask);

    IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
        IReadOnlyList<ProcessResourcePolicyBatchRequest> requests);
}
