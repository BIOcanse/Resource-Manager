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
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapDebugEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapEventDropsDebugEndpoint();
        app.MapNativeMemoryDebugEndpoint();
        app.MapSubscriptionHoldersDebugEndpoint();
        app.MapGet("/api/debug/kernel-leaks", (HttpResponse response, Application.KernelLeak.IKernelLeakReportSource reports) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(new Domain.KernelLeak.KernelLeakDebugSnapshot(reports.Read(), reports.ReadDiagnostics()));
        });
        app.MapGet("/api/debug/business-blocks", (HttpResponse response, IBusinessBlockQuery blocks,
            BusinessBlockDiagnosticsRegistry diagnostics,
            [Microsoft.AspNetCore.Mvc.FromServices] KernelEtwConsumerThreadDiagnostics kernelConsumer) =>
        {
            response.Headers.CacheControl = "no-store";
            var snapshot = blocks.Read();
            var resourceMetrics = kernelConsumer.Read();
            return snapshot.NativeSnapshot is { } native
                ? Results.Ok(new NativeBusinessBlockDebugSnapshot(native, diagnostics.ReadAll()) { KernelEtwConsumer = resourceMetrics })
                : Results.Ok(new BusinessBlockDebugSnapshot(snapshot, diagnostics.ReadAll()) { KernelEtwConsumer = resourceMetrics });
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
        }).AddEndpointFilter(SamplingCapacityFailure);

        // A scoped diagnostic lease; GPU frame tracing is opt-in, including for the cost measurement script.
        app.MapGet("/api/debug/frame-timing", async (HttpResponse response, IFrameTimingObservationSource frames,
            int? seconds, bool? gpuFrames, bool? outputResolution,
            [Microsoft.AspNetCore.Mvc.FromQuery] int[]? processIds, CancellationToken cancellation) =>
        {
            response.Headers.CacheControl = "no-store";
            var window = seconds is { } specified ? TimeSpan.FromSeconds(specified)
                : Infrastructure.Monitoring.MonitoringSettings.ProductDefaults.FramePipeline.DiagnosticWindow;
            if (window <= TimeSpan.Zero || window > frames.Retention || processIds?.Any(static pid => pid <= 0) == true)
            {
                return Results.BadRequest(new EndpointErrorPayload("A positive window within frame retention and positive processIds are required."));
            }

            var detail = (gpuFrames == true ? FrameDetail.GpuFrames : FrameDetail.None)
                | (outputResolution == true ? FrameDetail.OutputResolution : FrameDetail.None);
            using var lease = frames.AcquireSubscription(new(processIds is { Length: > 0 } ? processIds : null, detail));
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
            catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
            {
                return Results.Json(new ThreadCpuWindowTimeoutPayload("thread-cpu-window-timeout",
                    (error as ThreadCpuWindowWaitCanceledException)?.Diagnostics),
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });
        app.MapGet("/api/debug/cpu-residency", (HttpResponse response, EtwCpuCoreResidencyReader reader) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(reader.GetDiagnostics());
        });

        app.MapSchedulingInputsDebugEndpoint();

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
        }).AddEndpointFilter(SamplingCapacityFailure);

        return app;
    }

    internal static IEndpointRouteBuilder MapEventDropsDebugEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/debug/event-drops", (HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(Infrastructure.Telemetry.Etw.EtwEventDropDiagnostics.Read());
        });
        return app;
    }

    // The product Ledger's table and every registered workspace's capacity, read at request time. The read takes
    // no lease, allocates no workspace and starts nothing.
    internal static IEndpointRouteBuilder MapNativeMemoryDebugEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/debug/native-memory", (HttpResponse response,
            [Microsoft.AspNetCore.Mvc.FromServices] Infrastructure.Adaptation.ResourceManagerLedgerOwner ledger) =>
        {
            response.Headers.CacheControl = "no-store";
            var table = ledger.ReadTable();
            return Results.Ok(new NativeMemoryDebugPayload(
                new(table.RegisteredBytes, table.SystemPageBytes), ledger.ReadWorkspaceCapacities()));
        });
        return app;
    }

    // Who holds which subscription, at what interval, for which items. Each sampling role's tracker binds its
    // holder read in SubscriptionActivity when it is constructed; Present and the kernel ETW broker are read from
    // their own holder tables. The read takes no lease, samples nothing and changes nothing.
    internal static IEndpointRouteBuilder MapSubscriptionHoldersDebugEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/debug/subscriptions", (HttpResponse response,
            [Microsoft.AspNetCore.Mvc.FromServices] Infrastructure.RuntimeSpecialization.HostManagerSamplingSubscriptionOwner owner,
            [Microsoft.AspNetCore.Mvc.FromServices] Infrastructure.Telemetry.Etw.PresentFrameEtwSource present,
            [Microsoft.AspNetCore.Mvc.FromServices] Infrastructure.Telemetry.Etw.KernelEtwSessionBroker kernel) =>
        {
            response.Headers.CacheControl = "no-store";
            var session = kernel.GetSnapshot();
            return Results.Ok(new SubscriptionHoldersDebugPayload(owner.Activity.ReadHolders(), present.ReadHolders(),
                new(session.State, session.EnabledKeywords.ToString(), kernel.ReadHolders())));
        });
        return app;
    }

    internal static IEndpointRouteBuilder MapSchedulingInputsDebugEndpoint(this IEndpointRouteBuilder app)
    {
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
        }).AddEndpointFilter(SamplingCapacityFailure);
        return app;
    }


    private static async ValueTask<object?> SamplingCapacityFailure(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (ResourceManager.Subscription.SamplingSubscriptionCapacityException error)
        {
            return Results.Json(new EndpointErrorPayload(error.Reason.ToString()),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
    private sealed record ThreadCpuWindowTimeoutPayload(string Error, ThreadCpuWindowWaitDiagnostics? Wait);
}
