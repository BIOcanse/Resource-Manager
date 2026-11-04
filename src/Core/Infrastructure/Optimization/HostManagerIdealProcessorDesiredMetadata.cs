using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerIdealProcessorDesiredMetadata(
    int ProcessId,
    ulong ProcessStartKey,
    long ProcessStartedAtUnixMilliseconds,
    string ProcessName,
    string ExecutablePath,
    int ThreadId,
    ulong ThreadStartKey,
    ThreadIdealProcessor Previous,
    ThreadIdealProcessor Applied);
