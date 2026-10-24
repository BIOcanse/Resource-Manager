using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

/// <summary>One caller-owned placement read. Its inventory and verified parents never cross cycles.</summary>
public interface IThreadIdealProcessorReadCycle
{
    RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>> ReadProcessThreads(
        int processId, ulong processStartKey, int maximumThreadCount);
    RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> ReadThread(
        int processId, ulong processStartKey, int threadId);
}

internal sealed class UnsupportedThreadIdealProcessorReadCycle : IThreadIdealProcessorReadCycle
{
    internal static readonly UnsupportedThreadIdealProcessorReadCycle Instance = new();
    public RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>> ReadProcessThreads(
        int processId, ulong processStartKey, int maximumThreadCount)
        => RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Unavailable(50, "Thread ideal processor access is not supported by this writer.");
    public RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> ReadThread(int processId, ulong processStartKey, int threadId)
        => RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Unavailable(50, "Thread ideal processor access is not supported by this writer.");
}
