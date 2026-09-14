using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed class GpuWindowActionRecordTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedFactRoundTripsWithoutChangingTheOriginalRecord(bool completed)
    {
        var prepared = Prepared();
        var pending = GpuWindowActionRecord.Create(prepared);
        var serialized = pending.Metadata!["windowActionPayload"];
        var record = completed ? GpuWindowActionRecord.Complete(pending, Restored(prepared)) : pending;
        Assert.True(GpuWindowActionRecord.TryRead(record, out var fact));
        Assert.Equal(prepared, fact.Prepared);
        Assert.Equal(completed ? Restored(prepared) : null, fact.Result);
        Assert.Equal(!completed, fact.BlocksAutomaticAction);
        Assert.Equal(serialized, pending.Metadata["windowActionPayload"]);
        Assert.Equal(pending.RecordId, record.RecordId);
        if (completed) Assert.Throws<InvalidDataException>(() => GpuWindowActionRecord.Complete(record, Restored(prepared)));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("timeout")]
    [InlineData("restore-refused")]
    [InlineData("save-pending")]
    public void UnsettledFactBlocksTheExactProcessRegardlessOfTargetLabelOrAdapter(string reason)
    {
        var prepared = Prepared();
        var result = reason switch
        {
            "pending" => null,
            "timeout" => Unknown(prepared),
            "restore-refused" => Restored(prepared) with
            {
                Outcome = GpuWindowActionOutcome.RestorationUnconfirmed,
                Completion = Restored(prepared).Completion! with
                {
                    Restore = new(GpuWindowCallState.Rejected, 5),
                    AfterRestore = new(GpuWindowReadState.Available, null, prepared.Window.Before with { Width = 81 })
                }
            },
            _ => Unknown(prepared) with
            {
                Outcome = GpuWindowActionOutcome.NotExecuted, AuthorizationMayHaveBeenSent = false, PersistencePending = true
            }
        };
        var record = GpuWindowActionRecord.Create(prepared, result);
        var placement = Placement(record);
        var target = prepared.Window.Request;
        Assert.True(GpuWindowActionRecord.BlocksProcess([placement], "a-different-adapter-or-label", target.ProcessId, (ulong)target.CreationFileTimeUtc));
        Assert.False(GpuWindowActionRecord.BlocksProcess([placement], placement.TargetId, target.ProcessId + 1, (ulong)target.CreationFileTimeUtc));
        Assert.False(GpuWindowActionRecord.BlocksProcess([placement], placement.TargetId, target.ProcessId, (ulong)target.CreationFileTimeUtc + 1));
        Assert.True(GpuActionFacts.HasUnsettledActions([placement]));
        Assert.False(GpuActionFacts.HasPlacementEffects([placement]));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("no-payload")]
    [InlineData("id")]
    [InlineData("unknown-field")]
    [InlineData("missing-prepared")]
    [InlineData("missing-result")]
    [InlineData("null-window")]
    [InlineData("fractional-pid")]
    [InlineData("string-pid")]
    [InlineData("fractional-birth")]
    [InlineData("string-birth")]
    [InlineData("duplicate")]
    [InlineData("relative-worker")]
    [InlineData("invalid-method")]
    public void CorruptNewFactNeverFallsThroughToLegacyAlreadyRestored(string mutation)
    {
        var original = GpuWindowActionRecord.Create(Prepared());
        var metadata = new Dictionary<string, string>(original.Metadata!);
        var payload = JsonNode.Parse(metadata["windowActionPayload"])!.AsObject();
        var id = original.RecordId;
        switch (mutation)
        {
            case "version": metadata["windowActionVersion"] = "2"; break;
            case "no-payload": metadata.Remove("windowActionPayload"); break;
            case "id": id += ":corrupt"; break;
            case "unknown-field": payload["Invented"] = true; break;
            case "missing-prepared": payload.Remove("Prepared"); break;
            case "missing-result": payload.Remove("Result"); break;
            case "null-window": payload["Prepared"]!["Window"] = null; break;
            case "fractional-pid": payload["Prepared"]!["Window"]!["Request"]!["ProcessId"] = 19608.25; break;
            case "string-pid": payload["Prepared"]!["Window"]!["Request"]!["ProcessId"] = "19608"; break;
            case "fractional-birth": payload["Prepared"]!["Window"]!["Request"]!["CreationFileTimeUtc"] = 1.5; break;
            case "string-birth": payload["Prepared"]!["Window"]!["Request"]!["CreationFileTimeUtc"] = "134332600000000001"; break;
            case "relative-worker": payload["Prepared"]!["Worker"]!["ExecutablePath"] = "worker.exe"; break;
            case "invalid-method": payload["Prepared"]!["Window"]!["Request"]!["Method"] = 0; break;
        }
        if (mutation != "no-payload") metadata["windowActionPayload"] = mutation == "duplicate"
            ? "{\"Result\":null," + payload.ToJsonString()[1..] : payload.ToJsonString();
        metadata["assignedPositionId"] = "gpu:0";
        var bad = original with { RecordId = id, Metadata = metadata };
        Assert.True(GpuWindowActionRecord.IsActionFact(bad));
        Assert.False(GpuWindowActionRecord.TryRead(bad, out _));
        var placement = Placement(bad);
        Assert.True(GpuWindowActionRecord.BlocksProcess([placement], placement.TargetId, 1234, 1234));
        Assert.False(GpuWindowActionRecord.BlocksProcess([placement], "unrelated", 1234, 1234));
        var settlement = new HostManagerPlacementRollbackExecutor(null!, null!, null!, NullLogger.Instance).RestoreRecord(bad);
        Assert.Equal(HostManagerPlacementSettlementKind.RetainedActionFact, settlement.Kind);
        Assert.False(settlement.CanRemoveReceipt);
        Assert.Same(bad, settlement.Record);
        Assert.Equal(0U, HostManagerPlacementCoordinatorProjection.ProjectApplied([placement], []).RowCount);
    }

    [Theory]
    [InlineData("different-worker")]
    [InlineData("unobserved-exit")]
    [InlineData("wrong-outcome")]
    [InlineData("no-authorization")]
    [InlineData("pending-authorization")]
    [InlineData("no-change")]
    [InlineData("invented-read")]
    public void ResultCannotClaimMoreThanItsSingleExecutionFacts(string mutation)
    {
        var prepared = Prepared();
        var result = Restored(prepared);
        result = mutation switch
        {
            "different-worker" => result with { Prepared = prepared with { Worker = prepared.Worker with { ProcessId = 20000 } } },
            "unobserved-exit" => result with { Cleanup = result.Cleanup with { ExitObserved = false } },
            "wrong-outcome" => result with { Outcome = GpuWindowActionOutcome.RedrawRequested },
            "no-authorization" => result with { AuthorizationMayHaveBeenSent = false },
            "pending-authorization" => result with { PersistencePending = true },
            "no-change" => result with { Completion = result.Completion! with { Change = new(GpuWindowCallState.NotAttempted, null) } },
            _ => result with { Completion = result.Completion! with { AfterRestore = new(GpuWindowReadState.Unavailable, 5, prepared.Window.Before) } }
        };
        Assert.Throws<InvalidDataException>(() => GpuWindowActionRecord.Create(prepared, result));
    }

    [Fact]
    public void CompletedFactsAreRetainedButDoNotConsumePlacementBudgetOrBlockAnotherAction()
    {
        var prepared = Prepared();
        var fact = GpuWindowActionRecord.Create(prepared, Restored(prepared));
        var policy = Policy(prepared);
        var placement = Placement(fact, policy);
        Assert.False(GpuActionFacts.HasUnsettledActions([placement]));
        Assert.True(GpuActionFacts.HasPlacementEffects([placement]));
        var rows = new NativePlacementAppliedInput[1];
        Assert.Equal(1U, HostManagerPlacementCoordinatorProjection.ProjectApplied([placement], rows).RowCount);
        Assert.Equal((uint)NativePlacementKind.GpuShimPolicy, rows[0].PlacementKind);
        Assert.Throws<InvalidDataException>(() => HostManagerPlacementCoordinatorProjection.ProjectDesired(
            [new(placement, fact, 1)], new NativePlacementDesiredInput[1]));
        Assert.Equal(2, placement.Records.Count);
    }

    [Fact]
    public void LegacyTriggerKeepsItsOriginalNoWindowEffectMeaning()
    {
        var legacy = new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger, "legacy",
            new Dictionary<string, string> { ["assignedPositionId"] = "gpu:0" });
        Assert.False(GpuWindowActionRecord.IsActionFact(legacy));
        Assert.False(GpuWindowActionRecord.TryRead(legacy, out _));
        Assert.Equal(HostManagerPlacementSettlementKind.AlreadyRestored,
            new HostManagerPlacementRollbackExecutor(null!, null!, null!, NullLogger.Instance).RestoreRecord(legacy).Kind);
    }
}
