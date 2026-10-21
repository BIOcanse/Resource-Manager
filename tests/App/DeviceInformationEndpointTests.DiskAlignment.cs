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
    public async Task DiskAlignmentHttpRetainsExactValuesAndIndependentFailuresWithoutGetIo()
    {
        var api = new AlignmentApi(); var collector = new WindowsDiskLayoutInformationReader(api);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = collector.Read(), VolumeSpaceInformation = [] });
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, api.Reads);
        for (var phase = 0; phase < 6; phase++)
        {
            api.FailAlignment = phase == 1; api.FailLayout = phase == 2;
            api.Directory = phase == 3 ? null : phase == 4 ? [] : [DiskLayoutInformationCollectorTests.DiskA];
            await fixture.Provider.SampleNowAsync(CancellationToken.None); var reads = api.Reads;
            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); var root = document.RootElement;
            var disks = root.GetProperty("devices").EnumerateArray().Where(item => item.GetProperty("objectType").GetString() == "os_disk").ToArray();
            Assert.Equal(phase == 4 ? 0 : 1, disks.Length);
            if (phase != 4)
            {
                if (phase == 1 || phase == 3) AssertAlignmentEmpty(root);
                else Assert.Equal("1536", Field(root, DiskLayoutInformationCollectorTests.IdA, "storage_all_os_physical_sector_offset_reported_bytes").GetProperty("value").GetString());
                if (phase == 0 || phase == 5)
                {
                    var entry = Assert.Single(Field(root, DiskLayoutInformationCollectorTests.IdA, "storage_all_partition_alignment_actual").GetProperty("value").EnumerateArray());
                    Assert.True(entry.GetProperty("physicalStartAligned").GetBoolean()); Assert.Equal("0", entry.GetProperty("physicalStartRemainderBytes").GetString());
                    Assert.Equal("windows.storage-access-alignment", Field(root, DiskLayoutInformationCollectorTests.IdA, "storage_all_partition_alignment_actual").GetProperty("definition").GetProperty("sourceId").GetString());
                }
                if (phase == 1) Assert.Equal(1, Field(root, DiskLayoutInformationCollectorTests.IdA, "storage_all_partition_layout_reported").GetProperty("value").GetArrayLength());
                if (phase == 2)
                {
                    Assert.Equal(JsonValueKind.Null, Field(root, DiskLayoutInformationCollectorTests.IdA, "storage_all_partition_layout_reported").GetProperty("value").ValueKind);
                    Assert.Equal(JsonValueKind.Null, Field(root, DiskLayoutInformationCollectorTests.IdA, "storage_all_partition_alignment_actual").GetProperty("value").ValueKind);
                }
            }
            Assert.Equal(reads, api.Reads); Assert.Equal("Machine Model", Field(root, "machine", "machine_all_model_declared").GetProperty("value").GetString());
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task PersistedAlignmentStartsAsDeclarationsAndWholeReadFailureClearsAllCurrentFields()
    {
        var disk = DiskAlignmentInformationCollectorTests.Disk();
        var raw = DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = [disk], VolumeSpaceInformation = [] };
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 11, new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new IOException("failed"), raw);
        using var fixture = new Fixture(reader, persisted); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var initial = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); AssertAlignmentEmpty(initial.RootElement); Assert.Equal(0, reader.ReadCount);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var fresh = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); Assert.Equal("4096", Field(fresh.RootElement, disk.DeviceIdentity, "storage_all_os_physical_sector_size_reported_bytes").GetProperty("value").GetString());
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var failed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); AssertAlignmentEmpty(failed.RootElement); AssertDiskLayoutEmpty(failed.RootElement, disk.DeviceIdentity);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var recovered = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); Assert.True(Field(recovered.RootElement, disk.DeviceIdentity, "storage_all_partition_alignment_actual").GetProperty("value")[0].GetProperty("physicalStartAligned").GetBoolean());
        await app.StopAsync();
    }

    [Fact]
    public async Task UnifiedAlignmentStreamUsesOriginalWorkerAndCancellationRetainsCachedGeometry()
    {
        var api = new AlignmentApi(); var collector = new WindowsDiskLayoutInformationReader(api);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = collector.Read(), VolumeSpaceInformation = [] });
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, api.Reads);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "disk-alignment", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!); var root = message.RootElement.GetProperty("value");
        Assert.True(Field(root, DiskLayoutInformationCollectorTests.IdA, "storage_all_partition_alignment_actual").GetProperty("value")[0].GetProperty("physicalStartAligned").GetBoolean());
        Assert.True(api.Reads >= 1); await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var reads = api.Reads; var cached = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal("4096", Field(displayed.RootElement, DiskLayoutInformationCollectorTests.IdA, "storage_all_os_physical_sector_size_reported_bytes").GetProperty("value").GetString());
        await Task.Delay(1300); Assert.Equal(reads, api.Reads); Assert.Same(cached, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }

    private static void AssertAlignmentEmpty(JsonElement root)
    {
        var disk = Assert.Single(root.GetProperty("devices").EnumerateArray(), item => item.GetProperty("deviceIdentity").GetString() == DiskLayoutInformationCollectorTests.IdA);
        var fields = disk.GetProperty("fields").EnumerateArray().Where(item => item.GetProperty("definition").GetProperty("sourceId").GetString() == DiskAlignmentInformationCollectorTests.Source).ToArray();
        Assert.Equal(6, fields.Length); Assert.All(fields, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("value").ValueKind));
    }

    private sealed class AlignmentApi : IWindowsDiskLayoutInformationApi
    {
        internal IReadOnlyList<WindowsDiskInterface>? Directory = [DiskLayoutInformationCollectorTests.DiskA];
        internal int Reads; internal bool FailAlignment, FailLayout;
        public IReadOnlyList<WindowsDiskInterface>? Enumerate() => Directory;
        public WindowsDiskLayoutCapture? Read(WindowsDiskInterface device)
        {
            Reads++; var disk = DiskAlignmentInformationCollectorTests.Disk();
            return new(2, FailLayout ? null : disk.Layout) { AccessAlignment = FailAlignment ? null : disk.AccessAlignment };
        }
    }
}
