using System.Text.Json;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerOperationWireContractTests
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void PublishedStateUsesOneVersionedEnvelopeAndDecimalUInt64Fields()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-22T12:00:00Z");
        var operation = new HostManagerOperationSnapshot(
            "00000000000000000000000000000001",
            "component.install",
            "component.example",
            "Install component",
            HostManagerOperationStates.Succeeded,
            ulong.MaxValue,
            ulong.MaxValue - 1,
            1,
            3,
            timestamp,
            timestamp,
            timestamp,
            false,
            new HostManagerOperationProgress(
                ulong.MaxValue,
                100,
                ulong.MaxValue - 1,
                ulong.MaxValue,
                ulong.MaxValue,
                "complete",
                "done"),
            "installed",
            null);
        var state = new HostManagerOperationsPublishedState(
            timestamp,
            ulong.MaxValue,
            ulong.MaxValue,
            new HostManagerOperationCoordinatorHealth(
                true,
                false,
                null,
                null,
                12,
                ulong.MaxValue),
            [operation]);

        var json = JsonSerializer.SerializeToElement(
            HostManagerOperationWireProjection.Project(state),
            WebJson);

        Assert.Equal(
            HostManagerOperationWireProjection.Schema,
            json.GetProperty("schema").GetString());
        Assert.Equal(
            ulong.MaxValue.ToString(),
            json.GetProperty("publicationRevision").GetString());
        Assert.Equal(
            ulong.MaxValue.ToString(),
            json.GetProperty("configurationGeneration").GetString());
        var item = Assert.Single(json.GetProperty("operations").EnumerateArray());
        Assert.Equal(
            ulong.MaxValue.ToString(),
            item.GetProperty("configurationGeneration").GetString());
        Assert.Equal(
            (ulong.MaxValue - 1).ToString(),
            item.GetProperty("stateRevision").GetString());
        Assert.Equal(JsonValueKind.String, item.GetProperty("result").ValueKind);
        var progress = item.GetProperty("progress");
        Assert.Equal(
            ulong.MaxValue.ToString(),
            progress.GetProperty("sequence").GetString());
        Assert.Equal(
            (ulong.MaxValue - 1).ToString(),
            progress.GetProperty("bytesDone").GetString());
        Assert.Equal(
            ulong.MaxValue.ToString(),
            progress.GetProperty("bytesTotal").GetString());
        Assert.Equal(
            ulong.MaxValue.ToString(),
            progress.GetProperty("speedBytesPerSecond").GetString());
    }
}
