namespace ResourceManager.App.Domain.GpuPlacement;

public sealed record GpuPlacementProcessInstance(
    int ProcessId,
    ulong ProcessStartKey,
    string ProcessName,
    string ExecutablePath);
