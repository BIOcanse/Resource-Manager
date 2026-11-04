namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerComputeProcessIdentity(
    int ProcessId,
    ulong ProcessStartKey);

