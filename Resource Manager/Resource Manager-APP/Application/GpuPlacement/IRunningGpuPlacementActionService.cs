using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

public interface IRunningGpuPlacementActionService
{
    bool HasUnreleasedExternalControl => false;
    Task<RunningGpuPlacementPreparation> PrepareAsync(
        RunningGpuPlacementActionRequest request, CancellationToken cancellationToken);

    Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(
        RunningGpuPlacementActionRequest request,
        Func<GpuPlacementProcessInstance, CancellationToken, Task<GpuGraphicsApi?>> observeAsync,
        CancellationToken cancellationToken);

    Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(
        RunningGpuPlacementActionRequest request, RunningGpuApiObservationExecution execution,
        CancellationToken cancellationToken);

    Task<RunningGpuPlacementActionResult> TryApplyAsync(
        RunningGpuPlacementActionPlan plan, RunningGpuPlacementExecution execution, CancellationToken cancellationToken);
}

public sealed record RunningGpuPlacementExecution(
    int MaximumWindowCount,
    Func<GpuWindowActionRequest, Task<RunningGpuPlacementWindowResult>> ExecuteAsync,
    Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> ExecuteRemoteCallAsync,
    Func<GpuPlacementProcessInstance, ulong, CancellationToken, Task<PreparedOpenGlCallbacks?>> PrepareOpenGlAsync)
{
    public ulong RecreationDeadlineMilliseconds { get; init; }
    public ulong CleanupDeadlineMilliseconds { get; init; }
}

public sealed record RunningGpuApiObservationExecution(
    int DurationMilliseconds,
    Func<GpuRemoteCallExecution, CancellationToken, Task<GpuRemoteCallSnapshot>> ExecuteRemoteCallAsync,
    Func<GpuPlacementProcessInstance, TimeSpan, CancellationToken, Task> WaitAsync,
    CancellationToken CleanupCancellationToken);

public sealed record RunningGpuPlacementWindowResult(
    GpuWindowActionOutcome Outcome, RunningGpuPlacementActionRecord? Record, bool ContinueAllowed)
{
    public bool CanContinue => ContinueAllowed && Outcome is GpuWindowActionOutcome.NotExecuted
        or GpuWindowActionOutcome.RedrawRequested or GpuWindowActionOutcome.WindowRestored;
}

public sealed record RunningGpuPlacementActionPlan(
    RunningGpuPlacementActionRequest Request,
    byte[] PolicyValue,
    IReadOnlyDictionary<int, GpuGraphicsApi> GraphicsApis)
{
    public ExternalGpuPlacementPlan? External { get; init; }
}

public sealed record ExternalGpuPlacementPlan(
    GpuPlacementProcessInstance Root, GpuPlacementProcessInstance GpuProcess)
{
    public ExternalGpuRenderer Renderer { get; init; } = ExternalGpuRenderer.ChromiumAngle;
}

public enum ExternalGpuRenderer { ChromiumAngle, QtQuickD3D12, QtQuickVulkan }

public sealed record RunningGpuPlacementPreparation(
    RunningGpuPlacementActionPlan? Plan,
    string Message)
{
    internal IReadOnlyList<GpuPlacementProcessInstance> ApiObservationProcesses { get; init; } = [];
}

public sealed record RunningGpuPlacementProcessResult(
    GpuPlacementProcessInstance Identity,
    bool Configured,
    string ConfigurationStatus,
    int? ConfigurationError,
    GpuDeviceObservationReadResult? Before,
    GpuDeviceObservationReadResult? After)
{
    public GpuRecreationResult? Recreation { get; init; }
}

public sealed record RunningGpuPlacementActionRequest(
    string TargetId,
    string SoftwareId,
    string DisplayName,
    IReadOnlyList<GpuPlacementProcessInstance> Processes,
    string RuntimeState,
    ulong TargetAdapterKey,
    string PreferredRuntimeSwitchMethod)
{
    public string? SoftwareKind { get; init; }
    public string AssignedPositionId => $"gpu-luid:{TargetAdapterKey:x16}";
}

public sealed record RunningGpuPlacementActionResult(
    IReadOnlyList<RunningGpuPlacementActionRecord> Records,
    string Message,
    string Status)
{
    public IReadOnlyList<RunningGpuPlacementProcessResult> Processes { get; init; } = [];

    public bool Triggered => Records.Count > 0
        && Status.Equals(RunningGpuPlacementActionStatuses.RecreateRequested, StringComparison.OrdinalIgnoreCase);

    public bool Applied => Records.Count > 0
        && Status.Equals(RunningGpuPlacementActionStatuses.Applied, StringComparison.OrdinalIgnoreCase);
}

public static class RunningGpuPlacementActionStatuses
{
    public const string Unresolved = "execution-unresolved";
    public const string RestorationFailed = "window-restoration-failed";
    public const string Skipped = "skipped";
    public const string Prepared = "prepared";
    public const string RecreateRequested = "recreate-requested";
    public const string Applied = "applied-verified";
    public const string NotApplied = "not-applied";
}

public sealed record RunningGpuPlacementActionRecord(
    string RecordId,
    string Method,
    IReadOnlyDictionary<string, string> Metadata);
