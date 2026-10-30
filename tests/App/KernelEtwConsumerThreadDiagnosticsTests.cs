using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Diagnostics;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Frames;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwConsumerThreadDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDebugContractReadsHeldResourceThreadAndItsReleaseWithoutStartingCapture(bool nativeScheduling)
    {
        var observer = new KernelEtwConsumerThreadDiagnostics(NullLogger<KernelEtwConsumerThreadDiagnostics>.Instance);
        var query = new Query(nativeScheduling);
        var broker = new UnusedBroker();
        var builder = WebApplication.CreateBuilder();
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton(observer);
        builder.Services.AddSingleton<IBusinessBlockQuery>(query);
        builder.Services.AddSingleton<IKernelEtwSessionBroker>(broker);
        builder.Services.AddSingleton(new BusinessBlockDiagnosticsRegistry([], NullLogger<BusinessBlockDiagnosticsRegistry>.Instance));
        RegisterUnused<ResourceManager.App.Application.KernelLeak.IKernelLeakReportSource>(builder.Services);
        RegisterUnused<IMetricSnapshotPushSource>(builder.Services);
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
            .OfType<RouteEndpoint>().Single(item => item.RoutePattern.RawText == "/api/debug/business-blocks");
        using (var initial = await Read(endpoint, app.Services))
        {
            Assert.Empty(initial.RootElement.GetProperty("kernelEtwConsumer").GetProperty("activeThreads").EnumerateArray());
            Assert.Equal((int)BusinessBlockAttributionCoverage.None,
                initial.RootElement.GetProperty("kernelEtwConsumer").GetProperty("coverage").GetInt32());
        }

        using var ready = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();
        uint nativeThreadId = 0;
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                nativeThreadId = GetCurrentThreadId();
                using (observer.RegisterCurrentThread("kernel-resource-fixture"))
                {
                    ready.Set();
                    while (!stop.IsSet) Thread.SpinWait(20_000);
                }
            }
            catch (Exception error) { workerError = error; ready.Set(); }
        }) { Name = "KernelEtw.ResourceFixture", IsBackground = true };
        worker.Start();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(workerError);
            using var active = await Read(endpoint, app.Services);
            var resource = active.RootElement.GetProperty("kernelEtwConsumer");
            var thread = Assert.Single(resource.GetProperty("activeThreads").EnumerateArray());
            Assert.Equal(nativeThreadId, thread.GetProperty("nativeThreadId").GetUInt32());
            Assert.Equal("KernelEtw.ResourceFixture", thread.GetProperty("name").GetString());
            Assert.Equal("kernel-resource-fixture", thread.GetProperty("sessionName").GetString());
            Assert.Equal((int)BusinessBlockAttributionCoverage.DedicatedThreads, resource.GetProperty("coverage").GetInt32());
            Assert.False(resource.GetProperty("allocationCoverageComplete").GetBoolean());
            Assert.Equal(resource.GetProperty("totalCpuTime").GetString(), thread.GetProperty("cpuTime").GetString());
            Assert.Empty(active.RootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Empty(active.RootElement.GetProperty("scheduling").GetProperty("blocks").EnumerateArray());
            if (nativeScheduling)
                Assert.Equal("native-fixture", active.RootElement.GetProperty("scheduling").GetProperty("marker").GetString());
        }
        finally
        {
            stop.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        }
        Assert.Null(workerError);
        using var completed = await Read(endpoint, app.Services);
        var final = completed.RootElement.GetProperty("kernelEtwConsumer");
        Assert.Empty(final.GetProperty("activeThreads").EnumerateArray());
        Assert.True(final.GetProperty("allocationCoverageComplete").GetBoolean());
        Assert.Equal((int)BusinessBlockAttributionCoverage.DedicatedThreads, final.GetProperty("coverage").GetInt32());
        Assert.True(TimeSpan.Parse(final.GetProperty("totalCpuTime").GetString()!, CultureInfo.InvariantCulture) >= TimeSpan.Zero);
        Assert.Empty(query.Snapshot.Blocks);
        Assert.Empty(query.Snapshot.History);
        Assert.Equal(0, broker.Acquires);
    }

    private static async Task<JsonDocument> Read(RouteEndpoint endpoint, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "GET";
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        return JsonDocument.Parse(body.ToArray());
    }

    private sealed class Query(bool native) : IBusinessBlockQuery
    {
        internal BusinessBlocksSnapshot Snapshot { get; } = new(DateTimeOffset.UnixEpoch, 0, [], [])
        {
            RegistryVersion = "fixture", Program = 0,
            NativeSnapshot = native ? JsonSerializer.SerializeToElement(new { blocks = Array.Empty<object>(), marker = "native-fixture" }) : null
        };
        public BusinessBlocksSnapshot Read() => Snapshot;
    }

    private sealed class UnusedBroker : IKernelEtwSessionBroker
    {
        internal int Acquires;
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        { Acquires++; throw new InvalidOperationException("A diagnostic read must not acquire kernel ETW."); }
        public KernelEtwSessionSnapshot GetSnapshot() => throw new InvalidOperationException("A thread metric read needs no capture state.");
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

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}
