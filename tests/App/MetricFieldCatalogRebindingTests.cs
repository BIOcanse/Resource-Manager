using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class MetricFieldCatalogRebindingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OpenFieldSubscriptionRebindsAfterDeviceRemovalAndReturn(bool initiallyEmpty, bool scoped)
    {
        var original = StorageMetricSnapshotTestData.CommittedSnapshot();
        var binding = original.FieldBindings.Values.First(b => b.Field.Name == "storage_all_wear_reported");
        var first = original with
        {
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = binding }
        };
        var empty = first with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>(), CatalogGeneration = first.CatalogGeneration + 1 };
        var returnedBinding = binding with { RequestAddress = binding.RequestAddress + "-returned" };
        var returned = first with
        {
            CatalogGeneration = empty.CatalogGeneration + 1,
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { [returnedBinding.RequestAddress] = returnedBinding },
            Items = new Dictionary<string, MetricValue> { [returnedBinding.RequestAddress] = original.Items[binding.RequestAddress] },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            {
                [returnedBinding.RequestAddress] = original.Datasets[binding.RequestAddress] with { DatasetId = returnedBinding.RequestAddress }
            }
        };
        var catalog = new DashboardMonitoringCatalogState();
        catalog.PublishCatalogProbe(initiallyEmpty ? empty : first);
        var source = new Source(catalog);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<IMetricSnapshotPushSource>(source);
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapSubscriptionChannelEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses)) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            {
                Content = JsonContent.Create(new { version = 1, subscriptions = new[]
                { new { id = "field", path = $"/api/metrics/subscribe?fields={binding.Field.Name}&intervalMs=100"
                    + (scoped ? $"&device={Uri.EscapeDataString(binding.DeviceIdentity)}" : "") } } })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var initial = await ReadValue(reader, timeout.Token);
            Assert.Equal(initiallyEmpty ? 0 : 1, initial.GetArrayLength());
            if (!initiallyEmpty)
            {
                Assert.Equal("150", initial[0].GetProperty("fields")[0].GetProperty("integerValue").GetString());
                catalog.PublishCatalogProbe(empty);
                Assert.Equal(0, (await ReadValue(reader, timeout.Token)).GetArrayLength());
            }
            catalog.PublishCatalogProbe(returned);
            var next = await ReadValue(reader, timeout.Token);
            Assert.Equal(binding.DeviceIdentity, next[0].GetProperty("deviceIdentity").GetString());
            var field = next[0].GetProperty("fields")[0];
            Assert.Equal(binding.Field.Name, field.GetProperty("name").GetString());
            Assert.NotNull(original.Items[binding.RequestAddress].IntegerValue);
            Assert.Equal(original.Items[binding.RequestAddress].IntegerValue, field.GetProperty("integerValue").GetString());
            Assert.Contains(returnedBinding.RequestAddress, source.Requests.Last().Ids);
            Assert.DoesNotContain(binding.RequestAddress, source.Requests.Last().Ids);
        }
        finally
        {
            await timeout.CancelAsync();
            await app.StopAsync();
        }
        Assert.Equal(0, source.Active);
    }

    private static async Task<JsonElement> ReadValue(StreamReader reader, CancellationToken token)
    {
        using var document = JsonDocument.Parse((await reader.ReadLineAsync(token))!);
        return document.RootElement.GetProperty("value").Clone();
    }

    // The managed sampler boundary only publishes a value if that concrete address was acquired.
    private sealed class Source(DashboardMonitoringCatalogState catalog) : IMetricSnapshotPushSource
    {
        public List<MetricSampleRequest> Requests { get; } = [];
        public int Active;
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string id, MetricSampleRequest request,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken token)
        {
            Requests.Add(request);
            Interlocked.Increment(ref Active);
            var changes = Channel.CreateUnbounded<bool>();
            void Changed() => changes.Writer.TryWrite(true);
            catalog.Changed += Changed;
            try
            {
                do
                {
                    var snapshot = catalog.Current!;
                    yield return snapshot with
                    {
                        Items = snapshot.Items.Where(pair => request.Ids.Contains(pair.Key)).ToDictionary(),
                        Datasets = snapshot.Datasets.Where(pair => request.Ids.Contains(pair.Key)).ToDictionary()
                    };
                } while (await changes.Reader.ReadAsync(token));
            }
            finally
            {
                catalog.Changed -= Changed;
                Interlocked.Decrement(ref Active);
            }
        }
    }
}
