using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Application.Optimization;

public interface IProcessFreezeController
{
    ProcessFreezeTarget? TryReadTarget(int processId);

    ProcessFreezeWriteResult TryFreezeProcess(int processId);

    ProcessFreezeRestoreResult TryResumeThreads(IReadOnlyList<ProcessThreadSuspendRecord> threads);
}
