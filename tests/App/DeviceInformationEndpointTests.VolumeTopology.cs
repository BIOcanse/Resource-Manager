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
    public async Task NativeVolumeMetadataHttpKeepsExactRangesAndIndependentFailuresAndDirectoryRecovery()
    {
        var space = new VolumeSpaceInformationCollectorTests.Api(); var metadata = new VolumeTopologyInformationCollectorTests.Api();
        var collector = new WindowsVolumeSpaceInformationReader(space, metadata);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { VolumeSpaceInformation = collector.Read() });
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, space.Directories); Assert.Empty(metadata.Requests);
        for (var phase = 0; phase < 7; phase++)
        {
            metadata.FailureGroup = phase is >= 1 and <= 3 ? phase - 1 : -1;
            space.Roots = phase == 4 ? null : phase == 5 ? [] : [VolumeSpaceInformationCollectorTests.RootA, VolumeSpaceInformationCollectorTests.RootB];
            await fixture.Provider.SampleNowAsync(CancellationToken.None);
            var queries = metadata.Requests.Count; var directories = space.Directories;
            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var root = document.RootElement;
            if (phase == 5) Assert.DoesNotContain(root.GetProperty("devices").EnumerateArray(), device => device.GetProperty("objectType").GetString() == "volume");
            else if (phase == 4) { AssertVolumeValuesEmpty(root, VolumeSpaceInformationCollectorTests.IdA); AssertVolumeValuesEmpty(root, VolumeSpaceInformationCollectorTests.IdB); }
            else
            {
                Assert.Equal("4096000", Field(root, VolumeSpaceInformationCollectorTests.IdA, "volume_all_size_actual").GetProperty("value").GetString());
                var extent = Field(root, VolumeSpaceInformationCollectorTests.IdA, "volume_all_disk_extents_reported").GetProperty("value");
                if (phase == 1) Assert.Equal(JsonValueKind.Null, extent.ValueKind);
                else { Assert.Equal(3, extent.GetArrayLength()); Assert.Equal("9007199254740993", extent[0].GetProperty("startingOffsetBytes").GetString()); }
                var paths = Field(root, VolumeSpaceInformationCollectorTests.IdA, "volume_all_mount_paths_reported").GetProperty("value");
                if (phase == 2) Assert.Equal(JsonValueKind.Null, paths.ValueKind); else Assert.Equal(2, paths.GetArrayLength());
                var flags = Field(root, VolumeSpaceInformationCollectorTests.IdA, "volume_all_filesystem_flags_reported").GetProperty("value");
                if (phase == 3) Assert.Equal(JsonValueKind.Null, flags.ValueKind); else Assert.Equal("2147483648", flags.GetString());
                Assert.Equal(3, Field(root, VolumeSpaceInformationCollectorTests.IdB, "volume_all_disk_extents_reported").GetProperty("value").GetArrayLength());
            }
            Assert.Equal("Machine Model", Field(root, "machine", "machine_all_model_declared").GetProperty("value").GetString());
            Assert.Equal(queries, metadata.Requests.Count); Assert.Equal(directories, space.Directories);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task PersistedMetadataIsOnlyDeclarationUntilFreshCaptureAndFailureClearsItsValues()
    {
        var volume = new DeviceTopologyVolumeSpaceInformation(VolumeSpaceInformationCollectorTests.IdA)
        { DiskExtents = VolumeTopologyInformationCollectorTests.SampleExtents(), MountPaths = [@"D:\"], FileSystem = VolumeTopologyInformationCollectorTests.SampleFileSystem() };
        var raw = DeviceInformationTestData.Snapshot() with { VolumeSpaceInformation = [volume] };
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 9, new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new IOException("failed"), raw); using var fixture = new Fixture(reader, persisted);
        await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var initial = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        AssertVolumeValuesEmpty(initial.RootElement, volume.DeviceIdentity); Assert.Equal(0, reader.ReadCount);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var fresh = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal("  数据  ", Field(fresh.RootElement, volume.DeviceIdentity, "volume_all_label_configured").GetProperty("value").GetString());
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var failed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); AssertVolumeValuesEmpty(failed.RootElement, volume.DeviceIdentity);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var recovered = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal("89ABCDEF", Field(recovered.RootElement, volume.DeviceIdentity, "volume_all_filesystem_serial_reported").GetProperty("value").GetString());
        await app.StopAsync();
    }

    [Fact]
    public async Task UnifiedStreamUsesOriginalWorkerForVolumeMetadataAndCancellationKeepsCachedDisplay()
    {
        var space = new VolumeSpaceInformationCollectorTests.Api(); var metadata = new VolumeTopologyInformationCollectorTests.Api();
        var collector = new WindowsVolumeSpaceInformationReader(space, metadata);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { VolumeSpaceInformation = collector.Read() });
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Empty(metadata.Requests);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
            new { id = "volume-metadata", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var value = message.RootElement.GetProperty("value");
        Assert.Equal("9007199254740993", Field(value, VolumeSpaceInformationCollectorTests.IdA, "volume_all_disk_extents_reported").GetProperty("value")[0].GetProperty("startingOffsetBytes").GetString());
        Assert.Equal("windows.volume-metadata", Field(value, VolumeSpaceInformationCollectorTests.IdA, "volume_all_disk_extents_reported").GetProperty("definition").GetProperty("sourceId").GetString());
        Assert.True(space.Directories >= 1); Assert.Equal(space.Directories * 6, metadata.Requests.Count);
        await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var queries = metadata.Requests.Count; var cached = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal("89ABCDEF", Field(displayed.RootElement, VolumeSpaceInformationCollectorTests.IdA, "volume_all_filesystem_serial_reported").GetProperty("value").GetString());
        await Task.Delay(1300); Assert.Equal(queries, metadata.Requests.Count); Assert.Same(cached, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }
}
