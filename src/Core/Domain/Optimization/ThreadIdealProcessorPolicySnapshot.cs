namespace ResourceManager.App.Domain.Optimization;

public readonly record struct ThreadIdealProcessor(ushort Group, byte Number);

public sealed record ThreadIdealProcessorPolicySnapshot(
    int ProcessId,
    ulong ProcessStartKey,
    int ThreadId,
    ulong ThreadStartKey,
    ThreadIdealProcessor IdealProcessor,
    ushort AllowedGroup,
    ulong AllowedMask,
    IReadOnlyList<uint> SelectedCpuSetIds);
