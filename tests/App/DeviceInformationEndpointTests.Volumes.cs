using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task VolumeHttpSeparatesQuotaFailuresAndCompleteDirectoryChangesWhileGetDoesNotRead()
    {
        var api = new VolumeSpaceInformationCollectorTests.Api();
        var collector = new WindowsVolumeSpaceInformationReader(api);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { VolumeSpaceInformation = collector.Read() });
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields");
        Assert.Equal(0, api.Directories); Assert.Empty(api.Requested);
        for (var phase = 0; phase < 6; phase++)
        {
            api.FirstStatus = phase == 1 ? unchecked((int)0x80004005) : 0;
            api.Roots = phase switch { 2 => null, 4 => [], 5 => [VolumeSpaceInformationCollectorTests.RootA], _ => [VolumeSpaceInformationCollectorTests.RootA, VolumeSpaceInformationCollectorTests.RootB] };
            await fixture.Provider.SampleNowAsync(CancellationToken.None);
            var current = fixture.Provider.ReadState().InformationFields;
            var directories = api.Directories; var reads = api.Requested.Count;
            using var result = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var volumes = result.RootElement.GetProperty("devices").EnumerateArray().Where(item => item.GetProperty("objectType").GetString() == "volume").ToArray();
            Assert.Equal(phase == 4 ? 0 : phase == 5 ? 1 : 2, volumes.Length);
            if (phase is 1 or 2) AssertVolumeValuesEmpty(result.RootElement, VolumeSpaceInformationCollectorTests.IdA);
            if (phase == 2) AssertVolumeValuesEmpty(result.RootElement, VolumeSpaceInformationCollectorTests.IdB);
            if (phase is not (1 or 2 or 4))
            {
                Assert.Equal("4096000", Field(result.RootElement, VolumeSpaceInformationCollectorTests.IdA, "volume_all_size_actual").GetProperty("value").GetString());
                Assert.Equal("3276800", Field(result.RootElement, VolumeSpaceInformationCollectorTests.IdA, "volume_all_caller_total_bytes_actual").GetProperty("value").GetString());
                Assert.Equal("204800", Field(result.RootElement, VolumeSpaceInformationCollectorTests.IdA, "volume_all_user_available_bytes_actual").GetProperty("value").GetString());
                Assert.Equal("S-1-5-18", Field(result.RootElement, VolumeSpaceInformationCollectorTests.IdA, "volume_all_space_query_caller_sid").GetProperty("value").GetString());
            }
            if (phase == 1) Assert.Equal("4096000", Field(result.RootElement, VolumeSpaceInformationCollectorTests.IdB, "volume_all_size_actual").GetProperty("value").GetString());
            Assert.Equal("Machine Model", Field(result.RootElement, "machine", "machine_all_model_declared").GetProperty("value").GetString());
            Assert.Same(current, fixture.Provider.ReadState().InformationFields);
            Assert.Equal(directories, api.Directories); Assert.Equal(reads, api.Requested.Count);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task PersistedVolumeValuesAndWholeSourceFailureAreEmptyUntilFreshCapture()
    {
        var data = VolumeSpaceInformationCollectorTests.Sample(); data.ActualTotalAllocationUnits = ulong.MaxValue;
        var volume = WindowsVolumeSpaceInformationReader.Create(VolumeSpaceInformationCollectorTests.IdA, data, "S-1-5-18");
        var raw = DeviceInformationTestData.Snapshot() with { VolumeSpaceInformation = [volume] };
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 7,
            new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new IOException("failed"), raw);
        using var fixture = new Fixture(reader, persisted);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        AssertVolumeValuesEmpty(cached.RootElement, VolumeSpaceInformationCollectorTests.IdA); Assert.Equal(0, reader.ReadCount);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var first = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal("75557863725914323415040", Field(first.RootElement, VolumeSpaceInformationCollectorTests.IdA, "volume_all_size_actual").GetProperty("value").GetString());
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var failed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        AssertVolumeValuesEmpty(failed.RootElement, VolumeSpaceInformationCollectorTests.IdA);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var recovered = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal("75557863725914323415040", Field(recovered.RootElement, VolumeSpaceInformationCollectorTests.IdA, "volume_all_size_actual").GetProperty("value").GetString());
        Assert.Equal(3, reader.ReadCount); await app.StopAsync();
    }

    [Fact]
    public async Task OriginalWorkerAndUnifiedVolumeStreamReadOnlyWithDemandAndCancellationKeepsDisplay()
    {
        var api = new VolumeSpaceInformationCollectorTests.Api();
        var collector = new WindowsVolumeSpaceInformationReader(api);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { VolumeSpaceInformation = collector.Read() });
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields");
        Assert.Equal(0, api.Directories); Assert.Empty(api.Requested);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
            new { id = "volume-worker", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        var value = message.RootElement.GetProperty("value");
        var total = Field(value, VolumeSpaceInformationCollectorTests.IdA, "volume_all_size_actual");
        Assert.Equal("4096000", total.GetProperty("value").GetString());
        Assert.Equal("windows.volume-space", total.GetProperty("definition").GetProperty("sourceId").GetString());
        Assert.Equal("unsigned-integer", total.GetProperty("definition").GetProperty("valueKind").GetString());
        Assert.Equal("B", total.GetProperty("definition").GetProperty("unit").GetString());
        Assert.Equal("S-1-5-18", Field(value, VolumeSpaceInformationCollectorTests.IdA, "volume_all_space_query_caller_sid").GetProperty("value").GetString());
        Assert.True(api.Directories >= 1); Assert.Equal(api.Directories * 2, api.Requested.Count);
        await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var directories = api.Directories; var reads = api.Requested.Count;
        var current = fixture.Provider.ReadState().InformationFields;
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal("4096000", Field(cached.RootElement, VolumeSpaceInformationCollectorTests.IdA, "volume_all_size_actual").GetProperty("value").GetString());
        await Task.Delay(1300); Assert.Equal(directories, api.Directories); Assert.Equal(reads, api.Requested.Count);
        Assert.Same(current, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }

    private static void AssertVolumeValuesEmpty(JsonElement snapshot, string identity)
    {
        var volume = Assert.Single(snapshot.GetProperty("devices").EnumerateArray(), item => item.GetProperty("deviceIdentity").GetString() == identity);
        Assert.Equal(22, volume.GetProperty("fields").GetArrayLength());
        Assert.All(volume.GetProperty("fields").EnumerateArray(), field => Assert.Equal(JsonValueKind.Null, field.GetProperty("value").ValueKind));
    }
}
