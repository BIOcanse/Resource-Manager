using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Infrastructure.Windows;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsRunningGpuPlacementActionService(
    ILogger<WindowsRunningGpuPlacementActionService> logger,
    D3d11ProxyShimRuntime d3d11ProxyShimRuntime,
    WindowsGpuPlacementInjector gpuPlacementInjector,
    IGpuPlacementProcessHistoryStore processHistoryStore,
    IRuntimePlanProvider runtimePlans,
    WindowsGpuCallbackPreparationRuntime callbackPreparationRuntime) : IRunningGpuPlacementActionService
{
    public async Task<RunningGpuPlacementPreparation> PrepareAsync(
        RunningGpuPlacementActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TargetId) || string.IsNullOrWhiteSpace(request.SoftwareId) || request.TargetAdapterKey == 0)
            return new(null, "目标缺少软件身份或精确 GPU 位置。");
        var distinctProcesses = request.Processes.Distinct().ToArray();
        if (distinctProcesses.Select(static process => process.ProcessId).Distinct().Count() != distinctProcesses.Length)
            return new(null, "同一 PID 对应冲突的进程身份，未执行动作。");
        request = request with { Processes = distinctProcesses };
        var processes = distinctProcesses.ToDictionary(static process => process.ProcessId);
        var processIds = processes.Keys
            .Where(static processId => processId > 4)
            .Distinct()
            .ToHashSet();
        if (processIds.Count == 0)
        {
            return new(null, "目标没有可执行的普通用户态进程。");
        }

        var plan = runtimePlans.Current.GpuPlacement;
        if (!plan.GlobalPreciseProviderEnabled) return new(null, "GPU shim 已关闭。");
        var inputs = processIds.Select(processId => processes[processId])
            .Select(static process => new GpuPlacementObservedProcessInput(
                process.ProcessName, process.ExecutablePath, null, process.ProcessId, []))
            .Where(input => plan.Resolve(request.SoftwareId, request.DisplayName, null,
                JsonGpuPlacementProcessHistoryStore.BuildProcessKey(input.ProcessName, input.ExecutablePath))
                .AcceptsRuntimeGpuScheduling()).ToArray();
        if (inputs.Length == 0) return new(null, "目标未允许运行期 GPU shim。");
        var history = await processHistoryStore.GetSoftwareHistoryAsync(
            request.SoftwareId, request.DisplayName, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        plan = runtimePlans.Current.GpuPlacement;
        if (!plan.GlobalPreciseProviderEnabled) return new(null, "GPU shim 已关闭。");
        inputs = inputs.Where(input => plan.Resolve(request.SoftwareId, request.DisplayName, null,
            JsonGpuPlacementProcessHistoryStore.BuildProcessKey(input.ProcessName, input.ExecutablePath))
            .AcceptsRuntimeGpuScheduling()).ToArray();
        if (inputs.Length == 0) return new(null, "目标未允许运行期 GPU shim。");
        var graphicsApis = SelectRuntimeGraphicsApis(inputs, history);
        var unidentified = inputs.Where(input => !history.Processes.Any(row =>
                StringComparer.OrdinalIgnoreCase.Equals(row.ProcessKey,
                    JsonGpuPlacementProcessHistoryStore.BuildProcessKey(input.ProcessName, input.ExecutablePath))
                && GpuGraphicsApiRoutes.IsIdentified(row.GraphicsApi)))
            .Select(input => processes[input.ProcessId!.Value])
            .Where(static process => process.ProcessStartKey != 0
                && !string.IsNullOrWhiteSpace(process.ExecutablePath)
                && Path.IsPathFullyQualified(process.ExecutablePath)
                && Path.GetPathRoot(process.ExecutablePath) != process.ExecutablePath).ToArray();
        if (!d3d11ProxyShimRuntime.RuntimeProviderAvailable) return new(null, "GPU runtime provider unavailable。");
        if (!callbackPreparationRuntime.Available)
            foreach (var processId in graphicsApis.Where(static pair => pair.Value == GpuGraphicsApi.OpenGL)
                         .Select(static pair => pair.Key).ToArray())
                graphicsApis.Remove(processId);
        processIds = graphicsApis.Keys.ToHashSet();
        if (processIds.Count == 0) return new(null, "软件已记录的图形 API 没有适用的运行期移动路径。")
            { ApiObservationProcesses = unidentified };
        cancellationToken.ThrowIfCancellationRequested();
        return new(new(request with { Processes = processIds.Select(id => processes[id]).ToArray() },
            D3d11ProxyShimRuntime.CreateExactPolicyValue(request.TargetAdapterKey), graphicsApis), "已生成运行期动作计划，未发布策略或操作进程。")
            { ApiObservationProcesses = unidentified };
    }

    public async Task<RunningGpuPlacementActionResult> TryApplyAsync(
        RunningGpuPlacementActionPlan plan, RunningGpuPlacementExecution windows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(windows.ExecuteAsync);
        ArgumentNullException.ThrowIfNull(windows.ExecuteRemoteCallAsync);
        ArgumentNullException.ThrowIfNull(windows.PrepareOpenGlAsync);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windows.MaximumWindowCount);
        cancellationToken.ThrowIfCancellationRequested();
        var request = plan.Request;
        var policyPath = d3d11ProxyShimRuntime.GetPolicyPath(request.TargetId);
        if (!plan.PolicyValue.AsSpan().SequenceEqual(D3d11ProxyShimRuntime.CreateExactPolicyValue(request.TargetAdapterKey))
            || d3d11ProxyShimRuntime.ReadPolicy(request.TargetId) is not { } published
            || !published.AsSpan().SequenceEqual(plan.PolicyValue))
            return new([], "位置 owner 尚未发布计划中的策略，未操作进程。", RunningGpuPlacementActionStatuses.Skipped);

        var foregroundBefore = NativeMethods.GetForegroundWindow();
        var foregroundProcessId = TryGetWindowProcessId(foregroundBefore);
        if (foregroundProcessId is not null && request.Processes.Any(process => process.ProcessId == foregroundProcessId))
            return new([], "目标软件当前处于前台聚焦状态，GPU 运行时触发被禁止。", RunningGpuPlacementActionStatuses.Skipped);

        var ownedProcesses = new List<WindowsGpuPlacementInjector.ProviderProcess>();
        var callsStopped = false;
        RunningGpuPlacementProcessResult? preparationFailure = null;
        try
        {
            foreach (var process in request.Processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var api = plan.GraphicsApis[process.ProcessId];
                if (api == GpuGraphicsApi.OpenGL)
                {
                    var callbacks = await windows.PrepareOpenGlAsync(process, request.TargetAdapterKey, cancellationToken).ConfigureAwait(false);
                    if (callbacks is null)
                    {
                        preparationFailure = new(process, false, "opengl-preparation-not-completed", null, null, null);
                        callsStopped = true;
                        break;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    ownedProcesses.Add(await gpuPlacementInjector.OpenAndConfigureOpenGlAsync(
                        process, policyPath, callbacks, windows.ExecuteRemoteCallAsync, cancellationToken).ConfigureAwait(false));
                }
                else
                    ownedProcesses.Add(await gpuPlacementInjector.OpenAndConfigureAsync(
                        process, api, policyPath, windows.ExecuteRemoteCallAsync, cancellationToken).ConfigureAwait(false));
                if (ownedProcesses[^1].CallsStopped) { callsStopped = true; break; }
            }
            var configured = ownedProcesses.Where(static process => process.Result.Success)
                .ToDictionary(static process => process.Identity.ProcessId);
            var before = new Dictionary<int, GpuDeviceObservationReadResult>();
            foreach (var pair in configured)
            {
                if (callsStopped || cancellationToken.IsCancellationRequested) break;
                before.Add(pair.Key, await pair.Value.ReadDeviceObservationsAsync(cancellationToken).ConfigureAwait(false));
                if (pair.Value.CallsStopped) { callsStopped = true; break; }
            }
            var method = GpuPlacementRuntimeSwitchMethods.Normalize(request.PreferredRuntimeSwitchMethod)
                == GpuPlacementRuntimeSwitchMethods.WindowRerender ? GpuWindowActionMethod.Resize : GpuWindowActionMethod.Redraw;
            var available = configured.Where(pair => before.TryGetValue(pair.Key, out var read) && read.Success
                && !pair.Value.CallsStopped).ToDictionary();
            var batch = callsStopped ? new WindowBatchResult([], true, false)
                : await ExecuteWindowRequestsAsync(
                    EnumerateWindows(available, method, windows.MaximumWindowCount, cancellationToken), windows, cancellationToken);
            if (batch.Stopped) logger.LogDebug("Stopped the current GPU window plan for {Target} without fallback.", request.TargetId);
            var after = new Dictionary<int, GpuDeviceObservationReadResult>();
            if (!batch.Stopped)
            {
                foreach (var pair in available)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    after.Add(pair.Key, await pair.Value.ReadDeviceObservationsAsync(cancellationToken).ConfigureAwait(false));
                    if (pair.Value.CallsStopped) { callsStopped = true; break; }
                }
            }
            var processResults = ownedProcesses.Select(process => new RunningGpuPlacementProcessResult(
                process.Identity, process.Result.Success, process.Result.Status, process.Result.Win32Error,
                before.GetValueOrDefault(process.Identity.ProcessId), after.GetValueOrDefault(process.Identity.ProcessId))).ToArray();
            if (preparationFailure is not null) processResults = [.. processResults, preparationFailure];
            var foregroundChanged = foregroundBefore != NativeMethods.GetForegroundWindow();
            var provider = CreateProviderMetadata(ownedProcesses.Select(static process => process.Result).ToArray());
            var records = batch.Results.Where(static result => result.Record is not null)
                .Select(result => result.Record! with { Metadata = MergeFinalMetadata(result.Record!.Metadata, provider, request, foregroundChanged) }).ToArray();
            var status = callsStopped
                ? RunningGpuPlacementActionStatuses.Unresolved
                : batch.Results.Any(static result => result.Outcome == GpuWindowActionOutcome.RestorationUnconfirmed)
                ? RunningGpuPlacementActionStatuses.RestorationFailed
                : batch.Results.Any(static result => result.Outcome == GpuWindowActionOutcome.Unresolved)
                    ? RunningGpuPlacementActionStatuses.Unresolved
                    : batch.Results.Any(static result => result.Outcome is GpuWindowActionOutcome.WindowRestored or GpuWindowActionOutcome.RedrawRequested)
                        ? RunningGpuPlacementActionStatuses.RecreateRequested
                        : configured.Count != 0 ? RunningGpuPlacementActionStatuses.Prepared : RunningGpuPlacementActionStatuses.NotApplied;
            return new(records, callsStopped ? "远程调用未完成；保留原动作结果，不继续本次计划。"
                : batch.Stopped ? "窗口执行已停止；保留实际结果，不继续同计划的窗口操作。"
                : batch.LimitReached ? "已达到配置的单计划窗口上限；未操作其余窗口。"
                : "配置、窗口请求和设备事实已分别保留；不代表设备或工作负载已经迁移。", status) { Processes = processResults };
        }
        finally
        {
            foreach (var process in ownedProcesses) process.Dispose();
        }
    }

    internal static Dictionary<int, GpuGraphicsApi> SelectRuntimeGraphicsApis(
        IReadOnlyList<GpuPlacementObservedProcessInput> inputs, GpuPlacementSoftwareProcessHistory history)
    {
        var byExecutable = history.Processes.ToDictionary(static process => process.ProcessKey, StringComparer.OrdinalIgnoreCase);
        return inputs.Where(input => input.ProcessId is > 4
                && byExecutable.TryGetValue(JsonGpuPlacementProcessHistoryStore.BuildProcessKey(input.ProcessName, input.ExecutablePath), out var known)
                && GpuGraphicsApiRoutes.RuntimeProvider(known.GraphicsApi) is not null)
            .ToDictionary(static input => input.ProcessId!.Value, input => byExecutable[
                JsonGpuPlacementProcessHistoryStore.BuildProcessKey(input.ProcessName, input.ExecutablePath)].GraphicsApi!.Value);
    }

    internal static async Task<WindowBatchResult> ExecuteWindowRequestsAsync(
        IReadOnlyList<GpuWindowActionRequest> requests, RunningGpuPlacementExecution execution, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(execution.MaximumWindowCount);
        ArgumentNullException.ThrowIfNull(execution.ExecuteAsync);
        var results = new List<RunningGpuPlacementWindowResult>();
        for (var index = 0; index < requests.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested) return new(results, true, false);
            if (index >= execution.MaximumWindowCount) return new(results, false, true);
            var result = await execution.ExecuteAsync(requests[index]).ConfigureAwait(false);
            results.Add(result);
            if (!result.CanContinue) return new(results, true, false);
        }
        return new(results, cancellationToken.IsCancellationRequested, false);
    }

    internal sealed record WindowBatchResult(IReadOnlyList<RunningGpuPlacementWindowResult> Results, bool Stopped, bool LimitReached);

    private static IReadOnlyList<GpuWindowActionRequest> EnumerateWindows(
        IReadOnlyDictionary<int, WindowsGpuPlacementInjector.ProviderProcess> processes,
        GpuWindowActionMethod method, int maximumWindows, CancellationToken cancellationToken)
    {
        var windows = new List<GpuWindowActionRequest>();
        NativeMethods.EnumWindows((window, _) =>
        {
            if (cancellationToken.IsCancellationRequested || windows.Count > maximumWindows) return false;
            var processId = TryGetWindowProcessId(window);
            if (processId is not null && processes.TryGetValue(processId.Value, out var owner)
                && owner.OwnsWindow(window) && NativeMethods.IsWindowVisible(window) && !NativeMethods.IsIconic(window))
                windows.Add(new(processId.Value, checked((long)owner.Identity.ProcessStartKey),
                    checked((ulong)window.ToInt64()), method));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static int? TryGetWindowProcessId(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId == 0 ? null : checked((int)processId);
    }

    private static IReadOnlyDictionary<string, string> MergeFinalMetadata(
        IReadOnlyDictionary<string, string> recordMetadata, IReadOnlyDictionary<string, string> provider,
        RunningGpuPlacementActionRequest request, bool foregroundChanged)
    {
        var metadata = new Dictionary<string, string>(recordMetadata, StringComparer.Ordinal);
        foreach (var item in provider) metadata[item.Key] = item.Value;
        metadata["targetId"] = request.TargetId;
        metadata["softwareId"] = request.SoftwareId;
        metadata["assignedPositionId"] = request.AssignedPositionId;
        metadata["foregroundChangedDuringTrigger"] = foregroundChanged ? "true" : "false";
        return metadata;
    }

    private static IReadOnlyDictionary<string, string> CreateProviderMetadata(IReadOnlyList<RuntimeGpuProviderInjectionResult> results)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runtimeProviderAttemptedProcessIds"] = string.Join(',', results.Select(static result => result.ProcessId)),
            ["runtimeProviderReadyProcessIds"] = string.Join(',', results.Where(static result => result.Success).Select(static result => result.ProcessId)),
            ["runtimeProviderStatuses"] = string.Join(';', results.Select(static result => $"{result.ProcessId}:{result.Status}:0x{result.ProviderStatus:x}"))
        };
}
