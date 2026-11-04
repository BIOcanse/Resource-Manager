using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed record CpuThreadCounterDelta(DateTimeOffset ReadFrom, DateTimeOffset ReadThrough,
    long KernelTime100Ns, long UserTime100Ns);
public enum CpuThreadStartAddressSource { Win32StartAddress, SystemProcessInformation }
public sealed record CpuThreadDiagnosticFacts(long? CreationFileTime100Ns, string? Description,
    int? DescriptionHResult, ulong? StartAddress, CpuThreadStartAddressSource? StartAddressSource, string? StartModulePath,
    CpuThreadCounterDelta? Counters, int? NativeError);
public sealed record CpuProcessThreadWindow(DateTimeOffset MeasuredFrom, DateTimeOffset MeasuredThrough,
    long SessionGeneration, CpuProcessCoreResidency Process);

