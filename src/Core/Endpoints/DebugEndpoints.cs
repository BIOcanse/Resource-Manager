using ResourceManager.App.Application.Diagnostics;
using ResourceManager.App.Application.FrameTiming;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapDebugEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/debug/gpu-renderers", (HttpResponse response,
            IMetricSnapshotObservationSource metrics, ISchedulingProcessFactObservationSource processes) =>
        {
            response.Headers.CacheControl = "no-store";
            var hardware = metrics.ReadLatest(MetricSampleRequest.All);
            var keys = hardware?.GpuInventory.Adapters.Select(adapter => adapter.AdapterKey).ToArray() ?? [];
            var facts = hardware is null ? null : processes.ReadLatest(new SchedulingProcessFactRequest(
                SchedulingProcessMetricMask.RuntimeState | SchedulingProcessMetricMask.GpuUsage
                    | SchedulingProcessMetricMask.GpuDedicatedMemory, null, hardware.GpuInventory));
            var rows = facts?.Processes.Where(process => process.Gpus.Any(gpu =>
                gpu.HasMetric(SchedulingProcessMetricMask.GpuUsage)
                    || gpu.HasMetric(SchedulingProcessMetricMask.GpuDedicatedMemory))).Select(process => new
            {
                process.ProcessId, process.ProcessStartKey, process.ProcessName,
                adapters = WindowsGpuRendererConfirmation.Read(new(process.ProcessId, process.ProcessStartKey,
                    process.ProcessName, process.ExecutablePath ?? string.Empty), keys)
            }).ToArray();
            return Results.Ok(new { source = "graphics-kernel-client", processes = rows });
        });

        // 订阅 N 秒（默认 5，允许 1–30）后返回各进程的帧统计，用来在真实程序上核对帧率。
        app.MapGet("/api/debug/frame-timing", async (HttpResponse response, IFrameTimingObservationSource frames,
            int? seconds, CancellationToken cancellation) =>
        {
            response.Headers.CacheControl = "no-store";
            if (seconds is < 1 or > 30)
            {
                return Results.BadRequest(new { error = "seconds must be between 1 and 30." });
            }

            var window = TimeSpan.FromSeconds(seconds ?? 5);
            using var lease = frames.AcquireSubscription();
            await Task.Delay(window, cancellation);
            var snapshot = frames.Read(window);
            return snapshot is null ? Results.Ok(new { running = false }) : Results.Ok(new { running = true, snapshot });
        });

        app.MapGet("/api/debug/cpu-residency", (HttpResponse response, EtwCpuCoreResidencyReader reader) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(reader.GetDiagnostics());
        });

        app.MapGet("/api/debug/scheduling-inputs", (HttpResponse response,
            IMetricSnapshotObservationSource metrics, ISchedulingProcessFactObservationSource processes,
            WindowsGpuPlacementInjector gpuInjector) =>
        {
            response.Headers.CacheControl = "no-store";
            var hardware = metrics.ReadLatest(MetricSampleRequest.All);
            var facts = hardware is null ? null : processes.ReadLatest(new SchedulingProcessFactRequest(
                SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.GpuUsage
                    | SchedulingProcessMetricMask.GpuDedicatedMemory | SchedulingProcessMetricMask.RuntimeState,
                ExpectedMemoryUsageDependency: null, hardware.GpuInventory));
            var failures = facts?.Processes.Select(process => new
            {
                process.ProcessId,
                process.ProcessStartKey,
                failure = gpuInjector.GetKnownProcessFailure(new GpuPlacementProcessInstance(
                    process.ProcessId, process.ProcessStartKey, process.ProcessName, process.ExecutablePath ?? string.Empty))
            }).Where(process => process.failure is not null).ToArray();
            return Results.Ok(new { gpuInventory = hardware?.GpuInventory, processFacts = facts,
                gpuPlacementFailures = failures });
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
            return Results.Ok(new
            {
                ok = true,
                lines = logReader.ReadTail(tail ?? 100)
            });
        });

        app.MapGet("/api/debug/history/requirements", (
            HttpResponse response,
            IRuntimePlanProvider runtimePlanProvider) =>
        {
            response.Headers.CacheControl = "no-store";
            var plan = runtimePlanProvider.Current.HostManager;
            return Results.Ok(new
            {
                requirements = plan.DataHistory.Requirements,
                consumers = new[]
                {
                    new
                    {
                        module = plan.SmartCoordinator.Build.NativeModule,
                        requirements = plan.SmartCoordinator.DataHistory.Requirements
                    }
                }
            });
        });

        app.MapGet("/api/debug/metrics/capture", async (
            HttpRequest request,
            HttpResponse response,
            IMetricSampler sampler,
            CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var sampleRequest = request.Query.TryGetValue("ids", out var ids)
                ? MetricSampleRequest.ForIds(ids)
                : MetricSampleRequest.All;
            var snapshot = await sampler.CaptureSnapshotAsync(
                sampleRequest,
                cancellationToken);
            return Results.Ok(snapshot);
        });

        return app;
    }
}
