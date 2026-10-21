using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task DiskIdentityHttpUsesCurrentEvidenceAndSourceFailureClearsOnlyTheRelation()
    {
        var phase = 0;
        var reader = new Reader(() =>
        {
            var disk = DiskIdentityInformationCollectorTests.Disk();
            IReadOnlyList<DeviceTopologyStorageInformation>? physical = [DiskIdentityInformationCollectorTests.Hardware()];
            if (phase == 1) disk = disk with { HardwareIdentification = disk.HardwareIdentification! with { DeviceIdentifiers = null } };
            if (phase == 2) physical = [physical[0] with { Ata = null, HardwareIdentityEvidence = null }];
            if (phase == 3) physical = null;
            if (phase == 4) physical = [];
            return DiskIdentityInformationCollectorTests.Snapshot(disk, physical);
        });
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        for (phase = 0; phase < 6; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); var reads = reader.ReadCount;
            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); var root = document.RootElement;
            var relation = Field(root, DiskLayoutInformationCollectorTests.IdA, DiskIdentityInformationCollectorTests.RelationName).GetProperty("value");
            if (phase == 0 || phase == 5) Assert.Equal(DiskIdentityInformationCollectorTests.HardwareId, relation.GetString());
            else Assert.Equal(JsonValueKind.Null, relation.ValueKind);
            Assert.Equal(1, Field(root, DiskLayoutInformationCollectorTests.IdA, "storage_all_partition_layout_reported").GetProperty("value").GetArrayLength());
            Assert.True(Field(root, DiskLayoutInformationCollectorTests.IdA, "storage_all_partition_alignment_actual").GetProperty("value")[0].GetProperty("physicalStartAligned").GetBoolean());
            if (phase == 3) Assert.Null(Assert.Single(fixture.Provider.ReadState().Snapshot!.StorageInformation!).HardwareIdentityEvidence);
            if (phase == 4) Assert.DoesNotContain(root.GetProperty("devices").EnumerateArray(), item => item.GetProperty("deviceIdentity").GetString() == DiskIdentityInformationCollectorTests.HardwareId);
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task PersistedIdentityRelationIsNeverDisplayedAsCurrentAfterStartupOrWholeCaptureFailure()
    {
        var raw = DiskIdentityInformationCollectorTests.Snapshot(DiskIdentityInformationCollectorTests.Disk(), [DiskIdentityInformationCollectorTests.Hardware()]);
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 11, new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new IOException("failed"), raw);
        using var fixture = new Fixture(reader, persisted); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var initial = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); AssertRelationEmpty(initial.RootElement); Assert.Equal(0, reader.ReadCount);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var fresh = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); Assert.Equal(DiskIdentityInformationCollectorTests.HardwareId, Field(fresh.RootElement, DiskLayoutInformationCollectorTests.IdA, DiskIdentityInformationCollectorTests.RelationName).GetProperty("value").GetString());
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var failed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); AssertRelationEmpty(failed.RootElement); AssertDiskLayoutEmpty(failed.RootElement, DiskLayoutInformationCollectorTests.IdA); AssertAlignmentEmpty(failed.RootElement);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        using var recovered = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")); Assert.Equal(DiskIdentityInformationCollectorTests.HardwareId, Field(recovered.RootElement, DiskLayoutInformationCollectorTests.IdA, DiskIdentityInformationCollectorTests.RelationName).GetProperty("value").GetString());
        await app.StopAsync();
    }

    [Fact]
    public async Task UnifiedDiskIdentityStreamUsesOriginalOwnerAndRetainsConfirmedDisplayAfterCancellation()
    {
        var reader = new Reader(DiskIdentityInformationCollectorTests.Snapshot(DiskIdentityInformationCollectorTests.Disk(), [DiskIdentityInformationCollectorTests.Hardware()]));
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "disk-identity", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)); using var message = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        Assert.Equal(DiskIdentityInformationCollectorTests.HardwareId, Field(message.RootElement.GetProperty("value"), DiskLayoutInformationCollectorTests.IdA, DiskIdentityInformationCollectorTests.RelationName).GetProperty("value").GetString());
        Assert.True(reader.ReadCount >= 1); await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var reads = reader.ReadCount; var cached = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal(DiskIdentityInformationCollectorTests.HardwareId, Field(displayed.RootElement, DiskLayoutInformationCollectorTests.IdA, DiskIdentityInformationCollectorTests.RelationName).GetProperty("value").GetString());
        await Task.Delay(1300); Assert.Equal(reads, reader.ReadCount); Assert.Same(cached, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }

    private static void AssertRelationEmpty(JsonElement root)
        => Assert.Equal(JsonValueKind.Null, Field(root, DiskLayoutInformationCollectorTests.IdA, DiskIdentityInformationCollectorTests.RelationName).GetProperty("value").ValueKind);
}
