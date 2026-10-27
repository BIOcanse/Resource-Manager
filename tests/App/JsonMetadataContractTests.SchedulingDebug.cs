using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed partial class JsonMetadataContractTests
{
    [Fact]
    public async Task SchedulingInputsDebugHttpNeedsNoQueryParameterAndSerializesPublishedFacts()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options => ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        var source = new SchedulingDebugSource();
        builder.Services.AddSingleton<IMetricSnapshotPushSource>(source);
        builder.Services.AddSingleton<IResourceBreakdownPushSource>(source);
        builder.Services.AddSingleton<ISchedulingProcessFactObservationSource>(source);
        builder.Services.AddSingleton<WindowsGpuPlacementInjector>();
        await using var app = builder.Build();
        app.MapSchedulingInputsDebugEndpoint();
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses)), Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync("/api/debug/scheduling-inputs");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(78776, wire.RootElement.GetProperty("processFacts").GetProperty("processes")[0].GetProperty("processId").GetInt32());
            Assert.True(source.MetricsReleased && source.ProcessesReleased);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class SchedulingDebugSource : IMetricSnapshotPushSource, IResourceBreakdownPushSource, ISchedulingProcessFactObservationSource
    {
        internal bool MetricsReleased, ProcessesReleased;
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string subscriptionId, MetricSampleRequest request,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            var ticks = DateTimeOffset.UtcNow.UtcTicks;
            var snapshot = (HardwareMetricSnapshot)typeof(WindowsResourceBreakdownSamplerLifecycleTests)
                .GetMethod("CreateMemorySnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
            try
            {
                yield return snapshot with { Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
                    { ["cpu.usage"] = new("cpu.usage", SamplingObservationStatus.Current, 1, ticks, 1, 1, 1, 1) } };
            }
            finally { MetricsReleased = true; }
        }

        public async IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(string subscriptionId, ResourceBreakdownSampleRequest request,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            try { yield return new(DateTimeOffset.UtcNow, []); }
            finally { ProcessesReleased = true; }
        }

        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request)
            => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 1, 1, 0, 0, request.RequestedMetricMask, SchedulingProcessMetricMask.CpuUsage,
                [new(78776, 10, "cs2", @"D:\Games\Steam\cs2.exe", "catalog:game-counter-strike-2", "CS2", "game", "game", 95, SchedulingProcessMetricMask.CpuUsage, 17, 0, 1, [])]);

        public IDisposable AcquireSubscription(string subscriptionId, SchedulingProcessMetricMask metricMask, TimeSpan refreshInterval)
            => throw new InvalidOperationException("The endpoint must use the push source's owned single fetch.");
    }
}
