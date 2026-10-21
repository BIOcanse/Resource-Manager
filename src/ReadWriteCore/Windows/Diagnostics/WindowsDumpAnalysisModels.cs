using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Diagnostics;

public enum WindowsDumpAnalysisStatus { NotAnalyzed, Available, Partial, MissingDependency, PermissionDenied, Unreadable, Unsupported, TimedOut, InvalidEngineOutput }
public enum WindowsDumpFieldStatus { Unavailable, Available, Unsupported, SymbolsUnavailable }

/// <summary>All paths are supplied explicitly. Symbols are local existing directories only.
/// Installation, download, product path authorization and caller SID checks belong to the host.</summary>
public sealed record WindowsDumpAnalysisRequest(string DumpPath, string HelperExecutablePath,
    string EngineLibraryPath, ImmutableArray<string> LocalSymbolDirectories,
    TimeSpan Timeout, int MaximumFrames = 32, int MaximumOutputBytes = 131072);

public sealed record WindowsDumpStackFrame(ulong InstructionOffset, ulong ReturnOffset,
    ulong FrameOffset, ulong StackOffset, string? Symbol, ulong SymbolDisplacement, string? Module);

public sealed record WindowsDumpExceptionContext(uint ProcessorType, uint ProcessId, uint ThreadId,
    uint ExceptionCode, uint ExceptionFlags, ulong ExceptionAddress, bool FirstChance,
    ImmutableArray<ulong> ExceptionParameters, ImmutableArray<byte>? Amd64ThreadContext,
    WindowsDumpFieldStatus ThreadContextStatus);

/// <summary>Single owner current. Null never implies a parsed or accepted value.</summary>
public sealed record WindowsDumpAnalysisResult(WindowsDumpAnalysisStatus Status, string? Diagnostic,
    uint? BugcheckCodeReported, ImmutableArray<ulong>? BugcheckParametersReported,
    WindowsDumpExceptionContext? ExceptionContextInterpreted,
    ImmutableArray<WindowsDumpStackFrame>? SymbolizedStackInterpreted,
    string? SuspectedModuleInterpreted, WindowsDumpFieldStatus BugcheckStatus,
    WindowsDumpFieldStatus ExceptionStatus, WindowsDumpFieldStatus StackStatus,
    DeviceInformationSnapshot Information);
