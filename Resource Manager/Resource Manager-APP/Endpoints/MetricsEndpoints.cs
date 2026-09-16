using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Migration;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.ResourceTable;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/metrics/catalog", (
            DashboardMonitoringCatalogState catalogState) =>
        {
            var snapshot = catalogState.Current;
            // 读不到的条目也要发出去。
            //
            // 目录里每一条都带着 Selectable、DisabledReason 和缺哪个组件，
            // 界面也早就会把这些画成灰掉的条目加一句原因。先前这里按 Selectable 过滤，
            // 等于把算好的原因直接扔掉 —— 用户看到的就是"根本没有这一项"，
            // 分不清是这台机器读不到，还是这个软件压根不支持。
            // 比如 AMD 机器上的 CPU 电压/电流/温度要装 AMD SMU 组件才读得到，
            // 不发出来就没有任何地方告诉用户这件事。
            return Results.Ok(snapshot is null
                ? Array.Empty<MetricDefinition>()
                : MetricCatalog.FromSnapshot(snapshot));
        }).AllowAnonymous();

        app.MapGet("/api/metrics/snapshot", async (
            HttpRequest request,
            HttpResponse response,
            IMetricSampler sampler,
            IRuntimePlanProvider runtimePlanProvider,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            var snapshotRequest = request.Query.TryGetValue("ids", out var ids)
                ? MetricSampleRequest.ForIds(ids)
                : runtimePlanProvider.Current.Monitoring.DashboardMetricRequest;
            var snapshot = await sampler.GetSnapshotAsync(snapshotRequest, cancellationToken);
            var presentation = MetricCatalog.ApplyPresentation(snapshot);
            return Results.Ok(MetricSnapshotWireSnapshot.From(
                presentation,
                snapshotRequest));
        }).AllowAnonymous();

        app.MapGet("/api/metrics/gpu-specialized", (
            HttpResponse response,
            IGpuTelemetryWorkerClient gpuTelemetry,
            CancellationToken cancellationToken) =>
        {
            DisableResponseCache(response);
            cancellationToken.ThrowIfCancellationRequested();
            return Results.Ok(GpuTelemetryWireSnapshot.From(
                gpuTelemetry.GetLatestSnapshot()));
        }).AllowAnonymous();

        return app;
    }

    private static TimeSpan ParseSamplingSubscriptionInterval(
        IQueryCollection query)
    {
        const int defaultIntervalMilliseconds = 1_000;
        if (!query.TryGetValue("intervalMs", out var raw)
            || !int.TryParse(raw.FirstOrDefault(), out var milliseconds))
        {
            milliseconds = defaultIntervalMilliseconds;
        }
        return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 100, 60_000));
    }
}
