using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Theory]
    [InlineData("volume_all_free_bytes_actual", 200)]
    [InlineData("missing-field", 400)]
    [InlineData("", 400)]
    public async Task ExactInformationWireSelectionIsValidatedAndDoesNotSendUnselectedFields(string name, int status)
    {
        var reader = new Reader(VolumeSnapshot());
        using var fixture = new Fixture(reader);
        await fixture.Provider.SampleNowAsync(default);
        await using var cpu = new CpuTopologyProviderTestFixture();
        using var process = ProcessBasicFieldCollectorTests.Fixture(new ProcessBasicFieldCollectorTests.Api());
        var host = InformationFields(fixture.Provider, cpu, process.Sampler);
        var core = new ReadWriteCore(host.Reads, new SelectionDependencies());
        await using var app = await StartAsync(fixture.Provider, services => {
            services.AddSingleton(core);
            services.AddSingleton(new FieldSubscriptionCore(core, host.Subscriptions));
        });
        using var client = Client(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(new(HttpMethod.Post, "/api/subscriptions/stream") {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "volume", path = "/api/device-topology/fields/subscribe?intervalMs=100",
                    fields = name.Length == 0 ? Array.Empty<FieldReference>() : new[] { new FieldReference(VolumeSpaceInformationCollectorTests.IdA, name) } }
            } })
        }, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 200) {
            using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            using var frame = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
            var device = Assert.Single(frame.RootElement.GetProperty("value").GetProperty("devices").EnumerateArray());
            Assert.Equal(VolumeSpaceInformationCollectorTests.IdA, device.GetProperty("deviceIdentity").GetString());
            var field = Assert.Single(device.GetProperty("fields").EnumerateArray());
            Assert.Equal(name, field.GetProperty("definition").GetProperty("name").GetString());
        }
        await timeout.CancelAsync();
        response.Dispose();
        await app.StopAsync();
    }

    [Fact]
    public async Task ExactVolumeFreeSpaceUsesItsOwnItemWithoutRecapturingTopology()
    {
        var reader = new Reader(VolumeSnapshot());
        var api = new VolumeSpaceInformationCollectorTests.Api();
        var state = new ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks.BackendStateItems().Authority;
        using var fixture = new Fixture(reader, volumeSpace: new WindowsVolumeSpaceInformationReader(api), stateAuthority: state);
        await fixture.Provider.SampleNowAsync(default);
        await using var cpu = new CpuTopologyProviderTestFixture();
        using var process = ProcessBasicFieldCollectorTests.Fixture(new ProcessBasicFieldCollectorTests.Api());
        var host = InformationFields(fixture.Provider, cpu, process.Sampler);
        var selected = host.Reads.Catalog.Fields.Single(field => field.Field.DeviceIdentity == VolumeSpaceInformationCollectorTests.IdA
            && field.Field.FieldName == "volume_all_free_bytes_actual").Field;
        await fixture.Provider.StartAsync(default);
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var subscription = host.Subscriptions.SubscribeAsync("volume-only", [selected], TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator();
            Assert.True(await subscription.MoveNextAsync());
            Assert.Equal(selected, Assert.Single(subscription.Current).Field);
            Assert.Equal(1, reader.ReadCount);
            Assert.Equal(0, api.Directories);
            Assert.All(api.Requested, root => Assert.Equal(VolumeSpaceInformationCollectorTests.RootA, root));
            Assert.True(state.Read<DeviceInformationSnapshot>(ResourceManager.App.StateItems.StateItemUids.DeviceTopologyVolumeFreeSpaceCurrent).HasValue);
            api.FirstStatus = 5;
            do { Assert.True(await subscription.MoveNextAsync()); } while (Assert.Single(subscription.Current).Value is not null);
            Assert.Null(host.Reads.ReadCached(selected).Value);
            Assert.Equal(1, reader.ReadCount);
        }
        finally { await fixture.Provider.StopAsync(default); }
    }

    private static DeviceTopologySnapshot VolumeSnapshot() => DeviceInformationTestData.Snapshot() with {
        VolumeSpaceInformation = [new(VolumeSpaceInformationCollectorTests.IdA) { AvailableBytes = 500, TotalBytes = 1000 },
            new(VolumeSpaceInformationCollectorTests.IdB) { AvailableBytes = 700, TotalBytes = 2000 }]
    };
    private static HostFieldBackends InformationFields(DeviceTopologySnapshotProvider provider,
        CpuTopologyProviderTestFixture cpu, ResourceManager.App.Infrastructure.ResourceBreakdown.WindowsResourceBreakdownSampler process)
    {
        var metrics = new HostReadWriteBackend(null!, null!, new HardwareMetricCatalogState(), null!, null!);
        return new(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!), provider, cpu.Reader, new HostProcessFieldBackend(process));
    }
    private sealed class SelectionDependencies : IReadWriteDependencies {
        public DependencyStatus Query(string id) => new(id, DependencyAvailability.Missing, "No dependency is used.");
        public ValueTask<DependencyPackage> FetchAsync(string id, Uri uri, string destination, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<DependencyActionResult> InstallAsync(DependencyPackage package, CancellationToken token) => throw new NotSupportedException();
    }
}