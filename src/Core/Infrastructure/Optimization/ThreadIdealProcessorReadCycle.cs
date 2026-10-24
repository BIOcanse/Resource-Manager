using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal interface IThreadIdealProcessorNativeApi
{
    IntPtr OpenThread(int threadId, bool write);
    void CloseThread(IntPtr thread);
    int LastError { get; }
    uint ReadProcessId(IntPtr thread);
    bool TryReadTimes(IntPtr thread, out ulong creation, out ulong exit);
    bool TryReadIdeal(IntPtr thread, out ThreadIdealProcessor ideal);
    bool TryReadAffinity(IntPtr thread, out ushort group, out ulong mask);
    bool UsesCpuSets { get; }
    IReadOnlyList<uint>? ReadSelectedCpuSets(IntPtr thread);
    bool TrySetIdeal(IntPtr thread, ThreadIdealProcessor desired);
}

/// <summary>No handles are retained. The caller discards this inventory/parent table after one cycle.</summary>
internal sealed class ThreadIdealProcessorReadCycle(
    Func<RecoveryReadResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>> readInventory,
    Func<int, RecoveryReadResult<ProcessInstanceRecoverySnapshot>> readParent,
    IThreadIdealProcessorNativeApi api) : IThreadIdealProcessorReadCycle
{
    private RecoveryReadResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>? inventory;
    private readonly Dictionary<int, RecoveryReadResult<ProcessInstanceRecoverySnapshot>> parents = [];
    private readonly Dictionary<(int Pid, ulong Start, int Tid), RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>> threads = [];

    public RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>> ReadProcessThreads(
        int processId, ulong processStartKey, int maximumThreadCount)
    {
        if (processId <= 4 || processStartKey == 0 || maximumThreadCount <= 0)
            return RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Unavailable(87, "Invalid thread enumeration boundary.");
        var parent = ReadParent(processId, processStartKey);
        if (parent.Status != RecoveryReadStatus.Found)
            return new(parent.Status, null, parent.NativeErrorCode, parent.Message);
        inventory ??= readInventory();
        var snapshot = inventory.Value;
        if (snapshot.Status != RecoveryReadStatus.Found || snapshot.Value is null)
            return new(snapshot.Status, null, snapshot.NativeErrorCode, snapshot.Message);
        if (!snapshot.Value.TryGetValue(processId, out var threads))
            return RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.NotFoundOrExited(0, "Process is absent from the thread inventory.");
        if (threads.Count > maximumThreadCount)
            return RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Unavailable(122, "Thread enumeration exceeds the placement capacity.");
        var results = new List<ThreadIdealProcessorPolicySnapshot>(threads.Count);
        foreach (var threadId in threads)
        {
            if (threadId <= 0) continue;
            var read = ReadVerifiedThread(processId, processStartKey, threadId);
            if (read.Status == RecoveryReadStatus.Found && read.Value is not null) results.Add(read.Value);
        }
        return RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Found(results.OrderBy(static thread => thread.ThreadId).ToArray());
    }

    public RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> ReadThread(int processId, ulong processStartKey, int threadId)
    {
        if (processId <= 4 || processStartKey == 0 || threadId <= 0)
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Unavailable(87, "Invalid thread identity.");
        var parent = ReadParent(processId, processStartKey);
        return parent.Status == RecoveryReadStatus.Found
            ? ReadVerifiedThread(processId, processStartKey, threadId)
            : new(parent.Status, null, parent.NativeErrorCode, parent.Message);
    }

    private RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadParent(int processId, ulong expectedStart)
    {
        if (!parents.TryGetValue(processId, out var parent))
            parents.Add(processId, parent = readParent(processId));
        return WindowsProcessResourcePolicyWriter.VerifyIdealParent(parent, processId, expectedStart);
    }

    private RecoveryReadResult<ThreadIdealProcessorPolicySnapshot> ReadVerifiedThread(int processId, ulong processStartKey, int threadId)
    {
        var key = (processId, processStartKey, threadId);
        if (threads.TryGetValue(key, out var current)) return current;
        var thread = api.OpenThread(threadId, write: false);
        if (thread == IntPtr.Zero) current = WindowsProcessResourcePolicyWriter.IdealReadFailure(api.LastError);
        else
        {
            try { current = WindowsProcessResourcePolicyWriter.ReadIdealOnVerifiedHandle(api, thread, processId, processStartKey, threadId); }
            finally { api.CloseThread(thread); }
        }
        threads.Add(key, current);
        return current;
    }
}
