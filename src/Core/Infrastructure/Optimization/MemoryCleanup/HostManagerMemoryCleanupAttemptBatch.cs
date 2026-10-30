namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerMemoryCleanupAttemptBatch(
    Guid BatchId,
    IReadOnlySet<HostManagerComputeProcessIdentity> Processes,
    ulong AttemptGeneration = 0);
