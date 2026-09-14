using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed class GpuRemoteCallRecordTests
{
    [Theory]
    [InlineData(GpuRemoteCallKind.LoadObservationProvider)]
    [InlineData(GpuRemoteCallKind.StartApiObservation)]
    [InlineData(GpuRemoteCallKind.ReadApiObservation)]
    [InlineData(GpuRemoteCallKind.StopApiObservation)]
    public void ApiObservationRoundTripRemainsAnActionAndNeverBecomesAPlacement(GpuRemoteCallKind kind)
    {
        var pending = Pending();
        pending = pending with { Request = pending.Request with { Kind = kind } };
        Assert.True(GpuRemoteCallRecord.TryRead(pending.Encode(), out var read));
        Assert.Equal(pending.Request, read.Request);
        Assert.True(read.BlocksProcess);
        var placements = new[] { Placement(read.Encode()) };
        Assert.True(GpuActionFacts.HasUnsettledActions(placements));
        Assert.False(GpuActionFacts.HasPlacementEffects(placements));
        Assert.Empty(GpuActionFacts.AvailablePlacementEffects(placements));
        var restored = new HostManagerPlacementRollbackExecutor(null!, null!, null!, NullLogger.Instance).RestoreRecord(read.Encode());
        Assert.Equal(HostManagerPlacementSettlementKind.RetainedActionFact, restored.Kind);
        Assert.False(restored.CanRemoveReceipt);
    }

    private static GpuRemoteCallRecord Pending()
        => new(new(Guid.NewGuid(), new(12345, 134330000000000001, "fixture", "C:\\fixture.exe"),
            GpuRemoteCallKind.ReadDevices, 4096, 80, true),
            new(8192, 123, 134330000000000002, null, null, false, "running", null),
            new(8192, 123, 134330000000000002, null, null, false, "remote-call-cancelled", null));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteCallRoundTripKeepsItsOriginalResultAndOptionalLateSettlement(bool settled)
    {
        var original = Pending();
        var final = settled ? original with { Settlement = original.Result! with
            { ExitCode = 1, Status = "completed", Response = new byte[80], ResourcesReleased = true } } : original;
        var encoded = final.Encode();
        Assert.True(GpuRemoteCallRecord.TryRead(encoded, out var read));
        Assert.Equal(original.Request, read.Request);
        Assert.Equal(original.Result, read.Result);
        Assert.Equal(!settled, read.BlocksProcess);
        Assert.Equal(settled, read.Settlement is not null);
        Assert.Equal(original.RecordId, encoded.RecordId);
        Assert.True(original.BlocksProcess);
    }

    [Fact]
    public void RemoteCallUnknownNativeThreadCreationIsRetainedWithoutInventingIdentity()
    {
        var fact = Pending();
        fact = fact with { Started = fact.Started! with { ThreadCreationFileTimeUtc = null },
            Result = fact.Result! with { ThreadCreationFileTimeUtc = null } };
        Assert.True(GpuRemoteCallRecord.TryRead(fact.Encode(), out var read));
        Assert.Null(read.Result!.ThreadCreationFileTimeUtc);
        Assert.True(read.BlocksProcess);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("id")]
    [InlineData("no-payload")]
    [InlineData("unknown-field")]
    [InlineData("missing-request")]
    [InlineData("null-process")]
    [InlineData("string-pid")]
    [InlineData("fractional-pid")]
    [InlineData("string-birth")]
    [InlineData("fractional-birth")]
    [InlineData("relative-image")]
    [InlineData("unknown-kind")]
    [InlineData("zero-entry")]
    [InlineData("no-length")]
    [InlineData("duplicate")]
    [InlineData("changed-thread")]
    [InlineData("changed-birth")]
    [InlineData("changed-buffer")]
    [InlineData("wrong-response-length")]
    [InlineData("invented-completion")]
    [InlineData("unfinished-settlement")]
    public void RemoteCallCorruptionStaysAnActionFactAndNeverEntersNativeRestoration(string mutation)
    {
        var original = Pending().Encode();
        var metadata = new Dictionary<string, string>(original.Metadata!);
        var node = JsonNode.Parse(metadata["remoteCallPayload"])!.AsObject();
        var id = original.RecordId;
        switch (mutation)
        {
            case "version": metadata["remoteCallVersion"] = "2"; break;
            case "id": id += "x"; break;
            case "no-payload": metadata.Remove("remoteCallPayload"); break;
            case "unknown-field": node["Unexpected"] = 1; break;
            case "missing-request": node.Remove("Request"); break;
            case "null-process": node["Request"]!["Process"] = null; break;
            case "string-pid": node["Request"]!["Process"]!["ProcessId"] = "12345"; break;
            case "fractional-pid": node["Request"]!["Process"]!["ProcessId"] = 12345.25; break;
            case "string-birth": node["Request"]!["Process"]!["ProcessStartKey"] = "134330000000000001"; break;
            case "fractional-birth": node["Request"]!["Process"]!["ProcessStartKey"] = 1.5; break;
            case "relative-image": node["Request"]!["Process"]!["ExecutablePath"] = "fixture.exe"; break;
            case "unknown-kind": node["Request"]!["Kind"] = 99; break;
            case "zero-entry": node["Request"]!["FunctionAddress"] = 0; break;
            case "no-length": node["Request"]!["ParameterByteLength"] = 0; break;
            case "changed-thread": node["Result"]!["ThreadId"] = 456; break;
            case "changed-birth": node["Result"]!["ThreadCreationFileTimeUtc"] = 134330000000000003UL; break;
            case "changed-buffer": node["Result"]!["ParameterAddress"] = 9999; break;
            case "wrong-response-length": node["Result"]!["Response"] = Convert.ToBase64String([1]); break;
            case "invented-completion": node["Result"]!["Status"] = "completed"; break;
            case "unfinished-settlement": node["Settlement"] = node["Result"]!.DeepClone(); break;
        }
        if (mutation != "no-payload") metadata["remoteCallPayload"] = mutation == "duplicate"
            ? "{\"Result\":null," + node.ToJsonString()[1..] : node.ToJsonString();
        var record = original with { RecordId = id, Metadata = metadata };
        Assert.False(GpuRemoteCallRecord.TryRead(record, out _));
        Assert.True(GpuActionFacts.IsActionFact(record));
        var placement = Placement(record);
        Assert.True(GpuActionFacts.BlocksProcess([placement], placement.TargetId, 12345, 134330000000000001));
        Assert.False(GpuActionFacts.BlocksProcess([placement], "unrelated", 12345, 134330000000000001));
        var restored = new HostManagerPlacementRollbackExecutor(null!, null!, null!, NullLogger.Instance).RestoreRecord(record);
        Assert.Equal(HostManagerPlacementSettlementKind.RetainedActionFact, restored.Kind);
        Assert.False(restored.CanRemoveReceipt);
        Assert.Equal(0U, HostManagerPlacementCoordinatorProjection.ProjectApplied([placement], []).RowCount);
    }
}
