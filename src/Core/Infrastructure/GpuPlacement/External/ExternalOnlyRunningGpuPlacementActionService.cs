using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

public sealed class ExternalOnlyRunningGpuPlacementActionService(
    IRuntimePlanProvider runtimePlans,
    WindowsExternalGpuPlacementRuntime runtime) : IRunningGpuPlacementActionService
{
    public bool HasUnreleasedExternalControl => runtime.HasUnreleasedControl;

    public Task<RunningGpuPlacementPreparation> PrepareAsync(
        RunningGpuPlacementActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.TargetId) || string.IsNullOrWhiteSpace(request.SoftwareId)
            || request.TargetAdapterKey == 0)
            return Task.FromResult(new RunningGpuPlacementPreparation(null, "Missing exact software or target adapter identity."));
        var processes = request.Processes.Distinct().ToArray();
        if (processes.Select(static process => process.ProcessId).Distinct().Count() != processes.Length)
            return Task.FromResult(new RunningGpuPlacementPreparation(null, "Conflicting process identities for one PID."));
        var policy = runtimePlans.Current.GpuPlacement;
        if (!policy.GlobalPreciseProviderEnabled)
            return Task.FromResult(new RunningGpuPlacementPreparation(null, "Runtime GPU scheduling is disabled."));
        var allowed = processes.Where(process => policy.Resolve(request.SoftwareId, request.DisplayName, request.SoftwareKind,
            JsonGpuPlacementProcessHistoryStore.BuildProcessKey(process.ProcessName, process.ExecutablePath))
            .AcceptsExternalRuntimeGpuScheduling()).ToArray();
        var external = runtime.Prepare(allowed);
        if (external is null)
            return Task.FromResult(new RunningGpuPlacementPreparation(null,
                "No confirmed external renderer route is available for this process."));
        if (IsForeground(external.Root.ProcessId))
            return Task.FromResult(new RunningGpuPlacementPreparation(null,
                "The renderer root is in the foreground."));
        var api = external.Renderer switch
        {
            ExternalGpuRenderer.ChromiumAngle => GpuGraphicsApi.D3D11,
            ExternalGpuRenderer.QtQuickD3D12 => GpuGraphicsApi.D3D12,
            ExternalGpuRenderer.QtQuickVulkan => GpuGraphicsApi.Vulkan,
            _ => throw new InvalidOperationException("Unknown confirmed renderer.")
        };
        var plan = new RunningGpuPlacementActionPlan(request with { Processes = [external.GpuProcess] },
            [], new Dictionary<int, GpuGraphicsApi> { [external.GpuProcess.ProcessId] = api })
            { External = external };
        return Task.FromResult(new RunningGpuPlacementPreparation(plan,
            "External renderer recreation is prepared; no process was modified."));
    }

    public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(
        RunningGpuPlacementActionRequest request,
        Func<GpuPlacementProcessInstance, CancellationToken, Task<GpuGraphicsApi?>> observeAsync,
        CancellationToken cancellationToken)
        => PrepareAsync(request, cancellationToken);

    public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(
        RunningGpuPlacementActionRequest request, RunningGpuApiObservationExecution execution,
        CancellationToken cancellationToken)
        => PrepareAsync(request, cancellationToken);

    public Task<RunningGpuPlacementActionResult> TryApplyAsync(
        RunningGpuPlacementActionPlan plan, RunningGpuPlacementExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.External is not { } external || plan.PolicyValue.Length != 0
            || plan.Request.Processes.Count != 1 || plan.Request.Processes[0] != external.GpuProcess)
            return Task.FromResult(new RunningGpuPlacementActionResult([], "No external-only action plan was supplied.",
                RunningGpuPlacementActionStatuses.Skipped));
        var policy = runtimePlans.Current.GpuPlacement;
        if (!policy.GlobalPreciseProviderEnabled || !policy.Resolve(plan.Request.SoftwareId, plan.Request.DisplayName,
            plan.Request.SoftwareKind, JsonGpuPlacementProcessHistoryStore.BuildProcessKey(external.GpuProcess.ProcessName,
                external.GpuProcess.ExecutablePath)).AcceptsExternalRuntimeGpuScheduling())
            return Task.FromResult(new RunningGpuPlacementActionResult([], "Runtime GPU scheduling is now disabled.",
                RunningGpuPlacementActionStatuses.Skipped));
        if (IsForeground(external.Root.ProcessId))
            return Task.FromResult(new RunningGpuPlacementActionResult([], "The renderer root is in the foreground.",
                RunningGpuPlacementActionStatuses.Skipped));
        return runtime.ExecuteAsync(plan, execution, cancellationToken);
    }

    private static bool IsForeground(int processId)
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(window, out var foreground);
        return foreground == checked((uint)processId);
    }
}
