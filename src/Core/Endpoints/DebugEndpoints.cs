using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.App.Application.Diagnostics;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapDebugEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/debug/kernel-leaks", (HttpResponse response, Application.KernelLeak.IKernelLeakReportSource reports) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(new Domain.KernelLeak.KernelLeakDebugSnapshot(reports.Read(), reports.ReadDiagnostics()));
        });
        app.MapGet("/api/debug/business-blocks", (HttpResponse response, IBusinessBlockQuery blocks, BusinessBlockDiagnosticsRegistry diagnostics) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(new BusinessBlockDebugSnapshot(blocks.Read(), diagnostics.ReadAll()));
        });

        app.MapGet("/api/debug/gpu-renderers", async (HttpResponse response,
            IMetricSnapshotPushSource metrics, IResourceBreakdownPushSource resources,
            ISchedulingProcessFactObservationSource processes, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var hardware = await metrics.FetchOnceAsync("debug:gpu-renderers:hardware", MetricSampleRequest.All, cancellationToken);
            var keys = hardware?.GpuInventory.Adapters.Select(adapter => adapter.AdapterKey).ToArray() ?? [];
            var mask = SchedulingProcessMetricMask.RuntimeState | SchedulingProcessMetricMask.GpuUsage
                | SchedulingProcessMetricMask.GpuDedicatedMemory;
            var processSnapshot = await resources.FetchOnceAsync("debug:gpu-renderers:processes", new([], new Dictionary<string, string>(),
                ProcessSampleDetailLevel.SmartSchedulingLite, mask), cancellationToken);
            var facts = hardware is null || processSnapshot is null ? null
                : processes.ReadLatest(new SchedulingProcessFactRequest(mask, hardware.GpuInventory));
            var rows = facts?.Processes.Where(process => process.Gpus.Any(gpu =>
                gpu.HasMetric(SchedulingProcessMetricMask.GpuUsage)
                    || gpu.HasMetric(SchedulingProcessMetricMask.GpuDedicatedMemory))).Select(process => new GpuRendererProcessPayload(process.ProcessId, process.ProcessStartKey, process.ProcessName, WindowsGpuRendererConfirmation.Read(new(process.ProcessId, process.ProcessStartKey, process.ProcessName, process.ExecutablePath ?? string.Empty), keys))).ToArray();
            return Results.Ok(new GpuRendererConfirmationPayload("graphics-kernel-client", rows));
        });

        // 订阅 N 秒（默认 5，允许 1–30）后返回各进程的帧统计，用来在真实程序上核对帧率。
        app.MapGet("/api/debug/frame-timing", async (HttpResponse response, IFrameTimingObservationSource frames,
            int? seconds, CancellationToken cancellation) =>
        {
            response.Headers.CacheControl = "no-store";
            if (seconds is < 1 or > 30)
            {
                return Results.BadRequest(new EndpointErrorPayload("seconds must be between 1 and 30."));
            }

            var window = TimeSpan.FromSeconds(seconds ?? 5);
            using var lease = frames.AcquireSubscription();
            await Task.Delay(window, cancellation);
            var snapshot = frames.Read(window);
            return snapshot is null ? Results.Ok(new RunningStatePayload(false)) : Results.Ok(new FrameTimingStatePayload(true, snapshot));
        });

        app.MapGet("/api/debug/thread-cpu", async (HttpResponse response, EtwCpuCoreResidencyReader reader,
            int processId, long processStartKey, CancellationToken cancellation) =>
        {
            response.Headers.CacheControl = "no-store";
            if (processId <= 0 || processStartKey <= 0) return Results.BadRequest();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            budget.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                return Results.Ok(await reader.CaptureThreadWindowAsync("debug.thread-cpu", processId, processStartKey, budget.Token));
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
        });
        app.MapGet("/api/debug/cpu-residency", (HttpResponse response, EtwCpuCoreResidencyReader reader) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(reader.GetDiagnostics());
        });

        app.MapGet("/api/debug/scheduling-inputs", async (HttpResponse response,
            IMetricSnapshotPushSource metrics, IResourceBreakdownPushSource resources,
            ISchedulingProcessFactObservationSource processes, WindowsGpuPlacementInjector gpuInjector,
            CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var hardware = await metrics.FetchOnceAsync("debug:scheduling-inputs:hardware", MetricSampleRequest.All, cancellationToken);
            var mask = SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.GpuUsage
                | SchedulingProcessMetricMask.GpuDedicatedMemory | SchedulingProcessMetricMask.RuntimeState;
            var processSnapshot = await resources.FetchOnceAsync("debug:scheduling-inputs:processes", new([], new Dictionary<string, string>(),
                ProcessSampleDetailLevel.SmartSchedulingLite, mask), cancellationToken);
            var facts = hardware is null || processSnapshot is null ? null
                : processes.ReadLatest(new SchedulingProcessFactRequest(mask, hardware.GpuInventory));
            var failures = facts?.Processes.Select(process => new GpuPlacementFailurePayload(process.ProcessId, process.ProcessStartKey, gpuInjector.GetKnownProcessFailure(new GpuPlacementProcessInstance(process.ProcessId, process.ProcessStartKey, process.ProcessName, process.ExecutablePath ?? string.Empty)))).Where(process => process.failure is not null).ToArray();
            return Results.Ok(new DebugSchedulingPayload(hardware?.GpuInventory, facts, failures));
        });

        app.MapGet("/api/debug/freedom-points", (
            HttpResponse response,
            IRuntimePlanProvider runtimePlanProvider) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(runtimePlanProvider.Current.HostManager.FreedomPoints);
        });

        app.MapGet("/api/debug/logs", (
            int? tail,
            HttpContext context,
            IDebugDiagnosticLogReader logReader) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new DiagnosticLinesPayload(true, logReader.ReadTail(tail ?? 100)));
        });

        app.MapGet("/api/debug/history/requirements", (
            HttpResponse response,
            IRuntimePlanProvider runtimePlanProvider) =>
        {
            response.Headers.CacheControl = "no-store";
            var plan = runtimePlanProvider.Current.HostManager;
            return Results.Ok(new HistoryRequirementsPayload(plan.DataHistory.Requirements, new[] { new ModuleHistoryRequirementsPayload(plan.SmartCoordinator.Build.NativeModule, plan.SmartCoordinator.DataHistory.Requirements) }));
        });

        app.MapGet("/api/debug/metrics/capture", async (
            HttpRequest request,
            HttpResponse response,
            IMetricSnapshotPushSource metrics,
            CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var sampleRequest = request.Query.TryGetValue("ids", out var ids)
                ? MetricSampleRequest.ForIds(ids)
                : MetricSampleRequest.All;
            var snapshot = await metrics.FetchOnceAsync(
                "debug:metrics-capture", sampleRequest,
                cancellationToken);
            return Results.Ok(snapshot);
        });

        return app;
    }
}
