using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace ResourceManager.ReadWrite.Windows.Processes;

[Flags]
public enum WindowsProcessDetailsFields : byte
{
    None = 0,
    ImageSignature = 1,
    LoadedModules = 2,
    ThreadState = 4,
    All = ImageSignature | LoadedModules | ThreadState
}

public sealed record WindowsProcessLoadedModule(ulong BaseAddress, string Path)
{
    public uint? ImageSizeBytes { get; init; }
}
public sealed record WindowsProcessThreadState(uint ThreadId, uint State, uint WaitReason);

/// <summary>One immutable process inventory observation keyed by the existing PID/creation-FILETIME identity.</summary>
public sealed record WindowsProcessDetailsInventory(
    bool Succeeded,
    ImmutableArray<ProcessInstanceKey> ProcessIdentities,
    DateTimeOffset CapturedAt);

/// <summary>Raw, category-specific process detail results. Codes are preserved without interpretation.</summary>
public sealed record WindowsProcessDetailsReadResult(
    bool SignatureSucceeded,
    uint? SignatureStatus,
    bool ModulesSucceeded,
    ImmutableArray<WindowsProcessLoadedModule> Modules,
    bool ThreadsSucceeded,
    ImmutableArray<WindowsProcessThreadState> Threads)
{
    public static WindowsProcessDetailsReadResult Unavailable { get; } = new(false, null, false, [], false, []);
}

/// <summary>
/// Explicit source boundary for process identity inventory and typed detail rows.
/// Supplying a source to ConfigureProcessDetails routes both reads through it without native fallback.
/// </summary>
public interface IWindowsProcessDetailsCaptureSource
{
    WindowsProcessDetailsInventory ReadInventory(CancellationToken cancellationToken);

    IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> ReadDetails(
        IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selection,
        CancellationToken cancellationToken);
}
