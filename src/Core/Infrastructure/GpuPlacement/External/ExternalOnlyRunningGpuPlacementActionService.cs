using System.Text.Json;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

public sealed class ExternalOnlyRunningGpuPlacementActionService(
    IRuntimePlanProvider runtimePlans,
    WindowsExternalGpuPlacementRuntime runtime,
    IGpuPlacementProcessHistoryStore processHistoryStore,
    InteractiveDesktopObservationStore? desktops = null,
    ILogger<ExternalOnlyRunningGpuPlacementActionService>? logger = null) : IRunningGpuPlacementActionService
{
    public bool HasUnreleasedExternalControl => runtime.HasUnreleasedControl;
    public Task RecoverExternalControlAsync(CancellationToken cancellationToken) => runtime.RecoverAsync(cancellationToken);

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
        if (!IsKnownBackground(external.Root))
            return Task.FromResult(new RunningGpuPlacementPreparation(null,
                "The renderer root is foreground or its interactive desktop state is unknown."));
        var api = ConfirmedGraphicsApi(external.Renderer);
        var plan = new RunningGpuPlacementActionPlan(request with { Processes = [external.GpuProcess] },
            [], new Dictionary<int, GpuGraphicsApi> { [external.GpuProcess.ProcessId] = api })
            { External = external };
        return CompletePreparationAsync(request, external, plan, cancellationToken);
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
        if (!IsKnownBackground(external.Root))
            return Task.FromResult(new RunningGpuPlacementActionResult([], "The renderer root is foreground or its interactive desktop state is unknown.",
                RunningGpuPlacementActionStatuses.Skipped));
        return runtime.ExecuteAsync(plan, execution, cancellationToken);
    }

    private bool IsKnownBackground(GpuPlacementProcessInstance process)
    {
        if (process.ProcessStartKey > long.MaxValue) return false;
        var identity = new ProcessInstanceKey(process.ProcessId, (long)process.ProcessStartKey);
        var snapshot = WindowsProcessRuntimeStateSnapshot.Capture([identity], desktops);
        return snapshot.Processes.TryGetValue(identity, out var fact) && !fact.ForegroundFocused;
    }

    private async Task<RunningGpuPlacementPreparation> CompletePreparationAsync(
        RunningGpuPlacementActionRequest request,
        ExternalGpuPlacementPlan external,
        RunningGpuPlacementActionPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecordConfirmedRendererApiAsync(processHistoryStore, request.SoftwareId,
                request.DisplayName, external, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(error,
                "Could not retain the confirmed graphics API for startup routing of {SoftwareId}; runtime placement remains independent.",
                request.SoftwareId);
        }
        return new(plan, "External renderer recreation is prepared; no process was modified.");
    }

    internal static GpuGraphicsApi ConfirmedGraphicsApi(ExternalGpuRenderer renderer) => renderer switch
    {
        ExternalGpuRenderer.ChromiumAngle => GpuGraphicsApi.D3D11,
        ExternalGpuRenderer.QtQuickD3D12 => GpuGraphicsApi.D3D12,
        ExternalGpuRenderer.QtQuickVulkan => GpuGraphicsApi.Vulkan,
        _ => throw new InvalidOperationException("Unknown confirmed renderer.")
    };

    internal static Task RecordConfirmedRendererApiAsync(
        IGpuPlacementProcessHistoryStore history,
        string softwareId,
        string softwareName,
        ExternalGpuPlacementPlan? external,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (external is null) return Task.CompletedTask;
        // Renderer admission is backed by live client/backend evidence. The history is keyed by the
        // launch image, so do not attribute a child renderer's API to a different parent executable.
        if (!HasSameLaunchExecutable(external.Root, external.GpuProcess)) return Task.CompletedTask;
        return history.SaveFirstGraphicsApiAsync(softwareId, softwareName, external.GpuProcess,
            ConfirmedGraphicsApi(external.Renderer), cancellationToken);
    }

    private static bool HasSameLaunchExecutable(
        GpuPlacementProcessInstance root,
        GpuPlacementProcessInstance gpuProcess)
        => root.ProcessId > 4 && root.ProcessStartKey > 0
            && gpuProcess.ProcessId > 4 && gpuProcess.ProcessStartKey > 0
            && !string.IsNullOrWhiteSpace(root.ProcessName)
            && StringComparer.OrdinalIgnoreCase.Equals(root.ProcessName, gpuProcess.ProcessName)
            && Path.IsPathFullyQualified(root.ExecutablePath)
            && Path.IsPathFullyQualified(gpuProcess.ExecutablePath)
            && StringComparer.OrdinalIgnoreCase.Equals(root.ExecutablePath, gpuProcess.ExecutablePath);
}
