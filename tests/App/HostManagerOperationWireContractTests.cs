using System.Text.Json;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerOperationWireContractTests
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void SubjectAndStagePayloadsContainTypedFactsAndRejectText()
    {
        var subject = new HostManagerOperationSubject(HostManagerOperationTargetKind.Component, TargetId: "component-id");
        Assert.Equal(subject, ResourceManager.App.Infrastructure.Operations.HostManagerOperationRequestCodec.DecodeSubject(
            ResourceManager.App.Infrastructure.Operations.HostManagerOperationRequestCodec.EncodeSubject(subject)));
        Assert.Equal(new byte[] { 1 }, ResourceManager.App.Infrastructure.Operations.HostManagerOperationRequestCodec.EncodeStage(HostManagerOperationStage.Download));
        Assert.Throws<InvalidDataException>(() => ResourceManager.App.Infrastructure.Operations.HostManagerOperationRequestCodec.DecodeStage([2]));
        Assert.Throws<InvalidDataException>(() => ResourceManager.App.Infrastructure.Operations.HostManagerOperationRequestCodec.DecodeStage("download"u8));
    }

    [Fact]
    public void PublishedStateUsesOneVersionedEnvelopeAndDecimalUInt64Fields()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-22T12:00:00Z");
        var operation = new HostManagerOperationSnapshot(
            "00000000000000000000000000000001",
            "component.install",
            "component.example",
            new HostManagerOperationSubject(HostManagerOperationTargetKind.Component, TargetId: "example"),
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
                HostManagerOperationStage.Download),
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
        Assert.False(item.TryGetProperty("title", out _));
        Assert.False(item.TryGetProperty("result", out _));
        Assert.False(item.TryGetProperty("error", out _));
        Assert.Equal("example", item.GetProperty("subject").GetProperty("targetId").GetString());
        var progress = item.GetProperty("progress");
        Assert.Equal("Download", progress.GetProperty("stage").GetString());
        Assert.False(progress.TryGetProperty("message", out _));
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
