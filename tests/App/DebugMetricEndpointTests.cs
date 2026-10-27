using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Application.Diagnostics;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Frames;

namespace Resource_Manager_APP.Tests;

public sealed class DebugMetricEndpointTests
{
    [Theory]
    [InlineData("/api/debug/metrics/capture")]
    [InlineData("/api/debug/scheduling-inputs")]
    [InlineData("/api/debug/gpu-renderers")]
    public async Task RealAllMetricsCapacityFailureReturnsNamed503(string path)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture(
            AllMetricSubscriptionTestData.BoundedPlan(1024));
        using var sampler = WindowsHardwareMetricSamplerLifecycleTests.CreateSampler(
            subscriptions, AllMetricSubscriptionTestData.RecordedCatalog());
        var builder = WebApplication.CreateBuilder();
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton<IMetricSnapshotPushSource>(sampler);
        RegisterUnused<ResourceManager.App.Application.KernelLeak.IKernelLeakReportSource>(builder.Services);
        RegisterUnused<IBusinessBlockQuery>(builder.Services);
        RegisterUnused<BusinessBlockDiagnosticsRegistry>(builder.Services);
        RegisterUnused<IFrameTimingObservationSource>(builder.Services);
        RegisterUnused<EtwCpuCoreResidencyReader>(builder.Services);
        RegisterUnused<IRuntimePlanProvider>(builder.Services);
        RegisterUnused<IDebugDiagnosticLogReader>(builder.Services);
        RegisterUnused<IResourceBreakdownPushSource>(builder.Services);
        RegisterUnused<ISchedulingProcessFactObservationSource>(builder.Services);
        RegisterUnused<WindowsGpuPlacementInjector>(builder.Services);
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapDebugEndpoints",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [app]);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(item => item.RoutePattern.RawText == path);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var json = JsonDocument.Parse(body.ToArray());
        Assert.Equal("ItemCapacityExceeded", json.RootElement.GetProperty("error").GetString());
    }

    private static void RegisterUnused<T>(IServiceCollection services) where T : class
        => services.AddSingleton(typeof(T), typeof(T).IsInterface
            ? DispatchProxy.Create(typeof(T), typeof(UnusedService))
            : RuntimeHelpers.GetUninitializedObject(typeof(T)));

    public class UnusedService : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
            => throw new InvalidOperationException($"Unexpected downstream call: {method}");
    }
}
